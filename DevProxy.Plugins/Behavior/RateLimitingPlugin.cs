// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Behavior;

public enum RateLimitResponseWhenLimitExceeded
{
    Throttle,
    Custom
}

public enum RateLimitResetFormat
{
    SecondsLeft,
    UtcEpochSeconds
}

public sealed class RateLimitConfiguration
{
    public string HeaderLimit { get; set; } = "RateLimit-Limit";
    public string HeaderRemaining { get; set; } = "RateLimit-Remaining";
    public string HeaderReset { get; set; } = "RateLimit-Reset";
    public string HeaderRetryAfter { get; set; } = "Retry-After";
    public RateLimitResetFormat ResetFormat { get; set; } = RateLimitResetFormat.SecondsLeft;
    public int CostPerRequest { get; set; } = 2;
    public int ResetTimeWindowSeconds { get; set; } = 60;
    public int WarningThresholdPercent { get; set; } = 80;
    public int RateLimit { get; set; } = 120;
    public RateLimitResponseWhenLimitExceeded WhenLimitExceeded { get; set; } = RateLimitResponseWhenLimitExceeded.Throttle;
    public string CustomResponseFile { get; set; } = "rate-limit-response.json";
    public MockResponseResponse? CustomResponse { get; set; }
}

public sealed class RateLimitingPlugin(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<RateLimitConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public override string Name => nameof(RateLimitingPlugin);
    // initial values so that we know when we intercept the
    // first request and can set the initial values
    private int _resourcesRemaining = -1;
    private DateTime _resetTime = DateTime.MinValue;
    private RateLimitingCustomResponseLoader? _loader;

    public override async Task InitializeAsync(InitArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e);

        if (Configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Custom)
        {
            Configuration.CustomResponseFile = ProxyUtils.GetFullPath(Configuration.CustomResponseFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<RateLimitingCustomResponseLoader>(e.ServiceProvider, Configuration);
            _loader.InitFileWatcher();
        }
    }

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        var session = e.Session;
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

        // set the initial values for the first request
        if (_resetTime == DateTime.MinValue)
        {
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }
        if (_resourcesRemaining == -1)
        {
            _resourcesRemaining = Configuration.RateLimit;
        }

        // see if we passed the reset time window
        if (DateTime.Now > _resetTime)
        {
            _resourcesRemaining = Configuration.RateLimit;
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }

        // subtract the cost of the request
        _resourcesRemaining -= Configuration.CostPerRequest;
        if (_resourcesRemaining < 0)
        {
            _resourcesRemaining = 0;
            var request = e.Session.HttpClient.Request;

            Logger.LogRequest($"Exceeded resource limit when calling {request.Url}. Request will be throttled", MessageType.Failed, new LoggingContext(e.Session));
            if (Configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Throttle)
            {
                if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                {
                    value = new List<ThrottlerInfo>();
                    e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                }

                var throttledRequests = value as List<ThrottlerInfo>;
                throttledRequests?.Add(new ThrottlerInfo(
                    BuildThrottleKey(request),
                    ShouldThrottle,
                    _resetTime
                ));
                ThrottleResponse(e);
                state.HasBeenSet = true;
            }
            else
            {
                if (Configuration.CustomResponse is not null)
                {
                    var headersList = Configuration.CustomResponse.Headers is not null ?
                        Configuration.CustomResponse.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToList() :
                        [];

                    var retryAfterHeader = headersList.FirstOrDefault(h => h.Name.Equals(Configuration.HeaderRetryAfter, StringComparison.OrdinalIgnoreCase));
                    if (retryAfterHeader is not null && retryAfterHeader.Value == "@dynamic")
                    {
                        headersList.Add(new HttpHeader(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
                        _ = headersList.Remove(retryAfterHeader);
                    }

                    var headers = headersList.ToArray();

                    // allow custom throttling response
                    var responseCode = (HttpStatusCode)(Configuration.CustomResponse.StatusCode ?? 200);
                    if (responseCode == HttpStatusCode.TooManyRequests)
                    {
                        if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                        {
                            value = new List<ThrottlerInfo>();
                            e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                        }

                        var throttledRequests = value as List<ThrottlerInfo>;
                        throttledRequests?.Add(new ThrottlerInfo(
                            BuildThrottleKey(request),
                            ShouldThrottle,
                            _resetTime
                        ));
                    }

                    string body = Configuration.CustomResponse.Body is not null ?
                        JsonSerializer.Serialize(Configuration.CustomResponse.Body, ProxyUtils.JsonSerializerOptions) :
                        "";
                    e.Session.GenericResponse(body, responseCode, headers);
                    state.HasBeenSet = true;
                }
                else
                {
                    Logger.LogRequest($"Custom behavior not set. {Configuration.CustomResponseFile} not found.", MessageType.Failed, new LoggingContext(e.Session));
                }
            }
        }
        else
        {
            Logger.LogRequest($"Resources remaining: {_resourcesRemaining}", MessageType.Skipped, new LoggingContext(e.Session));
        }

        StoreRateLimitingHeaders(e);
    }

    public override async Task BeforeResponseAsync(ProxyResponseArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeResponseAsync(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        UpdateProxyResponse(e, HttpStatusCode.OK);
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ?
            (int)(_resetTime - DateTime.Now).TotalSeconds : 0,
            Configuration.HeaderRetryAfter);
    }

    private void ThrottleResponse(ProxyRequestArgs e) => UpdateProxyResponse(e, HttpStatusCode.TooManyRequests);

    private void UpdateProxyResponse(ProxyHttpEventArgsBase e, HttpStatusCode errorStatus)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;
        var response = e.Session.HttpClient.Response;

        // resources exceeded
        if (errorStatus == HttpStatusCode.TooManyRequests)
        {
            if (ProxyUtils.IsGraphRequest(request))
            {
                var requestId = Guid.NewGuid().ToString();
                var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
                headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

                body = JsonSerializer.Serialize(new GraphErrorResponseBody(
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
            }

            headers.Add(new(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
            if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new("Access-Control-Allow-Origin", "*"));
                headers.Add(new("Access-Control-Expose-Headers", Configuration.HeaderRetryAfter));
            }

            e.Session.GenericResponse(body ?? string.Empty, errorStatus, [.. headers.Select(h => new HttpHeader(h.Name, h.Value))]);
            return;
        }

        if (e.SessionData.TryGetValue(Name, out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
        }

        // add headers to the original API response, avoiding duplicates
        headers.ForEach(h => e.Session.HttpClient.Response.Headers.RemoveHeader(h.Name));
        e.Session.HttpClient.Response.Headers.AddHeaders(headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray());
    }
    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage()) : "")}";

    private static string BuildThrottleKey(Request r)
    {
        if (ProxyUtils.IsGraphRequest(r))
        {
            return GraphUtils.BuildThrottleKey(r);
        }
        else
        {
            return r.RequestUri.Host;
        }
    }

    private void StoreRateLimitingHeaders(ProxyRequestArgs e)
    {
        // add rate limiting headers if reached the threshold percentage
        if (_resourcesRemaining > Configuration.RateLimit - (Configuration.RateLimit * Configuration.WarningThresholdPercent / 100))
        {
            return;
        }

        var headers = new List<MockResponseHeader>();
        var reset = Configuration.ResetFormat == RateLimitResetFormat.SecondsLeft ?
            (_resetTime - DateTime.Now).TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) :  // drop decimals
            new DateTimeOffset(_resetTime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers.AddRange(
        [
            new(Configuration.HeaderLimit, Configuration.RateLimit.ToString(CultureInfo.InvariantCulture)),
            new(Configuration.HeaderRemaining, _resourcesRemaining.ToString(CultureInfo.InvariantCulture)),
            new(Configuration.HeaderReset, reset)
        ]);

        ExposeRateLimitingForCors(headers, e);

        e.SessionData.Add(Name, headers);
    }

    private void ExposeRateLimitingForCors(List<MockResponseHeader> headers, ProxyRequestArgs e)
    {
        var request = e.Session.HttpClient.Request;
        if (request.Headers.FirstOrDefault((h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is null)
        {
            return;
        }

        headers.Add(new("Access-Control-Allow-Origin", "*"));
        headers.Add(new("Access-Control-Expose-Headers", $"{Configuration.HeaderLimit}, {Configuration.HeaderRemaining}, {Configuration.HeaderReset}, {Configuration.HeaderRetryAfter}"));
    }
}
