using Granite.Toolkit.Core.Discovery;

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

    /// <summary>The Granite installs in IIS (shared with the launcher; see GraniteInstallScanner).</summary>
    public static async Task<IReadOnlyList<GraniteInstall>> DiscoverAsync(CancellationToken token) =>
        (await GraniteInstallScanner.ScanAsync(token)).Installs;

    public static Version? ReadAppVersion(GraniteInstall install) => GraniteInstallScanner.ReadAppVersion(install);

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
