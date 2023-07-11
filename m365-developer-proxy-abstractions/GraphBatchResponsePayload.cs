// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft365.DeveloperProxy.Abstractions;

public class GraphBatchResponsePayload {
    [JsonPropertyName("responses")]
    public GraphBatchResponsePayloadResponse[] Responses { get; set; } = Array.Empty<GraphBatchResponsePayloadResponse>();
}

public class GraphBatchResponsePayloadResponse {
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("status")]
    public int Status { get; set; } = 200;
    [JsonPropertyName("body")]
    public GraphBatchResponsePayloadResponseBody? Body { get; set; }
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public class GraphBatchResponsePayloadResponseBody {
    [JsonPropertyName("error")]
    public GraphBatchResponsePayloadResponseBodyError? Error { get; set; }
}

public class GraphBatchResponsePayloadResponseBodyError {
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}