using System.Diagnostics;
using Granite.Toolkit.Core.Logging;
using Granite.Toolkit.Core.Security;
using GraniteToolkit.Logic;

namespace GraniteToolkit.Services;

/// <summary>
/// Starts a module exe after checking its signature against the launcher's
/// own (see SignaturePolicy). Only ever from the launcher's folder, by full
/// path, never through a shell or the PATH.
/// </summary>
public sealed class ModuleLauncher
{
    private readonly SignatureInfo _self;
    private readonly Action<LogEntry> _log;

    public ModuleLauncher(SignatureInfo self, Action<LogEntry> log)
    {
        _self = self;
        _log = log;
    }

    public static bool IsPresent(ModuleInfo module) =>
        File.Exists(ModuleCatalog.ExePath(ToolkitPaths.AppFolder, module));

    /// <summary>Checks and starts the module. Returns the running process, or null with a reason when it was blocked.</summary>
    public (Process? Process, LaunchVerdict Verdict) Start(ModuleInfo module)
    {
        string path = ModuleCatalog.ExePath(ToolkitPaths.AppFolder, module);

        // Hold the exe open with read-only sharing from the check until the
        // process has started, so it can't be swapped in between.
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var signature = Authenticode.Verify(path);
        var verdict = SignaturePolicy.Decide(_self, signature);
        _log(new LogEntry(verdict.CanLaunch ? LogLevel.Info : LogLevel.Error,
            $"{module.Title}: {path} - signature {signature.State} ({signature.Detail}) - {verdict.Decision}: {verdict.Reason}"));

        if (!verdict.CanLaunch) return (null, verdict);

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = ToolkitPaths.AppFolder
        };
        var process = Process.Start(psi);
        _log(new LogEntry(LogLevel.Success, $"{module.Title} started (process {process?.Id})."));
        return (process, verdict);
    }
}
