using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

public enum PrereqId { IisFeatures, UrlRewrite, DotNet8Hosting, DotNet6Hosting }

/// <summary>One row of Step 2's prerequisite list.</summary>
public sealed record PrereqStatus(PrereqId Id, string Name, bool Installed, bool Required, string Detail, IReadOnlyList<string> MissingFeatures);

/// <summary>
/// Detects and installs what the core stack needs on the server: IIS
/// features, URL Rewrite, and the ASP.NET Core Hosting Bundle, all from
/// the release's own GraniteScaffold\Prerequisites folder (so no internet
/// access is needed on the server).
/// </summary>
public sealed class PrerequisiteService
{
    private readonly Action<LogEntry> _log;

    public PrerequisiteService(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    private static string DismPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    // Detection is shared with the launcher (Granite.Toolkit.Core.Discovery.ServerPrerequisites).
    private static bool AncmInstalled => ServerPrerequisites.AncmInstalled;

    private static bool UrlRewriteInstalled => ServerPrerequisites.UrlRewriteInstalled;

    private static string? AspNetCoreRuntime(int major) => ServerPrerequisites.AspNetCoreRuntime(major);

    public async Task<IReadOnlyList<PrereqStatus>> CheckAsync(CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(DismPath, WindowsFeatureList.GetFeaturesArgs(), TimeSpan.FromMinutes(5), token);
        IReadOnlyList<string> missing;
        string featureDetail;
        if (result.ExitCode == 0)
        {
            missing = WindowsFeatureList.Missing(WindowsFeatureList.ParseFeatureTable(result.Output));
            featureDetail = missing.Count == 0 ? "All present" : $"{missing.Count} missing: {string.Join(", ", missing)}";
        }
        else
        {
            missing = WindowsFeatureList.Required;
            featureDetail = $"Could not query Windows features (dism exit code {result.ExitCode}); assuming all are needed.";
        }

        string? rt8 = AspNetCoreRuntime(8);
        string? rt6 = AspNetCoreRuntime(6);
        bool ancm = AncmInstalled;

        string net8Detail = (rt8, ancm) switch
        {
            (not null, true) => $"ASP.NET Core {rt8} and the IIS module",
            (not null, false) => $"Runtime {rt8} found, but the IIS module is missing (the Hosting Bundle adds it)",
            (null, true) => "IIS module found, but no ASP.NET Core 8 runtime",
            _ => "Not installed"
        };

        return new[]
        {
            new PrereqStatus(PrereqId.IisFeatures, "IIS features", missing.Count == 0, true, featureDetail, missing),
            new PrereqStatus(PrereqId.UrlRewrite, "IIS URL Rewrite module", UrlRewriteInstalled, true,
                UrlRewriteInstalled ? "Installed" : "Not installed (Web Desktop's web.config uses it)", Array.Empty<string>()),
            new PrereqStatus(PrereqId.DotNet8Hosting, "ASP.NET Core 8 Hosting Bundle", rt8 is not null && ancm, true, net8Detail, Array.Empty<string>()),
            new PrereqStatus(PrereqId.DotNet6Hosting, "ASP.NET Core 6 Hosting Bundle", rt6 is not null, false,
                rt6 is not null ? $"ASP.NET Core {rt6}" : "Not installed (the core stack doesn't need it)", Array.Empty<string>())
        };
    }

    /// <summary>
    /// Finds an installer in the release's GraniteScaffold\Prerequisites
    /// folder. Newest version wins when there are several.
    /// </summary>
    public static string? FindInstaller(InstallContext c, PrereqId id)
    {
        (string sub, string pattern) = id switch
        {
            PrereqId.UrlRewrite => ("IIS", "rewrite_amd64*.msi"),
            PrereqId.DotNet8Hosting => (".NET", "dotnet-hosting-8.*-win.exe"),
            PrereqId.DotNet6Hosting => (".NET", "dotnet-hosting-6.*-win.exe"),
            _ => (string.Empty, string.Empty)
        };
        string dir = Path.Combine(c.PrerequisitesRoot, sub);
        if (pattern.Length == 0 || !Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, pattern).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>Installs whatever is missing. Returns true if Windows asked for a restart.</summary>
    public async Task<bool> InstallMissingAsync(InstallContext c, CancellationToken token)
    {
        var status = await CheckAsync(token);
        foreach (var s in status) Log(LogLevel.Detail, $"{s.Name}: {s.Detail}");
        bool restartNeeded = false;
        bool iisChanged = false;

        // Order matters: IIS first, then URL Rewrite (an IIS module), then
        // the Hosting Bundle last -- it only registers the ASP.NET Core
        // Module into IIS if IIS is already there when it runs.
        var iis = status.First(s => s.Id == PrereqId.IisFeatures);
        if (!iis.Installed)
        {
            if (!c.InstallIisFeatures)
                throw new InvalidOperationException($"IIS features are missing and \"Install missing IIS features\" is off: {string.Join(", ", iis.MissingFeatures)}");
            string[] args = WindowsFeatureList.EnableFeaturesArgs(iis.MissingFeatures);
            if (c.DryRun)
            {
                Log(LogLevel.DryRun, $"Would enable {iis.MissingFeatures.Count} Windows feature(s): {string.Join(", ", iis.MissingFeatures)}");
            }
            else
            {
                Log(LogLevel.Info, $"Enabling {iis.MissingFeatures.Count} Windows feature(s) (this can take several minutes)...");
                Log(LogLevel.Detail, ProcessRunner.Describe(DismPath, args));
                var r = await ProcessRunner.RunAsync(DismPath, args, TimeSpan.FromMinutes(30), token);
                if (!r.Succeeded(0, 3010))
                    throw new InvalidOperationException($"DISM could not enable the IIS features (exit code {r.ExitCode}).{Environment.NewLine}{r.Output.Trim()}");
                if (r.ExitCode == 3010) restartNeeded = true;
                Log(LogLevel.Success, "IIS features enabled.");
            }
            iisChanged = true;
        }
        else Log(LogLevel.Success, "IIS features already present.");

        restartNeeded |= await InstallIfMissingAsync(c, status, PrereqId.UrlRewrite, c.InstallUrlRewrite, required: true, token);
        restartNeeded |= await InstallIfMissingAsync(c, status, PrereqId.DotNet8Hosting, c.InstallDotNet8Hosting, required: true, token);
        restartNeeded |= await InstallIfMissingAsync(c, status, PrereqId.DotNet6Hosting, c.InstallDotNet6Hosting, required: false, token);
        iisChanged |= status.Any(s => !s.Installed && s.Id != PrereqId.IisFeatures &&
                                       (s.Id != PrereqId.DotNet6Hosting || c.InstallDotNet6Hosting));

        if (iisChanged)
        {
            // The Hosting Bundle adds C:\Program Files\dotnet to the machine
            // PATH, and Process App's web.config starts the app with
            // processPath="dotnet". IIS worker processes only see a PATH
            // change after WAS restarts; without this Process App fails with
            // a 500.x on first request until someone reboots.
            if (c.DryRun)
            {
                Log(LogLevel.DryRun, "Would restart IIS (WAS and W3SVC) to pick up the new modules and PATH.");
            }
            else
            {
                Log(LogLevel.Info, "Restarting IIS (WAS and W3SVC) to pick up the new modules and PATH...");
                await ProcessRunner.RunAsync("net.exe", new[] { "stop", "was", "/y" }, TimeSpan.FromMinutes(3), token);
                var start = await ProcessRunner.RunAsync("net.exe", new[] { "start", "w3svc" }, TimeSpan.FromMinutes(3), token);
                if (start.ExitCode != 0 && !start.Output.Contains("already been started", StringComparison.OrdinalIgnoreCase))
                    Log(LogLevel.Warning, $"W3SVC did not start cleanly (exit code {start.ExitCode}): {start.Output.Trim()}");
                else
                    Log(LogLevel.Success, "IIS restarted.");
            }
        }
        return restartNeeded;
    }

    private async Task<bool> InstallIfMissingAsync(InstallContext c, IReadOnlyList<PrereqStatus> status, PrereqId id, bool wanted, bool required, CancellationToken token)
    {
        var s = status.First(x => x.Id == id);
        if (s.Installed) { Log(LogLevel.Success, $"{s.Name} already installed."); return false; }
        if (!wanted)
        {
            if (required) throw new InvalidOperationException($"{s.Name} is missing and its install option on Step 2 is off. The core stack needs it.");
            return false;
        }

        string? installer = FindInstaller(c, id);
        if (installer is null)
            throw new FileNotFoundException($"{s.Name} installer not found under {c.PrerequisitesRoot}.");

        bool isMsi = installer.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        string file = isMsi ? "msiexec.exe" : installer;
        string[] args = isMsi
            ? new[] { "/i", installer, "/qn", "/norestart" }
            : new[] { "/install", "/quiet", "/norestart" };

        if (c.DryRun)
        {
            Log(LogLevel.DryRun, $"Would install {s.Name} from {Path.GetFileName(installer)}.");
            return false;
        }

        Log(LogLevel.Info, $"Installing {s.Name} from {Path.GetFileName(installer)}...");
        FileDeployer.Unblock(installer);
        Log(LogLevel.Detail, ProcessRunner.Describe(file, args));
        var r = await ProcessRunner.RunAsync(file, args, TimeSpan.FromMinutes(20), token);
        // 1641 / 3010: installed, restart initiated / required.
        if (!r.Succeeded(0, 1641, 3010))
            throw new InvalidOperationException($"{s.Name} installer failed with exit code {r.ExitCode}.{Environment.NewLine}{r.Output.Trim()}");
        Log(LogLevel.Success, $"{s.Name} installed.");
        return r.ExitCode != 0;
    }
}
