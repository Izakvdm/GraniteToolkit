using System.Diagnostics;
using GraniteDbSwitcher.Models;

namespace GraniteDbSwitcher.Core;

/// <summary>Runs appcmd to find the Granite installs and to recycle their pools.</summary>
public static class IisService
{
    public static bool IisInstalled => File.Exists(IisCommands.AppCmdPath);

    private static async Task<string> RunListAsync(string[] args, CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, args, TimeSpan.FromMinutes(1), token);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"appcmd {string.Join(' ', args)} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
        return r.Output;
    }

    public static async Task<IReadOnlyList<GraniteInstall>> DiscoverAsync(CancellationToken token)
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

        return installs;
    }

    /// <summary>FileVersion of the Business API (or, failing that, Process App) main dll.</summary>
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

    public static async Task<IReadOnlyDictionary<string, string>> PoolStatesAsync(CancellationToken token)
    {
        var pools = IisCommands.ParseAppPools(await RunListAsync(IisCommands.ListAppPools(), token));
        return pools.ToDictionary(p => p.Name, p => p.State, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Starts the pool if it's stopped, otherwise recycles it.</summary>
    public static async Task RecycleOrStartAsync(string pool, string? state, Action<LogEntry> log, CancellationToken token)
    {
        bool stopped = state is not null && !state.Equals("Started", StringComparison.OrdinalIgnoreCase);
        string[] args = stopped ? IisCommands.StartAppPool(pool) : IisCommands.RecycleAppPool(pool);
        log(new LogEntry(LogLevel.Detail, ProcessRunner.Describe(IisCommands.AppCmdPath, args)));
        var r = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, args, TimeSpan.FromMinutes(1), token);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't {(stopped ? "start" : "recycle")} app pool \"{pool}\" (exit code {r.ExitCode}): {r.Output.Trim()}");
    }
}
