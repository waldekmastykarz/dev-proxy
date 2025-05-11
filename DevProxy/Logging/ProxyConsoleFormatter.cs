// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;

namespace DevProxy.Logging;

sealed class ProxyConsoleFormatter : ConsoleFormatter
{
    private const string _boxTopLeft = "\u256d ";
    private const string _boxLeft = "\u2502 ";
    private const string _boxBottomLeft = "\u2570 ";
    // used to align single-line messages
    private const string _boxSpacing = "  ";
    private readonly ConcurrentDictionary<int, List<RequestLog>> _requestLogs = [];
    private readonly ProxyConsoleFormatterOptions _options;
    const string labelSpacing = " ";
    public const string DefaultCategoryName = "devproxy";

    public ProxyConsoleFormatter(IOptions<ProxyConsoleFormatterOptions> options) : base(DefaultCategoryName)
    {
        // needed to properly required rounded corners in the box
        Console.OutputEncoding = Encoding.UTF8;
        _options = options.Value;
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (logEntry.State is RequestLog requestLog)
        {
            LogRequest(requestLog, logEntry.Category, scopeProvider, textWriter);
        }
        else
        {
            LogMessage(logEntry, scopeProvider, textWriter);
        }
    }

    private void LogRequest(RequestLog requestLog, string category, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var messageType = requestLog.MessageType;

        // don't log intercepted response to console
        if (messageType == MessageType.InterceptedResponse ||
            (messageType == MessageType.Skipped && !_options.ShowSkipMessages) ||
                (messageType == MessageType.Timestamp && !_options.ShowTimestamps)
            )
        {
            return;
        }

        var requestId = GetRequestIdScope(scopeProvider);

        if (requestId is not null)
        {
            if (messageType == MessageType.FinishedProcessingRequest)
            {
                // log all request logs for the request
                var currentRequestLogs = _requestLogs[requestId.Value];
                var lastIndex = currentRequestLogs.Count - 1;
                for (var i = 0; i < currentRequestLogs.Count; ++i)
                {
                    var log = currentRequestLogs[i];
                    WriteLogMessageBoxedWithInvertedLabels(log, textWriter, i == lastIndex);
                }
                _ = _requestLogs.Remove(requestId.Value, out _);
            }
            else
            {
                // buffer request logs until the request is finished processing
                requestLog.PluginName = category == DefaultCategoryName ? null : category;
                var value = _requestLogs.GetOrAdd(requestId.Value, []);
                value.Add(requestLog);
            }
        }
    }

    private static int? GetRequestIdScope(IExternalScopeProvider? scopeProvider)
    {
        int? requestId = null;

        scopeProvider?.ForEachScope((scope, state) =>
        {
            if (scope is Dictionary<string, object> dictionary)
            {
                if (dictionary.TryGetValue(nameof(requestId), out var req))
                {
                    requestId = (int)req;
                }
            }
        }, "");

        return requestId;
    }

    private void LogMessage<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        // regular messages
        var logLevel = logEntry.LogLevel;
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

        WriteMessageBoxedWithInvertedLabels(message, logLevel, scopeProvider, textWriter);

        if (logEntry.Exception is not null)
        {
            textWriter.Write($" Exception Details: {logEntry.Exception}");
        }

        textWriter.WriteLine();
    }

    private void WriteMessageBoxedWithInvertedLabels(string? message, LogLevel logLevel, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (message is null)
        {
            return;
        }

        var label = GetLogLevelString(logLevel);
        var (bgColor, fgColor) = GetLogLevelColor(logLevel);

        textWriter.WriteColoredMessage($" {label} ", bgColor, fgColor);
        textWriter.Write($"{labelSpacing}{_boxSpacing}{(logLevel == LogLevel.Debug ? $"[{DateTime.Now:T}] " : "")}");

        if (_options.IncludeScopes && scopeProvider is not null)
        {
            scopeProvider.ForEachScope((scope, state) =>
            {
                if (scope is null)
                {
                    return;
                }

                if (scope is string scopeString)
                {
                    textWriter.Write(scopeString);
                    textWriter.Write(": ");
                }
                else if (scope.GetType().Name == "FormattedLogValues")
                {
                    textWriter.Write(scope.ToString());
                    textWriter.Write(": ");
                }
            }, textWriter);
        }

        textWriter.Write(message);
    }

    private void WriteLogMessageBoxedWithInvertedLabels(RequestLog log, TextWriter textWriter, bool lastMessage = false)
    {
        var label = GetMessageTypeString(log.MessageType);
        var (bgColor, fgColor) = GetMessageTypeColor(log.MessageType);

        void writePluginName()
        {
            if (_options.IncludeScopes && log.PluginName is not null)
            {
                var pluginName = log.PluginName[(log.PluginName.LastIndexOf('.') + 1)..];
                if (pluginName == nameof(ProxyEngine))
                {
                    return;
                }
                textWriter.Write($"{pluginName}: ");
            }
        }

#pragma warning disable IDE0010
        switch (log.MessageType)
#pragma warning restore IDE0010
        {
            case MessageType.InterceptedRequest:
                // always one line (method + URL)
                // print label and top of the box
                textWriter.WriteLine();
                textWriter.WriteColoredMessage($" {label} ", bgColor, fgColor);
                textWriter.Write($"{(label.Length < 4 ? " " : "")}{labelSpacing}{_boxTopLeft}");
                writePluginName();
                textWriter.WriteLine(log.Message);
                break;
            default:
                textWriter.WriteColoredMessage($" {label} ", bgColor, fgColor);
                textWriter.Write($"{(label.Length < 4 ? " " : "")}{labelSpacing}{(lastMessage ? _boxBottomLeft : _boxLeft)}");
                writePluginName();
                textWriter.WriteLine(log.Message);
                break;
        }
    }

    // from https://github.com/dotnet/runtime/blob/198a2596229f69b8e02902bfb4ffc2a30be3b339/src/libraries/Microsoft.Extensions.Logging.Console/src/SimpleConsoleFormatter.cs#L154
    private static string GetLogLevelString(LogLevel logLevel)
    {
#pragma warning disable IDE0072
        return logLevel switch
#pragma warning restore IDE0072
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel))
        };
    }

    private static (ConsoleColor bg, ConsoleColor fg) GetLogLevelColor(LogLevel logLevel)
    {
        var fgColor = Console.ForegroundColor;
        var bgColor = Console.BackgroundColor;

#pragma warning disable IDE0072
        return logLevel switch
#pragma warning restore IDE0072
        {
            LogLevel.Information => (bgColor, ConsoleColor.Blue),
            LogLevel.Warning => (ConsoleColor.DarkYellow, fgColor),
            LogLevel.Error => (ConsoleColor.DarkRed, fgColor),
            LogLevel.Debug => (bgColor, ConsoleColor.Gray),
            LogLevel.Trace => (bgColor, ConsoleColor.Gray),
            _ => (bgColor, fgColor)
        };
    }

    private static string GetMessageTypeString(MessageType messageType)
    {
#pragma warning disable IDE0072
        return messageType switch
#pragma warning restore IDE0072
        {
            MessageType.InterceptedRequest => "req",
            MessageType.InterceptedResponse => "res",
            MessageType.PassedThrough => "pass",
            MessageType.Chaos => "oops",
            MessageType.Warning => "warn",
            MessageType.Mocked => "mock",
            MessageType.Failed => "fail",
            MessageType.Tip => "tip",
            MessageType.Skipped => "skip",
            MessageType.Processed => "proc",
            MessageType.Timestamp => "time",
            _ => "    "
        };
    }

    private static (ConsoleColor bg, ConsoleColor fg) GetMessageTypeColor(MessageType messageType)
    {
        var fgColor = Console.ForegroundColor;
        var bgColor = Console.BackgroundColor;

#pragma warning disable IDE0072
        return messageType switch
#pragma warning restore IDE0072
        {
            MessageType.InterceptedRequest => (bgColor, ConsoleColor.Gray),
            MessageType.PassedThrough => (ConsoleColor.Gray, fgColor),
            MessageType.Skipped => (bgColor, ConsoleColor.Gray),
            MessageType.Processed => (ConsoleColor.DarkGreen, fgColor),
            MessageType.Chaos => (ConsoleColor.DarkRed, fgColor),
            MessageType.Warning => (ConsoleColor.DarkYellow, fgColor),
            MessageType.Mocked => (ConsoleColor.DarkMagenta, fgColor),
            MessageType.Failed => (ConsoleColor.DarkRed, fgColor),
            MessageType.Tip => (ConsoleColor.DarkBlue, fgColor),
            MessageType.Timestamp => (bgColor, ConsoleColor.Gray),
            _ => (bgColor, fgColor)
        };
    }
}