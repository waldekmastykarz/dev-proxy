﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class GraphBetaSupportGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(GraphBetaSupportGuidancePlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.AfterResponse += AfterResponseAsync;
    }

    private Task AfterResponseAsync(object? sender, ProxyResponseArgs e)
    {
        Request request = e.Session.HttpClient.Request;
        if (UrlsToWatch is not null &&
            e.HasRequestUrlMatch(UrlsToWatch) &&
            !string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) &&
            ProxyUtils.IsGraphBetaRequest(request))
            Logger.LogRequest(BuildBetaSupportMessage(), MessageType.Warning, new LoggingContext(e.Session));
        return Task.CompletedTask;
    }

    private static string GetBetaSupportGuidanceUrl() => "https://aka.ms/devproxy/guidance/beta-support";
    private static string[] BuildBetaSupportMessage()
    {
        return [$"Don't use beta APIs in production because they can change or be deprecated.", $"More info at {GetBetaSupportGuidanceUrl()}"];
    }
}
