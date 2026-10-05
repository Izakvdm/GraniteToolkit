using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.Core;

/// <summary>
/// Puts NiFi on the server as the Granite install guide describes, with
/// the folders locked down: extract NiFi and the JDK, edit nifi-env.cmd,
/// nifi.cmd, nifi.properties and bootstrap.conf, set the single-user login,
/// copy the JDBC driver, install the NSSM service, create the import
/// folders, start the service.
/// </summary>
public sealed class NiFiInstaller
{
    private readonly Action<LogEntry> _log;

    public NiFiInstaller(Action<LogEntry> log) => _log = log;

    /// <summary>What this run created, so a failure before NiFi is configured can be undone.</summary>
    public bool CreatedHome { get; private set; }
    public bool CreatedService { get; private set; }

    // ------------------------------------------------------------------ checks

    /// <summary>Reasons the install can't go ahead with these settings (empty when it can).</summary>
    public static IReadOnlyList<string> PreflightProblems(DeployContext c)
    {
        var problems = new List<string>();
        if (Directory.Exists(c.NiFiHome)) problems.Add($"{c.NiFiHome} already exists. Choose another install folder, or remove the old NiFi first.");
        if (NiFiServiceDiscovery.ServiceExists(c.ServiceName)) problems.Add($"A Windows service called {c.ServiceName} already exists. Choose another service name.");
        if (PortInUse(c.Port)) problems.Add($"Port {c.Port} is already in use on this server. Choose another port.");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(c.InstallRoot)!);
            if (drive.AvailableFreeSpace < 3L * 1024 * 1024 * 1024)
                problems.Add($"{drive.Name} has less than 3 GB free. NiFi needs about 2 GB plus room for its repositories.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            problems.Add($"Can't use drive {Path.GetPathRoot(c.InstallRoot)}: {ex.Message}");
        }
        return problems;
    }

    public static bool PortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>The SID for DOMAIN\user, or throws with a readable reason.</summary>
    public static SecurityIdentifier ResolveAccount(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account.Trim()).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            throw new ArgumentException($"Windows doesn't know the account \"{account}\". Use DOMAIN\\user or COMPUTER\\user.");
        }
    }

    // ------------------------------------------------------------------ install

    public async Task InstallFilesAsync(DeployContext c, CancellationToken token)
    {
        var media = c.Media ?? throw new InvalidOperationException("Install media not checked.");

        _log(new LogEntry(LogLevel.Info, $"Locking down {c.InstallRoot} (Administrators and SYSTEM only: NiFi's conf holds its keys)"));
        SecureFolders.EnsureAdminOnly(c.InstallRoot);

        _log(new LogEntry(LogLevel.Info, $"Extracting NiFi {media.NiFiVersion} to {c.NiFiHome}"));
        CreatedHome = true;
        await Task.Run(() => ExtractTopFolder(media.NiFi.Path, media.NiFiFolderName, c.NiFiHome), token);
        Require(Path.Combine(c.NiFiHome, "bin", "nifi.cmd"));

        string jdk = Path.Combine(c.NiFiHome, "jdk");
        _log(new LogEntry(LogLevel.Info, $"Extracting Java {media.JavaVersion} to {jdk}"));
        await Task.Run(() => ExtractSingleTopFolder(media.Jdk.Path, jdk), token);
        Require(Path.Combine(jdk, "bin", "java.exe"));
        if (!media.JavaIsLts)
            _log(new LogEntry(LogLevel.Warning, $"Java {media.JavaMajor} isn't a long-term-support release. Java 21 or 25 is recommended for client servers."));

        _log(new LogEntry(LogLevel.Info, "Pointing NiFi at its own JDK (bin\\nifi-env.cmd)"));
        await File.WriteAllTextAsync(Path.Combine(c.NiFiHome, "bin", "nifi-env.cmd"), NiFiConfigFiles.NiFiEnvCmd, System.Text.Encoding.ASCII, token);

        string cmdPath = Path.Combine(c.NiFiHome, "bin", "nifi.cmd");
        var (cmdText, patch) = NiFiConfigFiles.PatchNiFiCmd(await File.ReadAllTextAsync(cmdPath, token));
        switch (patch)
        {
            case NiFiConfigFiles.CmdPatch.Patched:
                await File.WriteAllTextAsync(cmdPath, cmdText, System.Text.Encoding.ASCII, token);
                _log(new LogEntry(LogLevel.Info, "Patched bin\\nifi.cmd so the service stays attached to Java (no start /MIN)"));
                break;
            case NiFiConfigFiles.CmdPatch.AlreadyPatched:
                _log(new LogEntry(LogLevel.Detail, "bin\\nifi.cmd already starts Java directly"));
                break;
            default:
                throw new InvalidDataException("bin\\nifi.cmd doesn't have the start /MIN line this NiFi version is expected to have. The service would restart in a loop, so the install stops here.");
        }

        _log(new LogEntry(LogLevel.Info, $"Port {c.Port}, heap {c.HeapSize}"));
        await EditAsync(Path.Combine(c.NiFiHome, "conf", "nifi.properties"), t => NiFiConfigFiles.SetProperty(t, "nifi.web.https.port", c.Port.ToString()), token);
        await EditAsync(Path.Combine(c.NiFiHome, "conf", "bootstrap.conf"), t => NiFiConfigFiles.SetHeap(t, c.HeapSize), token);

        _log(new LogEntry(LogLevel.Info, $"Setting the NiFi login for {c.AdminUser} (stored hashed in conf\\login-identity-providers.xml)"));
        var args = NiFiConfigFiles.CredentialsArguments(c.NiFiHome, c.AdminUser, c.AdminPassword, ';');
        // Not logged: the arguments include the password.
        var result = await ProcessRunner.RunAsync(Path.Combine(jdk, "bin", "java.exe"), args, TimeSpan.FromMinutes(3), token, c.NiFiHome);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"NiFi's SetSingleUserCredentials tool failed (exit {result.ExitCode}): {result.Output.Replace(c.AdminPassword, "********")}");

        _log(new LogEntry(LogLevel.Info, $"Installing the SQL Server JDBC driver ({media.JdbcJarName}) to {c.DriverFolder}"));
        Directory.CreateDirectory(c.DriverFolder);
        await Task.Run(() => CopyJdbcDriver(media, c.DriverFolder), token);

        _log(new LogEntry(LogLevel.Info, "Copying NSSM into the NiFi bin folder"));
        await Task.Run(() => ExtractEntry(media.Nssm.Path, media.NssmEntry, Path.Combine(c.NiFiHome, "bin", "nssm.exe")), token);
    }

    public void CreateImportFolders(DeployContext c)
    {
        _log(new LogEntry(LogLevel.Info, $"Creating import folders under {c.ImportRoot} (Administrators and SYSTEM only)"));
        SecureFolders.EnsureAdminOnly(c.ImportRoot);
        foreach (string feed in c.Feeds) Directory.CreateDirectory(Path.Combine(c.InboundFolder, feed));
        Directory.CreateDirectory(c.ArchiveFolder);
        Directory.CreateDirectory(c.ErrorFolder);

        if (!string.IsNullOrWhiteSpace(c.DropAccount))
        {
            var sid = ResolveAccount(c.DropAccount);
            var dir = new DirectoryInfo(c.InboundFolder);
            var security = dir.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(security);
            _log(new LogEntry(LogLevel.Success, $"{c.DropAccount} can drop files into {c.InboundFolder} (Modify on Inbound only)"));
        }
        else
        {
            _log(new LogEntry(LogLevel.Info, "No drop account given: only administrators can put files in Inbound. Grant the integration account Modify on Inbound when you know it."));
        }
    }

    public async Task InstallServiceAsync(DeployContext c, CancellationToken token)
    {
        string nssm = Path.Combine(c.NiFiHome, "bin", "nssm.exe");
        _log(new LogEntry(LogLevel.Info, $"Installing Windows service {c.ServiceName}"));
        foreach (string folder in NiFiConfigFiles.FoldersBeforeService(c.NiFiHome))
            Directory.CreateDirectory(folder);
        foreach (var args in NiFiConfigFiles.NssmInstallCommands(c.ServiceName, c.NiFiHome, c.Media!.NiFiVersion))
        {
            _log(new LogEntry(LogLevel.Detail, ProcessRunner.Describe(nssm, args)));
            var r = await ProcessRunner.RunAsync(nssm, args, TimeSpan.FromMinutes(1), token);
            if (args[0] == "install" && r.ExitCode == 0) CreatedService = true;
            if (r.ExitCode != 0) throw new InvalidOperationException($"nssm {args[0]} {args[1]} {(args.Length > 2 ? args[2] : "")} failed (exit {r.ExitCode}): {Clean(r.Output)}");
        }
        _log(new LogEntry(LogLevel.Success, $"Service {c.ServiceName} installed (automatic start, runs as LocalSystem)"));
    }

    public async Task StartServiceAsync(DeployContext c, CancellationToken token)
    {
        string nssm = Path.Combine(c.NiFiHome, "bin", "nssm.exe");
        _log(new LogEntry(LogLevel.Info, $"Starting {c.ServiceName}. The first start unpacks NiFi's libraries and takes 1 to 3 minutes."));
        var r = await ProcessRunner.RunAsync(nssm, new[] { "start", c.ServiceName }, TimeSpan.FromMinutes(2), token);
        if (r.ExitCode != 0)
        {
            await LogNssmEventsAsync(token);
            throw new InvalidOperationException($"The service didn't start (exit {r.ExitCode}): {Clean(r.Output)} NSSM's reason is above (from the Application event log).");
        }

        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (!await PortOpenAsync(c.Port, token))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"NiFi didn't open port {c.Port} within 6 minutes. Check {Path.Combine(c.NiFiHome, "logs", "nifi-app.log")}.");
            string? state = NiFiServiceInfo.ParseScState((await ProcessRunner.RunAsync(ScExe, new[] { "query", c.ServiceName }, TimeSpan.FromSeconds(20), token)).Output);
            if (state is "STOPPED")
            {
                await LogNssmEventsAsync(token);
                throw new InvalidOperationException("The service stopped while starting. NSSM's reason is above; NiFi's own logs are kept (see below).");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        _log(new LogEntry(LogLevel.Success, $"NiFi is listening on port {c.Port}"));
    }

    /// <summary>Undoes what this run created (service, NiFi folder). Best effort, logged.</summary>
    public async Task RollBackAsync(DeployContext c)
    {
        if (CreatedService)
        {
            string nssm = Path.Combine(c.NiFiHome, "bin", "nssm.exe");
            try
            {
                await ProcessRunner.RunAsync(nssm, new[] { "stop", c.ServiceName }, TimeSpan.FromMinutes(2), CancellationToken.None);
                var r = await ProcessRunner.RunAsync(nssm, new[] { "remove", c.ServiceName, "confirm" }, TimeSpan.FromMinutes(1), CancellationToken.None);
                _log(new LogEntry(r.ExitCode == 0 ? LogLevel.Info : LogLevel.Warning, r.ExitCode == 0 ? $"Removed service {c.ServiceName}" : $"Couldn't remove service {c.ServiceName}: {Clean(r.Output)}"));
            }
            catch (Exception ex)
            {
                _log(new LogEntry(LogLevel.Warning, $"Couldn't remove service {c.ServiceName}: {ex.Message}. Remove it with: sc delete {c.ServiceName}"));
            }
        }
        if (CreatedHome && Directory.Exists(c.NiFiHome))
        {
            KeepNiFiLogs(c);
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(c.NiFiHome, recursive: true);
                    _log(new LogEntry(LogLevel.Info, $"Removed {c.NiFiHome}"));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 5) _log(new LogEntry(LogLevel.Warning, $"Couldn't remove {c.NiFiHome}: {ex.Message}. Delete it by hand before trying again."));
                    else await Task.Delay(2000);
                }
            }
        }
    }

    /// <summary>
    /// Copies NiFi's logs out of the folder the rollback is about to delete,
    /// so a failed start can still be diagnosed:
    /// C:\ProgramData\Granite NiFi Deploy\Logs\failed-start-yyyyMMdd-HHmmss.
    /// </summary>
    private void KeepNiFiLogs(DeployContext c)
    {
        string logs = Path.Combine(c.NiFiHome, "logs");
        try
        {
            if (!Directory.Exists(logs) || !Directory.EnumerateFiles(logs).Any()) return;
            string target = Path.Combine(MediaStaging.LogFolder, $"failed-start-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(logs))
            {
                // NiFi may still hold a log open for a moment after the stop.
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(Path.Combine(target, Path.GetFileName(file)));
                src.CopyTo(dst);
            }
            _log(new LogEntry(LogLevel.Info, $"Kept NiFi's logs in {target}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log(new LogEntry(LogLevel.Warning, $"Couldn't keep NiFi's logs: {ex.Message}"));
        }
    }

    /// <summary>
    /// NSSM writes why it couldn't start the app to the Application event log
    /// (source "nssm"), not to the service's own log. Shows the last few.
    /// </summary>
    private async Task LogNssmEventsAsync(CancellationToken token)
    {
        try
        {
            string wevtutil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe");
            var r = await ProcessRunner.RunAsync(wevtutil,
                new[] { "qe", "Application", "/q:*[System[Provider[@Name='nssm'] and TimeCreated[timediff(@SystemTime) <= 600000]]]", "/c:6", "/rd:true", "/f:text" },
                TimeSpan.FromSeconds(30), token);
            var descriptions = NiFiServiceInfo.ParseEventDescriptions(r.Output);
            if (descriptions.Count == 0)
            {
                _log(new LogEntry(LogLevel.Warning, "NSSM logged nothing in the Application event log in the last 10 minutes."));
                return;
            }
            _log(new LogEntry(LogLevel.Error, "NSSM (Application event log, newest first):"));
            foreach (string d in descriptions)
                _log(new LogEntry(LogLevel.Error, "  " + d));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(new LogEntry(LogLevel.Warning, $"Couldn't read the Application event log: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string ScExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");

    private static async Task<bool> PortOpenAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync("localhost", port, cts.Token); // NiFi binds to localhost: IPv4 or IPv6
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task EditAsync(string path, Func<string, string> edit, CancellationToken token)
    {
        string text = await File.ReadAllTextAsync(path, token);
        await File.WriteAllTextAsync(path, edit(text), new System.Text.UTF8Encoding(false), token);
    }

    private static void Require(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{path} is missing after extraction.");
    }

    /// <summary>
    /// Extracts the zip, whose entries all sit under <paramref name="topFolder"/>,
    /// so that folder becomes <paramref name="destination"/>. .NET refuses
    /// entries that would land outside the target (zip slip).
    /// </summary>
    private static void ExtractTopFolder(string zipPath, string topFolder, string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(parent, "_extract-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            Directory.Move(Path.Combine(staging, topFolder), destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Extracts a zip that holds one top folder (a JDK) so that folder becomes <paramref name="destination"/>.</summary>
    private static void ExtractSingleTopFolder(string zipPath, string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(parent, "_extract-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            var tops = Directory.GetDirectories(staging);
            if (tops.Length != 1 || Directory.GetFiles(staging).Length > 0)
                throw new InvalidDataException($"{Path.GetFileName(zipPath)} should hold one top folder.");
            Directory.Move(tops[0], destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void ExtractEntry(string zipPath, string entryName, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException($"{entryName} not found in {Path.GetFileName(zipPath)}.");
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static void CopyJdbcDriver(MediaSet media, string driverFolder)
    {
        string target = Path.Combine(driverFolder, media.JdbcJarName);
        if (media.Jdbc.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(media.Jdbc.Path, target, overwrite: true);
            return;
        }
        using var zip = ZipFile.OpenRead(media.Jdbc.Path);
        var entry = zip.Entries.First(e => Path.GetFileName(e.FullName.Replace('\\', '/')).Equals(media.JdbcJarName, StringComparison.OrdinalIgnoreCase));
        entry.ExtractToFile(target, overwrite: true);
    }

    private static string Clean(string output) => string.Join(" ", output.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();
}
