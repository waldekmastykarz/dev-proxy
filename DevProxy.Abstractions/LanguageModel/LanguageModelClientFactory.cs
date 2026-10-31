// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DevProxy.Abstractions.LanguageModel;

public static class LanguageModelClientFactory
{
    public static ILanguageModelClient Create(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        var lmSection = configuration.GetSection("LanguageModel");
        var config = lmSection?.Get<LanguageModelConfiguration>() ?? new();

        var httpClient = CreateHttpClient();

        return config.Client switch
        {
            LanguageModelClient.Ollama => ActivatorUtilities.CreateInstance<OllamaLanguageModelClient>(serviceProvider, httpClient, config),
            LanguageModelClient.OpenAI => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider, httpClient, config),
            LanguageModelClient.AzureOpenAI => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider, httpClient, config),
            _ => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider, httpClient, config)
        };
    }

    // Redirects are disabled so that credentials (eg. the api-key header,
    // which HttpClient doesn't strip on redirect) aren't forwarded to
    // another origin
    internal static HttpClient CreateHttpClient() =>
#pragma warning disable CA2000 // The handler is owned by the long-lived HttpClient
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }, disposeHandler: true);
#pragma warning restore CA2000
}