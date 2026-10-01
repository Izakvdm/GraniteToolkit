using System.Runtime.InteropServices;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Copies each component from the release into the install folder, lays
/// the Hotfix binaries over it, and clears the "downloaded from the
/// internet" mark from every file.
/// </summary>
public sealed class FileDeployer
{
    /// <summary>
    /// Never copied from a Hotfix folder: these hold site-specific settings,
    /// and Hotfix\ProcessApp\appsettings.json in V6.0 is a developer's copy
    /// pointing at https://192.168.0.3:16000/.
    /// </summary>
    public static bool IsHotfixExcluded(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        return name == "appsettings.json"
               || (name.StartsWith("appsettings.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
               || name == "web.config"
               || name == "nlog.config"
               || name.EndsWith(".md", StringComparison.Ordinal);
    }

    private readonly Action<LogEntry> _log;

    public FileDeployer(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public async Task DeployAsync(InstallContext c, CancellationToken token)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (c.DryRun) Log(LogLevel.DryRun, $"Would create {c.InstallRoot}");
        else Directory.CreateDirectory(c.InstallRoot);

        foreach (var comp in c.EnabledComponents)
        {
            token.ThrowIfCancellationRequested();
            string src = c.ReleasePathFor(comp);
            string dst = c.InstallPathFor(comp);

            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
            {
                string backup = $"{dst}.bak-{stamp}";
                if (c.DryRun) Log(LogLevel.DryRun, $"Would rename the existing {dst} to {Path.GetFileName(backup)}");
                else
                {
                    await MoveWithRetryAsync(dst, backup, token);
                    Log(LogLevel.Warning, $"{dst} was not empty; renamed it to {Path.GetFileName(backup)}.");
                }
            }

            string? hotfix = c.HotfixPathFor(comp);
            bool applyHotfix = c.ApplyHotfix && hotfix is not null;
            string hotfixLabel = hotfix is null ? string.Empty : $"Hotfix\\{Path.GetFileName(hotfix)}";

            if (c.DryRun)
            {
                Log(LogLevel.DryRun, $"Would copy {Path.GetFileName(src)} to {dst}");
                if (applyHotfix) Log(LogLevel.DryRun, $"Would apply {hotfixLabel} over it (keeping appsettings, web.config and nlog.config)");
                continue;
            }

            int copied = await Task.Run(() => CopyTree(src, dst, overlay: false, token), token);
            Log(LogLevel.Info, $"Copied {Path.GetFileName(src)} ({copied} files).");
            if (applyHotfix)
            {
                int patched = await Task.Run(() => CopyTree(hotfix!, dst, overlay: true, token), token);
                Log(LogLevel.Info, $"Applied {hotfixLabel} ({patched} files).");
            }
            await Task.Run(() => { foreach (string f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories)) Unblock(f); }, token);
            Log(LogLevel.Success, $"{comp.Title} files in place.");
        }
    }

    /// <summary>
    /// Renames the old folder to .bak. An IIS worker process that has only
    /// just been stopped can hold files open for a few seconds, which makes
    /// the rename fail with "access denied"; retry for about 20 seconds.
    /// </summary>
    private async Task MoveWithRetryAsync(string from, string to, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (attempt < 10) { }
            catch (UnauthorizedAccessException) when (attempt < 10) { }
            Log(LogLevel.Detail, $"{Path.GetFileName(from)} is still in use; retrying the rename ({attempt}/9)...");
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    private static int CopyTree(string source, string target, bool overlay, CancellationToken token)
    {
        int count = 0;
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (overlay && IsHotfixExcluded(Path.GetFileName(file))) continue;
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
            count++;
        }
        return count;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    /// <summary>
    /// Removes the Zone.Identifier alternate data stream Windows attaches
    /// to downloaded files (what "Unblock" in file Properties does). A
    /// release copied from a downloaded zip carries it on every DLL, and
    /// the installers refuse to run silently with it. Best-effort: most
    /// files won't have one.
    /// </summary>
    public static void Unblock(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { DeleteFileW(path + ":Zone.Identifier"); } catch { /* best effort */ }
    }
}
