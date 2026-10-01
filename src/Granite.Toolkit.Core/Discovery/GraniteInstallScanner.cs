using System.Diagnostics;
using Granite.Toolkit.Core.Iis;
using Granite.Toolkit.Core.Processes;

namespace Granite.Toolkit.Core.Discovery;

/// <summary>What IIS on this machine holds, as far as Granite is concerned.</summary>
public sealed record IisScanResult(
    IReadOnlyList<GraniteInstall> Installs,
    IReadOnlyList<IisSite> Sites,
    IReadOnlyList<IisApp> Apps,
    IReadOnlyList<IisVdir> Vdirs);

/// <summary>
/// Reads IIS through appcmd (list only, never changes anything) and finds
/// the Granite installs on it. Moved here from the DB Switcher so the
/// launcher's dashboard uses exactly the same discovery.
/// </summary>
public static class GraniteInstallScanner
{
    public static bool IisInstalled => File.Exists(IisCommands.AppCmdPath);

    private static async Task<string> RunListAsync(string[] args, CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, args, TimeSpan.FromMinutes(1), token);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"appcmd {string.Join(' ', args)} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
        return r.Output;
    }

    /// <summary>Sites, apps and virtual directories from appcmd, plus the Granite installs built from them.</summary>
    public static async Task<IisScanResult> ScanAsync(CancellationToken token)
    {
        if (!IisInstalled)
            throw new InvalidOperationException($"IIS isn't installed on this machine (no {IisCommands.AppCmdPath}).");

        var sites = IisCommands.ParseSites(await RunListAsync(IisCommands.ListSites(), token));
        var apps = IisCommands.ParseApps(await RunListAsync(IisCommands.ListApps(), token));
        var vdirs = IisCommands.ParseVdirs(await RunListAsync(IisCommands.ListVdirs(), token));

        var installs = InstallDiscovery.Build(sites, apps, vdirs,
            File.Exists,
            path => { try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; } },
            Environment.ExpandEnvironmentVariables);

        foreach (var install in installs)
            install.AppVersion = ReadAppVersion(install);

        return new IisScanResult(installs, sites, apps, vdirs);
    }

    /// <summary>FileVersion of the Business API (or, failing that, Process App or Custodian) main dll.</summary>
    public static Version? ReadAppVersion(GraniteInstall install)
    {
        var candidates = new[]
        {
            (GraniteAppKind.BusinessApi, "Granite.Business.API.dll"),
            (GraniteAppKind.ProcessApp, "Granite.Process.App.dll"),
            (GraniteAppKind.Custodian, "Granite.Custodian.dll")
        };
        foreach (var (kind, dll) in candidates)
        {
            var app = install.Get(kind);
            if (app is null) continue;
            string path = Path.Combine(app.PhysicalPath, dll);
            try
            {
                string? fv = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (fv is not null && Version.TryParse(fv.Split(' ')[0], out var v)) return v;
            }
            catch { /* try the next one */ }
        }
        return null;
    }

    /// <summary>
    /// IIS sites whose root folder holds the Granite Attach app
    /// (GraniteAttach.dll), recognised by content like the core apps, not
    /// by site name.
    /// </summary>
    public static IReadOnlyList<IisSite> FindAttachSites(IisScanResult scan, Func<string, bool> fileExists, Func<string, string>? expandPath = null)
    {
        expandPath ??= p => p;
        var result = new List<IisSite>();
        foreach (var app in scan.Apps.Where(a => a.Path == "/"))
        {
            var vdir = scan.Vdirs.FirstOrDefault(v => v.AppName.Equals(app.AppName, StringComparison.OrdinalIgnoreCase) && v.Path == "/");
            if (vdir is null || string.IsNullOrWhiteSpace(vdir.PhysicalPath)) continue;
            string folder = expandPath(vdir.PhysicalPath).TrimEnd('\\', '/');
            if (!fileExists(Path.Combine(folder, "GraniteAttach.dll"))) continue;
            var site = scan.Sites.FirstOrDefault(s => s.Name.Equals(app.SiteName, StringComparison.OrdinalIgnoreCase));
            if (site is not null) result.Add(site);
        }
        return result;
    }
}
