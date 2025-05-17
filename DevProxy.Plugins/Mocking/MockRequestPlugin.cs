// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Plugins.Mocking;

public sealed class MockRequestConfiguration
{
    [JsonIgnore]
    public string MockFile { get; set; } = "mock-request.json";
    public MockRequest? Request { get; set; }
}

public class MockRequestPlugin(
    ILogger<MockRequestPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<MockRequestConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection), IDisposable
{
    private MockRequestLoader? _loader;
    private bool _isDisposed;
    private readonly HttpClient _httpClient = new();

    public override string Name => nameof(MockRequestPlugin);

    public override async Task InitializeAsync(InitArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e);

        Configuration.MockFile = ProxyUtils.GetFullPath(Configuration.MockFile, ProxyConfiguration.ConfigFile);

        _loader = ActivatorUtilities.CreateInstance<MockRequestLoader>(e.ServiceProvider, Configuration);
        _loader.InitFileWatcher();
    }

    public override async Task MockRequestAsync(EventArgs e)
    {
        await base.MockRequestAsync(e);

        if (Configuration.Request is null)
        {
            Logger.LogDebug("No mock request is configured. Skipping.");
            return;
        }

        using var requestMessage = GetRequestMessage();

        try
        {
            Logger.LogRequest("Sending mock request", MessageType.Mocked, Configuration.Request.Method, Configuration.Request.Url);

            _ = await _httpClient.SendAsync(requestMessage);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while sending the mock request to {Url}", Configuration.Request.Url);
        }
    }

    protected HttpRequestMessage GetRequestMessage()
    {
        Debug.Assert(Configuration.Request is not null, "The mock request is not configured");

        Logger.LogDebug("Preparing mock {Method} request to {Url}", Configuration.Request.Method, Configuration.Request.Url);
        var requestMessage = new HttpRequestMessage
        {
            RequestUri = new Uri(Configuration.Request.Url),
            Method = new HttpMethod(Configuration.Request.Method)
        };

        var contentType = "";
        if (Configuration.Request.Headers is not null)
        {
            Logger.LogDebug("Adding headers to the mock request");

            foreach (var header in Configuration.Request.Headers)
            {
                if (header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = header.Value;
                    continue;
                }

                requestMessage.Headers.Add(header.Name, header.Value);
            }
        }

        if (Configuration.Request.Body is not null)
        {
            Logger.LogDebug("Adding body to the mock request");

            requestMessage.Content = Configuration.Request.Body is string
                ? new StringContent(Configuration.Request.Body, Encoding.UTF8, contentType)
                : new StringContent(JsonSerializer.Serialize(Configuration.Request.Body, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
        }

        return requestMessage;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing)
        {
            _httpClient.Dispose();
            _loader?.Dispose();
        }

        _isDisposed = true;
    }
}