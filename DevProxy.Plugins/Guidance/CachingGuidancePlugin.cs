// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;

namespace DevProxy.Plugins.Guidance;

public sealed class CachingGuidancePluginConfiguration
{
    public int CacheThresholdSeconds { get; set; } = 5;
}

public sealed class CachingGuidancePlugin(
    ILogger<CachingGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection configurationSection) :
    BasePlugin<CachingGuidancePluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        configurationSection)
{
    public override string Name => nameof(CachingGuidancePlugin);
    private readonly Dictionary<string, DateTime> _interceptedRequests = [];

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var request = e.Session.HttpClient.Request;
        var url = request.RequestUri.AbsoluteUri;
        var now = DateTime.Now;

        if (!_interceptedRequests.TryGetValue(url, out var value))
        {
            value = now;
            _interceptedRequests.Add(url, value);
            Logger.LogRequest("First request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var lastIntercepted = value;
        var secondsSinceLastIntercepted = (now - lastIntercepted).TotalSeconds;
        if (secondsSinceLastIntercepted <= Configuration.CacheThresholdSeconds)
        {
            Logger.LogRequest(BuildCacheWarningMessage(request, Configuration.CacheThresholdSeconds, lastIntercepted), MessageType.Warning, new LoggingContext(e.Session));
        }
        else
        {
            Logger.LogRequest("Request outside of cache window", MessageType.Skipped, new LoggingContext(e.Session));
        }

        _interceptedRequests[url] = now;
    }

    private static string BuildCacheWarningMessage(Request r, int _warningSeconds, DateTime lastIntercepted) =>
        $"Another request to {r.RequestUri.PathAndQuery} intercepted within {_warningSeconds} seconds. Last intercepted at {lastIntercepted}. Consider using cache to avoid calling the API too often.";
}
