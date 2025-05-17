// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace DevProxy.Commands;

sealed class MsGraphDbCommand : Command
{
    private readonly ILogger _logger;

    public MsGraphDbCommand(ILogger<MsGraphDbCommand> logger) :
        base("msgraphdb", "Generate a local SQLite database with Microsoft Graph API metadata")
    {
        _logger = logger;
        ConfigureCommand();
    }

    private void ConfigureCommand()
    {
        this.SetHandler(GenerateMsGraphDbAsync);
    }

    private async Task GenerateMsGraphDbAsync(InvocationContext context)
    {
        _ = await MSGraphDbUtils.GenerateMSGraphDbAsync(_logger);
    }
}