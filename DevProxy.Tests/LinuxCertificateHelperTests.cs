// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using Xunit;

namespace DevProxy.Tests;

public class LinuxCertificateHelperTests
{
    private static Func<string, string?> Executables(params string[] names) =>
        name => names.Contains(name) ? $"/usr/sbin/{name}" : null;

    private static Func<string, bool> Directories(params string[] dirs) => dirs.Contains;

    [Fact]
    public void DetectTrustStore_Debian()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates"),
            Executables("update-ca-certificates"));

        Assert.NotNull(store);
        Assert.Equal("/usr/local/share/ca-certificates/devproxy-ca.crt", store.CertificatePath);
        Assert.Equal("/usr/sbin/update-ca-certificates", store.UpdateCommand);
        Assert.Empty(store.UpdateArguments);
        Assert.Equal(["--fresh"], store.RemoveUpdateArguments);
    }

    [Fact]
    public void DetectTrustStore_Fedora()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/etc/pki/ca-trust/source/anchors", "/etc/ca-certificates/trust-source/anchors"),
            Executables("update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/etc/pki/ca-trust/source/anchors/devproxy-ca.crt", store.CertificatePath);
        Assert.Equal("/usr/sbin/update-ca-trust", store.UpdateCommand);
        Assert.Equal(["extract"], store.UpdateArguments);
    }

    [Fact]
    public void DetectTrustStore_Arch()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/etc/ca-certificates/trust-source/anchors"),
            Executables("update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/etc/ca-certificates/trust-source/anchors/devproxy-ca.crt", store.CertificatePath);
    }

    [Fact]
    public void DetectTrustStore_PrefersDebianWhenBothToolsExist()
    {
        var store = LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates", "/etc/pki/ca-trust/source/anchors"),
            Executables("update-ca-certificates", "update-ca-trust"));

        Assert.NotNull(store);
        Assert.Equal("/usr/local/share/ca-certificates", store.AnchorDirectory);
    }

    [Fact]
    public void DetectTrustStore_UnknownDistro_ReturnsNull()
    {
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(Directories(), Executables()));
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(
            Directories("/usr/local/share/ca-certificates"), Executables()));
        Assert.Null(LinuxCertificateHelper.DetectTrustStore(
            Directories(), Executables("update-ca-trust", "update-ca-certificates")));
    }

    [Fact]
    public void Elevate_Root_RunsDirectly()
    {
        var command = new LinuxCommand("update-ca-certificates", []);

        var elevated = LinuxCertificateHelper.Elevate(command, isRoot: true, isInteractive: false);

        Assert.Same(command, elevated);
    }

    [Fact]
    public void Elevate_Interactive_UsesSudo()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("install", ["-m", "0644", "a", "b"]), isRoot: false, isInteractive: true);

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["--", "install", "-m", "0644", "a", "b"], elevated.Arguments);
    }

    [Fact]
    public void Elevate_NonInteractive_UsesSudoWithoutPrompt()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("rm", ["-f", "x"]), isRoot: false, isInteractive: false);

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["-n", "--", "rm", "-f", "x"], elevated.Arguments);
    }

    [Fact]
    public void GetTrustCommands_InstallsThenRefreshes()
    {
        var store = new LinuxTrustStore("Fedora/RHEL", "/etc/pki/ca-trust/source/anchors", "/usr/bin/update-ca-trust", ["extract"], ["extract"]);

        var commands = LinuxCertificateHelper.GetTrustCommands(store, "/tmp/cert.crt");

        Assert.Equal(2, commands.Count);
        Assert.Equal("install", commands[0].FileName);
        Assert.Equal(["-D", "-m", "0644", "/tmp/cert.crt", "/etc/pki/ca-trust/source/anchors/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/bin/update-ca-trust", commands[1].FileName);
        Assert.Equal(["extract"], commands[1].Arguments);
    }

    [Fact]
    public void GetUntrustCommands_RemovesThenRefreshes()
    {
        var store = new LinuxTrustStore("Debian/Ubuntu", "/usr/local/share/ca-certificates", "/usr/sbin/update-ca-certificates", [], ["--fresh"]);

        var commands = LinuxCertificateHelper.GetUntrustCommands(store);

        Assert.Equal(2, commands.Count);
        Assert.Equal("rm", commands[0].FileName);
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/sbin/update-ca-certificates", commands[1].FileName);
        Assert.Equal(["--fresh"], commands[1].Arguments);
    }
}
