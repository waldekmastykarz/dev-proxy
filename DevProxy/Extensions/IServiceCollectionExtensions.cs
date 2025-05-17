// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Proxy;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureDevProxyServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        string[] args)
    {
        _ = services.AddControllers();
        _ = services
            .AddApplicationServices(configuration, args)
            .AddHostedService<ProxyEngine>()
            .AddEndpointsApiExplorer()
            .AddSwaggerGen()
            .Configure<RouteOptions>(options => options.LowercaseUrls = true);

        return services;
    }

    static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        ConfigurationManager configuration,
        string[] args)
    {
        _ = services
            .AddSingleton((IConfigurationRoot)configuration)
            .AddSingleton<IProxyConfiguration, ProxyConfiguration>()
            .AddSingleton<IProxyStateController, ProxyStateController>()
            .AddSingleton<IProxyState, ProxyState>()
            .AddSingleton(sp => ProxyEngine.Certificate!)
            .AddSingleton(sp => LanguageModelClientFactory.Create(sp, configuration))
            .AddSingleton<ProxyEngine>()
            .AddSingleton<DevProxyCommand>();

        var isDiscover = args.Contains("--discover", StringComparer.OrdinalIgnoreCase);
        _ = services.AddPlugins(configuration, isDiscover);

        return services;
    }
}