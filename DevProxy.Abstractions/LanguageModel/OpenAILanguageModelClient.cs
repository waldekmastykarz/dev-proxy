// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;

namespace DevProxy.Abstractions.LanguageModel;

public sealed class OpenAILanguageModelClient(
    LanguageModelConfiguration? configuration,
    ILogger logger) : ILanguageModelClient, IDisposable
{
    private readonly LanguageModelConfiguration? _configuration = configuration;
    private readonly ILogger _logger = logger;
    private readonly HttpClient _httpClient = new();
    private bool? _lmAvailable;
    private readonly Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> _cacheChatCompletion = [];

    public async Task<bool> IsEnabledAsync()
    {
        if (_lmAvailable.HasValue)
        {
            return _lmAvailable.Value;
        }

        _lmAvailable = await IsEnabledInternalAsync();
        return _lmAvailable.Value;
    }

    private async Task<bool> IsEnabledInternalAsync()
    {
        using var scope = _logger.BeginScope(nameof(OpenAILanguageModelClient));

        if (_configuration is null || !_configuration.Enabled)
        {
            return false;
        }

        if (string.IsNullOrEmpty(_configuration.Url))
        {
            _logger.LogError("URL is not set. Language model will be disabled");
            return false;
        }

        if (string.IsNullOrEmpty(_configuration.Model))
        {
            _logger.LogError("Model is not set. Language model will be disabled");
            return false;
        }

        _logger.LogDebug("Checking LM availability at {Url}...", _configuration.Url);

        try
        {
            var testCompletion = await GenerateChatCompletionInternalAsync([new()
            {
                Content = "Are you there? Reply with a yes or no.",
                Role = "user"
            }]);
            if (testCompletion?.ErrorMessage is not null)
            {
                _logger.LogError("Error: {Error}", testCompletion.ErrorMessage);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't reach language model at {Url}", _configuration.Url);
            return false;
        }
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options = null)
    {
        var response = await GenerateChatCompletionAsync([new OpenAIChatCompletionMessage() { Content = prompt, Role = "user" }], options);
        if (response == null)
        {
            return null;
        }
        if (response.ErrorMessage is not null)
        {
            _logger.LogError("Error: {Error}", response.ErrorMessage);
            return null;
        }
        var openAIResponse = (OpenAIChatCompletionResponse)response;

        return new OpenAICompletionResponse
        {
            Choices = openAIResponse.Choices?.Select(c => new OpenAICompletionResponseChoice
            {
                ContentFilterResults = c.ContentFilterResults,
                FinishReason = c.FinishReason,
                Index = c.Index,
                LogProbabilities = c.LogProbabilities,
                Text = c.Message.Content
            }).ToArray(),
            Created = openAIResponse.Created,
            Error = openAIResponse.Error,
            Id = openAIResponse.Id,
            Model = openAIResponse.Model,
            Object = openAIResponse.Object,
            PromptFilterResults = openAIResponse.PromptFilterResults,
            Usage = openAIResponse.Usage,
        };
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options = null)
    {
        using var scope = _logger.BeginScope(nameof(OpenAILanguageModelClient));

        if (_configuration is null)
        {
            return null;
        }

        if (!_lmAvailable.HasValue)
        {
            _logger.LogError("Language model availability is not checked. Call {IsEnabled} first.", nameof(IsEnabledAsync));
            return null;
        }

        if (!_lmAvailable.Value)
        {
            return null;
        }

        if (_configuration.CacheResponses && _cacheChatCompletion.TryGetCacheValue(messages, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for message: {LastMessage}", messages.Last().Content);
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync([.. messages.Select(m => (OpenAIChatCompletionMessage)m)], options);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError("Error: {Error}. Code: {Code}", response.Error.Message, response.Error.Code);
            return null;
        }
        else
        {
            if (_configuration.CacheResponses && response.Response is not null)
            {
                _cacheChatCompletion[messages] = response;
            }

            return response;
        }
    }

    private async Task<OpenAIChatCompletionResponse?> GenerateChatCompletionInternalAsync(OpenAIChatCompletionMessage[] messages, CompletionOptions? options = null)
    {
        Debug.Assert(_configuration != null, "Configuration is null");

        try
        {
            var url = $"{_configuration.Url}/chat/completions";
            _logger.LogDebug("Requesting chat completion. Message: {LastMessage}", messages.Last().Content);

            var payload = new OpenAIChatCompletionRequest
            {
                Messages = messages,
                Model = _configuration.Model,
                Stream = false,
                Temperature = options?.Temperature
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload, ProxyUtils.JsonSerializerOptions);
            _logger.LogDebug("Response: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OpenAIChatCompletionResponse>();
            if (res is null)
            {
                return res;
            }

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate chat completion");
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal static class OpenAICacheChatCompletionExtensions
{
    public static IEnumerable<ILanguageModelChatCompletionMessage>? GetKey(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages)
    {
        return cache.Keys.FirstOrDefault(k => k.SequenceEqual(messages));
    }

    public static bool TryGetCacheValue(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages, out OpenAIChatCompletionResponse? value)
    {
        var key = cache.GetKey(messages);
        if (key is null)
        {
            value = null;
            return false;
        }

        value = cache[key];
        return true;
    }
}