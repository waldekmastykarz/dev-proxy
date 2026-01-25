// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace DevProxy.Logging;

/// <summary>
/// Logger provider for detached mode that writes logs to a file.
/// Similar to StdioFileLoggerProvider but with console output for critical messages.
/// </summary>
internal sealed class DetachedFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DetachedFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private bool _disposed;

    public string LogFilePath { get; }

    public DetachedFileLoggerProvider(string logFilePath)
    {
        LogFilePath = logFilePath;

        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new DetachedFileLogger(name, this));
    }

    internal void WriteLog(string categoryName, LogLevel logLevel, string message)
    {
        if (_disposed)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
#pragma warning disable IDE0072 // Add missing cases
        var level = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
#pragma warning restore IDE0072 // Add missing cases

        // Simplify category name (take last part after last dot)
        var simpleCategoryName = categoryName;
        var lastDotIndex = categoryName.LastIndexOf('.');
        if (lastDotIndex >= 0 && lastDotIndex < categoryName.Length - 1)
        {
            simpleCategoryName = categoryName[(lastDotIndex + 1)..];
        }

        using (_lock.EnterScope())
        {
            _writer.WriteLine($"[{timestamp}] {level}: {simpleCategoryName}: {message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loggers.Clear();
        _writer.Dispose();
    }
}

internal sealed class DetachedFileLogger(string categoryName, DetachedFileLoggerProvider provider) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly DetachedFileLoggerProvider _provider = provider;

    // Categories to filter out at lower log levels (only show Error+)
    private static readonly string[] _noisyPrefixes =
    [
        "Microsoft.Hosting.",
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
        "System."
    ];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        // Filter out noisy ASP.NET Core categories unless Error or higher
        foreach (var prefix in _noisyPrefixes)
        {
            if (_categoryName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return logLevel >= LogLevel.Error;
            }
        }

        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        _provider.WriteLog(_categoryName, logLevel, message);
    }
}