// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace DevProxy.Plugins.Reporters;

public class JsonReporter(
    ILogger<JsonReporter> logger,
    ISet<UrlToWatch> urlsToWatch) : BaseReporter(logger, urlsToWatch)
{
    public override string Name => nameof(JsonReporter);
    private string _fileExtension = ".json";
    public override string FileExtension => _fileExtension;

    private readonly Dictionary<Type, Func<object, object>> _transformers = new()
    {
        { typeof(ExecutionSummaryPluginReportByUrl), TransformExecutionSummary },
        { typeof(ExecutionSummaryPluginReportByMessageType), TransformExecutionSummary },
        { typeof(UrlDiscoveryPluginReport), TransformUrlDiscoveryReport }
    };

    protected override string GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Serializing report {ReportKey}...", report.Key);

        var reportData = report.Value;
        var reportType = reportData.GetType();
        _fileExtension = reportType.Name == nameof(UrlDiscoveryPluginReport) ? ".jsonc" : ".json";

        if (_transformers.TryGetValue(reportType, out var transform))
        {
            Logger.LogDebug("Transforming {ReportType} using {Transform}...", reportType.Name, transform.Method.Name);
            reportData = transform(reportData);
        }
        else
        {
            Logger.LogDebug("No transformer found for {ReportType}", reportType.Name);
        }

        if (reportData is string strVal)
        {
            Logger.LogDebug("{ReportKey} is a string. Checking if it's JSON...", report.Key);

            try
            {
                _ = JsonSerializer.Deserialize<object>(strVal, ProxyUtils.JsonSerializerOptions);
                Logger.LogDebug("{ReportKey} is already JSON, ignore", report.Key);
                // already JSON, ignore
                return strVal;
            }
            catch
            {
                Logger.LogDebug("{ReportKey} is not JSON, serializing...", report.Key);
            }
        }

        return JsonSerializer.Serialize(reportData, ProxyUtils.JsonSerializerOptions);
    }

    private static object TransformExecutionSummary(object report)
    {
        var executionSummaryReport = (ExecutionSummaryPluginReportBase)report;
        return executionSummaryReport.Data;
    }

    private static object TransformUrlDiscoveryReport(object report)
    {
        var urlDiscoveryPluginReport = (UrlDiscoveryPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine("{")
            .AppendLine("  // Wildcards")
            .AppendLine("  // ")
            .AppendLine("  // You can use wildcards to catch multiple URLs with the same pattern.")
            .AppendLine("  // For example, you can use the following URL pattern to catch all API requests to")
            .AppendLine("  // JSON Placeholder API:")
            .AppendLine("  // ")
            .AppendLine("  // https://jsonplaceholder.typicode.com/*")
            .AppendLine("  // ")
            .AppendLine("  // Excluding URLs")
            .AppendLine("  // ")
            .AppendLine("  // You can exclude URLs with ! to prevent them from being intercepted.")
            .AppendLine("  // For example, you can exclude the URL https://jsonplaceholder.typicode.com/authors")
            .AppendLine("  // by using the following URL pattern:")
            .AppendLine("  // ")
            .AppendLine("  // !https://jsonplaceholder.typicode.com/authors")
            .AppendLine("  // https://jsonplaceholder.typicode.com/*")
            .AppendLine("  \"urlsToWatch\": [")
            .AppendJoin($",{Environment.NewLine}", urlDiscoveryPluginReport.Data.Select(u => $"    \"{u}\""))
            .AppendLine("")
            .AppendLine("  ]")
            .AppendLine("}");

        return sb.ToString();
    }
}