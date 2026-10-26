// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace DevProxy.Tests;

public class LoggingConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureDevProxyLogging_Interactive_HonorsShowTimestamps(bool showTimestamps)
    {
        using var configuration = new ConfigurationManager();
        configuration["showTimestamps"] = showTimestamps.ToString();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ConfigureDevProxyLogging(configuration, new DevProxyConfigOptions()));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProxyConsoleFormatterOptions>>().Value;

        Assert.Equal(showTimestamps, options.ShowTimestamps);
    }
}
