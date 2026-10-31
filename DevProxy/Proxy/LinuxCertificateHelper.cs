// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace DevProxy.Proxy;

/// <summary>A distro-specific system CA trust store: where anchors go and how to refresh it.</summary>
internal sealed record LinuxTrustStore(
    string Name,
    string AnchorDirectory,
    string BundlePath,
    string UpdateCommand,
    IReadOnlyList<string> UpdateArguments,
    IReadOnlyList<string> RemoveUpdateArguments)
{
    public string CertificatePath => Path.Combine(AnchorDirectory, LinuxCertificateHelper.CertificateFileName);

    /// <summary>Anchor that earlier manual setup instructions told users to create.</summary>
    public string LegacyCertificatePath => Path.Combine(AnchorDirectory, LinuxCertificateHelper.LegacyCertificateFileName);
}

/// <summary>A process invocation (file name + argument list, no shell).</summary>
internal sealed record LinuxCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => string.Join(' ', [FileName, .. Arguments]);
}

internal static class LinuxCertificateHelper
{
    internal const string CertificateFileName = "devproxy-ca.crt";
    internal const string LegacyCertificateFileName = "dev-proxy-ca.crt";
    internal const string NssNickname = "Dev Proxy CA";

    private static readonly string[] _sbinDirectories = ["/usr/local/sbin", "/usr/sbin", "/sbin"];

    // Root-owned system directories. Tools that run as root are resolved only from here,
    // never from the caller's PATH, so a user-writable PATH entry can't be executed via sudo.
    private static readonly string[] _systemDirectories = ["/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>
    /// Detects the system CA trust store. Supports Debian/Ubuntu (update-ca-certificates),
    /// Fedora/RHEL (update-ca-trust) and Arch (update-ca-trust with trust-source anchors).
    /// Returns null when the distro isn't recognized. <paramref name="findExecutable"/> must
    /// only resolve from trusted system directories because the result runs as root.
    /// </summary>
    internal static LinuxTrustStore? DetectTrustStore(
        Func<string, bool> directoryExists,
        Func<string, string?> findExecutable)
    {
        var updateCaCertificates = findExecutable("update-ca-certificates");
        if (updateCaCertificates is not null && directoryExists("/usr/local/share/ca-certificates"))
        {
            return new(
                "Debian/Ubuntu",
                "/usr/local/share/ca-certificates",
                "/etc/ssl/certs/ca-certificates.crt",
                updateCaCertificates,
                [],
                ["--fresh"]);
        }

        var updateCaTrust = findExecutable("update-ca-trust");
        if (updateCaTrust is not null)
        {
            if (directoryExists("/etc/pki/ca-trust/source/anchors"))
            {
                return new("Fedora/RHEL", "/etc/pki/ca-trust/source/anchors", "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", updateCaTrust, ["extract"], ["extract"]);
            }

            if (directoryExists("/etc/ca-certificates/trust-source/anchors"))
            {
                return new("Arch", "/etc/ca-certificates/trust-source/anchors", "/etc/ca-certificates/extracted/tls-ca-bundle.pem", updateCaTrust, ["extract"], ["extract"]);
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps a command that needs root. Runs it directly when already root; otherwise via
    /// sudo, with <c>-n</c> when there's no terminal to prompt on so it fails instead of hanging.
    /// </summary>
    internal static LinuxCommand Elevate(LinuxCommand command, bool isRoot, bool isInteractive)
    {
        if (isRoot)
        {
            return command;
        }

        List<string> args = isInteractive ? [] : ["-n"];
        args.Add("--");
        args.Add(command.FileName);
        args.AddRange(command.Arguments);
        return new("sudo", args);
    }

    // install/rm are passed by name so sudo resolves them through its secure_path.
    internal static IReadOnlyList<LinuxCommand> GetTrustCommands(LinuxTrustStore store, string pemFilePath, bool removeLegacy)
    {
        List<LinuxCommand> commands = [new("install", ["-D", "-m", "0644", pemFilePath, store.CertificatePath])];
        if (removeLegacy)
        {
            commands.Add(new("rm", ["-f", store.LegacyCertificatePath]));
        }
        commands.Add(new(store.UpdateCommand, store.UpdateArguments));
        return commands;
    }

    internal static IReadOnlyList<LinuxCommand> GetUntrustCommands(LinuxTrustStore store, bool removeLegacy)
    {
        List<LinuxCommand> commands = [new("rm", ["-f", store.CertificatePath])];
        if (removeLegacy)
        {
            commands.Add(new("rm", ["-f", store.LegacyCertificatePath]));
        }
        commands.Add(new(store.UpdateCommand, store.RemoveUpdateArguments));
        return commands;
    }

    /// <summary>
    /// Whether <c>cert remove</c> has anything to undo. The bundle check covers a previous
    /// removal where the anchor was deleted but refreshing the store failed.
    /// </summary>
    internal static bool NeedsSystemStoreRemoval(bool anchorExists, bool legacyAnchorMatches, bool bundleContainsCertificate) =>
        anchorExists || legacyAnchorMatches || bundleContainsCertificate;

    /// <summary>Whether the PEM file at <paramref name="path"/> holds <paramref name="certificate"/>.</summary>
    internal static bool FileContainsCertificate(string path, X509Certificate2 certificate)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPemFile(path);
            try
            {
                return certificates.Any(c => c.RawData.AsSpan().SequenceEqual(certificate.RawData));
            }
            finally
            {
                foreach (var c in certificates)
                {
                    c.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    internal static string GetNssDatabaseDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");

    internal static LinuxTrustStore? DetectTrustStore() => DetectTrustStore(Directory.Exists, FindSystemExecutable);

    internal static bool TrustCertificate(X509Certificate2 certificate, LinuxTrustStore store, ILogger logger)
    {
        var pemFilePath = ExportCertificateToPem(certificate, logger);
        if (pemFilePath is null)
        {
            return false;
        }

        try
        {
            var (isRoot, isInteractive) = GetContext();
            logger.LogInformation(
                "Adding the Dev Proxy root certificate to the {Distro} system certificate store ({Path}).{SudoHint}",
                store.Name,
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            var removeLegacy = FileContainsCertificate(store.LegacyCertificatePath, certificate);
            foreach (var command in GetTrustCommands(store, pemFilePath, removeLegacy))
            {
                if (!Run(Elevate(command, isRoot, isInteractive), logger, redirectOutput: false))
                {
                    if (!isRoot && !isInteractive)
                    {
                        logger.LogError("If sudo requires a password, run 'devproxy cert ensure' in an interactive terminal.");
                    }
                    return false;
                }
            }
            logger.LogInformation("Certificate added to the system certificate store.");

            TrustInNssDatabase(pemFilePath, logger);
            return true;
        }
        finally
        {
            CleanupFile(pemFilePath);
        }
    }

    internal static void RemoveTrustedCertificate(X509Certificate2 certificate, ILogger logger)
    {
        var store = DetectTrustStore();
        var removeLegacy = store is not null && FileContainsCertificate(store.LegacyCertificatePath, certificate);
        if (store is null)
        {
            logger.LogWarning("Remove the Dev Proxy root certificate from your trust store manually.");
        }
        else if (NeedsSystemStoreRemoval(
            File.Exists(store.CertificatePath),
            removeLegacy,
            FileContainsCertificate(store.BundlePath, certificate)))
        {
            var (isRoot, isInteractive) = GetContext();
            logger.LogInformation(
                "Removing the Dev Proxy root certificate from the system certificate store ({Path}).{SudoHint}",
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            var removed = true;
            foreach (var command in GetUntrustCommands(store, removeLegacy))
            {
                if (!Run(Elevate(command, isRoot, isInteractive), logger, redirectOutput: false))
                {
                    removed = false;
                    break;
                }
            }
            if (removed)
            {
                logger.LogInformation("Certificate removed from the system certificate store.");
            }
        }

        RemoveFromNssDatabase(logger);
    }

    private static void TrustInNssDatabase(string pemFilePath, ILogger logger)
    {
        var certutil = FindExecutable("certutil");
        if (certutil is null)
        {
            logger.LogInformation(
                "To have Chrome and other NSS-based apps trust the certificate, install certutil (libnss3-tools on Debian/Ubuntu, nss-tools on Fedora/RHEL, nss on Arch) and run 'devproxy cert ensure' again.");
            return;
        }

        var nssDir = GetNssDatabaseDirectory();
        var db = $"sql:{nssDir}";
        if (!Directory.Exists(nssDir))
        {
            _ = Directory.CreateDirectory(nssDir);
            if (!Run(new(certutil, ["-N", "-d", db, "--empty-password"]), logger, redirectOutput: true))
            {
                return;
            }
        }

        // Replace any previous Dev Proxy root (it's regenerated per machine) so re-runs are idempotent.
        _ = Run(new(certutil, ["-D", "-d", db, "-n", NssNickname]), logger, redirectOutput: true, logFailure: false);
        if (Run(new(certutil, ["-A", "-d", db, "-n", NssNickname, "-t", "C,,", "-i", pemFilePath]), logger, redirectOutput: true))
        {
            logger.LogInformation("Certificate added to the NSS database ({Path}).", nssDir);
        }
    }

    private static void RemoveFromNssDatabase(ILogger logger)
    {
        var certutil = FindExecutable("certutil");
        var nssDir = GetNssDatabaseDirectory();
        if (certutil is null || !Directory.Exists(nssDir))
        {
            return;
        }

        if (Run(new(certutil, ["-D", "-d", $"sql:{nssDir}", "-n", NssNickname]), logger, redirectOutput: true, logFailure: false))
        {
            logger.LogInformation("Certificate removed from the NSS database ({Path}).", nssDir);
        }
    }

    internal static void LogManualInstructions(ILogger logger)
    {
        logger.LogWarning(
            "Couldn't detect your distro's certificate store. Trust the Dev Proxy root certificate manually: export it with 'openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out devproxy-ca.crt -passin pass:', add devproxy-ca.crt to your system's CA anchors and refresh the store. More information: https://aka.ms/devproxy/start");
    }

    private static (bool IsRoot, bool IsInteractive) GetContext() =>
        (Environment.UserName == "root",
         !Console.IsInputRedirected && Environment.GetEnvironmentVariable("CI") is null);

    internal static string? FindSystemExecutable(string name) =>
        _systemDirectories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    internal static string? FindExecutable(string name) =>
        FindExecutable(name, Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindExecutable(string name, string? path)
    {
        var directories = (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(_sbinDirectories);
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool Run(LinuxCommand command, ILogger logger, bool redirectOutput, bool logFailure = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var stderr = new System.Text.StringBuilder();
            if (redirectOutput)
            {
                process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
            }
            _ = process.Start();
            if (redirectOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                if (logFailure)
                {
                    var error = stderr.ToString().Trim();
                    if (error.Length > 0)
                    {
                        logger.LogError("Command '{Command}' failed with exit code {ExitCode}: {Error}", command, process.ExitCode, error);
                    }
                    else
                    {
                        logger.LogError("Command '{Command}' failed with exit code {ExitCode}.", command, process.ExitCode);
                    }
                }
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (logFailure)
            {
                logger.LogError(ex, "Couldn't run '{Command}'", command);
            }
            return false;
        }
    }

    private static string? ExportCertificateToPem(X509Certificate2 certificate, ILogger logger)
    {
        try
        {
            // Public certificate only; the private key never leaves the PFX.
            var pem = certificate.ExportCertificatePem();
            var pemFilePath = Path.Combine(Path.GetTempPath(), $"devproxy-ca-{Guid.NewGuid():N}.crt");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(pemFilePath, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(pem);
                writer.Write('\n');
            }
            return pemFilePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(ex, "Failed to export the root certificate to PEM");
            return null;
        }
    }

    private static void CleanupFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore cleanup failures
        }
    }
}
