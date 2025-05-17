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

        var lmSection = configuration.GetSection("LanguageModelClient");
        var config = lmSection?.Get<LanguageModelConfiguration>();

        return config?.Client switch
        {
            LanguageModelClient.Ollama => ActivatorUtilities.CreateInstance<OllamaLanguageModelClient>(serviceProvider),
            LanguageModelClient.OpenAI => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider),
            _ => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider)
        };
    }
}