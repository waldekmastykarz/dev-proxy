// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Models;

public class GraphBatchRequestPayload
{
    public IEnumerable<GraphBatchRequestPayloadRequest> Requests { get; set; } = [];
}

public class GraphBatchRequestPayloadRequest
{
    public string Id { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
#pragma warning disable CA2227
    public Dictionary<string, string>? Headers { get; set; } = [];
#pragma warning restore CA2227
    public object? Body { get; set; }
}