// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.LanguageModel;

public class OpenAIRequest
{
    [JsonPropertyName("frequency_penalty")]
    public long? FrequencyPenalty { get; set; }
    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; set; }
    public string Model { get; set; } = string.Empty;
    [JsonPropertyName("presence_penalty")]
    public long? PresencePenalty { get; set; }
    public object? Stop { get; set; }
    public bool? Stream { get; set; }
    public double? Temperature { get; set; }
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    public static bool TryGetOpenAIRequest(string content, ILogger logger, out OpenAIRequest? request)
    {
        logger.LogTrace("{Method} called", nameof(TryGetOpenAIRequest));

        request = null;

        if (string.IsNullOrEmpty(content))
        {
            logger.LogDebug("Request content is empty or null");
            return false;
        }

        try
        {
            logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Check for Responses API request (has "input" with optional "instructions" or "previous_response_id")
            // Must check before embedding to distinguish them
            if (rawRequest.TryGetProperty("input", out _) &&
                !rawRequest.TryGetProperty("voice", out _) &&
                (rawRequest.TryGetProperty("instructions", out _) ||
                 rawRequest.TryGetProperty("previous_response_id", out _) ||
                 rawRequest.TryGetProperty("store", out _) ||
                 // Check if input is a string (Responses API supports string input directly)
                 (rawRequest.TryGetProperty("input", out var inputProp) &&
                  (inputProp.ValueKind == JsonValueKind.String ||
                   // Or if input is an array with items that have "role" property (message items)
                   (inputProp.ValueKind == JsonValueKind.Array &&
                    inputProp.GetArrayLength() > 0 &&
                    inputProp[0].TryGetProperty("role", out _))))))
            {
                logger.LogDebug("Request is a Responses API request");
                request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Check for completion request (has "prompt", but not specific to image)
            if (rawRequest.TryGetProperty("prompt", out _) &&
                !rawRequest.TryGetProperty("size", out _) &&
                !rawRequest.TryGetProperty("n", out _))
            {
                logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Chat completion request
            if (rawRequest.TryGetProperty("messages", out _))
            {
                logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Embedding request
            if (rawRequest.TryGetProperty("input", out _) &&
                rawRequest.TryGetProperty("model", out _) &&
                !rawRequest.TryGetProperty("voice", out _))
            {
                logger.LogDebug("Request is an embedding request");
                request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Image generation request
            if (rawRequest.TryGetProperty("prompt", out _) &&
                (rawRequest.TryGetProperty("size", out _) || rawRequest.TryGetProperty("n", out _)))
            {
                logger.LogDebug("Request is an image generation request");
                request = JsonSerializer.Deserialize<OpenAIImageRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio transcription request
            if (rawRequest.TryGetProperty("file", out _))
            {
                logger.LogDebug("Request is an audio transcription request");
                request = JsonSerializer.Deserialize<OpenAIAudioRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio speech synthesis request
            if (rawRequest.TryGetProperty("input", out _) && rawRequest.TryGetProperty("voice", out _))
            {
                logger.LogDebug("Request is an audio speech synthesis request");
                request = JsonSerializer.Deserialize<OpenAIAudioSpeechRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Fine-tuning request
            if (rawRequest.TryGetProperty("training_file", out _))
            {
                logger.LogDebug("Request is a fine-tuning request");
                request = JsonSerializer.Deserialize<OpenAIFineTuneRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            logger.LogDebug("Request is not an OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }
}

public class OpenAIResponse : ILanguageModelCompletionResponse
{
    public long Created { get; set; }
    public OpenAIError? Error { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
#pragma warning disable CA1720
    public string Object { get; set; } = "text_completion";
#pragma warning restore CA1720
    [JsonPropertyName("prompt_filter_results")]
    public IEnumerable<OpenAIResponsePromptFilterResult> PromptFilterResults { get; set; } = [];
    public OpenAIResponseUsage Usage { get; set; } = new();
    public string? RequestUrl { get; set; }

    public string? ErrorMessage => Error?.Message;
    public virtual string? Response { get; }

    public OpenAIResponse ConvertToOpenAIResponse() => this;
}

public class OpenAIResponse<TChoice> : OpenAIResponse
{
    public IEnumerable<TChoice>? Choices { get; set; }
}

public abstract class OpenAIResponseChoice
{
    [JsonPropertyName("content_filter_results")]
#pragma warning disable CA2227
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = [];
#pragma warning restore CA2227
    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";
    public long Index { get; set; }
    [JsonPropertyName("logprobs")]
    public int? LogProbabilities { get; set; }
}

public class OpenAICompletionRequest : OpenAIRequest
{
    public string Prompt { get; set; } = string.Empty;
}

public class OpenAIChatCompletionRequest : OpenAIRequest
{
    public IEnumerable<OpenAIChatCompletionMessage> Messages { get; set; } = [];
}

public class OpenAIError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public class OpenAIResponseUsage
{
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }
    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokenDetails? PromptTokensDetails { get; set; }
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }
}

public class PromptTokenDetails
{
    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }
}

public class OpenAIResponsePromptFilterResult
{
    [JsonPropertyName("content_filter_results")]
#pragma warning disable CA2227
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = [];
#pragma warning restore CA2227
    [JsonPropertyName("prompt_index")]
    public long PromptIndex { get; set; }
}

public class OpenAIResponseContentFilterResult
{
    public bool Filtered { get; set; }
    public string Severity { get; set; } = "safe";
}

public class OpenAICompletionResponse : OpenAIResponse<OpenAICompletionResponseChoice>
{
    public override string? Response => Choices is not null && Choices.Any() ? Choices.Last().Text : null;
}

public class OpenAICompletionResponseChoice : OpenAIResponseChoice
{
    public string Text { get; set; } = string.Empty;
}

#region content parts

public abstract class OpenAIContentPart
{
    public string? Type { get; set; }
}

public class OpenAITextContentPart : OpenAIContentPart
{
    public string? Text { get; set; }
}

public class OpenAIImageContentPartUrl
{
    public string? Detail { get; set; } = "auto";
    public string? Url { get; set; }
}

public class OpenAIImageContentPart : OpenAIContentPart
{
    [JsonPropertyName("image_url")]
    public OpenAIImageContentPartUrl? Url { get; set; }
}

public class OpenAIAudioContentPartInputAudio
{
    public string? Data { get; set; }
    public string? Format { get; set; }
}

public class OpenAIAudioContentPart : OpenAIContentPart
{
    [JsonPropertyName("input_audio")]
    public OpenAIAudioContentPartInputAudio? InputAudio { get; set; }
}

public class OpenAIFileContentPartFile
{
    [JsonPropertyName("file_data")]
    public string? Data { get; set; }
    [JsonPropertyName("file_id")]
    public string? Id { get; set; }
    [JsonPropertyName("filename")]
    public string? Name { get; set; }
}

public class OpenAIFileContentPart : OpenAIContentPart
{
    public OpenAIFileContentPartFile? File { get; set; }
}

#endregion

public class OpenAIChatCompletionMessage : ILanguageModelChatCompletionMessage
{
    [JsonConverter(typeof(OpenAIContentPartJsonConverter))]
    public object Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }

        var m = (OpenAIChatCompletionMessage)obj;
        return Content == m.Content && Role == m.Role;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Role);
    }
}

public class OpenAIChatCompletionResponse : OpenAIResponse<OpenAIChatCompletionResponseChoice>
{
    public override string? Response => Choices is not null && Choices.Any() ?
        Choices.Last().Message.Content : null;
}

public class OpenAIChatCompletionResponseChoice : OpenAIResponseChoice
{
    public OpenAIChatCompletionResponseChoiceMessage Message { get; set; } = new();
}

public class OpenAIChatCompletionResponseChoiceMessage
{
    public string Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class OpenAIAudioRequest : OpenAIRequest
{
    public string File { get; set; } = string.Empty;
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public string? Prompt { get; set; }
    public string? Language { get; set; }
}

public class OpenAIAudioSpeechRequest : OpenAIRequest
{
    public string Input { get; set; } = string.Empty;
    public string Voice { get; set; } = string.Empty;
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public double? Speed { get; set; }
}

public class OpenAIAudioTranscriptionResponse : OpenAIResponse
{
    public string Text { get; set; } = string.Empty;
    public override string? Response => Text;
}

public class OpenAIEmbeddingRequest : OpenAIRequest
{
    public string? Input { get; set; }
    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }
    public int? Dimensions { get; set; }
}

public class OpenAIEmbeddingResponse : OpenAIResponse
{
    public IEnumerable<OpenAIEmbeddingData>? Data { get; set; }
    public override string? Response => null; // Embeddings don't have a text response
}

public class OpenAIEmbeddingData
{
    public IEnumerable<float>? Embedding { get; set; }
    public int Index { get; set; }
#pragma warning disable CA1720
    public string? Object { get; set; }
#pragma warning restore CA1720
}

public class OpenAIFineTuneRequest : OpenAIRequest
{
    [JsonPropertyName("training_file")]
    public string TrainingFile { get; set; } = string.Empty;
    [JsonPropertyName("validation_file")]
    public string? ValidationFile { get; set; }
    public int? Epochs { get; set; }
    [JsonPropertyName("batch_size")]
    public int? BatchSize { get; set; }
    [JsonPropertyName("learning_rate_multiplier")]
    public double? LearningRateMultiplier { get; set; }
    public string? Suffix { get; set; }
}

public class OpenAIFineTuneResponse : OpenAIResponse
{
    [JsonPropertyName("fine_tuned_model")]
    public string? FineTunedModel { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    [JsonPropertyName("training_file")]
    public string TrainingFile { get; set; } = string.Empty;
    [JsonPropertyName("validation_file")]
    public string? ValidationFile { get; set; }
    [JsonPropertyName("result_files")]
    public IEnumerable<object>? ResultFiles { get; set; }
    public override string? Response => FineTunedModel;
}

public class OpenAIImageRequest : OpenAIRequest
{
    public string Prompt { get; set; } = string.Empty;
    public int? N { get; set; }
    public string? Size { get; set; }
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public string? User { get; set; }
    public string? Quality { get; set; }
    public string? Style { get; set; }
}

public class OpenAIImageResponse : OpenAIResponse
{
    public IEnumerable<OpenAIImageData>? Data { get; set; }
    public override string? Response => null; // Image responses don't have a text response
}

public class OpenAIImageData
{
    public string? Url { get; set; }
    [JsonPropertyName("b64_json")]
    public string? Base64Json { get; set; }
    [JsonPropertyName("revised_prompt")]
    public string? RevisedPrompt { get; set; }
}

#region Responses API

/// <summary>
/// Request model for OpenAI Responses API (/v1/responses)
/// </summary>
public class OpenAIResponsesRequest : OpenAIRequest
{
    /// <summary>
    /// Text, array of text, or array of input items (messages) to generate a response for.
    /// </summary>
    [JsonConverter(typeof(OpenAIResponsesInputJsonConverter))]
    public object? Input { get; set; }

    /// <summary>
    /// System-level instructions for the model.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Whether to store the response for future reference.
    /// </summary>
    public bool? Store { get; set; }

    /// <summary>
    /// The ID of a previous response to continue the conversation.
    /// </summary>
    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }
}

/// <summary>
/// Input item for Responses API (similar to chat messages but with different structure)
/// </summary>
public class OpenAIResponsesInputItem
{
    /// <summary>
    /// The type of the item (e.g., "message", "function_call", "function_call_output")
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The role of the message (user, assistant, system)
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// The content of the item
    /// </summary>
    [JsonConverter(typeof(OpenAIResponsesInputContentJsonConverter))]
    public object? Content { get; set; }
}

/// <summary>
/// Response model for OpenAI Responses API
/// </summary>
public class OpenAIResponsesResponse : OpenAIResponse
{
    /// <summary>
    /// The timestamp when the response was created
    /// </summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// Array of output items from the model
    /// </summary>
    public IEnumerable<OpenAIResponsesOutputItem>? Output { get; set; }

    /// <summary>
    /// Helper property that returns the concatenated text from all message outputs
    /// </summary>
    [JsonPropertyName("output_text")]
    public string? OutputText { get; set; }

    /// <summary>
    /// The status of the response
    /// </summary>
    public string? Status { get; set; }

    public override string? Response => OutputText ?? GetOutputText();

    private string? GetOutputText()
    {
        if (Output is null)
        {
            return null;
        }

        var textParts = Output
            .Where(o => o.Type == "message" && o.Content is not null)
            .SelectMany(o => o.Content!)
            .Where(c => c.Type == "output_text" && !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text);

        return string.Join("", textParts);
    }
}

/// <summary>
/// Output item from Responses API
/// </summary>
public class OpenAIResponsesOutputItem
{
    /// <summary>
    /// The unique identifier for this output item
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The type of the output item (e.g., "message", "reasoning", "function_call")
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The status of this output item
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// The role of the message (for message type items)
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// The content array for message type items
    /// </summary>
    public IEnumerable<OpenAIResponsesOutputContent>? Content { get; set; }
}

/// <summary>
/// Content item within a Responses API output
/// </summary>
public class OpenAIResponsesOutputContent
{
    /// <summary>
    /// The type of content (e.g., "output_text")
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The text content
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Annotations on the content
    /// </summary>
    public IEnumerable<object>? Annotations { get; set; }
}

/// <summary>
/// JSON converter to handle the flexible "input" field in Responses API requests
/// which can be a string, array of strings, or array of input items
/// </summary>
public class OpenAIResponsesInputJsonConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartArray:
                var items = new List<object>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var str = reader.GetString();
                        if (str is not null)
                        {
                            items.Add(str);
                        }
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var item = JsonSerializer.Deserialize<OpenAIResponsesInputItem>(ref reader, options);
                        if (item is not null)
                        {
                            items.Add(item);
                        }
                    }
                }
                return items;
            case JsonTokenType.Null:
                return null;
            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>
/// JSON converter to handle the flexible "content" field in Responses API input items
/// which can be a string or array of content parts
/// </summary>
public class OpenAIResponsesInputContentJsonConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartArray:
                var items = new List<OpenAIContentPart>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var item = JsonSerializer.Deserialize<OpenAITextContentPart>(ref reader, options);
                        if (item is not null)
                        {
                            items.Add(item);
                        }
                    }
                }
                return items;
            case JsonTokenType.Null:
                return null;
            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

#endregion
