// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        Assert.Equal("/usr/local/share/ca-certificates/dev-proxy-ca.crt", store.LegacyCertificatePath);
        Assert.Equal("/etc/ssl/certs/ca-certificates.crt", store.BundlePath);
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
        Assert.Equal("/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", store.BundlePath);
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
        Assert.Equal("/etc/ca-certificates/extracted/tls-ca-bundle.pem", store.BundlePath);
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
    public void Elevate_Root_RunsAbsoluteCommandDirectly()
    {
        var command = new LinuxCommand("/usr/sbin/update-ca-certificates", []);

        var elevated = LinuxCertificateHelper.Elevate(command, isRoot: true, isInteractive: false, _ => null);

        Assert.Same(command, elevated);
    }

    [Fact]
    public void Elevate_Root_ResolvesBareCommandFromSystemDirectories()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("rm", ["-f", "x"]), isRoot: true, isInteractive: false, name => $"/usr/bin/{name}");

        Assert.NotNull(elevated);
        Assert.Equal("/usr/bin/rm", elevated.FileName);
        Assert.Equal(["-f", "x"], elevated.Arguments);
    }

    [Fact]
    public void Elevate_Root_UnresolvedBareCommand_ReturnsNull()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("install", []), isRoot: true, isInteractive: false, _ => null);

        Assert.Null(elevated);
    }

    [Fact]
    public void Elevate_Interactive_UsesSudo()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("install", ["-m", "0644", "a", "b"]), isRoot: false, isInteractive: true)!;

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["--", "install", "-m", "0644", "a", "b"], elevated.Arguments);
    }

    [Fact]
    public void Elevate_NonInteractive_UsesSudoWithoutPrompt()
    {
        var elevated = LinuxCertificateHelper.Elevate(
            new("rm", ["-f", "x"]), isRoot: false, isInteractive: false)!;

        Assert.Equal("sudo", elevated.FileName);
        Assert.Equal(["-n", "--", "rm", "-f", "x"], elevated.Arguments);
    }

    private static readonly LinuxTrustStore _fedora = new("Fedora/RHEL", "/etc/pki/ca-trust/source/anchors", "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", "/usr/bin/update-ca-trust", ["extract"], ["extract"]);
    private static readonly LinuxTrustStore _debian = new("Debian/Ubuntu", "/usr/local/share/ca-certificates", "/etc/ssl/certs/ca-certificates.crt", "/usr/sbin/update-ca-certificates", [], ["--fresh"]);

    [Fact]
    public void GetNssDatabaseDirectory_PrefersExistingLegacyDatabase()
    {
        Assert.Equal("/home/u/.pki/nssdb",
            LinuxCertificateHelper.GetNssDatabaseDirectory("/home/u", Directories("/home/u/.pki/nssdb", "/home/u/.local/share/pki/nssdb")));
    }

    [Fact]
    public void GetNssDatabaseDirectory_DefaultsToXdgLocation()
    {
        Assert.Equal("/home/u/.local/share/pki/nssdb",
            LinuxCertificateHelper.GetNssDatabaseDirectory("/home/u", Directories()));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void ShouldSkipTrust_OnlyWhenSudoCantRunWithoutPrompt(bool isRoot, bool isInteractive, bool canSudo, bool expected)
    {
        var probed = false;
        var skip = LinuxCertificateHelper.ShouldSkipTrust(isRoot, isInteractive, () => { probed = true; return canSudo; });

        Assert.Equal(expected, skip);
        Assert.Equal(!isRoot && !isInteractive, probed);
    }

    [Fact]
    public void GetSudoCheckCommand_ListsExactCommandWithoutPrompt()
    {
        var check = LinuxCertificateHelper.GetSudoCheckCommand(new("install", ["-m", "0644", "a", "b"]));

        Assert.Equal("sudo", check.FileName);
        Assert.Equal(["-n", "-l", "--", "install", "-m", "0644", "a", "b"], check.Arguments);
    }

    [Fact]
    public void FindSystemExecutable_IgnoresPath()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var name = $"devproxy-test-{Guid.NewGuid():N}";
            File.WriteAllText(Path.Combine(dir.FullName, name), string.Empty);

            Assert.NotNull(LinuxCertificateHelper.FindExecutable(name, dir.FullName));
            Assert.Null(LinuxCertificateHelper.FindSystemExecutable(name));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void GetTrustCommands_InstallsThenRefreshes()
    {
        var store = _fedora;

        var commands = LinuxCertificateHelper.GetTrustCommands(store, "/tmp/cert.crt", removeLegacy: false);

        Assert.Equal(2, commands.Count);
        Assert.Equal("install", commands[0].FileName);
        Assert.Equal(["-D", "-m", "0644", "/tmp/cert.crt", "/etc/pki/ca-trust/source/anchors/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/bin/update-ca-trust", commands[1].FileName);
        Assert.Equal(["extract"], commands[1].Arguments);
    }

    [Fact]
    public void GetTrustCommands_RemovesLegacyAnchorBeforeRefresh()
    {
        var commands = LinuxCertificateHelper.GetTrustCommands(_debian, "/tmp/cert.crt", removeLegacy: true);

        Assert.Equal(["install", "rm", "/usr/sbin/update-ca-certificates"], commands.Select(c => c.FileName));
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/dev-proxy-ca.crt"], commands[1].Arguments);
    }

    [Fact]
    public void GetUntrustCommands_RemovesLegacyAnchorBeforeRefresh()
    {
        var commands = LinuxCertificateHelper.GetUntrustCommands(_debian, removeLegacy: true);

        Assert.Equal(["rm", "rm", "/usr/sbin/update-ca-certificates"], commands.Select(c => c.FileName));
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/dev-proxy-ca.crt"], commands[1].Arguments);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void NeedsSystemStoreRemoval(bool anchorExists, bool legacyMatches, bool bundleContains, bool expected) =>
        Assert.Equal(expected, LinuxCertificateHelper.NeedsSystemStoreRemoval(anchorExists, legacyMatches, bundleContains));

    [Fact]
    public void FileContainsCertificate_MatchesOnlyTheSameCertificate()
    {
        using var root = CreateCertificate("CN=Dev Proxy CA");
        using var other = CreateCertificate("CN=Other CA");
        var bundle = Path.GetTempFileName();
        try
        {
            File.WriteAllText(bundle, other.ExportCertificatePem() + "\n" + root.ExportCertificatePem() + "\n");
            Assert.True(LinuxCertificateHelper.FileContainsCertificate(bundle, root));

            File.WriteAllText(bundle, other.ExportCertificatePem());
            Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));

            File.WriteAllText(bundle, "not a certificate");
            Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));
        }
        finally
        {
            File.Delete(bundle);
        }

        Assert.False(LinuxCertificateHelper.FileContainsCertificate(bundle, root));
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void GetUntrustCommands_RemovesThenRefreshes()
    {
        var store = _debian;

        var commands = LinuxCertificateHelper.GetUntrustCommands(store, removeLegacy: false);

        Assert.Equal(2, commands.Count);
        Assert.Equal("rm", commands[0].FileName);
        Assert.Equal(["-f", "/usr/local/share/ca-certificates/devproxy-ca.crt"], commands[0].Arguments);
        Assert.Equal("/usr/sbin/update-ca-certificates", commands[1].FileName);
        Assert.Equal(["--fresh"], commands[1].Arguments);
    }
}
