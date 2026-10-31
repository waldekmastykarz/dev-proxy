// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using DevProxy.State;
using Microsoft.AspNetCore.Connections;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DevProxy.Tests;

public class PortConflictTests
{
    [Fact]
    public void IsPortAvailable_ReturnsFalse_WhenPortIsInUse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortConflict.IsPortAvailable("127.0.0.1", port));
    }

    [Fact]
    public void IsPortAvailable_ReturnsTrue_WhenPortIsFree()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Assert.True(PortConflict.IsPortAvailable("127.0.0.1", port));
    }

    [Theory]
    [InlineData("127.0.0.1", 0)]
    [InlineData("not-an-ip", 8000)]
    public void IsPortAvailable_LeavesUncheckableInputToKestrel(string ipAddress, int port)
    {
        Assert.True(PortConflict.IsPortAvailable(ipAddress, port));
    }

    [Fact]
    public void FindOwningInstance_MatchesProxyPort()
    {
        var states = new[]
        {
            new ProxyInstanceState { Pid = 1, Port = 8001, ApiUrl = "http://127.0.0.1:8898/" },
            new ProxyInstanceState { Pid = 2, Port = 8000, ApiUrl = "http://127.0.0.1:8897/" }
        };

        Assert.Equal(2, PortConflict.FindOwningInstance(states, PortKind.Proxy, 8000, excludePid: 0)?.Pid);
    }

    [Fact]
    public void FindOwningInstance_MatchesApiPort()
    {
        var states = new[]
        {
            new ProxyInstanceState { Pid = 1, Port = 8001, ApiUrl = "http://127.0.0.1:8898/" },
            new ProxyInstanceState { Pid = 2, Port = 8000, ApiUrl = "http://127.0.0.1:8897" }
        };

        Assert.Equal(2, PortConflict.FindOwningInstance(states, PortKind.Api, 8897, excludePid: 0)?.Pid);
    }

    [Fact]
    public void FindOwningInstance_IgnoresCurrentProcessAndPortZero()
    {
        var states = new[] { new ProxyInstanceState { Pid = 5, Port = 8000, ApiUrl = "http://127.0.0.1:0" } };

        Assert.Null(PortConflict.FindOwningInstance(states, PortKind.Proxy, 8000, excludePid: 5));
        Assert.Null(PortConflict.FindOwningInstance(states, PortKind.Api, 0, excludePid: 0));
    }

    [Fact]
    public void FormatMessage_DevProxyInstance_SuggestsStopAndPortZero()
    {
        var message = PortConflict.FormatMessage(PortKind.Api, 8897, new ProxyInstanceState { Pid = 42 });

        Assert.Contains("API port 8897 is already in use by another Dev Proxy instance (PID: 42).", message, StringComparison.Ordinal);
        Assert.Contains("devproxy stop --pid 42", message, StringComparison.Ordinal);
        Assert.Contains("devproxy --api-port 0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessage_OtherApplication_SuggestsPortZeroAndConfig()
    {
        var message = PortConflict.FormatMessage(PortKind.Proxy, 8000, owner: null);

        Assert.Contains("Port 8000 is already in use by another application.", message, StringComparison.Ordinal);
        Assert.Contains("devproxy --port 0", message, StringComparison.Ordinal);
        Assert.Contains("'port' in your devproxyrc.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("devproxy stop", message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAddressInUse_DetectsWrappedKestrelException()
    {
        var ex = new IOException("Failed to bind", new AddressInUseException("Address already in use"));

        Assert.True(PortConflict.IsAddressInUse(ex));
        Assert.False(PortConflict.IsAddressInUse(new IOException("other")));
    }
}
