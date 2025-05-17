// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Behavior;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

public class GraphMockResponsePlugin(
    ILogger<GraphMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    MockResponsePlugin(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public override string Name => nameof(GraphMockResponsePlugin);

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Configuration.NoMocks)
        {
            Logger.LogRequest("Mocks are disabled", MessageType.Skipped, new LoggingContext(e.Session));
            // mocking has been disabled. Nothing to do
            return;
        }

        if (!ProxyUtils.IsGraphBatchUrl(e.Session.HttpClient.Request.RequestUri))
        {
            // not a batch request, use the basic mock functionality
            await base.BeforeRequestAsync(e);
            return;
        }

        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(e.Session.HttpClient.Request.BodyString, ProxyUtils.JsonSerializerOptions);
        if (batch == null)
        {
            await base.BeforeRequestAsync(e);
            return;
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            GraphBatchResponsePayloadResponse? response = null;
            var requestId = Guid.NewGuid().ToString();
            var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
            var headers = ProxyUtils
                .BuildGraphResponseHeaders(e.Session.HttpClient.Request, requestId, requestDate);

            if (e.SessionData.TryGetValue(nameof(RateLimitingPlugin), out var pluginData) &&
                pluginData is List<MockResponseHeader> rateLimitingHeaders)
            {
                ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
            }

            var mockResponse = GetMatchingMockResponse(request, e.Session.HttpClient.Request.RequestUri);
            if (mockResponse == null)
            {
                response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)HttpStatusCode.BadGateway,
                    Headers = headers.ToDictionary(h => h.Name, h => h.Value),
                    Body = new GraphBatchResponsePayloadResponseBody
                    {
                        Error = new GraphBatchResponsePayloadResponseBodyError
                        {
                            Code = "BadGateway",
                            Message = "No mock response found for this request"
                        }
                    }
                };

                Logger.LogRequest($"502 {request.Url}", MessageType.Mocked, new LoggingContext(e.Session));
            }
            else
            {
                dynamic? body = null;
                var statusCode = HttpStatusCode.OK;
                if (mockResponse.Response?.StatusCode is not null)
                {
                    statusCode = (HttpStatusCode)mockResponse.Response.StatusCode;
                }

                if (mockResponse.Response?.Headers is not null)
                {
                    ProxyUtils.MergeHeaders(headers, [.. mockResponse.Response.Headers]);
                }

                // default the content type to application/json unless set in the mock response
                if (!headers.Any(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                {
                    headers.Add(new("content-type", "application/json"));
                }

                if (mockResponse.Response?.Body is not null)
                {
                    var bodyString = JsonSerializer.Serialize(mockResponse.Response.Body, ProxyUtils.JsonSerializerOptions) as string;
                    // we get a JSON string so need to start with the opening quote
                    if (bodyString?.StartsWith("\"@", StringComparison.OrdinalIgnoreCase) ?? false)
                    {
                        // we've got a mock body starting with @-token which means we're sending
                        // a response from a file on disk
                        // if we can read the file, we can immediately send the response and
                        // skip the rest of the logic in this method
                        // remove the surrounding quotes and the @-token
                        var filePath = Path.Combine(Path.GetDirectoryName(Configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(bodyString.Trim('"')[1..]));
                        if (!File.Exists(filePath))
                        {
                            Logger.LogError("File {FilePath} not found. Serving file path in the mock response", filePath);
                            body = bodyString;
                        }
                        else
                        {
                            var bodyBytes = await File.ReadAllBytesAsync(filePath);
                            body = Convert.ToBase64String(bodyBytes);
                        }
                    }
                    else
                    {
                        body = mockResponse.Response.Body;
                    }
                }
                response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)statusCode,
                    Headers = headers.ToDictionary(h => h.Name, h => h.Value),
                    Body = body
                };

                Logger.LogRequest($"{mockResponse.Response?.StatusCode ?? 200} {mockResponse.Request?.Url}", MessageType.Mocked, new LoggingContext(e.Session));
            }

            responses.Add(response);
        }

        var batchRequestId = Guid.NewGuid().ToString();
        var batchRequestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var batchHeaders = ProxyUtils.BuildGraphResponseHeaders(e.Session.HttpClient.Request, batchRequestId, batchRequestDate);
        var batchResponse = new GraphBatchResponsePayload
        {
            Responses = [.. responses]
        };
        var batchResponseString = JsonSerializer.Serialize(batchResponse, ProxyUtils.JsonSerializerOptions);
        ProcessMockResponse(ref batchResponseString, batchHeaders, e, null);
        e.Session.GenericResponse(batchResponseString ?? string.Empty, HttpStatusCode.OK, batchHeaders.Select(h => new HttpHeader(h.Name, h.Value)));
        Logger.LogRequest($"200 {e.Session.HttpClient.Request.RequestUri}", MessageType.Mocked, new LoggingContext(e.Session));
        e.ResponseState.HasBeenSet = true;
    }

    protected MockResponse? GetMatchingMockResponse(GraphBatchRequestPayloadRequest request, Uri batchRequestUri)
    {
        if (Configuration.NoMocks ||
            Configuration.Mocks is null ||
            !Configuration.Mocks.Any())
        {
            return null;
        }

        var mockResponse = Configuration.Mocks.FirstOrDefault(mockResponse =>
        {
            if (mockResponse.Request?.Method != request.Method)
            {
                return false;
            }
            // URLs in batch are relative to Graph version number so we need
            // to make them absolute using the batch request URL
            var absoluteRequestFromBatchUrl = ProxyUtils
                .GetAbsoluteRequestUrlFromBatch(batchRequestUri, request.Url)
                .ToString();
            if (mockResponse.Request.Url == absoluteRequestFromBatchUrl)
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!mockResponse.Request.Url.Contains('*', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            //turn mock URL with wildcard into a regex and match against the request URL
            var mockResponseUrlRegex = Regex.Escape(mockResponse.Request.Url).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(absoluteRequestFromBatchUrl, $"^{mockResponseUrlRegex}$");
        });
        return mockResponse;
    }
}