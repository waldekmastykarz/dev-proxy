// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Utils;
using Shouldly;
using System.Text.Json;

namespace DevProxy.Abstractions.Tests.LanguageModel;

/// <summary>
/// Tests for other OpenAI API types (embeddings, images, audio, fine-tuning).
/// </summary>
public class OpenAIOtherApiTypesTests
{
    #region Embedding API Tests

    [Fact]
    public void EmbeddingRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIEmbeddingRequest
        {
            Model = "text-embedding-ada-002",
            Input = "The quick brown fox jumps over the lazy dog",
            EncodingFormat = "float",
            Dimensions = 1536
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("text-embedding-ada-002");
        deserialized.Input.ShouldBe("The quick brown fox jumps over the lazy dog");
        deserialized.EncodingFormat.ShouldBe("float");
        deserialized.Dimensions.ShouldBe(1536);
    }

    [Fact]
    public void EmbeddingResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "emb-123",
            "object": "list",
            "model": "text-embedding-ada-002",
            "data": [
                {
                    "object": "embedding",
                    "index": 0,
                    "embedding": [0.1, 0.2, 0.3]
                }
            ],
            "usage": {
                "prompt_tokens": 10,
                "total_tokens": 10
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Data.ShouldNotBeNull();
        response.Data.Count().ShouldBe(1);
        response.Data.First().Embedding.ShouldNotBeNull();
        response.Data.First().Index.ShouldBe(0);
        response.Response.ShouldBeNull(); // Embeddings don't have text response
    }

    #endregion

    #region Image API Tests

    [Fact]
    public void ImageRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIImageRequest
        {
            Model = "dall-e-3",
            Prompt = "A sunset over the mountains",
            N = 1,
            Size = "1024x1024",
            Quality = "hd",
            Style = "vivid"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIImageRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("dall-e-3");
        deserialized.Prompt.ShouldBe("A sunset over the mountains");
        deserialized.N.ShouldBe(1);
        deserialized.Size.ShouldBe("1024x1024");
        deserialized.Quality.ShouldBe("hd");
        deserialized.Style.ShouldBe("vivid");
    }

    [Fact]
    public void ImageResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "img-123",
            "created": 1677652288,
            "data": [
                {
                    "url": "https://example.com/image.png",
                    "revised_prompt": "A beautiful sunset over snow-capped mountains"
                }
            ]
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIImageResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Data.ShouldNotBeNull();
        response.Data.Count().ShouldBe(1);
        response.Data.First().Url.ShouldBe("https://example.com/image.png");
        response.Data.First().RevisedPrompt.ShouldBe("A beautiful sunset over snow-capped mountains");
        response.Response.ShouldBeNull(); // Images don't have text response
    }

    #endregion

    #region Audio API Tests

    [Fact]
    public void AudioSpeechRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIAudioSpeechRequest
        {
            Model = "tts-1",
            Input = "Hello, this is a test.",
            Voice = "nova",
            ResponseFormat = "mp3",
            Speed = 1.0
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIAudioSpeechRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("tts-1");
        deserialized.Input.ShouldBe("Hello, this is a test.");
        deserialized.Voice.ShouldBe("nova");
        deserialized.ResponseFormat.ShouldBe("mp3");
        deserialized.Speed.ShouldBe(1.0);
    }

    [Fact]
    public void AudioTranscriptionRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIAudioRequest
        {
            Model = "whisper-1",
            File = "audio.mp3",
            ResponseFormat = "json",
            Language = "en"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIAudioRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("whisper-1");
        deserialized.File.ShouldBe("audio.mp3");
        deserialized.Language.ShouldBe("en");
    }

    [Fact]
    public void AudioTranscriptionResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "audio-123",
            "text": "Hello, how are you doing today?"
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIAudioTranscriptionResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Text.ShouldBe("Hello, how are you doing today?");
        response.Response.ShouldBe("Hello, how are you doing today?");
    }

    #endregion

    #region Fine-tuning API Tests

    [Fact]
    public void FineTuneRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIFineTuneRequest
        {
            Model = "gpt-3.5-turbo",
            TrainingFile = "file-abc123",
            ValidationFile = "file-def456",
            Epochs = 3,
            BatchSize = 4,
            LearningRateMultiplier = 0.1,
            Suffix = "custom-model"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);

        // Assert
        json.ShouldContain("\"training_file\"");
        json.ShouldContain("\"validation_file\"");
        json.ShouldContain("\"batch_size\"");
        json.ShouldContain("\"learning_rate_multiplier\"");
    }

    [Fact]
    public void FineTuneResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "ft-123",
            "model": "gpt-3.5-turbo",
            "fine_tuned_model": "ft:gpt-3.5-turbo:org:custom:abc123",
            "status": "succeeded",
            "training_file": "file-abc123",
            "created_at": 1677652288,
            "updated_at": 1677652388
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIFineTuneResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Id.ShouldBe("ft-123");
        response.FineTunedModel.ShouldBe("ft:gpt-3.5-turbo:org:custom:abc123");
        response.Status.ShouldBe("succeeded");
        response.TrainingFile.ShouldBe("file-abc123");
        response.Response.ShouldBe("ft:gpt-3.5-turbo:org:custom:abc123");
    }

    #endregion
}
