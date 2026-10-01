using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

public enum StageState { Pending, Running, Done, Failed, Skipped }

public sealed record InstallResult(bool Succeeded, bool VerifyFailed, bool RestartNeeded, bool Cancelled, string SummaryText, string LogFile);

/// <summary>
/// Runs the install end to end, in ten stages, reporting each log line and
/// stage change back to Step 6. With InstallContext.DryRun set it runs
/// every read-only check for real (SQL connection, database/login/site/
/// port checks, script parsing) and logs every change it would make
/// without making any.
/// </summary>
/// <remarks>
/// Stops at the first failed stage. Everything is written so a second run
/// after fixing the cause is safe: pre-flight catches the half-installed
/// state (existing database, sites) and says so, rather than the install
/// tripping over it midway.
/// </remarks>
public sealed class InstallRunner
{
    public static IReadOnlyList<string> StageNames { get; } = new[]
    {
        "Pre-flight checks",
        "Prerequisites (IIS, URL Rewrite, .NET)",
        "HTTPS certificate",
        "Database",
        "Copy application files",
        "Configure applications",
        "IIS app pools and sites",
        "Firewall rules",
        "Verify installation",
        "Summary"
    };

    private readonly Action<LogEntry> _uiLog;
    private readonly Action<int, StageState> _stageChanged;
    private readonly string _wizardVersion;
    private InstallContext _context = new();
    private StreamWriter? _logWriter;

    public InstallRunner(Action<LogEntry> log, Action<int, StageState> stageChanged, string wizardVersion)
    {
        _uiLog = log;
        _stageChanged = stageChanged;
        _wizardVersion = wizardVersion;
    }

    public static string LogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite Install Wizard", "Logs");

    /// <summary>Replaces every password the wizard knows with ********, for the log and error text.</summary>
    public static string Mask(string text, InstallContext c)
    {
        foreach (string secret in new[] { c.AppPassword, c.SqlAdminPassword })
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                text = text.Replace(secret, "********", StringComparison.Ordinal);
        return text;
    }

    private void Log(LogEntry entry)
    {
        var masked = new LogEntry(entry.Level, Mask(entry.Message, _context));
        _uiLog(masked);
        try
        {
            lock (this) _logWriter?.WriteLine($"{masked.Timestamp:HH:mm:ss} [{masked.Level,-7}] {masked.Message}");
        }
        catch { /* the log file is a convenience; never fail the install over it */ }
    }

    private void Log(LogLevel level, string message) => Log(new LogEntry(level, message));

    public async Task<InstallResult> RunAsync(InstallContext c, CancellationToken token)
    {
        _context = c;
        WizardDataFolder.PrepareLogFolder(); // admin-only (see WizardDataFolder); throws rather than log into an open folder
        string logFile = Path.Combine(LogFolder, $"install-{DateTime.Now:yyyyMMdd-HHmmss}{(c.DryRun ? "-dryrun" : "")}.log");
        _logWriter = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };

        var state = new RunState();
        int stage = 0;
        try
        {
            Log(LogLevel.Stage, $"GraniteWMS Install Wizard {_wizardVersion}: {(c.DryRun ? "DRY RUN (nothing will be changed)" : "INSTALL")}");
            Log(LogLevel.Info, $"Release: {c.ReleaseFolder}   Install folder: {c.InstallRoot}");
            Log(LogLevel.Info, $"Log file: {logFile}");

            Func<Task>[] stages =
            {
                () => PreflightAsync(c, token),
                async () => state.RestartNeeded = await new PrerequisiteService(Log).InstallMissingAsync(c, token),
                () => CertificateAsync(c, state, token),
                () => new DatabaseInstaller(Log).RunAsync(c, token),
                async () =>
                {
                    // Replaced sites go first: their app pools hold files open
                    // in the very folders the copy renames to .bak.
                    if (c.ReplaceExistingSites) await new IisService(Log).RemoveSitesBeingReplacedAsync(c, token);
                    await new FileDeployer(Log).DeployAsync(c, token);
                },
                () => ConfigureAsync(c, token),
                () => new IisService(Log).CreateSitesAsync(c, state.Thumbprint ?? string.Empty, token),
                () => c.OpenFirewall ? new IisService(Log).OpenFirewallAsync(c, token) : SkipAsync("Firewall rules turned off on Step 4."),
                () => VerifyAsync(c, state, token),
                () => SummaryAsync(c, state, logFile)
            };

            for (stage = 0; stage < stages.Length; stage++)
            {
                token.ThrowIfCancellationRequested();
                _stageChanged(stage, StageState.Running);
                Log(LogLevel.Stage, $"=== {stage + 1}. {StageNames[stage]} ===");
                await stages[stage]();
                _stageChanged(stage, StageState.Done);
            }

            return new InstallResult(true, state.VerifyFailed, state.RestartNeeded, false, state.Summary, logFile);
        }
        catch (OperationCanceledException)
        {
            if (stage < StageNames.Count) _stageChanged(stage, StageState.Failed);
            Log(LogLevel.Warning, $"Cancelled during stage {stage + 1} ({StageNames[Math.Min(stage, StageNames.Count - 1)]}). Stages before it finished; this one may be partly done.");
            return new InstallResult(false, false, state.RestartNeeded, true, string.Empty, logFile);
        }
        catch (Exception ex)
        {
            if (stage < StageNames.Count) _stageChanged(stage, StageState.Failed);
            Log(LogLevel.Error, ex.Message);
            Log(LogLevel.Error, $"Stopped at stage {stage + 1} ({StageNames[Math.Min(stage, StageNames.Count - 1)]}). " +
                                (stage == 0 ? "Nothing has been changed." : "Stages before it finished; see the log above for what failed."));
            try { lock (this) _logWriter?.WriteLine(Mask(ex.ToString(), c)); } catch { /* ignore */ }
            return new InstallResult(false, false, state.RestartNeeded, false, string.Empty, logFile);
        }
        finally
        {
            lock (this) { _logWriter?.Dispose(); _logWriter = null; }
        }
    }

    private sealed class RunState
    {
        public bool RestartNeeded;
        public string? Thumbprint;
        public string? CertFile;
        public List<string> Checks = new();
        public bool VerifyFailed;
        public string Summary = string.Empty;
    }

    private Task SkipAsync(string reason)
    {
        Log(LogLevel.Info, reason);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // 1. Pre-flight: collect every problem, report them all at once, and
    //    stop before anything changes.
    // ---------------------------------------------------------------------
    private async Task PreflightAsync(InstallContext c, CancellationToken token)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                errors.Add("The wizard is not running as Administrator.");
        }

        errors.AddRange(ReleaseFolderCheck.Problems(c.ReleaseFolder));
        if (!Path.IsPathFullyQualified(c.InstallRoot))
            errors.Add(@"The install folder must be a full path, e.g. C:\GraniteWMS.");

        if (!c.IsEnabled(GraniteComponent.BusinessApi))
            errors.Add("The Business API must be installed: every other component depends on it.");
        var ports = c.EnabledComponents.Select(x => c.Sites[x.Key].Port).ToList();
        if (ports.Distinct().Count() != ports.Count) errors.Add("Two components are set to the same port.");

        // SQL Server
        try
        {
            SqlServerInfo info = await SqlServerInspector.InspectAsync(c, token);
            Log(LogLevel.Success, $"SQL Server {info.Version}, connected as {info.ConnectedAs}.");
            errors.AddRange(SqlServerInspector.Blockers(info, c));
            warnings.AddRange(SqlServerInspector.Warnings(info, c));
            if (info.AppLoginExists)
            {
                var (ok, message) = await SqlServerInspector.CheckExistingAppLoginAsync(c, token);
                if (ok) Log(LogLevel.Success, message);
                else if (c.ResetExistingAppLoginPassword) warnings.Add(message);
                else errors.Add(message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"Cannot connect to SQL Server {c.SqlServer}: {ex.Message}");
        }

        // The create script parses cleanly (catches a changed release early).
        try
        {
            if (c.DatabaseMode == DatabaseMode.CreateNew && File.Exists(c.CreateScriptPath))
            {
                var parsed = GraniteSqlScriptParser.Parse(await File.ReadAllTextAsync(c.CreateScriptPath, token),
                    new Dictionary<string, string> { ["DatabaseName"] = c.DatabaseName, ["DefaultFilePrefix"] = c.DatabaseName });
                if (parsed.UnresolvedVariables.Count > 0)
                    errors.Add($"GraniteDatabase_Create.sql uses SQLCMD variables with no value: {string.Join(", ", parsed.UnresolvedVariables)}");
                else
                    Log(LogLevel.Info, $"GraniteDatabase_Create.sql parsed: {parsed.Batches.Count} batches.");
            }
        }
        catch (NotSupportedException ex) { errors.Add($"GraniteDatabase_Create.sql: {ex.Message}"); }

        // IIS: existing sites and bound ports. On a server without IIS yet
        // there's nothing to clash with, but something else may already
        // listen on a port.
        var sites = await IisService.ListSitesAsync(token);
        var siteCheck = SiteConflictCheck.Evaluate(sites, LocalAddressDiscovery.ListeningTcpPorts(), c);
        errors.AddRange(siteCheck.Errors);
        warnings.AddRange(siteCheck.Warnings);

        if (c.CertMode == CertificateMode.UseExisting)
        {
            X509Certificate2? cert = string.IsNullOrWhiteSpace(c.ExistingCertThumbprint) ? null : CertificateService.Find(c.ExistingCertThumbprint);
            if (cert is null) errors.Add("The certificate chosen on Step 5 is no longer in Local Computer\\Personal.");
            else if (!cert.HasPrivateKey) errors.Add("The certificate chosen on Step 5 has no private key, so IIS can't use it.");
        }

        foreach (var comp in c.EnabledComponents)
        {
            string dst = c.InstallPathFor(comp);
            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
                warnings.Add($"{dst} is not empty; it will be renamed to a .bak folder first.");
        }

        foreach (string w in warnings) Log(LogLevel.Warning, w);
        if (errors.Count > 0)
        {
            foreach (string e in errors) Log(LogLevel.Error, e);
            throw new InvalidOperationException($"Pre-flight found {errors.Count} problem{(errors.Count == 1 ? "" : "s")}. Nothing has been changed.");
        }
        Log(LogLevel.Success, "Pre-flight checks passed.");
    }

    // ---------------------------------------------------------------------
    // 3. Certificate
    // ---------------------------------------------------------------------
    private Task CertificateAsync(InstallContext c, RunState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (c.CertMode == CertificateMode.UseExisting)
        {
            var cert = CertificateService.Find(c.ExistingCertThumbprint!)!;
            state.Thumbprint = cert.Thumbprint;
            Log(LogLevel.Success, $"Using existing certificate {cert.Subject} ({cert.Thumbprint}), expires {cert.NotAfter:yyyy-MM-dd}.");
            return Task.CompletedTask;
        }

        string names = string.Join(", ", c.CertDnsNames.Concat(c.CertIpAddresses));
        if (c.DryRun)
        {
            Log(LogLevel.DryRun, $"Would create self-signed certificate \"{c.CertFriendlyName}\" for {names}.");
            state.Thumbprint = "DRY-RUN";
            return Task.CompletedTask;
        }

        Log(LogLevel.Info, $"Creating self-signed certificate \"{c.CertFriendlyName}\" for {names}...");
        X509Certificate2 created = CertificateService.CreateSelfSigned(c.CertFriendlyName, c.CertDnsNames, c.CertIpAddresses);
        state.Thumbprint = created.Thumbprint;
        Log(LogLevel.Success, $"Certificate created: {created.Thumbprint}, expires {created.NotAfter:yyyy-MM-dd}.");

        state.CertFile = CertificateService.ExportPublic(created, Path.Combine(c.InstallRoot, "Certificates"), c.CertFriendlyName);
        Log(LogLevel.Info, $"Public certificate saved to {state.CertFile}. Install it on scanners and client PCs so they trust the sites.");
        if (c.TrustCertificate)
        {
            CertificateService.TrustOnThisMachine(created);
            Log(LogLevel.Success, "Certificate added to this server's Trusted Root store.");
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // 6. appsettings.json per component
    // ---------------------------------------------------------------------
    private async Task ConfigureAsync(InstallContext c, CancellationToken token)
    {
        foreach (var comp in c.EnabledComponents)
        {
            token.ThrowIfCancellationRequested();
            // A dry run hasn't copied anything, so it reads the shipped file from the release.
            string folder = c.DryRun ? c.ReleasePathFor(comp) : c.InstallPathFor(comp);
            string path = Path.Combine(folder, "appsettings.json");
            if (!File.Exists(path)) throw new FileNotFoundException($"appsettings.json not found for {comp.Title}: {path}");

            AppSettingsResult result = AppSettingsWriter.Apply(comp, await File.ReadAllTextAsync(path, token), c);
            string changes = string.Join("; ", result.Changes);
            if (c.DryRun)
            {
                Log(LogLevel.DryRun, $"Would update {comp.ReleaseFolder}\\appsettings.json: {changes}");
                continue;
            }

            string backup = path + ".orig";
            if (!File.Exists(backup)) File.Copy(path, backup);
            await File.WriteAllTextAsync(path, result.Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), token);
            Log(LogLevel.Success, $"{comp.ReleaseFolder}\\appsettings.json updated: {changes}");
        }
    }

    // ---------------------------------------------------------------------
    // 9. Verify
    // ---------------------------------------------------------------------
    private async Task VerifyAsync(InstallContext c, RunState state, CancellationToken token)
    {
        if (c.DryRun)
        {
            Log(LogLevel.DryRun, "Would check the app login can read the database and every site answers over HTTPS.");
            return;
        }
        var (lines, anyFailed) = await new VerificationService(Log).VerifyAsync(c, token);
        state.Checks = lines;
        state.VerifyFailed = anyFailed;
        if (anyFailed) Log(LogLevel.Warning, "Some checks failed. The install itself finished; see the errors above.");
    }

    // ---------------------------------------------------------------------
    // 10. Summary
    // ---------------------------------------------------------------------
    private async Task SummaryAsync(InstallContext c, RunState state, string logFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GraniteWMS install summary ({DateTime.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine();
        foreach (var comp in c.EnabledComponents)
            sb.AppendLine($"{comp.Title + ":",-15} {c.UrlFor(comp.Key)}");
        sb.AppendLine();
        sb.AppendLine($"Install folder:   {c.InstallRoot}");
        sb.AppendLine($"SQL Server:       {c.SqlServer}");
        sb.AppendLine($"Database:         {c.DatabaseName} ({(c.DatabaseMode == DatabaseMode.CreateNew ? "created new" : "existing, data kept")})");
        sb.AppendLine($"App SQL login:    {c.AppLogin}");
        sb.AppendLine($"Certificate:      {state.Thumbprint}");
        if (state.CertFile is not null) sb.AppendLine($"Certificate file: {state.CertFile}");
        sb.AppendLine($"Log:              {logFile}");
        if (state.Checks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Checks:");
            foreach (string line in state.Checks) sb.AppendLine("  " + line);
        }
        if (state.RestartNeeded)
        {
            sb.AppendLine();
            sb.AppendLine("Windows asked for a restart. Restart the server before go-live.");
        }
        sb.AppendLine();
        sb.AppendLine("Next: sign in to Web Desktop, and install the certificate file on scanners and client PCs.");
        state.Summary = sb.ToString();

        if (c.DryRun)
        {
            Log(LogLevel.Success, "Dry run finished. Nothing was changed.");
            return;
        }

        string summaryPath = Path.Combine(c.InstallRoot, "InstallSummary.txt");
        await File.WriteAllTextAsync(summaryPath, state.Summary);
        string profilePath = Path.Combine(c.InstallRoot, "InstallProfile.json");
        await File.WriteAllTextAsync(profilePath, InstallProfile.From(c, _wizardVersion).ToJson());
        Log(LogLevel.Success, $"Summary written to {summaryPath}");
        Log(LogLevel.Success, $"Answers saved (no passwords) to {profilePath}");
    }
}
