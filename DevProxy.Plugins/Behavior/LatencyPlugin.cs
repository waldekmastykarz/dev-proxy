// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Behavior;

public sealed class LatencyConfiguration
{
    public int MinMs { get; set; }
    public int MaxMs { get; set; } = 5000;
}

public sealed class LatencyPlugin(
    ILogger<LatencyPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<LatencyConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public override string Name => nameof(LatencyPlugin);
    private readonly Random _random = new();

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        if (!e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var delay = _random.Next(Configuration.MinMs, Configuration.MaxMs);
        Logger.LogRequest($"Delaying request for {delay}ms", MessageType.Chaos, new LoggingContext(e.Session));
        await Task.Delay(delay);
    }
}
