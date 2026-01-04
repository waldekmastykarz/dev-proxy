// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Utils;
using Shouldly;
using System.Text.Json;

namespace DevProxy.Abstractions.Tests.LanguageModel;

/// <summary>
/// Tests for existing Chat Completions API functionality to ensure backward compatibility.
/// </summary>
public class OpenAIChatCompletionsSerializationTests
{
    #region Request Tests

    [Fact]
    public void ChatCompletionRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIChatCompletionRequest
        {
            Model = "gpt-4",
            Messages =
            [
                new OpenAIChatCompletionMessage { Role = "system", Content = "You are helpful." },
                new OpenAIChatCompletionMessage { Role = "user", Content = "Hello!" }
            ],
            Temperature = 0.7,
            MaxTokens = 100
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("gpt-4");
        deserialized.Messages.Count().ShouldBe(2);
        deserialized.Temperature.ShouldBe(0.7);
        deserialized.MaxTokens.ShouldBe(100);
    }

    [Fact]
    public void ChatCompletionRequest_DeserializesFromJson()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-4-turbo",
            "messages": [
                {"role": "user", "content": "What is the weather like?"}
            ],
            "temperature": 0.5,
            "stream": true
        }
        """;

        // Act
        var request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        request.ShouldNotBeNull();
        request.Model.ShouldBe("gpt-4-turbo");
        request.Messages.Count().ShouldBe(1);
        request.Temperature.ShouldBe(0.5);
        request.Stream.ShouldBe(true);
    }

    [Fact]
    public void ChatCompletionMessage_WithStringContent_WorksCorrectly()
    {
        // Arrange
        var message = new OpenAIChatCompletionMessage
        {
            Role = "user",
            Content = "Simple text message"
        };

        // Act
        var json = JsonSerializer.Serialize(message, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIChatCompletionMessage>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Role.ShouldBe("user");
        deserialized.Content.ShouldBe("Simple text message");
    }

    #endregion

    #region Response Tests

    [Fact]
    public void ChatCompletionResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "chatcmpl-123",
            "object": "chat.completion",
            "created": 1677652288,
            "model": "gpt-4",
            "choices": [
                {
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": "Hello! How can I help you today?"
                    },
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 12,
                "total_tokens": 22
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIChatCompletionResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Id.ShouldBe("chatcmpl-123");
        response.Object.ShouldBe("chat.completion");
        response.Model.ShouldBe("gpt-4");
        response.Choices.ShouldNotBeNull();
        response.Choices.Count().ShouldBe(1);
        response.Choices.First().Message.Content.ShouldBe("Hello! How can I help you today?");
        response.Choices.First().FinishReason.ShouldBe("stop");
    }

    [Fact]
    public void ChatCompletionResponse_Response_ReturnsLastChoiceContent()
    {
        // Arrange
        var response = new OpenAIChatCompletionResponse
        {
            Id = "chatcmpl-123",
            Model = "gpt-4",
            Choices =
            [
                new OpenAIChatCompletionResponseChoice
                {
                    Message = new OpenAIChatCompletionResponseChoiceMessage
                    {
                        Role = "assistant",
                        Content = "First response"
                    }
                },
                new OpenAIChatCompletionResponseChoice
                {
                    Message = new OpenAIChatCompletionResponseChoiceMessage
                    {
                        Role = "assistant",
                        Content = "Second response"
                    }
                }
            ]
        };

        // Act
        var text = response.Response;

        // Assert
        text.ShouldBe("Second response");
    }

    [Fact]
    public void ChatCompletionResponse_UsageTokens_DeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "chatcmpl-123",
            "model": "gpt-4",
            "choices": [],
            "usage": {
                "prompt_tokens": 50,
                "completion_tokens": 100,
                "total_tokens": 150,
                "prompt_tokens_details": {
                    "cached_tokens": 20
                }
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIChatCompletionResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Usage.PromptTokens.ShouldBe(50);
        response.Usage.CompletionTokens.ShouldBe(100);
        response.Usage.TotalTokens.ShouldBe(150);
        response.Usage.PromptTokensDetails.ShouldNotBeNull();
        response.Usage.PromptTokensDetails.CachedTokens.ShouldBe(20);
    }

    #endregion

    #region Completion API Tests

    [Fact]
    public void CompletionRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAICompletionRequest
        {
            Model = "gpt-3.5-turbo-instruct",
            Prompt = "Once upon a time"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAICompletionRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("gpt-3.5-turbo-instruct");
        deserialized.Prompt.ShouldBe("Once upon a time");
    }

    [Fact]
    public void CompletionResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "cmpl-123",
            "object": "text_completion",
            "created": 1677652288,
            "model": "gpt-3.5-turbo-instruct",
            "choices": [
                {
                    "index": 0,
                    "text": ", there was a brave knight.",
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": 5,
                "completion_tokens": 7,
                "total_tokens": 12
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAICompletionResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Id.ShouldBe("cmpl-123");
        response.Choices.ShouldNotBeNull();
        response.Choices.First().Text.ShouldBe(", there was a brave knight.");
        response.Response.ShouldBe(", there was a brave knight.");
    }

    #endregion
}
