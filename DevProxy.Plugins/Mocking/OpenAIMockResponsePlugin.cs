// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

public sealed class OpenAIMockResponsePlugin(
    ILogger<OpenAIMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(OpenAIMockResponsePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        Logger.LogInformation("Checking language model availability...");
        if (!await languageModelClient.IsEnabledAsync(cancellationToken))
        {
            Logger.LogError("Local language model is not enabled. The {Plugin} will not be used.", Name);
            Enabled = false;
        }
    }

    public override async Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new(e.Session));
            return;
        }

        if (!TryGetOpenAIRequest(request.BodyString, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new(e.Session));
            return;
        }

        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            if ((await languageModelClient.GenerateCompletionAsync(completionRequest.Prompt, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAICompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else if (openAiRequest is OpenAIChatCompletionRequest chatRequest)
        {
            if ((await languageModelClient
                .GenerateChatCompletionAsync(chatRequest.Messages, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAIChatCompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else if (openAiRequest is OpenAIResponsesRequest responsesRequest)
        {
            // Convert Responses API request to chat completion messages for the local LLM
            var messages = ConvertResponsesInputToMessages(responsesRequest);

            if ((await languageModelClient
                .GenerateChatCompletionAsync(messages, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            // Convert the chat completion response to a Responses API response format
            var chatResponse = lmResponse.ConvertToOpenAIResponse() as OpenAIChatCompletionResponse;
            var responsesResponse = ConvertToResponsesResponse(chatResponse);
            SendMockResponse<OpenAIResponsesResponse>(responsesResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    private bool TryGetOpenAIRequest(string content, out OpenAIRequest? request)
    {
        request = null;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            Logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Check for Responses API request (has "input" with specific Responses API fields)
            // Must check before embedding to distinguish them
            if (rawRequest.TryGetProperty("input", out var inputProp) &&
                !rawRequest.TryGetProperty("voice", out _) &&
                (rawRequest.TryGetProperty("instructions", out _) ||
                 rawRequest.TryGetProperty("previous_response_id", out _) ||
                 rawRequest.TryGetProperty("store", out _) ||
                 // Or if input is an array with items that have "role" property (message items)
                 // This distinguishes from embeddings which use arrays of strings
                 (inputProp.ValueKind == JsonValueKind.Array &&
                  inputProp.GetArrayLength() > 0 &&
                  inputProp[0].ValueKind == JsonValueKind.Object &&
                  inputProp[0].TryGetProperty("role", out _))))
            {
                Logger.LogDebug("Request is a Responses API request");
                request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            if (rawRequest.TryGetProperty("prompt", out _))
            {
                Logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            if (rawRequest.TryGetProperty("messages", out _))
            {
                Logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            Logger.LogDebug("Request is not an OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            Logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }

    private static List<OpenAIChatCompletionMessage> ConvertResponsesInputToMessages(OpenAIResponsesRequest responsesRequest)
    {
        var messages = new List<OpenAIChatCompletionMessage>();

        // Add instructions as system message if present
        if (!string.IsNullOrEmpty(responsesRequest.Instructions))
        {
            messages.Add(new OpenAIChatCompletionMessage
            {
                Role = "system",
                Content = responsesRequest.Instructions
            });
        }

        // Convert input to messages
        if (responsesRequest.Input is string inputString)
        {
            messages.Add(new OpenAIChatCompletionMessage
            {
                Role = "user",
                Content = inputString
            });
        }
        else if (responsesRequest.Input is IEnumerable<object> inputItems)
        {
            foreach (var item in inputItems)
            {
                if (item is OpenAIResponsesInputItem inputItem)
                {
                    var content = inputItem.Content?.ToString() ?? "";
                    messages.Add(new OpenAIChatCompletionMessage
                    {
                        Role = inputItem.Role ?? "user",
                        Content = content
                    });
                }
                else if (item is string str)
                {
                    messages.Add(new OpenAIChatCompletionMessage
                    {
                        Role = "user",
                        Content = str
                    });
                }
            }
        }

        return messages;
    }

    private static OpenAIResponsesResponse ConvertToResponsesResponse(OpenAIChatCompletionResponse? chatResponse)
    {
        var outputText = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new OpenAIResponsesResponse
        {
            Id = $"resp_{Guid.NewGuid():N}",
            Object = "response",
            Created = now,
            CreatedAt = now,
            Model = chatResponse?.Model ?? "unknown",
            Status = "completed",
            OutputText = outputText,
            Output =
            [
                new OpenAIResponsesOutputItem
                {
                    Id = $"msg_{Guid.NewGuid():N}",
                    Type = "message",
                    Status = "completed",
                    Role = "assistant",
                    Content =
                    [
                        new OpenAIResponsesOutputContent
                        {
                            Type = "output_text",
                            Text = outputText,
                            Annotations = []
                        }
                    ]
                }
            ],
            Usage = chatResponse?.Usage ?? new OpenAIResponseUsage()
        };
    }

    private void SendMockResponse<TResponse>(OpenAIResponse response, string localLmUrl, ProxyRequestArgs e) where TResponse : OpenAIResponse
    {
        e.Session.GenericResponse(
            // we need this cast or else the JsonSerializer drops derived properties
            JsonSerializer.Serialize((TResponse)response, ProxyUtils.JsonSerializerOptions),
            HttpStatusCode.OK,
            [
                new HttpHeader("content-type", "application/json"),
                new HttpHeader("access-control-allow-origin", "*")
            ]
        );
        e.ResponseState.HasBeenSet = true;
        Logger.LogRequest($"200 {localLmUrl}", MessageType.Mocked, new(e.Session));
    }
}
