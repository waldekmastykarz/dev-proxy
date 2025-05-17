﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using DevProxy.Plugins.Models;

namespace DevProxy.Plugins.Behavior;

enum GraphRandomErrorFailMode
{
    Random,
    PassThru
}

public sealed class GraphRandomErrorConfiguration
{
    public IEnumerable<int> AllowedErrors { get; set; } = [];
    public int Rate { get; set; } = 50;
    public int RetryAfterInSeconds { get; set; } = 5;
}

public sealed class GraphRandomErrorPlugin(
    ILogger<GraphRandomErrorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<GraphRandomErrorConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _allowedErrorsOptionName = "--allowed-errors";
    private const string _rateOptionName = "--failure-rate";

    private readonly GraphRandomErrorConfiguration _configuration = new();

    public override string Name => nameof(GraphRandomErrorPlugin);

    private readonly Dictionary<string, HttpStatusCode[]> _methodStatusCode = new()
    {
        {
            "GET", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "POST", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PUT", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PATCH", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "DELETE", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        }
    };
    private readonly Random _random = new();

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var context = e.Context;

        // Configure the allowed errors
        var allowedErrors = context.ParseResult.GetValueForOption<IEnumerable<int>?>(_allowedErrorsOptionName, e.Options);
        if (allowedErrors?.Any() ?? false)
        {
            _configuration.AllowedErrors = [.. allowedErrors];
        }

        if (_configuration.AllowedErrors.Any())
        {
            foreach (var k in _methodStatusCode.Keys)
            {
                _methodStatusCode[k] = [.. _methodStatusCode[k].Where(e => _configuration.AllowedErrors.Any(a => (int)e == a))];
            }
        }

        var rate = context.ParseResult.GetValueForOption<int?>(_rateOptionName, e.Options);
        if (rate is not null)
        {
            _configuration.Rate = rate.Value;
        }
    }

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        var state = e.ResponseState;
        if (state.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (!e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var failMode = ShouldFail();
        if (failMode == GraphRandomErrorFailMode.PassThru && _configuration.Rate != 100)
        {
            Logger.LogRequest("Pass through", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (ProxyUtils.IsGraphBatchUrl(e.Session.HttpClient.Request.RequestUri))
        {
            FailBatch(e);
        }
        else
        {
            FailResponse(e);
        }
        state.HasBeenSet = true;
    }

    // uses config to determine if a request should be failed
    private GraphRandomErrorFailMode ShouldFail() => _random.Next(1, 100) <= _configuration.Rate ? GraphRandomErrorFailMode.Random : GraphRandomErrorFailMode.PassThru;

    private void FailResponse(ProxyRequestArgs e)
    {
        // pick a random error response for the current request method
        var methodStatusCodes = _methodStatusCode[e.Session.HttpClient.Request.Method ?? "GET"];
        var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];
        UpdateProxyResponse(e, errorStatus);
    }

    private void FailBatch(ProxyRequestArgs e)
    {
        var batchResponse = new GraphBatchResponsePayload();

        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(e.Session.HttpClient.Request.BodyString, ProxyUtils.JsonSerializerOptions);
        if (batch == null)
        {
            UpdateProxyBatchResponse(e, batchResponse);
            return;
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            try
            {
                // pick a random error response for the current request method
                var methodStatusCodes = _methodStatusCode[request.Method];
                var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];

                var response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)errorStatus,
                    Body = new GraphBatchResponsePayloadResponseBody
                    {
                        Error = new GraphBatchResponsePayloadResponseBodyError
                        {
                            Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                            Message = "Some error was generated by the proxy.",
                        }
                    }
                };

                if (errorStatus == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterDate = DateTime.Now.AddSeconds(_configuration.RetryAfterInSeconds);
                    var requestUrl = ProxyUtils.GetAbsoluteRequestUrlFromBatch(e.Session.HttpClient.Request.RequestUri, request.Url);
                    var throttledRequests = e.GlobalData[RetryAfterPlugin.ThrottledRequestsKey] as List<ThrottlerInfo>;
                    throttledRequests?.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(requestUrl), ShouldThrottle, retryAfterDate));
                    response.Headers = new Dictionary<string, string> { { "Retry-After", _configuration.RetryAfterInSeconds.ToString(CultureInfo.InvariantCulture) } };
                }

                responses.Add(response);
            }
            catch { }
        }
        batchResponse.Responses = [.. responses];

        UpdateProxyBatchResponse(e, batchResponse);
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = GraphUtils.BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? _configuration.RetryAfterInSeconds : 0, "Retry-After");
    }

    private void UpdateProxyResponse(ProxyRequestArgs e, HttpStatusCode errorStatus)
    {
        var session = e.Session;
        var requestId = Guid.NewGuid().ToString();
        var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);
        if (errorStatus == HttpStatusCode.TooManyRequests)
        {
            var retryAfterDate = DateTime.Now.AddSeconds(_configuration.RetryAfterInSeconds);
            if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
            {
                value = new List<ThrottlerInfo>();
                e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
            }

            var throttledRequests = value as List<ThrottlerInfo>;
            throttledRequests?.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(request), ShouldThrottle, retryAfterDate));
            headers.Add(new("Retry-After", _configuration.RetryAfterInSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        var body = JsonSerializer.Serialize(new GraphErrorResponseBody(
            new GraphErrorResponseError
            {
                Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                Message = BuildApiErrorMessage(request),
                InnerError = new GraphErrorResponseInnerError
                {
                    RequestId = requestId,
                    Date = requestDate
                }
            }),
            ProxyUtils.JsonSerializerOptions
        );
        Logger.LogRequest($"{(int)errorStatus} {errorStatus}", MessageType.Chaos, new LoggingContext(e.Session));
        session.GenericResponse(body ?? string.Empty, errorStatus, headers.Select(h => new HttpHeader(h.Name, h.Value)));
    }

    private void UpdateProxyBatchResponse(ProxyRequestArgs ev, GraphBatchResponsePayload response)
    {
        // failed batch uses a fixed 424 error status code
        var errorStatus = HttpStatusCode.FailedDependency;

        var session = ev.Session;
        var requestId = Guid.NewGuid().ToString();
        var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);

        var body = JsonSerializer.Serialize(response, ProxyUtils.JsonSerializerOptions);
        Logger.LogRequest($"{(int)errorStatus} {errorStatus}", MessageType.Chaos, new LoggingContext(ev.Session));
        session.GenericResponse(body, errorStatus, headers.Select(h => new HttpHeader(h.Name, h.Value)));
    }

    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage()) : "")}";

    public override Option[] GetOptions()
    {
        var _allowedErrors = new Option<IEnumerable<int>>(_allowedErrorsOptionName, "List of errors that Dev Proxy may produce")
        {
            ArgumentHelpName = "allowed errors",
            AllowMultipleArgumentsPerToken = true
        };
        _allowedErrors.AddAlias("-a");

        var _rateOption = new Option<int?>(_rateOptionName, "The percentage of chance that a request will fail");
        _rateOption.AddAlias("-f");
        _rateOption.ArgumentHelpName = "failure rate";
        _rateOption.AddValidator((input) =>
        {
            try
            {
                var value = input.GetValueForOption(_rateOption);
                if (value.HasValue && (value < 0 || value > 100))
                {
                    input.ErrorMessage = $"{value} is not a valid failure rate. Specify a number between 0 and 100";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

        return [_allowedErrors, _rateOption];
    }
}
