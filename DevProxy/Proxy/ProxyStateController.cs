// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Titanium.Web.Proxy;

namespace DevProxy.Proxy;

sealed class ProxyStateController(
    IProxyState proxyState,
    IEnumerable<IPlugin> plugins,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<ProxyStateController> logger) : IProxyStateController
{
    private static readonly Lock consoleLock = new();

    public IProxyState ProxyState { get; } = proxyState;

    private readonly IEnumerable<IPlugin> _plugins = plugins;
    private readonly IHostApplicationLifetime _hostApplicationLifetime = hostApplicationLifetime;
    private readonly ILogger _logger = logger;
    private ExceptionHandler ExceptionHandler => ex => _logger.LogError(ex, "An error occurred in a plugin");

    public void StartRecording()
    {
        if (ProxyState.IsRecording)
        {
            return;
        }

        ProxyState.IsRecording = true;
        PrintRecordingIndicator(ProxyState.IsRecording);
    }

    public async Task StopRecordingAsync()
    {
        if (!ProxyState.IsRecording)
        {
            return;
        }

        ProxyState.IsRecording = false;
        PrintRecordingIndicator(ProxyState.IsRecording);

        // clone the list so that we can clear the original
        // list in case a new recording is started, and
        // we let plugins handle previously recorded requests
        var clonedLogs = ProxyState.RequestLogs.ToArray();
        ProxyState.RequestLogs.Clear();
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.AfterRecordingStopAsync(new RecordingArgs(clonedLogs)
                {
                    GlobalData = ProxyState.GlobalData
                });
            }
            catch (Exception ex)
            {
                ExceptionHandler(ex);
            }
        }
        _logger.LogInformation("DONE");
    }

    public async Task MockRequestAsync()
    {
        foreach (var plugin in _plugins.Where(p => p.Enabled))
        {
            try
            {
                await plugin.MockRequestAsync(new());
            }
            catch (Exception ex)
            {
                ExceptionHandler(ex);
            }
        }
    }

    public void StopProxy()
    {
        _hostApplicationLifetime.StopApplication();
    }

    private static void PrintRecordingIndicator(bool isRecording)
    {
        lock (consoleLock)
        {
            if (isRecording)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.Write("◉");
                Console.ResetColor();
                Console.Error.WriteLine(" Recording... ");
            }
            else
            {
                Console.Error.WriteLine("○ Stopped recording");
            }
        }
    }
}
