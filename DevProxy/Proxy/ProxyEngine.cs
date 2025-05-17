﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.VisualStudio.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Proxy;

enum ToggleSystemProxyAction
{
    On,
    Off
}

sealed class ProxyEngine(
    IEnumerable<IPlugin> plugins,
    IProxyConfiguration proxyConfiguration,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStateController proxyController,
    ILogger<ProxyEngine> logger) : BackgroundService, IDisposable
{
    private readonly IEnumerable<IPlugin> _plugins = plugins;
    private readonly ILogger _logger = logger;
    private readonly IProxyConfiguration _config = proxyConfiguration;

    internal static ProxyServer ProxyServer { get; private set; }
    private ExplicitProxyEndPoint? _explicitEndPoint;
    // lists of URLs to watch, used for intercepting requests
    private readonly ISet<UrlToWatch> _urlsToWatch = urlsToWatch;
    // lists of hosts to watch extracted from urlsToWatch,
    // used for deciding which URLs to decrypt for further inspection
    private readonly HashSet<UrlToWatch> _hostsToWatch = [];
    private readonly IProxyStateController _proxyController = proxyController;
    // Dictionary for plugins to store data between requests
    // the key is HashObject of the SessionEventArgs object
    private readonly ConcurrentDictionary<int, Dictionary<string, object>> _pluginData = [];
    private InactivityTimer? _inactivityTimer;

    public static X509Certificate2? Certificate => ProxyServer?.CertificateManager.RootCertificate;

    private ExceptionHandler ExceptionHandler => ex => _logger.LogError(ex, "An error occurred in a plugin");

    static ProxyEngine()
    {
        ProxyServer = new ProxyServer();
        ProxyServer.CertificateManager.PfxFilePath = Environment.GetEnvironmentVariable("DEV_PROXY_CERT_PATH") ?? string.Empty;
        ProxyServer.CertificateManager.RootCertificateName = "Dev Proxy CA";
        ProxyServer.CertificateManager.CertificateStorage = new CertificateDiskCache();
        // we need to change this to a value lower than 397
        // to avoid the ERR_CERT_VALIDITY_TOO_LONG error in Edge
        ProxyServer.CertificateManager.CertificateValidDays = 365;

        using var joinableTaskContext = new JoinableTaskContext();
        var joinableTaskFactory = new JoinableTaskFactory(joinableTaskContext);
        _ = joinableTaskFactory.Run(async () => await ProxyServer.CertificateManager.LoadOrCreateRootCertificateAsync());
    }

    private static void ToggleSystemProxy(ToggleSystemProxyAction toggle, string? ipAddress = null, int? port = null)
    {
        var bashScriptPath = Path.Join(ProxyUtils.AppFolder, "toggle-proxy.sh");
        var args = toggle switch
        {
            ToggleSystemProxyAction.On => $"on {ipAddress} {port}",
            ToggleSystemProxyAction.Off => "off",
            _ => throw new NotImplementedException()
        };

        var startInfo = new ProcessStartInfo()
        {
            FileName = "/bin/bash",
            Arguments = $"{bashScriptPath} {args}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process() { StartInfo = startInfo };
        _ = process.Start();
        process.WaitForExit();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Debug.Assert(ProxyServer is not null, "Proxy server is not initialized");

        if (!_urlsToWatch.Any())
        {
            _logger.LogError("No URLs to watch configured. Please add URLs to watch in the devproxyrc.json config file.");
            return;
        }

        LoadHostNamesFromUrls();

        ProxyServer.BeforeRequest += OnRequestAsync;
        ProxyServer.BeforeResponse += OnBeforeResponseAsync;
        ProxyServer.AfterResponse += OnAfterResponseAsync;
        ProxyServer.ServerCertificateValidationCallback += OnCertificateValidationAsync;
        ProxyServer.ClientCertificateSelectionCallback += OnCertificateSelectionAsync;

        var ipAddress = string.IsNullOrEmpty(_config.IPAddress) ? IPAddress.Any : IPAddress.Parse(_config.IPAddress);
        _explicitEndPoint = new ExplicitProxyEndPoint(ipAddress, _config.Port, true);
        // Fired when a CONNECT request is received
        _explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequestAsync;
        if (_config.InstallCert)
        {
            await ProxyServer.CertificateManager.EnsureRootCertificateAsync(stoppingToken);
        }
        else
        {
            _explicitEndPoint.GenericCertificate = await ProxyServer
                .CertificateManager
                .LoadRootCertificateAsync(stoppingToken);
        }

        ProxyServer.AddEndPoint(_explicitEndPoint);
        await ProxyServer.StartAsync(cancellationToken: stoppingToken);

        // run first-run setup on macOS
        FirstRunSetup();

        foreach (var endPoint in ProxyServer.ProxyEndPoints)
        {
            _logger.LogInformation("Dev Proxy listening on {IPAddress}:{Port}...", endPoint.IpAddress, endPoint.Port);
        }

        if (_config.AsSystemProxy)
        {
            if (RunTime.IsWindows)
            {
                ProxyServer.SetAsSystemHttpProxy(_explicitEndPoint);
                ProxyServer.SetAsSystemHttpsProxy(_explicitEndPoint);
            }
            else if (RunTime.IsMac)
            {
                ToggleSystemProxy(ToggleSystemProxyAction.On, _config.IPAddress, _config.Port);
            }
            else
            {
                _logger.LogWarning("Configure your operating system to use this proxy's port and address {IPAddress}:{Port}", _config.IPAddress, _config.Port);
            }
        }
        else
        {
            _logger.LogInformation("Configure your application to use this proxy's port and address");
        }

        var isInteractive = !Console.IsInputRedirected &&
            Environment.GetEnvironmentVariable("CI") is null;

        if (isInteractive)
        {
            // only print hotkeys when they can be used
            PrintHotkeys();
        }

        if (_config.Record)
        {
            StartRecording();
        }

        if (_config.TimeoutSeconds.HasValue)
        {
            _inactivityTimer = new InactivityTimer(_config.TimeoutSeconds.Value, _proxyController.StopProxy);
        }

        if (!isInteractive)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested && ProxyServer.ProxyRunning)
            {
                while (!Console.KeyAvailable)
                {
                    await Task.Delay(10, stoppingToken);
                }

                await ReadKeysAsync();
            }
        }
        catch (TaskCanceledException)
        {
            throw;
        }
    }

    private void FirstRunSetup()
    {
        if (!RunTime.IsMac ||
            _config.NoFirstRun ||
            !IsFirstRun() ||
            !_config.InstallCert)
        {
            return;
        }

        var bashScriptPath = Path.Join(ProxyUtils.AppFolder, "trust-cert.sh");
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            Arguments = bashScriptPath,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        using var process = new Process() { StartInfo = startInfo };
        _ = process.Start();
        process.WaitForExit();
    }

    private static bool IsFirstRun()
    {
        var firstRunFilePath = Path.Combine(ProxyUtils.AppFolder!, ".hasrun");
        if (File.Exists(firstRunFilePath))
        {
            return false;
        }

        try
        {
            File.WriteAllText(firstRunFilePath, "");
        }
        catch { }

        return true;
    }

    private async Task ReadKeysAsync()
    {
        var key = Console.ReadKey(true).Key;
#pragma warning disable IDE0010
        switch (key)
#pragma warning restore IDE0010
        {
            case ConsoleKey.R:
                StartRecording();
                break;
            case ConsoleKey.S:
                await StopRecordingAsync();
                break;
            case ConsoleKey.C:
                Console.Clear();
                PrintHotkeys();
                break;
            case ConsoleKey.W:
                await _proxyController.MockRequestAsync();
                break;
        }
    }

    private void StartRecording()
    {
        if (_proxyController.ProxyState.IsRecording)
        {
            return;
        }

        _proxyController.StartRecording();
    }

    private async Task StopRecordingAsync()
    {
        if (!_proxyController.ProxyState.IsRecording)
        {
            return;
        }

        await _proxyController.StopRecordingAsync();
    }

    // Convert strings from config to regexes.
    // From the list of URLs, extract host names and convert them to regexes.
    // We need this because before we decrypt a request, we only have access
    // to the host name, not the full URL.
    private void LoadHostNamesFromUrls()
    {
        foreach (var urlToWatch in _urlsToWatch)
        {
            // extract host from the URL
            var urlToWatchPattern = Regex.Unescape(urlToWatch.Url.ToString())
                .Trim('^', '$')
                .Replace(".*", "*", StringComparison.OrdinalIgnoreCase);
            string hostToWatch;
            if (urlToWatchPattern.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                // if the URL contains a protocol, extract the host from the URL
                var urlChunks = urlToWatchPattern.Split("://");
                var slashPos = urlChunks[1].IndexOf('/', StringComparison.OrdinalIgnoreCase);
                hostToWatch = slashPos < 0 ? urlChunks[1] : urlChunks[1][..slashPos];
            }
            else
            {
                // if the URL doesn't contain a protocol,
                // we assume the whole URL is a host name
                hostToWatch = urlToWatchPattern;
            }

            // remove port number if present
            var portPos = hostToWatch.IndexOf(':', StringComparison.OrdinalIgnoreCase);
            if (portPos > 0)
            {
                hostToWatch = hostToWatch[..portPos];
            }

            var hostToWatchRegexString = Regex.Escape(hostToWatch).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            Regex hostRegex = new($"^{hostToWatchRegexString}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            // don't add the same host twice
            if (!_hostsToWatch.Any(h => h.Url.ToString() == hostRegex.ToString()))
            {
                _ = _hostsToWatch.Add(new UrlToWatch(hostRegex, urlToWatch.Exclude));
            }
        }
    }

    private void StopProxy()
    {
        // Unsubscribe & Quit
        try
        {
            if (_explicitEndPoint != null)
            {
                _explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequestAsync;
            }

            if (ProxyServer is not null)
            {
                ProxyServer.BeforeRequest -= OnRequestAsync;
                ProxyServer.BeforeResponse -= OnBeforeResponseAsync;
                ProxyServer.AfterResponse -= OnAfterResponseAsync;
                ProxyServer.ServerCertificateValidationCallback -= OnCertificateValidationAsync;
                ProxyServer.ClientCertificateSelectionCallback -= OnCertificateSelectionAsync;

                if (ProxyServer.ProxyRunning)
                {
                    ProxyServer.Stop();
                }
            }

            _inactivityTimer?.Stop();

            if (RunTime.IsMac && _config.AsSystemProxy)
            {
                ToggleSystemProxy(ToggleSystemProxyAction.Off);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while stopping the proxy");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopRecordingAsync();
        StopProxy();

        await base.StopAsync(cancellationToken);
    }

    async Task OnBeforeTunnelConnectRequestAsync(object sender, TunnelConnectSessionEventArgs e)
    {
        // Ensures that only the targeted Https domains are proxyied
        if (!IsProxiedHost(e.HttpClient.Request.RequestUri.Host) ||
            !IsProxiedProcess(e))
        {
            e.DecryptSsl = false;
        }
        await Task.CompletedTask;
    }

    private static int GetProcessId(TunnelConnectSessionEventArgs e)
    {
        if (RunTime.IsWindows)
        {
            return e.HttpClient.ProcessId.Value;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "lsof",
            Arguments = $"-i :{e.ClientRemoteEndPoint?.Port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = new Process
        {
            StartInfo = psi
        };
        _ = proc.Start();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        var lines = output.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        var matchingLine = lines.FirstOrDefault(l => l.Contains($"{e.ClientRemoteEndPoint?.Port}->", StringComparison.OrdinalIgnoreCase));
        if (matchingLine is null)
        {
            return -1;
        }
        var pidString = Regex.Matches(matchingLine, @"^.*?\s+(\d+)")?.FirstOrDefault()?.Groups[1]?.Value;
        if (pidString is null)
        {
            return -1;
        }

        return int.TryParse(pidString, out var pid) ? pid : -1;
    }

    private bool IsProxiedProcess(TunnelConnectSessionEventArgs e)
    {
        // If no process names or IDs are specified, we proxy all processes
        if (!_config.WatchPids.Any() &&
            !_config.WatchProcessNames.Any())
        {
            return true;
        }

        var processId = GetProcessId(e);
        if (processId == -1)
        {
            return false;
        }

        if (_config.WatchPids.Any() &&
            _config.WatchPids.Contains(processId))
        {
            return true;
        }

        if (_config.WatchProcessNames.Any())
        {
            var processName = Process.GetProcessById(processId).ProcessName;
            if (_config.WatchProcessNames.Contains(processName))
            {
                return true;
            }
        }

        return false;
    }

    async Task OnRequestAsync(object sender, SessionEventArgs e)
    {
        _inactivityTimer?.Reset();
        if (IsProxiedHost(e.HttpClient.Request.RequestUri.Host) &&
            IsIncludedByHeaders(e.HttpClient.Request.Headers))
        {
            if (!_pluginData.TryAdd(e.GetHashCode(), []))
            {
                throw new InvalidOperationException($"Unable to initialize the plugin data storage for hash key {e.GetHashCode()}");
            }
            var responseState = new ResponseState();
            var proxyRequestArgs = new ProxyRequestArgs(e, responseState)
            {
                SessionData = _pluginData[e.GetHashCode()],
                GlobalData = _proxyController.ProxyState.GlobalData
            };
            if (!proxyRequestArgs.HasRequestUrlMatch(_urlsToWatch))
            {
                return;
            }

            // we need to keep the request body for further processing
            // by plugins
            e.HttpClient.Request.KeepBody = true;
            if (e.HttpClient.Request.HasBody)
            {
                _ = await e.GetRequestBodyAsString();
            }

            using var scope = _logger.BeginScope(e.HttpClient.Request.Method ?? "", e.HttpClient.Request.Url, e.GetHashCode());

            e.UserData = e.HttpClient.Request;

            _logger.LogRequest($"{e.HttpClient.Request.Method} {e.HttpClient.Request.Url}", MessageType.InterceptedRequest, new LoggingContext(e));

            _logger.LogRequest($"{DateTimeOffset.UtcNow}", MessageType.Timestamp, new LoggingContext(e));

            await HandleRequestAsync(e, proxyRequestArgs);
        }
    }

    private async Task HandleRequestAsync(SessionEventArgs e, ProxyRequestArgs proxyRequestArgs)
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.BeforeRequestAsync(proxyRequestArgs);
            }
            catch (Exception ex)
            {
                ExceptionHandler(ex);
            }
        }

        // We only need to set the proxy header if the proxy has not set a response and the request is going to be sent to the target.
        if (!proxyRequestArgs.ResponseState.HasBeenSet)
        {
            _logger?.LogRequest("Passed through", MessageType.PassedThrough, new LoggingContext(e));
            AddProxyHeader(e.HttpClient.Request);
        }
    }

    private static void AddProxyHeader(Request r) => r.Headers?.AddHeader("Via", $"{r.HttpVersion} dev-proxy/{ProxyUtils.ProductVersion}");

    private bool IsProxiedHost(string hostName)
    {
        var urlMatch = _hostsToWatch.FirstOrDefault(h => h.Url.IsMatch(hostName));
        return urlMatch is not null && !urlMatch.Exclude;
    }

    private bool IsIncludedByHeaders(HeaderCollection requestHeaders)
    {
        if (_config.FilterByHeaders is null)
        {
            return true;
        }

        foreach (var header in _config.FilterByHeaders)
        {
            _logger.LogDebug("Checking header {Header} with value {Value}...",
                header.Name,
                string.IsNullOrEmpty(header.Value) ? "(any)" : header.Value
            );

            if (requestHeaders.HeaderExists(header.Name))
            {
                if (string.IsNullOrEmpty(header.Value))
                {
                    _logger.LogDebug("Request has header {Header}", header.Name);
                    return true;
                }

                if (requestHeaders.GetHeaders(header.Name)!.Any(h => h.Value.Contains(header.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("Request header {Header} contains value {Value}", header.Name, header.Value);
                    return true;
                }
            }
            else
            {
                _logger.LogDebug("Request doesn't have header {Header}", header.Name);
            }
        }

        _logger.LogDebug("Request doesn't match any header filter. Ignoring");
        return false;
    }

    // Modify response
    async Task OnBeforeResponseAsync(object sender, SessionEventArgs e)
    {
        // read response headers
        if (IsProxiedHost(e.HttpClient.Request.RequestUri.Host))
        {
            var proxyResponseArgs = new ProxyResponseArgs(e, new ResponseState())
            {
                SessionData = _pluginData[e.GetHashCode()],
                GlobalData = _proxyController.ProxyState.GlobalData
            };
            if (!proxyResponseArgs.HasRequestUrlMatch(_urlsToWatch))
            {
                return;
            }

            using var scope = _logger.BeginScope(e.HttpClient.Request.Method ?? "", e.HttpClient.Request.Url, e.GetHashCode());

            // necessary to make the response body available to plugins
            e.HttpClient.Response.KeepBody = true;
            if (e.HttpClient.Response.HasBody)
            {
                _ = await e.GetResponseBody();
            }

            foreach (var plugin in _plugins.Where(p => p.Enabled))
            {
                try
                {
                    await plugin.BeforeResponseAsync(proxyResponseArgs);
                }
                catch (Exception ex)
                {
                    ExceptionHandler(ex);
                }
            }
        }
    }
    async Task OnAfterResponseAsync(object sender, SessionEventArgs e)
    {
        // read response headers
        if (IsProxiedHost(e.HttpClient.Request.RequestUri.Host))
        {
            var proxyResponseArgs = new ProxyResponseArgs(e, new ResponseState())
            {
                SessionData = _pluginData[e.GetHashCode()],
                GlobalData = _proxyController.ProxyState.GlobalData
            };
            if (!proxyResponseArgs.HasRequestUrlMatch(_urlsToWatch))
            {
                // clean up
                _ = _pluginData.Remove(e.GetHashCode(), out _);
                return;
            }

            // necessary to repeat to make the response body
            // of mocked requests available to plugins
            e.HttpClient.Response.KeepBody = true;

            using var scope = _logger.BeginScope(e.HttpClient.Request.Method ?? "", e.HttpClient.Request.Url, e.GetHashCode());

            var message = $"{e.HttpClient.Request.Method} {e.HttpClient.Request.Url}";
            _logger.LogRequest(message, MessageType.InterceptedResponse, new LoggingContext(e));
            foreach (var plugin in _plugins.Where(p => p.Enabled))
            {
                try
                {
                    await plugin.AfterResponseAsync(proxyResponseArgs);
                }
                catch (Exception ex)
                {
                    ExceptionHandler(ex);
                }
            }
            _logger.LogRequest(message, MessageType.FinishedProcessingRequest, new LoggingContext(e));

            // clean up
            _ = _pluginData.Remove(e.GetHashCode(), out _);
        }
    }

    // Allows overriding default certificate validation logic
    Task OnCertificateValidationAsync(object sender, CertificateValidationEventArgs e)
    {
        // set IsValid to true/false based on Certificate Errors
        if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
        {
            e.IsValid = true;
        }

        return Task.CompletedTask;
    }

    // Allows overriding default client certificate selection logic during mutual authentication
    Task OnCertificateSelectionAsync(object sender, CertificateSelectionEventArgs e) =>
        // set e.clientCertificate to override
        Task.CompletedTask;

    private static void PrintHotkeys()
    {
        Console.WriteLine("");
        Console.WriteLine("Hotkeys: issue (w)eb request, (r)ecord, (s)top recording, (c)lear screen");
        Console.WriteLine("Press CTRL+C to stop Dev Proxy");
        Console.WriteLine("");
    }

    public override void Dispose()
    {
        base.Dispose();

        _inactivityTimer?.Dispose();
    }
}
