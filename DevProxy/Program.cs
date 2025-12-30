// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using System.Net;

static WebApplication BuildApplication(string[] args, DevProxyConfigOptions options)
{
    var builder = WebApplication.CreateBuilder(args);

    _ = builder.Configuration.ConfigureDevProxyConfig(options);
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options);

    var defaultIpAddress = "127.0.0.1";
    var ipAddress = options.IPAddress ??
        builder.Configuration.GetValue("ipAddress", defaultIpAddress) ??
        defaultIpAddress;
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        var apiPort = builder.Configuration.GetValue("apiPort", 8897);
        options.Listen(IPAddress.Parse(ipAddress), apiPort);
    });

    var app = builder.Build();

    _ = app.UseSwagger();
    _ = app.UseSwaggerUI();
    _ = app.MapControllers();

    return app;
}

static async Task<int> RunProxyAsync(string[] args, DevProxyConfigOptions options)
{
    var app = BuildApplication(args, options);
    try
    {
        var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
        return await devProxyCommand.InvokeAsync(args, app);
    }
    finally
    {
        // Dispose the app to clean up all services (including FileSystemWatchers in BaseLoader)
        await app.DisposeAsync();
    }
}

_ = Announcement.ShowAsync();

var options = new DevProxyConfigOptions();
options.ParseOptions(args);

int exitCode;
do
{
    // Reset the restart flag before each run
    ConfigFileWatcher.Reset();
    exitCode = await RunProxyAsync(args, options);

    // Wait for proxy to fully stop (including system proxy deregistration)
    // before starting the new instance
    if (ConfigFileWatcher.ProxyStoppedCompletionSource is not null)
    {
#pragma warning disable VSTHRD003 // Intentionally waiting for external signal
        await ConfigFileWatcher.ProxyStoppedCompletionSource.Task;
#pragma warning restore VSTHRD003
    }
} while (ConfigFileWatcher.IsRestarting);

Environment.Exit(exitCode);
