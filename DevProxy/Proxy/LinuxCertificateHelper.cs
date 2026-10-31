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
    public string CertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.CertificateFileName}";

    /// <summary>Anchor that earlier manual setup instructions told users to create.</summary>
    public string LegacyCertificatePath => $"{AnchorDirectory}/{LinuxCertificateHelper.LegacyCertificateFileName}";
}

/// <summary>A process invocation (file name + argument list, no shell).</summary>
internal enum LinuxTrustResult
{
    Trusted,
    /// <summary>The system store trusts the certificate, but NSS-based apps don't.</summary>
    TrustedSystemOnly,
    Skipped,
    Failed,
}

internal enum CommandOutcome
{
    Succeeded,
    /// <summary>sudo needed a password and couldn't prompt for it.</summary>
    PasswordRequired,
    Failed,
}

internal enum SystemTrustOutcome
{
    Trusted,
    Skipped,
    /// <summary>Skipped, but the anchor file was added and couldn't be removed again.</summary>
    SkippedAnchorLeft,
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
    /// Runs the trust commands in order. If sudo needs a password it can't prompt for, nothing
    /// is trusted: when the anchor was already installed, it's removed again with
    /// <paramref name="rollback"/> (null when there's nothing to undo).
    /// </summary>
    internal static SystemTrustOutcome TrustSystemStore(
        IReadOnlyList<LinuxCommand> commands, LinuxCommand? rollback, Func<LinuxCommand, CommandOutcome> run)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            switch (run(commands[i]))
            {
                case CommandOutcome.Succeeded:
                    continue;
                case CommandOutcome.PasswordRequired:
                    if (i == 0 || rollback is null)
                    {
                        return SystemTrustOutcome.Skipped;
                    }
                    return run(rollback) == CommandOutcome.Succeeded
                        ? SystemTrustOutcome.Skipped
                        : SystemTrustOutcome.SkippedAnchorLeft;
                case CommandOutcome.Failed:
                default:
                    return SystemTrustOutcome.Failed;
            }
        }
        return SystemTrustOutcome.Trusted;
    }

    internal static CommandOutcome RunAll(IEnumerable<LinuxCommand> commands, Func<LinuxCommand, CommandOutcome> run)
    {
        foreach (var command in commands)
        {
            var outcome = run(command);
            if (outcome != CommandOutcome.Succeeded)
            {
                return outcome;
            }
        }
        return CommandOutcome.Succeeded;
    }

    internal static bool IsPasswordRequired(string stderr) =>
        stderr.Contains("a password is required", StringComparison.OrdinalIgnoreCase);

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
            // Undo the anchor install only if it changed something.
            LinuxCommand? rollback = FileContainsCertificate(store.CertificatePath, certificate)
                ? null
                : new("rm", ["-f", store.CertificatePath]);

            logger.LogInformation(
                "Adding the Dev Proxy root certificate to the {Distro} system certificate store ({Path}).{SudoHint}",
                store.Name,
                store.CertificatePath,
                isRoot ? string.Empty : " You might be asked for your password.");

            switch (TrustSystemStore(commands, rollback, c => RunElevated(c, isRoot, isInteractive, logger)))
            {
                case SystemTrustOutcome.Skipped:
                    logger.LogWarning(
                        "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. To trust the certificate, run 'devproxy cert ensure' in an interactive terminal.");
                    return LinuxTrustResult.Skipped;
                case SystemTrustOutcome.SkippedAnchorLeft:
                    logger.LogWarning(
                        "Skipped trusting the Dev Proxy root certificate because sudo needs a password and there's no terminal to ask for it. The certificate file {Path} was added but isn't active, and couldn't be removed. To finish trusting it, run 'devproxy cert ensure' in an interactive terminal. To remove it, run 'sudo rm {Path}'.",
                        store.CertificatePath, store.CertificatePath);
                    return LinuxTrustResult.Skipped;
                case SystemTrustOutcome.Failed:
                    return LinuxTrustResult.Failed;
                case SystemTrustOutcome.Trusted:
                default:
                    break;
            }
            logger.LogInformation("Certificate added to the system certificate store.");

            return TrustInNssDatabase(pemFilePath, logger)
                ? LinuxTrustResult.Trusted
                : LinuxTrustResult.TrustedSystemOnly;
        }
        finally
        {
            CleanupFile(pemFilePath);
        }
    }

    /// <summary>Returns false when the system store still trusts the certificate.</summary>
    internal static bool RemoveTrustedCertificate(X509Certificate2 certificate, ILogger logger)
    {
        var removed = true;
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

            var outcome = RunAll(GetUntrustCommands(store, removeLegacy), c => RunElevated(c, isRoot, isInteractive, logger));
            if (outcome == CommandOutcome.Succeeded)
            {
                logger.LogInformation("Certificate removed from the system certificate store.");
            }
            else
            {
                removed = false;
                logger.LogError(
                    "The system certificate store still trusts the Dev Proxy root certificate{Reason}. To finish removing it, run 'devproxy cert remove' in an interactive terminal.",
                    outcome == CommandOutcome.PasswordRequired ? " because sudo needs a password and there's no terminal to ask for it" : string.Empty);
            }
        }

        // Run NSS removal even if the system store step failed, so it's as complete as possible.
        return RemoveFromNssDatabase(logger) && removed;
    }

    /// <summary>Returns false when certutil is available but couldn't add the certificate.</summary>
    private static bool TrustInNssDatabase(string pemFilePath, ILogger logger)
    {
        var certutil = FindCertutil();
        if (certutil is null)
        {
            logger.LogInformation(
                "To have Chrome and other NSS-based apps trust the certificate, install certutil (libnss3-tools on Debian/Ubuntu, nss-tools on Fedora/RHEL, nss on Arch) and run 'devproxy cert ensure' again.");
            return true;
        }

        var nssDir = GetNssDatabaseDirectory();
        var db = $"sql:{nssDir}";
        string error;
        if (!Directory.Exists(nssDir))
        {
            try
            {
                _ = Directory.CreateDirectory(nssDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNssTrustFailure(logger, nssDir, ex.Message, leftoverEntry: false);
                return false;
            }
            error = Execute(new(certutil, ["-N", "-d", db, "--empty-password"]), redirectOutput: true).Error;
            if (error.Length > 0)
            {
                LogNssTrustFailure(logger, nssDir, error, leftoverEntry: false);
                return false;
            }
        }

        // Replace any previous Dev Proxy root (it's regenerated per machine) so re-runs are idempotent.
        _ = Run(new(certutil, ["-D", "-d", db, "-n", NssNickname]), logger, redirectOutput: true, logFailure: false);
        error = Execute(new(certutil, ["-A", "-d", db, "-n", NssNickname, "-t", "C,,", "-i", pemFilePath]), redirectOutput: true).Error;
        if (error.Length == 0)
        {
            logger.LogInformation("Certificate added to the NSS database ({Path}).", nssDir);
            return true;
        }

        // A failed add can leave an entry without trust flags behind; try to remove it.
        var leftoverEntry =
            Run(new(certutil, ["-L", "-d", db, "-n", NssNickname]), logger, redirectOutput: true, logFailure: false) &&
            !Run(new(certutil, ["-D", "-d", db, "-n", NssNickname]), logger, redirectOutput: true, logFailure: false);
        LogNssTrustFailure(logger, nssDir, error, leftoverEntry);
        return false;
    }

    internal static string FormatToolError(string error) =>
        string.Join(' ', error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).TrimEnd('.');

    private static void LogNssTrustFailure(ILogger logger, string nssDir, string error, bool leftoverEntry)
    {
        logger.LogWarning(
            "The system certificate store trusts the Dev Proxy root certificate, but Chrome and other NSS-based apps don't: couldn't add it to the NSS database ({Path}): {Error}. If the database has a password, run 'devproxy cert ensure' in an interactive terminal. Otherwise, check that you can write to it.",
            nssDir, FormatToolError(error));
        if (leftoverEntry)
        {
            logger.LogWarning(
                "The NSS database still has a '{Nickname}' entry that couldn't be removed. Remove it with: certutil -D -d sql:{Path} -n \"{Nickname}\"",
                NssNickname, nssDir, NssNickname);
        }
    }

    /// <summary>Returns false when certutil found the certificate but couldn't delete it.</summary>
    private static bool RemoveFromNssDatabase(ILogger logger)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nssDirs = GetNssDatabaseDirectoriesForRemoval(home, Directory.Exists).ToArray();
        if (nssDirs.Length == 0)
        {
            return true;
        }

        var certutil = FindCertutil();
        if (certutil is null)
        {
            logger.LogWarning(
                "Couldn't find certutil to check the NSS database ({Paths}). If you added the Dev Proxy root certificate there, remove '{Nickname}' manually or install certutil and run 'devproxy cert remove' again.",
                string.Join(", ", nssDirs), NssNickname);
            return true;
        }

        var removed = true;
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
                removed = false;
                logger.LogError("Couldn't remove the Dev Proxy root certificate from the NSS database ({Path}). Remove '{Nickname}' manually.", nssDir, NssNickname);
            }
        }
        return removed;
    }

    /// <summary>
    /// The active NSS database can change (e.g. ~/.pki/nssdb is created after the certificate was
    /// added to ~/.local/share/pki/nssdb), so removal checks every database that exists.
    /// </summary>
    internal static IEnumerable<string> GetNssDatabaseDirectoriesForRemoval(string home, Func<string, bool> dirExists) =>
        new[] { $"{home}/.pki/nssdb", $"{home}/.local/share/pki/nssdb" }.Where(dirExists);

    /// <summary>
    /// Replaces rather than follows an existing entry: deleting a symlink doesn't touch its target,
    /// and CreateNew fails if something is recreated at the path in between.
    /// </summary>
    internal static void WriteNewFile(string path, string contents)
    {
        File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

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
            WriteNewFile(path, certificate.ExportCertificatePem() + "\n");
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

    private static CommandOutcome RunElevated(LinuxCommand command, bool isRoot, bool isInteractive, ILogger logger)
    {
        var elevated = Elevate(command, isRoot, isInteractive);
        if (elevated is null)
        {
            logger.LogError("Couldn't find {Command} in {Directories}.", command.FileName, string.Join(", ", _systemDirectories));
            return CommandOutcome.Failed;
        }

        // Without a terminal (sudo -n), capture stderr to tell a missing password from other failures.
        var captureError = !isRoot && !isInteractive;
        var (succeeded, error) = Execute(elevated, redirectOutput: captureError);
        if (succeeded)
        {
            return CommandOutcome.Succeeded;
        }
        if (captureError && IsPasswordRequired(error))
        {
            return CommandOutcome.PasswordRequired;
        }
        logger.LogError("Command '{Command}' failed{Error}", elevated, error.Length > 0 ? $": {error}" : ".");
        return CommandOutcome.Failed;
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
        var (succeeded, error) = Execute(command, redirectOutput);
        if (!succeeded && logFailure)
        {
            logger.LogError("Command '{Command}' failed{Error}", command, error.Length > 0 ? $": {error}" : ".");
        }
        return succeeded;
    }

    /// <summary>
    /// Runs the command. On failure, Error has the captured stderr (when redirected), the exit
    /// code, or why it couldn't start; it's empty on success.
    /// </summary>
    private static (bool Succeeded, string Error) Execute(LinuxCommand command, bool redirectOutput)
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
        if (redirectOutput)
        {
            // Untranslated messages, so failures like sudo's "a password is required" can be detected.
            startInfo.Environment["LC_ALL"] = "C";
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

            if (process.ExitCode == 0)
            {
                return (true, string.Empty);
            }
            var error = stderr.ToString().Trim();
            return (false, error.Length > 0 ? error : $"exit code {process.ExitCode}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
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
