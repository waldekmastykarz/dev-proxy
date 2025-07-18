// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace DevProxy.Plugins.Reporting;

public sealed class MinimalPermissionsGuidancePluginReportApiResult
{
    public required string ApiName { get; init; }
    public required IEnumerable<string> ExcessivePermissions { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    public required IEnumerable<string> Requests { get; init; }
    public required IEnumerable<string> TokenPermissions { get; init; }
    public required bool UsesMinimalPermissions { get; init; }
}

public sealed class MinimalPermissionsGuidancePluginReport
{
    public required IEnumerable<ApiPermissionError> Errors { get; init; }
    public required IEnumerable<MinimalPermissionsGuidancePluginReportApiResult> Results { get; init; }
    public required IEnumerable<string> UnmatchedRequests { get; init; }
}

public sealed class MinimalPermissionsGuidancePluginConfiguration
{
    public string? ApiSpecsFolderPath { get; set; }
}

public sealed class MinimalPermissionsGuidancePlugin(
    HttpClient httpClient,
    ILogger<MinimalPermissionsGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigSection) :
    BaseReportingPlugin<MinimalPermissionsGuidancePluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigSection)
{
    private Dictionary<string, OpenApiDocument>? _apiSpecsByUrl;

    public override string Name => nameof(MinimalPermissionsGuidancePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        if (string.IsNullOrWhiteSpace(Configuration.ApiSpecsFolderPath))
        {
            Enabled = false;
            throw new InvalidOperationException("ApiSpecsFolderPath is required.");
        }
        Configuration.ApiSpecsFolderPath = ProxyUtils.GetFullPath(Configuration.ApiSpecsFolderPath, ProxyConfiguration.ConfigFile);
        if (!Path.Exists(Configuration.ApiSpecsFolderPath))
        {
            Enabled = false;
            throw new InvalidOperationException($"ApiSpecsFolderPath '{Configuration.ApiSpecsFolderPath}' does not exist.");
        }
    }

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        var interceptedRequests = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedRequest &&
                !l.Message.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                l.Context?.Session is not null &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri) &&
                l.Context.Session.HttpClient.Request.Headers.Any(h => h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            );
        if (!interceptedRequests.Any())
        {
            Logger.LogRequest("No requests to process", MessageType.Skipped);
            return;
        }

        Logger.LogInformation("Checking if recorded API requests use minimal permissions as defined in API specs...");

        _apiSpecsByUrl ??= await LoadApiSpecsAsync(Configuration.ApiSpecsFolderPath!, cancellationToken);
        if (_apiSpecsByUrl is null || _apiSpecsByUrl.Count == 0)
        {
            Logger.LogWarning("No API definitions found in the specified folder.");
            return;
        }

        var (requestsByApiSpec, unmatchedApiSpecRequests) = GetRequestsByApiSpec(interceptedRequests, _apiSpecsByUrl);

        var errors = new List<ApiPermissionError>();
        var results = new List<MinimalPermissionsGuidancePluginReportApiResult>();
        var unmatchedRequests = new List<string>(
            unmatchedApiSpecRequests.Select(r => r.Message)
        );

        foreach (var (apiSpec, requests) in requestsByApiSpec)
        {
            var minimalPermissions = apiSpec.CheckMinimalPermissions(requests, Logger);

            var result = new MinimalPermissionsGuidancePluginReportApiResult
            {
                ApiName = GetApiName(minimalPermissions.OperationsFromRequests.Any() ?
                    minimalPermissions.OperationsFromRequests.First().OriginalUrl : null),
                Requests = [.. minimalPermissions.OperationsFromRequests
                    .Select(o => $"{o.Method} {o.OriginalUrl}")
                    .Distinct()],
                TokenPermissions = [.. minimalPermissions.TokenPermissions.Distinct()],
                MinimalPermissions = minimalPermissions.MinimalScopes,
                ExcessivePermissions = [.. minimalPermissions.TokenPermissions.Except(minimalPermissions.MinimalScopes)],
                UsesMinimalPermissions = !minimalPermissions.TokenPermissions.Except(minimalPermissions.MinimalScopes).Any()
            };
            results.Add(result);

            var unmatchedApiRequests = minimalPermissions.OperationsFromRequests
                .Where(o => minimalPermissions.UnmatchedOperations.Contains($"{o.Method} {o.TokenizedUrl}"))
                .Select(o => $"{o.Method} {o.OriginalUrl}");
            unmatchedRequests.AddRange(unmatchedApiRequests);
            errors.AddRange(minimalPermissions.Errors);

            if (result.UsesMinimalPermissions)
            {
                Logger.LogInformation(
                    "API {ApiName} is called with minimal permissions: {MinimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.MinimalPermissions)
                );
            }
            else
            {
                Logger.LogWarning(
                    "Calling API {ApiName} with excessive permissions: {ExcessivePermissions}. Minimal permissions are: {MinimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.ExcessivePermissions),
                    string.Join(", ", result.MinimalPermissions)
                );
            }

            if (unmatchedApiRequests.Any())
            {
                Logger.LogWarning(
                    "Unmatched requests for API {ApiName}:{NewLine}- {UnmatchedRequests}",
                    result.ApiName,
                    Environment.NewLine,
                    string.Join($"{Environment.NewLine}- ", unmatchedApiRequests)
                );
            }

            if (minimalPermissions.Errors.Any())
            {
                Logger.LogWarning(
                    "Errors for API {ApiName}:{NewLine}- {Errors}",
                    result.ApiName,
                    Environment.NewLine,
                    string.Join($"{Environment.NewLine}- ", minimalPermissions.Errors.Select(e => $"{e.Request}: {e.Error}"))
                );
            }
        }

        var report = new MinimalPermissionsGuidancePluginReport()
        {
            Results = [.. results],
            UnmatchedRequests = [.. unmatchedRequests],
            Errors = [.. errors]
        };

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    private async Task<Dictionary<string, OpenApiDocument>> LoadApiSpecsAsync(string apiSpecsFolderPath, CancellationToken cancellationToken)
    {
        var apiDefinitions = new Dictionary<string, OpenApiDocument>();
        foreach (var file in Directory.EnumerateFiles(apiSpecsFolderPath, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(file);
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping file '{File}' because it is not a JSON or YAML file", file);
                continue;
            }

            Logger.LogDebug("Processing file '{File}'...", file);
            try
            {
                var fileContents = await File.ReadAllTextAsync(file, cancellationToken);
                fileContents = ProxyUtils.ReplaceVariables(fileContents, ProxyConfiguration.Env, v => $"{{{v}}}");

                var apiDefinition = new OpenApiStringReader().Read(fileContents, out _);
                if (apiDefinition is null)
                {
                    continue;
                }
                if (apiDefinition.Servers is null || apiDefinition.Servers.Count == 0)
                {
                    Logger.LogDebug("No servers found in API definition file '{File}'", file);
                    continue;
                }
                foreach (var server in apiDefinition.Servers)
                {
                    if (server.Url is null)
                    {
                        Logger.LogDebug("No URL found for server '{Server}'", server.Description ?? "unnamed");
                        continue;
                    }
                    apiDefinitions[server.Url] = apiDefinition;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load API definition from file '{File}'", file);
            }
        }
        return apiDefinitions;
    }

    private (Dictionary<OpenApiDocument, List<RequestLog>> RequestsByApiSpec, IEnumerable<RequestLog> UnmatchedRequests) GetRequestsByApiSpec(IEnumerable<RequestLog> interceptedRequests, Dictionary<string, OpenApiDocument> apiSpecsByUrl)
    {
        var unmatchedRequests = new List<RequestLog>();
        var requestsByApiSpec = new Dictionary<OpenApiDocument, List<RequestLog>>();
        foreach (var request in interceptedRequests)
        {
            var url = request.Message.Split(' ')[1];
            Logger.LogDebug("Matching request {RequestUrl} to API specs...", url);

            var matchingKey = apiSpecsByUrl.Keys.FirstOrDefault(url.StartsWith);
            if (matchingKey is null)
            {
                Logger.LogDebug("No matching API spec found for {RequestUrl}", url);
                unmatchedRequests.Add(request);
                continue;
            }

            if (!requestsByApiSpec.TryGetValue(apiSpecsByUrl[matchingKey], out var value))
            {
                value = [];
                requestsByApiSpec[apiSpecsByUrl[matchingKey]] = value;
            }

            value.Add(request);
        }

        return (requestsByApiSpec, unmatchedRequests);
    }

    private static string GetApiName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Unknown";
        }

        var uri = new Uri(url);
        return uri.Authority;
    }
}
