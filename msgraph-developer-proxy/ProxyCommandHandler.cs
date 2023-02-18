// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Graph.DeveloperProxy.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.RegularExpressions;

namespace Microsoft.Graph.DeveloperProxy;

public class ProxyCommandHandler : ICommandHandler {
    public Option<int?> Port { get; set; }
    public Option<LogLevel?> LogLevel { get; set; }
    public Option<bool?> Record { get; set; }
    public Option<bool?> Interactive { get; set; }

    private readonly PluginEvents _pluginEvents;
    private readonly ISet<Regex> _urlsToWatch;
    private readonly ILogger _logger;

    public ProxyCommandHandler(Option<int?> port,
                               Option<LogLevel?> logLevel,
                               Option<bool?> record,
                               Option<bool?> interactive,
                               PluginEvents pluginEvents,
                               ISet<Regex> urlsToWatch,
                               ILogger logger) {
        Port = port ?? throw new ArgumentNullException(nameof(port));
        LogLevel = logLevel ?? throw new ArgumentNullException(nameof(logLevel));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Interactive = interactive ?? throw new ArgumentNullException(nameof(interactive));
        _pluginEvents = pluginEvents ?? throw new ArgumentNullException(nameof(pluginEvents));
        _urlsToWatch = urlsToWatch ?? throw new ArgumentNullException(nameof(urlsToWatch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int Invoke(InvocationContext context) {
        return InvokeAsync(context).GetAwaiter().GetResult();
    }

    public async Task<int> InvokeAsync(InvocationContext context) {
        var port = context.ParseResult.GetValueForOption(Port);
        if (port is not null) {
            Configuration.Port = port.Value;
        }
        var logLevel = context.ParseResult.GetValueForOption(LogLevel);
        if (logLevel is not null) {
            _logger.LogLevel = logLevel.Value;
        }
        var record = context.ParseResult.GetValueForOption(Record);
        if (record is not null) {
            Configuration.Record = record.Value;
        }
        var interactive = context.ParseResult.GetValueForOption(Interactive);
        if (interactive is not null) {
            Configuration.Interactive = interactive.Value;
        }

        CancellationToken? cancellationToken = (CancellationToken?)context.BindingContext.GetService(typeof(CancellationToken?));

        _pluginEvents.RaiseOptionsLoaded(new OptionsLoadedArgs(context));

        var newReleaseInfo = await UpdateNotification.CheckForNewVersion();
        if (newReleaseInfo != null) {
            _logger.LogError($"New version {newReleaseInfo.Version} of the Graph Developer Proxy is available.");
            _logger.LogError($"See {newReleaseInfo.Url} for more information.");
            _logger.LogError(string.Empty);
        }

        try {
            await new ProxyEngine(Configuration, _urlsToWatch, _pluginEvents, _logger).Run(cancellationToken);
            return 0;
        }
        catch (Exception ex) {
            _logger.LogError("An error occurred while running the Developer Proxy");
            _logger.LogError(ex.Message.ToString());
            _logger.LogError(ex.StackTrace?.ToString() ?? string.Empty);
            var inner = ex.InnerException;

            while (inner is not null) {
                _logger.LogError("============ Inner exception ============");
                _logger.LogError(inner.Message.ToString());
                _logger.LogError(inner.StackTrace?.ToString() ?? string.Empty);
                inner = inner.InnerException;
            }
#if DEBUG
            throw; // so debug tools go straight to the source of the exception when attached
#else
                return 1;
#endif
        }

    }

    public static ProxyConfiguration Configuration { get => ConfigurationFactory.Value; }

    private static readonly Lazy<ProxyConfiguration> ConfigurationFactory = new(() => {
        var builder = new ConfigurationBuilder();
        var configuration = builder
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
        var configObject = new ProxyConfiguration();
        configuration.Bind(configObject);

        return configObject;
    });
}
