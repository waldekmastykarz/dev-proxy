// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Generation;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporters;

public class MarkdownReporter(
    ILogger<MarkdownReporter> logger,
    ISet<UrlToWatch> urlsToWatch) : BaseReporter(logger, urlsToWatch)
{
    public override string Name => nameof(MarkdownReporter);
    public override string FileExtension => ".md";

    private readonly Dictionary<Type, Func<object, string?>> _transformers = new()
    {
        { typeof(ApiCenterMinimalPermissionsPluginReport), TransformApiCenterMinimalPermissionsReport },
        { typeof(ApiCenterOnboardingPluginReport), TransformApiCenterOnboardingReport },
        { typeof(ApiCenterProductionVersionPluginReport), TransformApiCenterProductionVersionReport },
        { typeof(ExecutionSummaryPluginReportByUrl), TransformExecutionSummaryByUrl },
        { typeof(ExecutionSummaryPluginReportByMessageType), TransformExecutionSummaryByMessageType },
        { typeof(HttpFileGeneratorPlugin), TransformHttpFileGeneratorReport },
        { typeof(GraphMinimalPermissionsGuidancePluginReport), TransformGraphMinimalPermissionsGuidanceReport },
        { typeof(GraphMinimalPermissionsPluginReport), TransformGraphMinimalPermissionsReport },
        { typeof(MinimalCsomPermissionsPluginReport), TransformMinimalCsomPermissionsReport },
        { typeof(MinimalPermissionsPluginReport), TransformMinimalPermissionsReport },
        { typeof(OpenApiSpecGeneratorPluginReport), TransformOpenApiSpecGeneratorReport },
        { typeof(UrlDiscoveryPluginReport), TransformUrlDiscoveryReport }
    };

    private const string _requestsInterceptedMessage = "Requests intercepted";
    private const string _requestsPassedThroughMessage = "Requests passed through";

    protected override string? GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Transforming {Report}...", report.Key);

        var reportType = report.Value.GetType();

        if (_transformers.TryGetValue(reportType, out var transform))
        {
            Logger.LogDebug("Transforming {ReportType} using {Transform}...", reportType.Name, transform.Method.Name);

            return transform(report.Value);
        }
        else
        {
            Logger.LogDebug("No transformer found for {ReportType}", reportType.Name);
            return null;
        }
    }

    private static string? TransformApiCenterOnboardingReport(object report)
    {
        var apiCenterOnboardingReport = (ApiCenterOnboardingPluginReport)report;

        if (apiCenterOnboardingReport.NewApis.Any() &&
            apiCenterOnboardingReport.ExistingApis.Any())
        {
            return null;
        }

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Azure API Center onboarding report")
            .AppendLine();

        if (apiCenterOnboardingReport.NewApis.Any())
        {
            var apisPerSchemeAndHost = apiCenterOnboardingReport.NewApis.GroupBy(x =>
            {
                var u = new Uri(x.Url);
                return u.GetLeftPart(UriPartial.Authority);
            });

            _ = sb.AppendLine("## ⚠️ New APIs that aren't registered in Azure API Center")
                .AppendLine();

            foreach (var apiPerHost in apisPerSchemeAndHost)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {apiPerHost.Key}")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, apiPerHost.Select(a => $"- {a.Method} {a.Url}"))
                    .AppendLine();
            }

            _ = sb.AppendLine();
        }

        if (apiCenterOnboardingReport.ExistingApis.Any())
        {
            _ = sb.AppendLine("## ✅ APIs that are already registered in Azure API Center")
                .AppendLine()
                .AppendLine("API|Definition ID|Operation ID")
                .AppendLine("---|------------|------------")
                .AppendJoin(Environment.NewLine, apiCenterOnboardingReport.ExistingApis.Select(a => $"{a.MethodAndUrl}|{a.ApiDefinitionId}|{a.OperationId}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    private static string? TransformApiCenterMinimalPermissionsReport(object report)
    {
        var apiCenterMinimalPermissionsReport = (ApiCenterMinimalPermissionsPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine("# Azure API Center minimal permissions report")
            .AppendLine();

        _ = sb.AppendLine("## ℹ️ Summary")
            .AppendLine()
            .AppendLine("<table>")
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🔎 APIs inspected</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.Results.Count(), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🔎 Requests inspected</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.Results.Sum(r => r.Requests.Count()), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>✅ APIs called using minimal permissions</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.Results.Count(r => r.UsesMinimalPermissions), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🛑 APIs called using excessive permissions</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.Results.Count(r => !r.UsesMinimalPermissions), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>⚠️ Unmatched requests</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.UnmatchedRequests.Count(), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🛑 Errors</td><td align=\"right\">{0}</td></tr>{1}", apiCenterMinimalPermissionsReport.Errors.Count(), Environment.NewLine)
            .AppendLine("</table>")
            .AppendLine();

        _ = sb.AppendLine("## 🔌 APIs")
            .AppendLine();

        if (apiCenterMinimalPermissionsReport.Results.Any())
        {
            foreach (var apiResult in apiCenterMinimalPermissionsReport.Results)
            {
                _ = sb.AppendFormat(CultureInfo.InvariantCulture, "### {0}{1}", apiResult.ApiName, Environment.NewLine)
                    .AppendLine()
                    .AppendFormat(CultureInfo.InvariantCulture, apiResult.UsesMinimalPermissions ? "✅ Called using minimal permissions{0}" : "🛑 Called using excessive permissions{0}", Environment.NewLine)
                    .AppendLine()
                    .AppendLine("#### Permissions")
                    .AppendLine()
                    .AppendFormat(CultureInfo.InvariantCulture, "- Minimal permissions: {0}{1}", string.Join(", ", apiResult.MinimalPermissions.Order().Select(p => $"`{p}`")), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Permissions on the token: {0}{1}", string.Join(", ", apiResult.TokenPermissions.Order().Select(p => $"`{p}`")), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Excessive permissions: {0}{1}", apiResult.ExcessivePermissions.Any() ? string.Join(", ", apiResult.ExcessivePermissions.Order().Select(p => $"`{p}`")) : "none", Environment.NewLine)
                    .AppendLine()
                    .AppendLine("#### Requests")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}")).AppendLine()
                    .AppendLine();
            }
        }
        else
        {
            _ = sb.AppendLine("No APIs found.")
                .AppendLine();
        }

        _ = sb.AppendLine("## ⚠️ Unmatched requests")
            .AppendLine();

        _ = apiCenterMinimalPermissionsReport.UnmatchedRequests.Any()
            ? sb.AppendLine("The following requests were not matched to any API in API Center:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiCenterMinimalPermissionsReport.UnmatchedRequests
                    .Select(r => $"- {r}").Order()).AppendLine()
                .AppendLine()
            : sb.AppendLine("No unmatched requests found.")
                .AppendLine();

        _ = sb.AppendLine("## 🛑 Errors")
            .AppendLine();

        _ = apiCenterMinimalPermissionsReport.Errors.Any()
            ? sb.AppendLine("The following errors occurred while determining minimal permissions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiCenterMinimalPermissionsReport.Errors
                    .OrderBy(o => o.Request)
                    .Select(e => $"- `{e.Request}`: {e.Error}")).AppendLine()
                .AppendLine()
            : sb.AppendLine("No errors occurred.");

        return sb.ToString();
    }

    private static string? TransformApiCenterProductionVersionReport(object report)
    {
        static string getReadableApiStatus(ApiCenterProductionVersionPluginReportItemStatus status)
        {
            return status switch
            {
                ApiCenterProductionVersionPluginReportItemStatus.NotRegistered => "🛑 Not registered",
                ApiCenterProductionVersionPluginReportItemStatus.NonProduction => "⚠️ Non-production",
                ApiCenterProductionVersionPluginReportItemStatus.Production => "✅ Production",
                _ => "Unknown"
            };
        }

        var apiCenterProductionVersionReport = (ApiCenterProductionVersionPluginReport)report;

        var groupedPerStatus = apiCenterProductionVersionReport
            .GroupBy(a => a.Status)
            .OrderBy(g => (int)g.Key);

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Azure API Center lifecycle report")
            .AppendLine();

        foreach (var group in groupedPerStatus)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## {getReadableApiStatus(group.Key)} APIs")
                .AppendLine();

            _ = group.Key == ApiCenterProductionVersionPluginReportItemStatus.NonProduction
                ? sb.AppendLine("API|Recommendation")
                    .AppendLine("---|------------")
                    .AppendJoin(Environment.NewLine, group
                        .OrderBy(a => a.Url)
                        .Select(a => $"{a.Method} {a.Url}|{a.Recommendation ?? ""}"))
                    .AppendLine()
                : sb.AppendJoin(Environment.NewLine, group
                        .OrderBy(a => a.Url)
                        .Select(a => $"- {a.Method} {a.Url}"))
                    .AppendLine();

            _ = sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string TransformExecutionSummaryByMessageType(object report)
    {
        var executionSummaryReport = (ExecutionSummaryPluginReportByMessageType)report;

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Dev Proxy execution summary")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}")
            .AppendLine();

        _ = sb.AppendLine("## Message types");

        var data = executionSummaryReport.Data;
        var sortedMessageTypes = data.Keys.OrderBy(k => k);
        foreach (var messageType in sortedMessageTypes)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"### {messageType}");

            if (messageType is _requestsInterceptedMessage or
                _requestsPassedThroughMessage)
            {
                _ = sb.AppendLine();

                var sortedMethodAndUrls = data[messageType][messageType].Keys.OrderBy(k => k);
                foreach (var methodAndUrl in sortedMethodAndUrls)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({data[messageType][messageType][methodAndUrl]}) {methodAndUrl}");
                }
            }
            else
            {
                var sortedMessages = data[messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine()
                        .AppendLine(CultureInfo.InvariantCulture, $"#### {message}")
                        .AppendLine();

                    var sortedMethodAndUrls = data[messageType][message].Keys.OrderBy(k => k);
                    foreach (var methodAndUrl in sortedMethodAndUrls)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({data[messageType][message][methodAndUrl]}) {methodAndUrl}");
                    }
                }
            }
        }

        AddExecutionSummaryReportSummary(executionSummaryReport.Logs, sb);
        _ = sb.AppendLine();

        return sb.ToString();
    }

    private static string TransformExecutionSummaryByUrl(object report)
    {
        var executionSummaryReport = (ExecutionSummaryPluginReportByUrl)report;

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Dev Proxy execution summary")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}")
            .AppendLine();

        _ = sb.AppendLine("## Requests");

        var data = executionSummaryReport.Data;
        var sortedMethodAndUrls = data.Keys.OrderBy(k => k);
        foreach (var methodAndUrl in sortedMethodAndUrls)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"### {methodAndUrl}");

            var sortedMessageTypes = data[methodAndUrl].Keys.OrderBy(k => k);
            foreach (var messageType in sortedMessageTypes)
            {
                _ = sb.AppendLine()
                    .AppendLine(CultureInfo.InvariantCulture, $"#### {messageType}")
                    .AppendLine();

                var sortedMessages = data[methodAndUrl][messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({data[methodAndUrl][messageType][message]}) {message}");
                }
            }
        }

        AddExecutionSummaryReportSummary(executionSummaryReport.Logs, sb);
        _ = sb.AppendLine();

        return sb.ToString();
    }

    private static void AddExecutionSummaryReportSummary(IEnumerable<RequestLog> requestLogs, StringBuilder sb)
    {
        static string getReadableMessageTypeForSummary(MessageType messageType)
        {
#pragma warning disable IDE0072
            return messageType switch
#pragma warning restore IDE0072
            {
                MessageType.Chaos => "Requests with chaos",
                MessageType.Failed => "Failures",
                MessageType.InterceptedRequest => _requestsInterceptedMessage,
                MessageType.Mocked => "Requests mocked",
                MessageType.PassedThrough => _requestsPassedThroughMessage,
                MessageType.Tip => "Tips",
                MessageType.Warning => "Warnings",
                _ => "Unknown"
            };
        }

        var data = requestLogs
          .Where(log => log.MessageType != MessageType.InterceptedResponse)
          .Select(log => getReadableMessageTypeForSummary(log.MessageType))
          .OrderBy(log => log)
          .GroupBy(log => log)
          .ToDictionary(group => group.Key, group => group.Count());

        _ = sb.AppendLine()
            .AppendLine("## Summary")
            .AppendLine()
            .AppendLine("Category|Count")
            .AppendLine("--------|----:");

        foreach (var messageType in data.Keys)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{messageType}|{data[messageType]}");
        }
    }

    private static string? TransformGraphMinimalPermissionsGuidanceReport(object report)
    {
        var minimalPermissionsGuidanceReport = (GraphMinimalPermissionsGuidancePluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine("# Minimal permissions report")
            .AppendLine();

        void transformPermissionsInfo(GraphMinimalPermissionsInfo permissionsInfo, string type)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## Minimal {type} permissions")
                .AppendLine()
                .AppendLine("### Operations")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.Operations.Select(o => $"- {o.Method} {o.Endpoint}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Minimal permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.MinimalPermissions.Select(p => $"- {p}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Permissions on the token")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.PermissionsFromTheToken.Select(p => $"- {p}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Excessive permissions");

            _ = permissionsInfo.ExcessPermissions.Any()
                ? sb.AppendLine()
                    .AppendLine("The following permissions included in token are unnecessary:")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, permissionsInfo.ExcessPermissions.Select(p => $"- {p}"))
                    .AppendLine()
                : sb.AppendLine()
                    .AppendLine("The token has the minimal permissions required.");

            _ = sb.AppendLine();
        }

        if (minimalPermissionsGuidanceReport.DelegatedPermissions is not null)
        {
            transformPermissionsInfo(minimalPermissionsGuidanceReport.DelegatedPermissions, "delegated");
        }
        if (minimalPermissionsGuidanceReport.ApplicationPermissions is not null)
        {
            transformPermissionsInfo(minimalPermissionsGuidanceReport.ApplicationPermissions, "application");
        }

        if (minimalPermissionsGuidanceReport.ExcludedPermissions is not null &&
            minimalPermissionsGuidanceReport.ExcludedPermissions.Any())
        {
            _ = sb.AppendLine("## Excluded permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, minimalPermissionsGuidanceReport.ExcludedPermissions.Select(p => $"- {p}"))
                .AppendLine();
        }

        return sb.ToString();
    }

    private static string? TransformGraphMinimalPermissionsReport(object report)
    {
        var minimalPermissionsReport = (GraphMinimalPermissionsPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"# Minimal {minimalPermissionsReport.PermissionsType.ToString().ToLowerInvariant()} permissions report")
            .AppendLine();

        _ = sb.AppendLine("## Requests")
            .AppendLine()
            .AppendJoin(Environment.NewLine, minimalPermissionsReport.Requests.Select(r => $"- {r.Method} {r.Url}"))
            .AppendLine()

            .AppendLine()
            .AppendLine("## Minimal permissions")
            .AppendLine()
            .AppendJoin(Environment.NewLine, minimalPermissionsReport.MinimalPermissions.Select(p => $"- {p}"))
            .AppendLine();

        if (minimalPermissionsReport.Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("## 🛑 Errors")
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following URLs:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, minimalPermissionsReport.Errors.Select(e => $"- {e}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    private static string? TransformOpenApiSpecGeneratorReport(object report)
    {
        var openApiSpecGeneratorReport = (OpenApiSpecGeneratorPluginReport)report;

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Generated OpenAPI specs")
            .AppendLine()
            .AppendLine("Server URL|File name")
            .AppendLine("---|---------")
            .AppendJoin(Environment.NewLine, openApiSpecGeneratorReport.Select(r => $"{r.ServerUrl}|{r.FileName}"))
            .AppendLine()
            .AppendLine();

        return sb.ToString();
    }

    private static string? TransformUrlDiscoveryReport(object report)
    {
        var urlDiscoveryPluginReport = (UrlDiscoveryPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine("## Wildcards")
            .AppendLine("")
            .AppendLine("You can use wildcards to catch multiple URLs with the same pattern.")
            .AppendLine("For example, you can use the following URL pattern to catch all API requests to")
            .AppendLine("JSON Placeholder API:")
            .AppendLine("")
            .AppendLine("```text")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("```")
            .AppendLine("")
            .AppendLine("## Excluding URLs")
            .AppendLine("")
            .AppendLine("You can exclude URLs with ! to prevent them from being intercepted.")
            .AppendLine("For example, you can exclude the URL `https://jsonplaceholder.typicode.com/authors`")
            .AppendLine("by using the following URL pattern:")
            .AppendLine("")
            .AppendLine("```text")
            .AppendLine("!https://jsonplaceholder.typicode.com/authors")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("```")
            .AppendLine("")
            .AppendLine("Intercepted URLs:")
            .AppendLine()
            .AppendLine("```text")

            .AppendJoin(Environment.NewLine, urlDiscoveryPluginReport.Data)

            .AppendLine("")
            .AppendLine("```");
        return sb.ToString();
    }

    private static string? TransformHttpFileGeneratorReport(object report)
    {
        var httpFileGeneratorReport = (HttpFileGeneratorPluginReport)report;

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Generated HTTP files")
            .AppendLine()
            .AppendJoin(Environment.NewLine, $"- {httpFileGeneratorReport}")
            .AppendLine()
            .AppendLine();

        return sb.ToString();
    }

    private static string? TransformMinimalPermissionsReport(object report)
    {
        var minimalPermissionsReport = (MinimalPermissionsPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine($"# Minimal permissions report");

        foreach (var apiResult in minimalPermissionsReport.Results)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"## API {apiResult.ApiName}:")

                .AppendLine("### Requests")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}"))
                .AppendLine()

                .AppendLine()
                .AppendLine("### Minimal permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.MinimalPermissions.Select(p => $"- {p}"))
                .AppendLine();
        }

        if (minimalPermissionsReport.Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("## 🛑 Errors")
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following URLs:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, minimalPermissionsReport.Errors.Select(e => $"- {e}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    private static string? TransformMinimalCsomPermissionsReport(object report)
    {
        var minimalPermissionsReport = (MinimalCsomPermissionsPluginReport)report;

        var sb = new StringBuilder();
        _ = sb.AppendLine($"# Minimal CSOM permissions report")
            .AppendLine()

            .AppendLine("## Actions")
            .AppendLine()
            .AppendJoin(Environment.NewLine, minimalPermissionsReport.Actions.Select(a => $"- {a}"))
            .AppendLine()

            .AppendLine()
            .AppendLine("## Minimal permissions")
            .AppendLine()
            .AppendJoin(Environment.NewLine, minimalPermissionsReport.MinimalPermissions.Select(p => $"- {p}"))
            .AppendLine();

        if (minimalPermissionsReport.Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("## 🛑 Errors")
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following actions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, minimalPermissionsReport.Errors.Select(e => $"- {e}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }
}