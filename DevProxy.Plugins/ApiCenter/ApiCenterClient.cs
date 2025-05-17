// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Azure.Core;
using Azure.Core.Diagnostics;
using Azure.Identity;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Handlers;
using DevProxy.Plugins.Models.ApiCenter;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Tracing;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DevProxy.Plugins.ApiCenter;

internal sealed class ApiCenterClientConfiguration
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
}

internal sealed class ApiCenterClient : IDisposable
{
    private readonly ApiCenterClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly TokenCredential _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions()
    {
        ExcludeInteractiveBrowserCredential = true,
        // fails on Ubuntu
        ExcludeSharedTokenCacheCredential = true
    });
    private readonly HttpClient _httpClient;
    private readonly AuthenticationDelegatingHandler _authenticationHandler;
    private readonly string[] _scopes = ["https://management.azure.com/.default"];

    internal ApiCenterClient(ApiCenterClientConfiguration configuration, ILogger<ApiCenterClient> logger)
    {
        if (string.IsNullOrEmpty(configuration.SubscriptionId))
        {
            throw new ArgumentException($"Specify {nameof(ApiCenterClientConfiguration.SubscriptionId)} in the configuration.");
        }
        if (string.IsNullOrEmpty(configuration.ResourceGroupName))
        {
            throw new ArgumentException($"Specify {nameof(ApiCenterClientConfiguration.ResourceGroupName)} in the configuration.");
        }
        if (string.IsNullOrEmpty(configuration.ServiceName))
        {
            throw new ArgumentException($"Specify {nameof(ApiCenterClientConfiguration.ServiceName)} in the configuration.");
        }
        if (string.IsNullOrEmpty(configuration.WorkspaceName))
        {
            throw new ArgumentException($"Specify {nameof(ApiCenterClientConfiguration.WorkspaceName)} in the configuration.");
        }

        // load configuration from env vars
        if (configuration.SubscriptionId.StartsWith('@'))
        {
            configuration.SubscriptionId = Environment.GetEnvironmentVariable(configuration.SubscriptionId[1..]) ?? configuration.SubscriptionId;
        }
        if (configuration.ResourceGroupName.StartsWith('@'))
        {
            configuration.ResourceGroupName = Environment.GetEnvironmentVariable(configuration.ResourceGroupName[1..]) ?? configuration.ResourceGroupName;
        }
        if (configuration.ServiceName.StartsWith('@'))
        {
            configuration.ServiceName = Environment.GetEnvironmentVariable(configuration.ServiceName[1..]) ?? configuration.ServiceName;
        }
        if (configuration.WorkspaceName.StartsWith('@'))
        {
            configuration.WorkspaceName = Environment.GetEnvironmentVariable(configuration.WorkspaceName[1..]) ?? configuration.WorkspaceName;
        }

        _configuration = configuration;
        _logger = logger;

        _authenticationHandler = new AuthenticationDelegatingHandler(_credential, _scopes)
        {
            InnerHandler = new TracingDelegatingHandler(logger)
            {
                InnerHandler = new HttpClientHandler()
            }
        };
        _httpClient = new HttpClient(_authenticationHandler);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            using var azureLogger = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Verbose);
        }
    }

    internal Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        _authenticationHandler.GetAccessTokenAsync(cancellationToken);

    internal async Task<Api[]?> GetApisAsync()
    {
        _logger.LogInformation("Loading APIs from API Center...");

        var res = await _httpClient.GetStringAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis?api-version=2024-03-01");
        var collection = JsonSerializer.Deserialize<Collection<Api>>(res, ProxyUtils.JsonSerializerOptions);
        return collection?.Value;
    }

    internal async Task<Api?> PutApiAsync(Api api, string apiName)
    {
        using var content = new StringContent(JsonSerializer.Serialize(api, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
        var res = await _httpClient.PutAsync($"https://management.azure.com/subscriptions/{_configuration.SubscriptionId}/resourceGroups/{_configuration.ResourceGroupName}/providers/Microsoft.ApiCenter/services/{_configuration.ServiceName}/workspaces/{_configuration.WorkspaceName}/apis/{apiName}?api-version=2024-03-01", content);

        var resContent = await res.Content.ReadAsStringAsync();
        _logger.LogDebug("{Response}", resContent);

        return res.IsSuccessStatusCode ?
            JsonSerializer.Deserialize<Api>(resContent, ProxyUtils.JsonSerializerOptions) :
            null;
    }

    internal async Task<ApiDeployment[]?> GetDeploymentsAsync(string apiId)
    {
        _logger.LogDebug("Loading API deployments for {ApiName}...", apiId);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com{apiId}/deployments?api-version=2024-03-01");
        var collection = JsonSerializer.Deserialize<Collection<ApiDeployment>>(res, ProxyUtils.JsonSerializerOptions);
        return collection?.Value;
    }

    internal async Task<ApiVersion[]?> GetVersionsAsync(string apiId)
    {
        _logger.LogDebug("Loading API versions for {ApiName}...", apiId);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com{apiId}/versions?api-version=2024-03-01");
        var collection = JsonSerializer.Deserialize<Collection<ApiVersion>>(res, ProxyUtils.JsonSerializerOptions);
        return collection?.Value;
    }

    internal async Task<ApiVersion?> PutVersionAsync(ApiVersion apiVersion, string apiId, string apiName)
    {
        using var content = new StringContent(JsonSerializer.Serialize(apiVersion, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
        var res = await _httpClient.PutAsync($"https://management.azure.com{apiId}/versions/{apiName}?api-version=2024-03-01", content);

        var resContent = await res.Content.ReadAsStringAsync();
        _logger.LogDebug("{Response}", resContent);

        return res.IsSuccessStatusCode ?
            JsonSerializer.Deserialize<ApiVersion>(resContent, ProxyUtils.JsonSerializerOptions) :
            null;
    }

    internal async Task<ApiDefinition[]?> GetDefinitionsAsync(string versionId)
    {
        _logger.LogDebug("Loading API definitions for version {Id}...", versionId);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com{versionId}/definitions?api-version=2024-03-01");
        var collection = JsonSerializer.Deserialize<Collection<ApiDefinition>>(res, ProxyUtils.JsonSerializerOptions);
        return collection?.Value;
    }

    internal async Task<ApiDefinition?> GetDefinitionAsync(string definitionId)
    {
        _logger.LogDebug("Loading API definition {Id}...", definitionId);

        var res = await _httpClient.GetStringAsync($"https://management.azure.com{definitionId}?api-version=2024-03-01");
        return JsonSerializer.Deserialize<ApiDefinition>(res, ProxyUtils.JsonSerializerOptions);
    }

    internal async Task<ApiDefinition?> PutDefinitionAsync(ApiDefinition apiDefinition, string apiVersionId, string definitionName)
    {
        using var content = new StringContent(JsonSerializer.Serialize(apiDefinition, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
        var res = await _httpClient.PutAsync($"https://management.azure.com{apiVersionId}/definitions/{definitionName}?api-version=2024-03-01", content);

        var resContent = await res.Content.ReadAsStringAsync();
        _logger.LogDebug("{Response}", resContent);

        return res.IsSuccessStatusCode ?
            JsonSerializer.Deserialize<ApiDefinition>(resContent, ProxyUtils.JsonSerializerOptions) :
            null;
    }

    internal async Task<HttpResponseMessage> PostImportSpecificationAsync(ApiSpecImport apiSpecImport, string definitionId)
    {
        using var content = new StringContent(JsonSerializer.Serialize(apiSpecImport, ProxyUtils.JsonSerializerOptions), Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync($"https://management.azure.com{definitionId}/importSpecification?api-version=2024-03-01", content);
    }

    internal async Task<ApiSpecExportResult?> PostExportSpecificationAsync(string definitionId)
    {
        var definitionRes = await _httpClient.PostAsync($"https://management.azure.com{definitionId}/exportSpecification?api-version=2024-03-01", null);
        return await definitionRes.Content.ReadFromJsonAsync<ApiSpecExportResult>(ProxyUtils.JsonSerializerOptions);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _authenticationHandler.Dispose();
        GC.SuppressFinalize(this);
    }
}