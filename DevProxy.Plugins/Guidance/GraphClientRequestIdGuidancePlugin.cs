﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphClientRequestIdGuidancePlugin(
    ILogger<GraphClientRequestIdGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphClientRequestIdGuidancePlugin);

    public override async Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.BeforeRequestAsync(e);

        var request = e.Session.HttpClient.Request;
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

        if (WarnNoClientRequestId(request))
        {
            Logger.LogRequest(BuildAddClientRequestIdMessage(), MessageType.Warning, new LoggingContext(e.Session));

            if (!ProxyUtils.IsSdkRequest(request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkMessage(), MessageType.Tip, new LoggingContext(e.Session));
            }
        }
        else
        {
            Logger.LogRequest("client-request-id header present", MessageType.Skipped, new LoggingContext(e.Session));
        }
    }

    private static bool WarnNoClientRequestId(Request request) =>
        ProxyUtils.IsGraphRequest(request) &&
        !request.Headers.HeaderExists("client-request-id");

    private static string GetClientRequestIdGuidanceUrl() => "https://aka.ms/devproxy/guidance/client-request-id";
    private static string BuildAddClientRequestIdMessage() =>
        $"To help Microsoft investigate errors, to each request to Microsoft Graph add the client-request-id header with a unique GUID. More info at {GetClientRequestIdGuidanceUrl()}";
}
