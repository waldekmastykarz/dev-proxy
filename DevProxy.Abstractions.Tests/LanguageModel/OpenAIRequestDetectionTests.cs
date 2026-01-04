// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace DevProxy.Abstractions.Tests.LanguageModel;

/// <summary>
/// Tests for OpenAIRequest.TryGetOpenAIRequest method to verify correct detection
/// of different OpenAI API request types including Chat Completions and Responses API.
/// </summary>
public class OpenAIRequestDetectionTests
{
    private readonly ILogger _logger;

    public OpenAIRequestDetectionTests()
    {
        _logger = Mock.Of<ILogger>();
    }

    #region Chat Completions API Tests

    [Fact]
    public void TryGetOpenAIRequest_ChatCompletionRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-4",
            "messages": [
                {"role": "system", "content": "You are a helpful assistant."},
                {"role": "user", "content": "Hello!"}
            ]
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIChatCompletionRequest>();
        var chatRequest = (OpenAIChatCompletionRequest)request;
        chatRequest.Model.ShouldBe("gpt-4");
        chatRequest.Messages.Count().ShouldBe(2);
    }

    [Fact]
    public void TryGetOpenAIRequest_ChatCompletionRequestWithOptions_ParsesAllProperties()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-4",
            "messages": [{"role": "user", "content": "Hello!"}],
            "temperature": 0.7,
            "max_tokens": 150,
            "top_p": 0.9,
            "stream": true
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        var chatRequest = (OpenAIChatCompletionRequest)request;
        chatRequest.Temperature.ShouldBe(0.7);
        chatRequest.MaxTokens.ShouldBe(150);
        chatRequest.TopP.ShouldBe(0.9);
        chatRequest.Stream.ShouldBe(true);
    }

    #endregion

    #region Completion API Tests

    [Fact]
    public void TryGetOpenAIRequest_CompletionRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-3.5-turbo-instruct",
            "prompt": "Say hello"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAICompletionRequest>();
        var completionRequest = (OpenAICompletionRequest)request;
        completionRequest.Prompt.ShouldBe("Say hello");
    }

    #endregion

    #region Responses API Tests

    [Fact]
    public void TryGetOpenAIRequest_ResponsesApiWithStringInputAndInstructions_ReturnsTrue()
    {
        // Arrange
        // Note: A Responses API request with only model + string input is indistinguishable
        // from an embedding request. Including instructions makes it unambiguous.
        var json = """
        {
            "model": "gpt-5",
            "input": "Write a one-sentence bedtime story about a unicorn.",
            "instructions": "You are a helpful assistant."
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIResponsesRequest>();
        var responsesRequest = (OpenAIResponsesRequest)request;
        responsesRequest.Model.ShouldBe("gpt-5");
        responsesRequest.Input.ShouldBe("Write a one-sentence bedtime story about a unicorn.");
        responsesRequest.Instructions.ShouldBe("You are a helpful assistant.");
    }

    [Fact]
    public void TryGetOpenAIRequest_ResponsesApiWithInstructions_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": "What is the capital of France?",
            "instructions": "You are a helpful geography assistant."
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIResponsesRequest>();
        var responsesRequest = (OpenAIResponsesRequest)request;
        responsesRequest.Instructions.ShouldBe("You are a helpful geography assistant.");
    }

    [Fact]
    public void TryGetOpenAIRequest_ResponsesApiWithMessageItems_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": [
                {"role": "user", "content": "What is the capital of France?"}
            ]
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIResponsesRequest>();
    }

    [Fact]
    public void TryGetOpenAIRequest_ResponsesApiWithPreviousResponseId_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": "And its population?",
            "previous_response_id": "resp_12345"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIResponsesRequest>();
        var responsesRequest = (OpenAIResponsesRequest)request;
        responsesRequest.PreviousResponseId.ShouldBe("resp_12345");
    }

    [Fact]
    public void TryGetOpenAIRequest_ResponsesApiWithStore_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": "Hello!",
            "store": false
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIResponsesRequest>();
        var responsesRequest = (OpenAIResponsesRequest)request;
        responsesRequest.Store.ShouldBe(false);
    }

    #endregion

    #region Other API Types Tests

    [Fact]
    public void TryGetOpenAIRequest_EmbeddingRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "text-embedding-ada-002",
            "input": "The quick brown fox"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIEmbeddingRequest>();
    }

    [Fact]
    public void TryGetOpenAIRequest_ImageGenerationRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "dall-e-3",
            "prompt": "A white siamese cat",
            "n": 1,
            "size": "1024x1024"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIImageRequest>();
    }

    [Fact]
    public void TryGetOpenAIRequest_AudioSpeechRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "tts-1",
            "input": "Hello, how are you?",
            "voice": "alloy"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIAudioSpeechRequest>();
    }

    [Fact]
    public void TryGetOpenAIRequest_FineTuneRequest_ReturnsTrue()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-3.5-turbo",
            "training_file": "file-abc123"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeTrue();
        request.ShouldNotBeNull();
        request.ShouldBeOfType<OpenAIFineTuneRequest>();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryGetOpenAIRequest_EmptyString_ReturnsFalse()
    {
        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest("", _logger, out var request);

        // Assert
        result.ShouldBeFalse();
        request.ShouldBeNull();
    }

    [Fact]
    public void TryGetOpenAIRequest_NullString_ReturnsFalse()
    {
        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(null!, _logger, out var request);

        // Assert
        result.ShouldBeFalse();
        request.ShouldBeNull();
    }

    [Fact]
    public void TryGetOpenAIRequest_InvalidJson_ReturnsFalse()
    {
        // Arrange
        var json = "{ invalid json }";

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeFalse();
        request.ShouldBeNull();
    }

    [Fact]
    public void TryGetOpenAIRequest_UnrecognizedRequest_ReturnsFalse()
    {
        // Arrange
        var json = """
        {
            "unknown_field": "value"
        }
        """;

        // Act
        var result = OpenAIRequest.TryGetOpenAIRequest(json, _logger, out var request);

        // Assert
        result.ShouldBeFalse();
        request.ShouldBeNull();
    }

    #endregion
}
