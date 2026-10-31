// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;

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
    public string CertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.CertificateFileName}";

    /// <summary>Anchor that earlier manual setup instructions told users to create.</summary>
    public string LegacyCertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.LegacyCertificateFileName}";
}

/// <summary>A process invocation (file name + argument list, no shell).</summary>
internal enum LinuxTrustResult
{
    Trusted,
    Skipped,
    Failed,
}

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
    /// Wraps a command that needs root. When already root, runs it directly, resolving bare
    /// names from system directories only (PATH could point to user-writable directories);
    /// returns null if it can't be resolved. Otherwise runs it via sudo, with <c>-n</c> when
    /// there's no terminal to prompt on so it fails instead of hanging.
    /// </summary>
    internal static LinuxCommand? Elevate(LinuxCommand command, bool isRoot, bool isInteractive) =>
        Elevate(command, isRoot, isInteractive, FindSystemExecutable);

    internal static LinuxCommand? Elevate(LinuxCommand command, bool isRoot, bool isInteractive, Func<string, string?> findSystemExecutable)
    {
        if (isRoot)
        {
            if (command.FileName.StartsWith('/'))
            {
                return command;
            }
            var resolved = findSystemExecutable(command.FileName);
            return resolved is null ? null : command with { FileName = resolved };
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
        GetNssDatabaseDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);

    /// <summary>
    /// Chromium uses the legacy ~/.pki/nssdb when it exists and ~/.local/share/pki/nssdb
    /// otherwise (M146+). Install and remove share this selection.
    /// </summary>
    internal static string GetNssDatabaseDirectory(string home, Func<string, bool> dirExists)
    {
        var legacy = $"{home}/.pki/nssdb";
        return dirExists(legacy) ? legacy : $"{home}/.local/share/pki/nssdb";
    }

    /// <summary>
    /// Without a terminal, sudo can't prompt for a password. If it also isn't allowed to run
    /// without one (e.g. a restricted sudoers grant), skip trusting instead of failing.
    /// </summary>
    internal static bool ShouldSkipTrust(bool isRoot, bool isInteractive, Func<bool> canSudoWithoutPassword) =>
        !isRoot && !isInteractive && !canSudoWithoutPassword();

    internal static LinuxTrustStore? DetectTrustStore() => DetectTrustStore(Directory.Exists, FindSystemExecutable);

    internal static LinuxTrustResult TrustCertificate(X509Certificate2 certificate, LinuxTrustStore store, ILogger logger)
    {
        var pemFilePath = ExportCertificateToPem(certificate, logger);
        if (pemFilePath is null)
        {
            return LinuxTrustResult.Failed;
        }

        try
        {
            var (isRoot, isInteractive) = GetContext();
            var removeLegacy = FileContainsCertificate(store.LegacyCertificatePath, certificate);
            var commands = GetTrustCommands(store, pemFilePath, removeLegacy);
            if (ShouldSkipTrust(isRoot, isInteractive, () => commands.All(CanSudoWithoutPassword)))
            {
                logger.LogWarning(
                    "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. To trust the certificate, run 'devproxy cert ensure' in an interactive terminal.");
                return LinuxTrustResult.Skipped;
            }

            logger.LogInformation(
                "Adding the Dev Proxy root certificate to the {Distro} system certificate store ({Path}).{SudoHint}",
                store.Name,
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            foreach (var command in commands)
            {
                if (!RunElevated(command, isRoot, isInteractive, logger))
                {
                    if (!isRoot && !isInteractive)
                    {
                        logger.LogError("If sudo requires a password, run 'devproxy cert ensure' in an interactive terminal.");
                    }
                    return LinuxTrustResult.Failed;
                }
            }
            logger.LogInformation("Certificate added to the system certificate store.");

            TrustInNssDatabase(pemFilePath, logger);
            return LinuxTrustResult.Trusted;
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
                if (!RunElevated(command, isRoot, isInteractive, logger))
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
        var certutil = FindCertutil();
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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nssDirs = GetNssDatabaseDirectoriesForRemoval(home, Directory.Exists).ToArray();
        if (nssDirs.Length == 0)
        {
            return;
        }

        var certutil = FindCertutil();
        if (certutil is null)
        {
            logger.LogWarning(
                "Couldn't find certutil to check the NSS database ({Paths}). If you added the Dev Proxy root certificate there, remove '{Nickname}' manually or install certutil and run 'devproxy cert remove' again.",
                string.Join(", ", nssDirs), NssNickname);
            return;
        }

        foreach (var nssDir in nssDirs)
        {
            var db = $"sql:{nssDir}";
            // Only attempt (and report failures of) removal when the certificate is there.
            if (!Run(new(certutil, ["-L", "-d", db, "-n", NssNickname]), logger, redirectOutput: true, logFailure: false))
            {
                continue;
            }
            if (Run(new(certutil, ["-D", "-d", db, "-n", NssNickname]), logger, redirectOutput: true))
            {
                logger.LogInformation("Certificate removed from the NSS database ({Path}).", nssDir);
            }
            else
            {
                logger.LogError("Couldn't remove the Dev Proxy root certificate from the NSS database ({Path}). Remove '{Nickname}' manually.", nssDir, NssNickname);
            }
        }
    }

    /// <summary>
    /// The active NSS database can change (e.g. ~/.pki/nssdb is created after the certificate was
    /// added to ~/.local/share/pki/nssdb), so removal checks every database that exists.
    /// </summary>
    internal static IEnumerable<string> GetNssDatabaseDirectoriesForRemoval(string home, Func<string, bool> dirExists) =>
        new[] { $"{home}/.pki/nssdb", $"{home}/.local/share/pki/nssdb" }.Where(dirExists);

    /// <summary>
    /// Writes the public root certificate to devproxy-ca.crt in the current directory so the user
    /// can add it to their CA anchors, regardless of where the PFX is stored.
    /// </summary>
    internal static void WriteManualTrustInstructions(X509Certificate2 certificate, ILogger logger)
    {
        const string help = "More information: https://aka.ms/devproxy/start";
        var path = Path.GetFullPath(CertificateFileName);
        try
        {
            File.WriteAllText(path, certificate.ExportCertificatePem() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Couldn't detect your distro's certificate store, so trust the Dev Proxy root certificate manually. Couldn't save it to {Path}: {Error}. {Help}",
                path, ex.Message, help);
            return;
        }

        logger.LogWarning(
            "Couldn't detect your distro's certificate store. Saved the Dev Proxy root certificate to {Path}. Add it to your system's CA anchors and refresh the store. {Help}",
            path, help);
    }

    private static (bool IsRoot, bool IsInteractive) GetContext() =>
        (Environment.UserName == "root",
         !Console.IsInputRedirected && Environment.GetEnvironmentVariable("CI") is null);

    // 'sudo -n -l <command>' checks that sudo allows this exact command without a password,
    // without running it, so command-scoped sudoers grants are honored.
    internal static LinuxCommand GetSudoCheckCommand(LinuxCommand command) =>
        new("sudo", ["-n", "-l", "--", command.FileName, .. command.Arguments]);

    private static bool CanSudoWithoutPassword(LinuxCommand command) =>
        Run(GetSudoCheckCommand(command), NullLogger.Instance, redirectOutput: true, logFailure: false);

    private static bool RunElevated(LinuxCommand command, bool isRoot, bool isInteractive, ILogger logger)
    {
        var elevated = Elevate(command, isRoot, isInteractive);
        if (elevated is null)
        {
            logger.LogError("Couldn't find {Command} in {Directories}.", command.FileName, string.Join(", ", _systemDirectories));
            return false;
        }
        return Run(elevated, logger, redirectOutput: false);
    }

    // When running as root, don't pick up certutil from PATH, which might be user-writable.
    private static string? FindCertutil() =>
        GetContext().IsRoot ? FindSystemExecutable("certutil") : FindExecutable("certutil");

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
