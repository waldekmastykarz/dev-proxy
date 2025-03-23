// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.CommandHandlers;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net;

namespace DevProxy;

internal enum OutputFormat
{
    Text,
    Json
}

internal class ProxyHost
{
    internal static readonly string PortOptionName = "--port";
    private readonly Option<int?> _portOption;
    internal static readonly string IpAddressOptionName = "--ip-address";
    private readonly Option<string?> _ipAddressOption;
    internal static readonly string LogLevelOptionName = "--log-level";
    private static Option<LogLevel?>? _logLevelOption;
    internal static readonly string RecordOptionName = "--record";
    private readonly Option<bool?> _recordOption;
    internal static readonly string WatchPidsOptionName = "--watch-pids";
    private readonly Option<IEnumerable<int>> _watchPidsOption;
    internal static readonly string WatchProcessNamesOptionName = "--watch-process-names";
    private readonly Option<IEnumerable<string>> _watchProcessNamesOption;
    internal static readonly string ConfigFileOptionName = "--config-file";
    private static Option<string?>? _configFileOption;
    internal static readonly string NoFirstRunOptionName = "--no-first-run";
    private readonly Option<bool?> _noFirstRunOption;
    internal static readonly string AsSystemProxyOptionName = "--as-system-proxy";
    private readonly Option<bool?> _asSystemProxyOption;
    internal static readonly string InstallCertOptionName = "--install-cert";
    private readonly Option<bool?> _installCertOption;
    internal static readonly string UrlsToWatchOptionName = "--urls-to-watch";
    private static Option<IEnumerable<string>?>? _urlsToWatchOption;
    internal static readonly string TimeoutOptionName = "--timeout";
    private readonly Option<long?> _timeoutOption;
    internal static readonly string DiscoverOptionName = "--discover";
    private readonly Option<bool?> _discoverOption;

    private static bool _configFileResolved = false;
    private static string _configFile = "devproxyrc.json";
    public static string ConfigFile
    {
        get
        {
            if (_configFileResolved)
            {
                return _configFile;
            }

            if (_configFileOption is null)
            {
                _configFileOption = new Option<string?>("--config-file", "The path to the configuration file");
                _configFileOption.AddAlias("-c");
                _configFileOption.ArgumentHelpName = "configFile";
                _configFileOption.AddValidator(input =>
                {
                    var filePath = ProxyUtils.ReplacePathTokens(input.Tokens[0].Value);
                    if (string.IsNullOrEmpty(filePath))
                    {
                        return;
                    }

                    if (!File.Exists(filePath))
                    {
                        input.ErrorMessage = $"Configuration file {filePath} does not exist";
                    }
                });
            }

            var result = _configFileOption.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the config file option
            var error = result.Errors.Where(e => e.SymbolResult?.Symbol == _configFileOption).FirstOrDefault();
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            var configFile = result.GetValueForOption(_configFileOption);
            if (configFile is not null)
            {
                _configFile = configFile;
            }
            else
            {
                // if there's no config file in the current working folder
                // fall back to the default config file in the app folder
                if (!File.Exists(_configFile))
                {
                    if (File.Exists("devproxyrc.jsonc"))
                    {
                        _configFile = "devproxyrc.jsonc";
                    }
                    else
                    {
                        _configFile = "~appFolder/devproxyrc.json";
                    }
                }
            }

            _configFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configFile));

            _configFileResolved = true;

            return _configFile;
        }
    }

    private static bool _logLevelResolved = false;
    private static LogLevel? _logLevel;
    public static LogLevel? LogLevel
    {
        get
        {
            if (_logLevelResolved)
            {
                return _logLevel;
            }

            if (_logLevelOption is null)
            {
                _logLevelOption = new Option<LogLevel?>(
                    "--log-level",
                    $"Level of messages to log. Allowed values: {string.Join(", ", Enum.GetNames(typeof(LogLevel)))}"
                )
                {
                    ArgumentHelpName = "logLevel"
                };
                _logLevelOption.AddValidator(input =>
                {
                    if (!Enum.TryParse<LogLevel>(input.Tokens[0].Value, true, out _))
                    {
                        input.ErrorMessage = $"{input.Tokens[0].Value} is not a valid log level. Allowed values are: {string.Join(", ", Enum.GetNames(typeof(LogLevel)))}";
                    }
                });
            }

            var result = _logLevelOption.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            var error = result.Errors.Where(e => e.SymbolResult?.Symbol == _logLevelOption).FirstOrDefault();
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            _logLevel = result.GetValueForOption(_logLevelOption);
            _logLevelResolved = true;

            return _logLevel;
        }
    }

    private static bool _urlsToWatchResolved = false;
    private static IEnumerable<string>? _urlsToWatch;
    public static IEnumerable<string>? UrlsToWatch
    {
        get
        {
            if (_urlsToWatchResolved)
            {
                return _urlsToWatch;
            }

            var result = _urlsToWatchOption!.Parse(Environment.GetCommandLineArgs());
            // since we're parsing all args, and other options are not instantiated yet
            // we're getting here a bunch of other errors, so we only need to look for
            // errors related to the log level option
            var error = result.Errors.Where(e => e.SymbolResult?.Symbol == _urlsToWatchOption).FirstOrDefault();
            if (error is not null)
            {
                // Logger is not available here yet so we need to fallback to Console
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error.Message);
                Console.ForegroundColor = color;
                Environment.Exit(1);
            }

            _urlsToWatch = result.GetValueForOption(_urlsToWatchOption!);
            _urlsToWatchResolved = true;

            return _urlsToWatch;
        }
    }

    public ProxyHost()
    {
        _portOption = new Option<int?>(PortOptionName, "The port for the proxy to listen on");
        _portOption.AddAlias("-p");
        _portOption.ArgumentHelpName = "port";

        _ipAddressOption = new Option<string?>(IpAddressOptionName, "The IP address for the proxy to bind to")
        {
            ArgumentHelpName = "ipAddress"
        };
        _ipAddressOption.AddValidator(input =>
        {
            if (!IPAddress.TryParse(input.Tokens[0].Value, out _))
            {
                input.ErrorMessage = $"{input.Tokens[0].Value} is not a valid IP address";
            }
        });

        _recordOption = new Option<bool?>(RecordOptionName, "Use this option to record all request logs");

        _watchPidsOption = new Option<IEnumerable<int>>(WatchPidsOptionName, "The IDs of processes to watch for requests")
        {
            ArgumentHelpName = "pids",
            AllowMultipleArgumentsPerToken = true
        };

        _watchProcessNamesOption = new Option<IEnumerable<string>>(WatchProcessNamesOptionName, "The names of processes to watch for requests")
        {
            ArgumentHelpName = "processNames",
            AllowMultipleArgumentsPerToken = true
        };

        _noFirstRunOption = new Option<bool?>(NoFirstRunOptionName, "Skip the first run experience");
        _discoverOption = new Option<bool?>(DiscoverOptionName, "Run Dev Proxy in discovery mode");

        _asSystemProxyOption = new Option<bool?>(AsSystemProxyOptionName, "Set Dev Proxy as the system proxy");
        _asSystemProxyOption.AddValidator(input =>
        {
            try
            {
                _ = input.GetValueForOption(_asSystemProxyOption);
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

        _installCertOption = new Option<bool?>(InstallCertOptionName, "Install self-signed certificate");
        _installCertOption.AddValidator(input =>
        {
            try
            {
                var asSystemProxy = input.GetValueForOption(_asSystemProxyOption) ?? true;
                var installCert = input.GetValueForOption(_installCertOption) ?? true;
                if (asSystemProxy && !installCert)
                {
                    input.ErrorMessage = $"Requires option '--{_asSystemProxyOption.Name}' to be 'false'";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });

        _urlsToWatchOption = new(UrlsToWatchOptionName, "The list of URLs to watch for requests")
        {
            ArgumentHelpName = "urlsToWatch",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };
        _urlsToWatchOption.AddAlias("-u");
        
        _timeoutOption = new Option<long?>(TimeoutOptionName, "Time in seconds after which Dev Proxy exits. Resets when Dev Proxy intercepts a request.")
        {
            ArgumentHelpName = "timeout",
        };
        _timeoutOption.AddValidator(input =>
        {
            try
            {
                if (!long.TryParse(input.Tokens[0].Value, out long timeoutInput) || timeoutInput < 1)
                {
                    input.ErrorMessage = $"{input.Tokens[0].Value} is not valid as a timeout value";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });
        _timeoutOption.AddAlias("-t");

        ProxyCommandHandler.Configuration.ConfigFile = ConfigFile;
    }

    public RootCommand GetRootCommand(ILogger logger)
    {
        var command = new RootCommand {
            _portOption,
            _ipAddressOption,
            // _logLevelOption is set while initializing the Program
            // As such, it's always set here
            _logLevelOption!,
            _recordOption,
            _watchPidsOption,
            _watchProcessNamesOption,
            // _configFileOption is set during the call to load
            // `ProxyCommandHandler.Configuration`. As such, it's always set here
            _configFileOption!,
            _noFirstRunOption,
            _asSystemProxyOption,
            _installCertOption,
            // _urlsToWatchOption is set while initialize the Program
            // As such, it's always set here
            _urlsToWatchOption!,
            _timeoutOption,
            _discoverOption
        };
        command.Description = "Dev Proxy is a command line tool for testing Microsoft Graph, SharePoint Online and any other HTTP APIs.";
        var outputOption = new Option<string>("--output", $"Output format. Allowed values: {string.Join(", ", Enum.GetNames<OutputFormat>())}")
        
        {
            ArgumentHelpName = "output"
        };
        outputOption.AddAlias("-o");
        outputOption.Arity = ArgumentArity.ZeroOrOne;
        outputOption.SetDefaultValue("text");
        outputOption.AddValidator(input =>
        {
            var value = input.GetValueForOption(outputOption);
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (!Enum.TryParse<OutputFormat>(value, true, out _))
            {
                input.ErrorMessage = $"{value} is not a valid output format. Allowed values are: {string.Join(", ", Enum.GetNames<OutputFormat>())}";
            }
        });
        command.AddGlobalOption(outputOption);

        var msGraphDbCommand = new Command("msgraphdb", "Generate a local SQLite database with Microsoft Graph API metadata")
        {
            Handler = new MSGraphDbCommandHandler(logger)
        };
        command.Add(msGraphDbCommand);

        var configCommand = new Command("config", "Manage Dev Proxy configs");

        var configGetCommand = new Command("get", "Download the specified config from the Sample Solution Gallery");
        var configIdArgument = new Argument<string>("config-id", "The ID of the config to download");
        configGetCommand.AddArgument(configIdArgument);
        configGetCommand.SetHandler(async configId => await ConfigGetCommandHandler.DownloadConfigAsync(configId, logger), configIdArgument);
        configCommand.Add(configGetCommand);

        var configNewCommand = new Command("new", "Create new Dev Proxy configuration file");
        var nameArgument = new Argument<string>("name", "Name of the configuration file")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArgument.SetDefaultValue("devproxyrc.json");
        configNewCommand.AddArgument(nameArgument);
        configNewCommand.SetHandler(async name => await ConfigNewCommandHandler.CreateConfigFileAsync(name, logger), nameArgument);
        configCommand.Add(configNewCommand);

        var configOpenCommand = new Command("open", "Open devproxyrc.json");
        configOpenCommand.SetHandler(() =>
        {
            var cfgPsi = new ProcessStartInfo(ConfigFile)
            {
                UseShellExecute = true
            };
            Process.Start(cfgPsi);
        });
        configCommand.Add(configOpenCommand);

        command.Add(configCommand);

        var outdatedCommand = new Command("outdated", "Check for new version");
        var outdatedShortOption = new Option<bool>("--short", "Return version only");
        outdatedCommand.AddOption(outdatedShortOption);
        outdatedCommand.SetHandler(async versionOnly => await OutdatedCommandHandler.CheckVersionAsync(versionOnly, logger), outdatedShortOption);
        command.Add(outdatedCommand);

        var jwtCommand = new Command("jwt", "Manage JSON Web Tokens");
        var jwtCreateCommand = new Command("create", "Create a new JWT token");
        var jwtNameOption = new Option<string>("--name", "The name of the user to create the token for.");
        jwtNameOption.AddAlias("-n");
        jwtCreateCommand.AddOption(jwtNameOption);

        var jwtAudienceOption = new Option<IEnumerable<string>>("--audience", "The audiences to create the token for. Specify once for each audience")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtAudienceOption.AddAlias("-a");
        jwtCreateCommand.AddOption(jwtAudienceOption);

        var jwtIssuerOption = new Option<string>("--issuer", "The issuer of the token.");
        jwtIssuerOption.AddAlias("-i");
        jwtCreateCommand.AddOption(jwtIssuerOption);

        var jwtRolesOption = new Option<IEnumerable<string>>("--roles", "A role claim to add to the token. Specify once for each role.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtRolesOption.AddAlias("-r");
        jwtCreateCommand.AddOption(jwtRolesOption);

        var jwtScopesOption = new Option<IEnumerable<string>>("--scopes", "A scope claim to add to the token. Specify once for each scope.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        jwtScopesOption.AddAlias("-s");
        jwtCreateCommand.AddOption(jwtScopesOption);

        var jwtClaimsOption = new Option<Dictionary<string, string>>("--claims",
            description: "Claims to add to the token. Specify once for each claim in the format \"name:value\".",
            parseArgument: result =>
            {
                var claims = new Dictionary<string, string>();
                foreach (var token in result.Tokens)
                {
                    var claim = token.Value.Split(":");

                    if (claim.Length != 2)
                    {
                        result.ErrorMessage = $"Invalid claim format: '{token.Value}'. Expected format is name:value.";
                        return claims ?? [];
                    }

                    try
                    {
                        var (key, value) = (claim[0], claim[1]);
                        claims.Add(key, value);
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = ex.Message;
                    }
                }
                return claims;
            }
        )
        {
            AllowMultipleArgumentsPerToken = true,
        };
        jwtCreateCommand.AddOption(jwtClaimsOption);

        var jwtValidForOption = new Option<double>("--valid-for", "The duration for which the token is valid. Duration is set in minutes.");
        jwtValidForOption.AddAlias("-v");
        jwtCreateCommand.AddOption(jwtValidForOption);

        var jwtSigningKeyOption = new Option<string>("--signing-key", "The signing key to sign the token. Minimum length is 32 characters.");
        jwtSigningKeyOption.AddAlias("-k");
        jwtSigningKeyOption.AddValidator(input =>
        {
            try
            {
                var value = input.GetValueForOption(jwtSigningKeyOption);
                if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
                {
                    input.ErrorMessage = $"Requires option '--{jwtSigningKeyOption.Name}' to be at least 32 characters";
                }
            }
            catch (InvalidOperationException ex)
            {
                input.ErrorMessage = ex.Message;
            }
        });
        jwtCreateCommand.AddOption(jwtSigningKeyOption);

        jwtCreateCommand.SetHandler(
            JwtCommandHandler.GetToken,
            new JwtBinder(
                jwtNameOption,
                jwtAudienceOption,
                jwtIssuerOption,
                jwtRolesOption,
                jwtScopesOption,
                jwtClaimsOption,
                jwtValidForOption,
                jwtSigningKeyOption
            )
        );
        jwtCommand.Add(jwtCreateCommand);

        command.Add(jwtCommand);

        var certCommand = new Command("cert", "Manage the Dev Proxy certificate");
        var certEnsureCommand = new Command("ensure", "Ensure certificates are setup (creates root if required). Also makes root certificate trusted.");
        certEnsureCommand.SetHandler(async () => await CertEnsureCommandHandler.EnsureCertAsync(logger));
        certCommand.Add(certEnsureCommand);
        command.Add(certCommand);

        return command;
    }

    public ProxyCommandHandler GetCommandHandler(PluginEvents pluginEvents, Option[] optionsFromPlugins, ISet<UrlToWatch> urlsToWatch, ILogger logger) => new(
        pluginEvents,
        [
            _portOption,
            _ipAddressOption,
            _logLevelOption!,
            _recordOption,
            _watchPidsOption,
            _watchProcessNamesOption,
            _noFirstRunOption,
            _asSystemProxyOption,
            _installCertOption,
            _timeoutOption,
            _discoverOption,
            .. optionsFromPlugins,
        ],
        urlsToWatch,
        logger
    );
}

