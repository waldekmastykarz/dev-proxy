// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.OpenTelemetry;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

public class LanguageModelPricesPluginConfiguration
{
#pragma warning disable CA2227
    public PricesData? Prices { get; set; }
#pragma warning restore CA2227
    public string? PricesFile { get; set; }
}

public sealed class OpenAITelemetryPluginConfiguration : LanguageModelPricesPluginConfiguration
{
    public string Application { get; set; } = "default";
    public string Currency { get; set; } = "USD";
    public string Environment { get; set; } = "development";
    public string ExporterEndpoint { get; set; } = "http://localhost:4318";
    public bool IncludePrompt { get; set; } = true;
    public bool IncludeCompletion { get; set; } = true;
    public bool IncludeCosts { get; set; }
}

public sealed class OpenAITelemetryPlugin(
    ILogger<OpenAITelemetryPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<OpenAITelemetryPluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection), IDisposable
{
    public override string Name => nameof(OpenAITelemetryPlugin);
    private LanguageModelPricesLoader? _loader;

    private const string ActivitySourceName = "DevProxy.OpenAI";
    private const string OpenAISystem = "openai";
    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(ActivitySourceName);
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;

    private static Histogram<long>? _tokenUsageMetric;
    private static Histogram<double>? _requestCostMetric;
    private static Counter<double>? _totalCostMetric;

    public override async Task InitializeAsync(InitArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e);

        if (Configuration.IncludeCosts)
        {
            Configuration.PricesFile = ProxyUtils.GetFullPath(Configuration.PricesFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<LanguageModelPricesLoader>(e.ServiceProvider, Configuration);
            _loader.InitFileWatcher();
        }

        InitializeOpenTelemetryExporter();
    }

    private void InitializeOpenTelemetryExporter()
    {
        Logger.LogTrace("InitializeOpenTelemetryExporter() called");

        try
        {
            void configureOtlpExporter(OtlpExporterOptions options)
            {
                // We use protobuf to allow intercepting Dev Proxy's own LLM traffic
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri(Configuration.ExporterEndpoint + "/v1/traces");
            }

            var resourceBuilder = ResourceBuilder
                .CreateDefault()
                .AddService(serviceName: "DevProxy.OpenAI", serviceVersion: ProxyUtils.ProductVersion);

            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(configureOtlpExporter)
                .Build();

            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(ActivitySourceName)
                .AddView(SemanticConvention.GEN_AI_METRIC_CLIENT_TOKEN_USAGE, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864]
                })
                .AddView(SemanticConvention.GEN_AI_USAGE_COST, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 50, 100]
                })
                .AddView(SemanticConvention.GEN_AI_USAGE_TOTAL_COST, new MetricStreamConfiguration())
                .AddOtlpExporter(configureOtlpExporter)
                .Build();

            _tokenUsageMetric = _meter.CreateHistogram<long>(
                SemanticConvention.GEN_AI_METRIC_CLIENT_TOKEN_USAGE,
                "tokens",
                "Number of tokens processed");
            _requestCostMetric = _meter.CreateHistogram<double>(
                SemanticConvention.GEN_AI_USAGE_COST,
                "cost",
                $"Estimated cost per request in {Configuration.Currency}");
            _totalCostMetric = _meter.CreateCounter<double>(
                SemanticConvention.GEN_AI_USAGE_TOTAL_COST,
                "cost",
                $"Total estimated cost for the session in {Configuration.Currency}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize OpenTelemetry exporter");
        }

        Logger.LogTrace("InitializeOpenTelemetryExporter() finished");
    }

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        Logger.LogTrace("BeforeRequestAsync() called");

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (!TryGetOpenAIRequest(request.BodyString, out var openAiRequest) || openAiRequest is null)
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        // store for use in response
        e.SessionData["OpenAIRequest"] = openAiRequest;

        var activity = _activitySource.StartActivity(
            $"openai.{GetOperationName(openAiRequest)}",
            ActivityKind.Client);

        if (activity is null)
        {
            Logger.LogWarning("Failed to start OpenTelemetry activity for OpenAI request");
            return;
        }

        // add generic request tags
        _ = activity.SetTag("http.method", request.Method)
            .SetTag("http.url", request.RequestUri.ToString())
            .SetTag("http.scheme", request.RequestUri.Scheme)
            .SetTag("http.host", request.RequestUri.Host)
            .SetTag("http.target", request.RequestUri.PathAndQuery)
            .SetTag(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem)
            .SetTag(SemanticConvention.GEN_AI_ENVIRONMENT, Configuration.Environment)
            .SetTag(SemanticConvention.GEN_AI_APPLICATION_NAME, Configuration.Application);

        AddCommonRequestTags(activity, openAiRequest);
        AddRequestTypeSpecificTags(activity, openAiRequest);

        // store for use in response
        e.SessionData["OpenAIActivity"] = activity;

        Logger.LogTrace("OnRequestAsync() finished");

        await Task.CompletedTask;
    }

    public override async Task AfterResponseAsync(ProxyResponseArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.AfterResponseAsync(e);

        Logger.LogTrace("AfterResponseAsync() called");

        if (!e.SessionData.TryGetValue("OpenAIActivity", out var activityObj) ||
            activityObj is not Activity activity)
        {
            return;
        }

        try
        {
            var response = e.Session.HttpClient.Response;

            _ = activity.SetTag("http.status_code", response.StatusCode);

#pragma warning disable IDE0010
            switch (response.StatusCode)
#pragma warning restore IDE0010
            {
                case int code when code is >= 200 and < 300:
                    ProcessSuccessResponse(activity, e);
                    break;
                case int code when code >= 400:
                    ProcessErrorResponse(activity, e);
                    break;
            }
        }
        finally
        {
            activity.Stop();

            // Clean up session data
            _ = e.SessionData.Remove("OpenAIActivity");
            _ = e.SessionData.Remove("OpenAIRequest");

            Logger.LogRequest("OpenTelemetry information emitted", MessageType.Processed, new LoggingContext(e.Session));
        }

        await Task.CompletedTask;
    }

    private void ProcessErrorResponse(Activity activity, ProxyResponseArgs e)
    {
        Logger.LogTrace("ProcessErrorResponse() called");

        var response = e.Session.HttpClient.Response;

        _ = activity.SetTag("error", true)
            .SetTag("error.type", "http")
            .SetTag("error.message", $"HTTP {response.StatusCode}");

        if (response.HasBody)
        {
            try
            {
                var errorObj = JsonSerializer.Deserialize<JsonElement>(response.BodyString);
                if (errorObj.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("message", out var message))
                    {
                        _ = activity.SetTag("error.details", message.GetString());
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parsing errors in error responses
            }
        }

        Logger.LogTrace("ProcessErrorResponse() finished");
    }

    private void ProcessSuccessResponse(Activity activity, ProxyResponseArgs e)
    {
        Logger.LogTrace("ProcessSuccessResponse() called");

        var response = e.Session.HttpClient.Response;

        if (!response.HasBody || string.IsNullOrEmpty(response.BodyString))
        {
            Logger.LogDebug("Response body is empty or null");
            return;
        }

        if (!e.SessionData.TryGetValue("OpenAIRequest", out var requestObj) ||
            requestObj is not OpenAIRequest openAiRequest)
        {
            Logger.LogDebug("OpenAI request not found in session data");
            return;
        }

        AddResponseTypeSpecificTags(activity, openAiRequest, response.BodyString);

        Logger.LogTrace("ProcessSuccessResponse() finished");
    }

    private void AddResponseTypeSpecificTags(Activity activity, OpenAIRequest openAiRequest, string responseBody)
    {
        Logger.LogTrace("AddResponseTypeSpecificTags() called");

        try
        {
            switch (openAiRequest)
            {
                case OpenAIChatCompletionRequest:
                    AddChatCompletionResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAICompletionRequest:
                    AddCompletionResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIEmbeddingRequest:
                    AddEmbeddingResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIImageRequest:
                    AddImageResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIAudioRequest:
                    AddAudioResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIFineTuneRequest:
                    AddFineTuneResponseTags(activity, openAiRequest, responseBody);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported OpenAI request type: {openAiRequest.GetType().Name}");
            }
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to deserialize OpenAI response");
            _ = activity.SetTag("error", ex.Message);
        }
    }

    private void AddFineTuneResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddFineTuneResponseTags() called");

        var fineTuneResponse = JsonSerializer.Deserialize<OpenAIFineTuneResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (fineTuneResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, fineTuneResponse);

        _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_STATUS, fineTuneResponse.Status)
            .SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, fineTuneResponse.Id);

        if (!string.IsNullOrEmpty(fineTuneResponse.FineTunedModel))
        {
            _ = activity.SetTag("ai.response.fine_tuned_model", fineTuneResponse.FineTunedModel);
        }

        Logger.LogTrace("AddFineTuneResponseTags() finished");
    }

    private void AddAudioResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddAudioResponseTags() called");

        var audioResponse = JsonSerializer.Deserialize<OpenAIAudioTranscriptionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (audioResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, audioResponse);

        // Record the transcription text if configured
        if (Configuration.IncludeCompletion && !string.IsNullOrEmpty(audioResponse.Text))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, audioResponse.Text);
        }

        Logger.LogTrace("AddAudioResponseTags() finished");
    }

    private void AddImageResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddImageResponseTags() called");

        var imageResponse = JsonSerializer.Deserialize<OpenAIImageResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (imageResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, imageResponse);

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, imageResponse.Id);

        if (imageResponse.Data != null)
        {
            _ = activity.SetTag("ai.response.image.count", imageResponse.Data.Count());

            if (Configuration.IncludeCompletion &&
                imageResponse.Data.Any() &&
                !string.IsNullOrEmpty(imageResponse.Data.FirstOrDefault()?.RevisedPrompt))
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_REVISED_PROMPT,
                    imageResponse.Data.First().RevisedPrompt);
            }
        }

        Logger.LogTrace("AddImageResponseTags() finished");
    }

    private void AddEmbeddingResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddEmbeddingResponseTags() called");

        var embeddingResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (embeddingResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, embeddingResponse);

        // Embedding response doesn't have a "completion" but we can record some metadata
        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, embeddingResponse.Id);

        if (embeddingResponse.Data is not null)
        {
            _ = activity.SetTag("ai.embedding.count", embeddingResponse.Data.Count());

            // If there's only one embedding, record the dimensions
            if (embeddingResponse.Data.Count() == 1 &&
                embeddingResponse.Data.First().Embedding is not null)
            {
                _ = activity.SetTag("ai.embedding.dimensions", embeddingResponse.Data.FirstOrDefault()?.Embedding?.Count() ?? 0);
            }
        }

        Logger.LogTrace("AddEmbeddingResponseTags() finished");
    }

    private void AddChatCompletionResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddChatCompletionResponseTags() called");

        var chatResponse = JsonSerializer.Deserialize<OpenAIChatCompletionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (chatResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, chatResponse);

        if (chatResponse.Choices?.FirstOrDefault()?.Message is not null)
        {
            if (Configuration.IncludeCompletion)
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, chatResponse.Choices.First().Message.Content);
            }

            _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, chatResponse.Choices.First().FinishReason);
        }

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, chatResponse.Id);

        Logger.LogTrace("AddChatCompletionResponseTags() finished");
    }

    private void AddCompletionResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddCompletionResponseTags() called");

        var completionResponse = JsonSerializer.Deserialize<OpenAICompletionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (completionResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, completionResponse);

        if (completionResponse.Choices?.FirstOrDefault() is not null)
        {
            if (Configuration.IncludeCompletion)
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, completionResponse.Choices.First().Text);
            }

            _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, completionResponse.Choices.First().FinishReason);
        }

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, completionResponse.Id);

        Logger.LogTrace("AddCompletionResponseTags() finished");
    }

    private void AddRequestTypeSpecificTags(Activity activity, OpenAIRequest openAiRequest)
    {
        switch (openAiRequest)
        {
            case OpenAIChatCompletionRequest chatRequest:
                AddChatCompletionRequestTags(activity, chatRequest);
                break;
            case OpenAICompletionRequest completionRequest:
                AddCompletionRequestTags(activity, completionRequest);
                break;
            case OpenAIEmbeddingRequest embeddingRequest:
                AddEmbeddingRequestTags(activity, embeddingRequest);
                break;
            case OpenAIImageRequest imageRequest:
                AddImageRequestTags(activity, imageRequest);
                break;
            case OpenAIAudioRequest audioRequest:
                AddAudioRequestTags(activity, audioRequest);
                break;
            case OpenAIAudioSpeechRequest speechRequest:
                AddAudioSpeechRequestTags(activity, speechRequest);
                break;
            case OpenAIFineTuneRequest fineTuneRequest:
                AddFineTuneRequestTags(activity, fineTuneRequest);
                break;
            default:
                throw new InvalidOperationException($"Unsupported OpenAI request type: {openAiRequest.GetType().Name}");
        }
    }

    private void AddCompletionRequestTags(Activity activity, OpenAICompletionRequest completionRequest)
    {
        Logger.LogTrace("AddCompletionRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_CONTENT_COMPLETION)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_CONTENT_COMPLETION);

        if (Configuration.IncludePrompt)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, completionRequest.Prompt);
        }

        Logger.LogTrace("AddCompletionRequestTags() finished");
    }

    private void AddChatCompletionRequestTags(Activity activity, OpenAIChatCompletionRequest chatRequest)
    {
        Logger.LogTrace("AddChatCompletionRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT);

        if (Configuration.IncludePrompt)
        {
            // Format messages to a more readable form for the span
            var formattedMessages = chatRequest.Messages
                .Select(m => $"{m.Role}: {m.Content}")
                .ToArray();

            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, string.Join("\n", formattedMessages));
        }

        Logger.LogTrace("AddChatCompletionRequestTags() finished");
    }

    private void AddEmbeddingRequestTags(Activity activity, OpenAIEmbeddingRequest embeddingRequest)
    {
        Logger.LogTrace("AddEmbeddingRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING);

        if (Configuration.IncludePrompt && embeddingRequest.Input is not null)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, embeddingRequest.Input);
        }

        if (embeddingRequest.EncodingFormat is not null)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_ENCODING_FORMATS, embeddingRequest.EncodingFormat);
        }

        if (embeddingRequest.Dimensions.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_EMBEDDING_DIMENSION, embeddingRequest.Dimensions.Value);
        }

        Logger.LogTrace("AddEmbeddingRequestTags() finished");
    }

    private void AddImageRequestTags(Activity activity, OpenAIImageRequest imageRequest)
    {
        Logger.LogTrace("AddImageRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE);

        if (Configuration.IncludePrompt && !string.IsNullOrEmpty(imageRequest.Prompt))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, imageRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(imageRequest.Size))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_SIZE, imageRequest.Size);
        }

        if (!string.IsNullOrEmpty(imageRequest.Quality))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_QUALITY, imageRequest.Quality);
        }

        if (!string.IsNullOrEmpty(imageRequest.Style))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_STYLE, imageRequest.Style);
        }

        if (imageRequest.N.HasValue)
        {
            _ = activity.SetTag("ai.request.image.count", imageRequest.N.Value);
        }

        Logger.LogTrace("AddImageRequestTags() finished");
    }

    private void AddAudioRequestTags(Activity activity, OpenAIAudioRequest audioRequest)
    {
        Logger.LogTrace("AddAudioRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (!string.IsNullOrEmpty(audioRequest.ResponseFormat))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, audioRequest.ResponseFormat);
        }

        if (!string.IsNullOrEmpty(audioRequest.Prompt) && Configuration.IncludePrompt)
        {
            _ = activity.SetTag("ai.request.audio.prompt", audioRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(audioRequest.Language))
        {
            _ = activity.SetTag("ai.request.audio.language", audioRequest.Language);
        }

        Logger.LogTrace("AddAudioRequestTags() finished");
    }

    private void AddAudioSpeechRequestTags(Activity activity, OpenAIAudioSpeechRequest speechRequest)
    {
        Logger.LogTrace("AddAudioSpeechRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (Configuration.IncludePrompt && !string.IsNullOrEmpty(speechRequest.Input))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, speechRequest.Input);
        }

        if (!string.IsNullOrEmpty(speechRequest.Voice))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_VOICE, speechRequest.Voice);
        }

        if (!string.IsNullOrEmpty(speechRequest.ResponseFormat))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, speechRequest.ResponseFormat);
        }

        if (speechRequest.Speed.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_SPEED, speechRequest.Speed.Value);
        }

        Logger.LogTrace("AddAudioSpeechRequestTags() finished");
    }

    private void AddFineTuneRequestTags(Activity activity, OpenAIFineTuneRequest fineTuneRequest)
    {
        Logger.LogTrace("AddFineTuneRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING);

        if (!string.IsNullOrEmpty(fineTuneRequest.TrainingFile))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TRAINING_FILE, fineTuneRequest.TrainingFile);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.ValidationFile))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_VALIDATION_FILE, fineTuneRequest.ValidationFile);
        }

        if (fineTuneRequest.BatchSize.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_BATCH_SIZE, fineTuneRequest.BatchSize.Value);
        }

        if (fineTuneRequest.LearningRateMultiplier.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_LRM,
                fineTuneRequest.LearningRateMultiplier.Value);
        }

        if (fineTuneRequest.Epochs.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_EPOCHS,
                fineTuneRequest.Epochs.Value);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.Suffix))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_SUFFIX,
                fineTuneRequest.Suffix);
        }

        Logger.LogTrace("AddFineTuneRequestTags() finished");
    }

    private void AddCommonRequestTags(Activity activity, OpenAIRequest openAiRequest)
    {
        Logger.LogTrace("AddCommonRequestTags() called");

        if (openAiRequest.Temperature.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TEMPERATURE,
                openAiRequest.Temperature.Value);
        }

        if (openAiRequest.MaxTokens.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_MAX_TOKENS,
                openAiRequest.MaxTokens.Value);
        }

        if (openAiRequest.TopP.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TOP_P,
                openAiRequest.TopP.Value);
        }

        if (openAiRequest.PresencePenalty.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_PRESENCE_PENALTY,
                openAiRequest.PresencePenalty.Value);
        }

        if (openAiRequest.FrequencyPenalty.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FREQUENCY_PENALTY,
                openAiRequest.FrequencyPenalty.Value);
        }

        if (openAiRequest.Stream.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IS_STREAM,
                openAiRequest.Stream.Value);
        }

        Logger.LogTrace("AddCommonRequestTags() finished");
    }

    private void RecordUsageMetrics(Activity activity, OpenAIRequest request, OpenAIResponse response)
    {
        Logger.LogTrace("RecordUsageMetrics() called");

        var usage = response.Usage;
        if (usage is null)
        {
            return;
        }

        Debug.Assert(_tokenUsageMetric is not null, "Token usage histogram is not initialized");
        _tokenUsageMetric.Record(response.Usage?.PromptTokens ?? 0,
        [
            new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
            new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
            new(SemanticConvention.GEN_AI_TOKEN_TYPE, SemanticConvention.GEN_AI_TOKEN_TYPE_INPUT),
            new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
            new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
        ]);
        _tokenUsageMetric.Record(response.Usage?.CompletionTokens ?? 0,
        [
            new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
            new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
            new(SemanticConvention.GEN_AI_TOKEN_TYPE, SemanticConvention.GEN_AI_TOKEN_TYPE_OUTPUT),
            new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
            new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
        ]);

        _ = activity.SetTag(SemanticConvention.GEN_AI_USAGE_INPUT_TOKENS, usage.PromptTokens)
            .SetTag(SemanticConvention.GEN_AI_USAGE_OUTPUT_TOKENS, usage.CompletionTokens)
            .SetTag(SemanticConvention.GEN_AI_USAGE_TOTAL_TOKENS, usage.TotalTokens);

        if (!Configuration.IncludeCosts || Configuration.Prices is null)
        {
            Logger.LogDebug("Cost tracking is disabled or prices data is not available");
            return;
        }

        if (string.IsNullOrEmpty(response.Model))
        {
            Logger.LogDebug("Response model is empty or null");
            return;
        }

        var (inputCost, outputCost) = Configuration.Prices.CalculateCost(response.Model, usage.PromptTokens, usage.CompletionTokens);

        if (inputCost > 0)
        {
            var totalCost = inputCost + outputCost;
            _ = activity.SetTag(SemanticConvention.GEN_AI_USAGE_COST, totalCost);

            Debug.Assert(_requestCostMetric is not null, "Cost histogram is not initialized");
            Debug.Assert(_totalCostMetric is not null, "Total cost counter is not initialized");

            _requestCostMetric.Record(totalCost,
            [
                new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
                new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
                new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
                new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
            ]);
            _totalCostMetric.Add(totalCost,
            [
                new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
                new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
                new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
                new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
            ]);
        }
        else
        {
            Logger.LogDebug("Input cost is zero, skipping cost metrics recording");
        }

        Logger.LogTrace("RecordUsageMetrics() finished");
    }

    private static string GetOperationName(OpenAIRequest request)
    {
        if (request == null)
        {
            return "unknown";
        }

        return request switch
        {
            OpenAIChatCompletionRequest => "chat.completions",
            OpenAICompletionRequest => "completions",
            OpenAIEmbeddingRequest => "embeddings",
            OpenAIImageRequest => "images.generations",
            OpenAIAudioRequest => "audio.transcriptions",
            OpenAIAudioSpeechRequest => "audio.speech",
            OpenAIFineTuneRequest => "fine_tuning.jobs",
            _ => "unknown"
        };
    }

    private bool TryGetOpenAIRequest(string content, out OpenAIRequest? request)
    {
        Logger.LogTrace("TryGetOpenAIRequest() called");

        request = null;

        if (string.IsNullOrEmpty(content))
        {
            Logger.LogDebug("Request content is empty or null");
            return false;
        }

        try
        {
            Logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Check for completion request (has "prompt", but not specific to image)
            if (rawRequest.TryGetProperty("prompt", out _) &&
                !rawRequest.TryGetProperty("size", out _) &&
                !rawRequest.TryGetProperty("n", out _))
            {
                Logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Chat completion request
            if (rawRequest.TryGetProperty("messages", out _))
            {
                Logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Embedding request
            if (rawRequest.TryGetProperty("input", out _) &&
                rawRequest.TryGetProperty("model", out _) &&
                !rawRequest.TryGetProperty("voice", out _))
            {
                Logger.LogDebug("Request is an embedding request");
                request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Image generation request
            if (rawRequest.TryGetProperty("prompt", out _) &&
                (rawRequest.TryGetProperty("size", out _) || rawRequest.TryGetProperty("n", out _)))
            {
                Logger.LogDebug("Request is an image generation request");
                request = JsonSerializer.Deserialize<OpenAIImageRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio transcription request
            if (rawRequest.TryGetProperty("file", out _))
            {
                Logger.LogDebug("Request is an audio transcription request");
                request = JsonSerializer.Deserialize<OpenAIAudioRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio speech synthesis request
            if (rawRequest.TryGetProperty("input", out _) && rawRequest.TryGetProperty("voice", out _))
            {
                Logger.LogDebug("Request is an audio speech synthesis request");
                request = JsonSerializer.Deserialize<OpenAIAudioSpeechRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Fine-tuning request
            if (rawRequest.TryGetProperty("training_file", out _))
            {
                Logger.LogDebug("Request is a fine-tuning request");
                request = JsonSerializer.Deserialize<OpenAIFineTuneRequest>(content, ProxyUtils.JsonSerializerOptions);
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

    public void Dispose()
    {
        _loader?.Dispose();
        _activitySource?.Dispose();
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
    }
}
