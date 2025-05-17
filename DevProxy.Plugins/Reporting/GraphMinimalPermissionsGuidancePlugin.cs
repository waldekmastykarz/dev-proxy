﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using DevProxy.Plugins.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;

namespace DevProxy.Plugins.Reporting;

public sealed class GraphMinimalPermissionsGuidancePluginReport
{
    public GraphMinimalPermissionsInfo? DelegatedPermissions { get; set; }
    public GraphMinimalPermissionsInfo? ApplicationPermissions { get; set; }
    public IEnumerable<string>? ExcludedPermissions { get; set; }
}

public sealed class GraphMinimalPermissionsOperationInfo
{
    public string Method { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}

public sealed class GraphMinimalPermissionsInfo
{
    public IEnumerable<string> MinimalPermissions { get; set; } = [];
    public IEnumerable<string> PermissionsFromTheToken { get; set; } = [];
    public IEnumerable<string> ExcessPermissions { get; set; } = [];
    public IEnumerable<GraphMinimalPermissionsOperationInfo> Operations { get; set; } = [];
}

public sealed class GraphMinimalPermissionsGuidancePluginConfiguration
{
    public IEnumerable<string>? PermissionsToExclude { get; set; }
}

public sealed class GraphMinimalPermissionsGuidancePlugin(
    ILogger<GraphMinimalPermissionsGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<GraphMinimalPermissionsGuidancePluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public override string Name => nameof(GraphMinimalPermissionsGuidancePlugin);

    public override async Task InitializeAsync(InitArgs e)
    {
        await base.InitializeAsync(e);

        // we need to do it this way because .NET doesn't distinguish between
        // an empty array and a null value and we want to be able to tell
        // if the user hasn't specified a value and we should use the default
        // set or if they have specified an empty array and we shouldn't exclude
        // any permissions
        if (Configuration.PermissionsToExclude is null)
        {
            Configuration.PermissionsToExclude = ["profile", "openid", "offline_access", "email"];
        }
        else
        {
            // remove empty strings
            Configuration.PermissionsToExclude = Configuration.PermissionsToExclude.Where(p => !string.IsNullOrEmpty(p));
        }
    }

    public override async Task AfterRecordingStopAsync(RecordingArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.AfterRecordingStopAsync(e);

        if (!e.RequestLogs.Any())
        {
            return;
        }

        var methodAndUrlComparer = new MethodAndUrlComparer();
        var delegatedEndpoints = new List<(string method, string url)>();
        var applicationEndpoints = new List<(string method, string url)>();

        // scope for delegated permissions
        IEnumerable<string> scopesToEvaluate = [];
        // roles for application permissions
        IEnumerable<string> rolesToEvaluate = [];

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedRequest)
            {
                continue;
            }

            var methodAndUrlString = request.Message;
            var methodAndUrl = GetMethodAndUrl(methodAndUrlString);
            if (methodAndUrl.method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, methodAndUrl.url))
            {
                Logger.LogDebug("URL not matched: {Url}", methodAndUrl.url);
                continue;
            }

            var requestsFromBatch = Array.Empty<(string method, string url)>();

            var uri = new Uri(methodAndUrl.url);
            if (!ProxyUtils.IsGraphUrl(uri))
            {
                continue;
            }

            if (ProxyUtils.IsGraphBatchUrl(uri))
            {
                var graphVersion = ProxyUtils.IsGraphBetaUrl(uri) ? "beta" : "v1.0";
                requestsFromBatch = GetRequestsFromBatch(request.Context?.Session.HttpClient.Request.BodyString!, graphVersion, uri.Host);
            }
            else
            {
                methodAndUrl = (methodAndUrl.method, GetTokenizedUrl(methodAndUrl.url));
            }

            var (type, permissions) = GetPermissionsAndType(request);
            if (type == GraphPermissionsType.Delegated)
            {
                // use the scopes from the last request in case the app is using incremental consent
                scopesToEvaluate = permissions;

                if (ProxyUtils.IsGraphBatchUrl(uri))
                {
                    delegatedEndpoints.AddRange(requestsFromBatch);
                }
                else
                {
                    delegatedEndpoints.Add(methodAndUrl);
                }
            }
            else
            {
                // skip empty roles which are returned in case we couldn't get permissions information
                // 
                // application permissions are always the same because they come from app reg
                // so we can just use the first request that has them
                if (permissions.Any() && !rolesToEvaluate.Any())
                {
                    rolesToEvaluate = permissions;

                    if (ProxyUtils.IsGraphBatchUrl(uri))
                    {
                        applicationEndpoints.AddRange(requestsFromBatch);
                    }
                    else
                    {
                        applicationEndpoints.Add(methodAndUrl);
                    }
                }
            }
        }

        // Remove duplicates
        delegatedEndpoints = [.. delegatedEndpoints.Distinct(methodAndUrlComparer)];
        applicationEndpoints = [.. applicationEndpoints.Distinct(methodAndUrlComparer)];

        if (delegatedEndpoints.Count == 0 && applicationEndpoints.Count == 0)
        {
            return;
        }

        var report = new GraphMinimalPermissionsGuidancePluginReport
        {
            ExcludedPermissions = Configuration.PermissionsToExclude
        };

        Logger.LogWarning("This plugin is in preview and may not return the correct results.\r\nPlease review the permissions and test your app before using them in production.\r\nIf you have any feedback, please open an issue at https://aka.ms/devproxy/issue.\r\n");

        if (Configuration.PermissionsToExclude is not null &&
            Configuration.PermissionsToExclude.Any())
        {
            Logger.LogInformation("Excluding the following permissions: {Permissions}", string.Join(", ", Configuration.PermissionsToExclude));
        }

        if (delegatedEndpoints.Count > 0)
        {
            var delegatedPermissionsInfo = new GraphMinimalPermissionsInfo();
            report.DelegatedPermissions = delegatedPermissionsInfo;

            Logger.LogInformation("Evaluating delegated permissions for: {Endpoints}", string.Join(", ", delegatedEndpoints.Select(e => $"{e.method} {e.url}")));

            await EvaluateMinimalScopesAsync(delegatedEndpoints, scopesToEvaluate, GraphPermissionsType.Delegated, delegatedPermissionsInfo);
        }

        if (applicationEndpoints.Count > 0)
        {
            var applicationPermissionsInfo = new GraphMinimalPermissionsInfo();
            report.ApplicationPermissions = applicationPermissionsInfo;

            Logger.LogInformation("Evaluating application permissions for: {Endpoints}", string.Join(", ", applicationEndpoints.Select(e => $"{e.method} {e.url}")));

            await EvaluateMinimalScopesAsync(applicationEndpoints, rolesToEvaluate, GraphPermissionsType.Application, applicationPermissionsInfo);
        }

        StoreReport(report, e);
    }

    private static (string method, string url)[] GetRequestsFromBatch(string batchBody, string graphVersion, string graphHostName)
    {
        var requests = new List<(string method, string url)>();

        if (string.IsNullOrEmpty(batchBody))
        {
            return [.. requests];
        }

        try
        {
            var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(batchBody, ProxyUtils.JsonSerializerOptions);
            if (batch == null)
            {
                return [.. requests];
            }

            foreach (var request in batch.Requests)
            {
                try
                {
                    var method = request.Method;
                    var url = request.Url;
                    var absoluteUrl = $"https://{graphHostName}/{graphVersion}{url}";
                    requests.Add((method, GetTokenizedUrl(absoluteUrl)));
                }
                catch { }
            }
        }
        catch { }

        return [.. requests];
    }

    /// <summary>
    /// Returns permissions and type (delegated or application) from the access token
    /// used on the request.
    /// If it can't get the permissions, returns PermissionType.Application
    /// and an empty array
    /// </summary>
    private static (GraphPermissionsType type, IEnumerable<string> permissions) GetPermissionsAndType(RequestLog request)
    {
        var authHeader = request.Context?.Session.HttpClient.Request.Headers.GetFirstHeader("Authorization");
        if (authHeader == null)
        {
            return (GraphPermissionsType.Application, []);
        }

        var token = authHeader.Value.Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
        var tokenChunks = token.Split('.');
        if (tokenChunks.Length != 3)
        {
            return (GraphPermissionsType.Application, []);
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtSecurityToken = handler.ReadJwtToken(token);

            var scopeClaim = jwtSecurityToken.Claims.FirstOrDefault(c => c.Type == "scp");
            if (scopeClaim == null)
            {
                // possibly an application token
                // roles is an array so we need to handle it differently
                var roles = jwtSecurityToken.Claims
                  .Where(c => c.Type == "roles")
                  .Select(c => c.Value);
                if (!roles.Any())
                {
                    return (GraphPermissionsType.Application, []);
                }
                else
                {
                    return (GraphPermissionsType.Application, roles);
                }
            }
            else
            {
                return (GraphPermissionsType.Delegated, scopeClaim.Value.Split(' '));
            }
        }
        catch
        {
            return (GraphPermissionsType.Application, []);
        }
    }

    private async Task EvaluateMinimalScopesAsync(IEnumerable<(string method, string url)> endpoints, IEnumerable<string> permissionsFromAccessToken, GraphPermissionsType scopeType, GraphMinimalPermissionsInfo permissionsInfo)
    {
        var payload = endpoints.Select(e => new GraphRequestInfo { Method = e.method, Url = e.url });

        permissionsInfo.Operations = [.. endpoints.Select(e => new GraphMinimalPermissionsOperationInfo
        {
            Method = e.method,
            Endpoint = e.url
        })];
        permissionsInfo.PermissionsFromTheToken = permissionsFromAccessToken;

        try
        {
            var url = $"https://devxapi-func-prod-eastus.azurewebsites.net/permissions?scopeType={GraphUtils.GetScopeTypeString(scopeType)}";
            using var client = new HttpClient();
            var stringPayload = JsonSerializer.Serialize(payload, ProxyUtils.JsonSerializerOptions);
            Logger.LogDebug("Calling {Url} with payload{NewLine}{Payload}", url, Environment.NewLine, stringPayload);

            var response = await client.PostAsJsonAsync(url, payload);
            var content = await response.Content.ReadAsStringAsync();

            Logger.LogDebug("Response:{NewLine}{Content}", Environment.NewLine, content);

            var resultsAndErrors = JsonSerializer.Deserialize<GraphResultsAndErrors>(content, ProxyUtils.JsonSerializerOptions);
            var minimalPermissions = resultsAndErrors?.Results?.Select(p => p.Value) ?? [];
            var errors = resultsAndErrors?.Errors?.Select(e => $"- {e.Url} ({e.Message})") ?? [];

            if (scopeType == GraphPermissionsType.Delegated)
            {
                minimalPermissions = await GraphUtils.UpdateUserScopesAsync(minimalPermissions, endpoints, scopeType, Logger);
            }

            if (minimalPermissions.Any())
            {
                var excessPermissions = permissionsFromAccessToken
                    .Except(Configuration.PermissionsToExclude ?? [])
                    .Where(p => !minimalPermissions.Contains(p));

                permissionsInfo.MinimalPermissions = minimalPermissions;
                permissionsInfo.ExcessPermissions = excessPermissions;

                Logger.LogInformation("Minimal permissions: {MinimalPermissions}", string.Join(", ", minimalPermissions));
                Logger.LogInformation("Permissions on the token: {TokenPermissions}", string.Join(", ", permissionsFromAccessToken));

                if (excessPermissions.Any())
                {
                    Logger.LogWarning("The following permissions are unnecessary: {Permissions}", string.Join(", ", excessPermissions));
                }
                else
                {
                    Logger.LogInformation("The token has the minimal permissions required.");
                }
            }
            if (errors.Any())
            {
                Logger.LogError("Couldn't determine minimal permissions for the following URLs: {Errors}", string.Join(", ", errors));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while retrieving minimal permissions: {Message}", ex.Message);
        }
    }

    private static (string method, string url) GetMethodAndUrl(string message)
    {
        var info = message.Split(" ");
        if (info.Length > 2)
        {
            info = [info[0], string.Join(" ", info.Skip(1))];
        }
        return (method: info[0], url: info[1]);
    }

    private static string GetTokenizedUrl(string absoluteUrl)
    {
        var sanitizedUrl = ProxyUtils.SanitizeUrl(absoluteUrl);
        return "/" + string.Join("", new Uri(sanitizedUrl).Segments.Skip(2).Select(Uri.UnescapeDataString));
    }
}
