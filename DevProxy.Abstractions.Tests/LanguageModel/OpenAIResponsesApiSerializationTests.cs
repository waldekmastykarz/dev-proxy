// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Utils;
using Shouldly;
using System.Text.Json;

namespace DevProxy.Abstractions.Tests.LanguageModel;

/// <summary>
/// Tests for OpenAI Responses API request and response model serialization/deserialization.
/// </summary>
public class OpenAIResponsesApiSerializationTests
{
    #region Request Serialization Tests

    [Fact]
    public void ResponsesRequest_WithStringInput_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIResponsesRequest
        {
            Model = "gpt-5",
            Input = "Hello, world!"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIResponsesRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Model.ShouldBe("gpt-5");
        deserialized.Input.ShouldBe("Hello, world!");
    }

    [Fact]
    public void ResponsesRequest_WithInstructions_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIResponsesRequest
        {
            Model = "gpt-5",
            Input = "What is 2+2?",
            Instructions = "You are a math tutor."
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<OpenAIResponsesRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        deserialized.ShouldNotBeNull();
        deserialized.Instructions.ShouldBe("You are a math tutor.");
    }

    [Fact]
    public void ResponsesRequest_WithPreviousResponseId_SerializesCorrectly()
    {
        // Arrange
        var request = new OpenAIResponsesRequest
        {
            Model = "gpt-5",
            Input = "Continue",
            PreviousResponseId = "resp_abc123"
        };

        // Act
        var json = JsonSerializer.Serialize(request, ProxyUtils.JsonSerializerOptions);

        // Assert
        json.ShouldContain("\"previous_response_id\"");
        json.ShouldContain("resp_abc123");
    }

    [Fact]
    public void ResponsesRequest_DeserializesFromStringInput()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": "Hello!"
        }
        """;

        // Act
        var request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        request.ShouldNotBeNull();
        request.Input.ShouldBe("Hello!");
    }

    [Fact]
    public void ResponsesRequest_DeserializesFromArrayInput()
    {
        // Arrange
        var json = """
        {
            "model": "gpt-5",
            "input": [
                {"type": "message", "role": "user", "content": "Hello!"},
                {"type": "message", "role": "assistant", "content": "Hi there!"}
            ]
        }
        """;

        // Act
        var request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        request.ShouldNotBeNull();
        request.Input.ShouldNotBeNull();
        request.Input.ShouldBeAssignableTo<IEnumerable<object>>();
        var items = ((IEnumerable<object>)request.Input).ToList();
        items.Count.ShouldBe(2);
    }

    #endregion

    #region Response Serialization Tests

    [Fact]
    public void ResponsesResponse_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "resp_12345",
            "object": "response",
            "created_at": 1756315696,
            "model": "gpt-5-2025-08-07",
            "status": "completed",
            "output": [
                {
                    "id": "msg_12345",
                    "type": "message",
                    "status": "completed",
                    "role": "assistant",
                    "content": [
                        {
                            "type": "output_text",
                            "text": "Hello! How can I help you today?"
                        }
                    ]
                }
            ],
            "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 8,
                "total_tokens": 18
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIResponsesResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Id.ShouldBe("resp_12345");
        response.Object.ShouldBe("response");
        response.Model.ShouldBe("gpt-5-2025-08-07");
        response.Status.ShouldBe("completed");
        response.CreatedAt.ShouldBe(1756315696);
        response.Output.ShouldNotBeNull();
        response.Output.Count().ShouldBe(1);
    }

    [Fact]
    public void ResponsesResponse_GetOutputText_ReturnsCorrectText()
    {
        // Arrange
        var response = new OpenAIResponsesResponse
        {
            Id = "resp_123",
            Model = "gpt-5",
            Output =
            [
                new OpenAIResponsesOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content =
                    [
                        new OpenAIResponsesOutputContent
                        {
                            Type = "output_text",
                            Text = "Hello, world!"
                        }
                    ]
                }
            ]
        };

        // Act
        var text = response.Response;

        // Assert
        text.ShouldBe("Hello, world!");
    }

    [Fact]
    public void ResponsesResponse_WithOutputText_ReturnsOutputTextDirectly()
    {
        // Arrange
        var response = new OpenAIResponsesResponse
        {
            Id = "resp_123",
            Model = "gpt-5",
            OutputText = "Direct output text"
        };

        // Act
        var text = response.Response;

        // Assert
        text.ShouldBe("Direct output text");
    }

    [Fact]
    public void ResponsesResponse_UsageDeserialization_WorksCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "resp_12345",
            "model": "gpt-5",
            "output": [],
            "usage": {
                "prompt_tokens": 100,
                "completion_tokens": 50,
                "total_tokens": 150
            }
        }
        """;

        // Act
        var response = JsonSerializer.Deserialize<OpenAIResponsesResponse>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        response.ShouldNotBeNull();
        response.Usage.ShouldNotBeNull();
        response.Usage.PromptTokens.ShouldBe(100);
        response.Usage.CompletionTokens.ShouldBe(50);
        response.Usage.TotalTokens.ShouldBe(150);
    }

    #endregion

    #region Output Item Tests

    [Fact]
    public void ResponsesOutputItem_Message_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "msg_123",
            "type": "message",
            "status": "completed",
            "role": "assistant",
            "content": [
                {"type": "output_text", "text": "Hello!"}
            ]
        }
        """;

        // Act
        var item = JsonSerializer.Deserialize<OpenAIResponsesOutputItem>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        item.ShouldNotBeNull();
        item.Id.ShouldBe("msg_123");
        item.Type.ShouldBe("message");
        item.Status.ShouldBe("completed");
        item.Role.ShouldBe("assistant");
        item.Content.ShouldNotBeNull();
        item.Content.Count().ShouldBe(1);
        item.Content.First().Type.ShouldBe("output_text");
        item.Content.First().Text.ShouldBe("Hello!");
    }

    [Fact]
    public void ResponsesOutputItem_Reasoning_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
            "id": "rs_123",
            "type": "reasoning",
            "content": [],
            "summary": []
        }
        """;

        // Act
        var item = JsonSerializer.Deserialize<OpenAIResponsesOutputItem>(json, ProxyUtils.JsonSerializerOptions);

        // Assert
        item.ShouldNotBeNull();
        item.Type.ShouldBe("reasoning");
    }

    #endregion
}
