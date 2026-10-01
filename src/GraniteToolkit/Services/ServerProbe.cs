using System.Runtime.InteropServices;
using Granite.Toolkit.Core.Discovery;
using Granite.Toolkit.Core.Processes;
using Granite.Toolkit.Core.Sql;
using GraniteToolkit.Logic;

namespace GraniteToolkit.Services;

/// <summary>
/// Gathers the dashboard's snapshot. Read-only by design: it lists IIS
/// through appcmd, reads the registry and file versions, and queries Task
/// Scheduler. It never connects to SQL Server (that needs credentials,
/// which belong in the module that uses them) and never changes anything.
/// </summary>
public static class ServerProbe
{
    public static async Task<ServerSnapshot> GatherAsync(CancellationToken token)
    {
        string? iisVersion = ServerPrerequisites.IisVersion();
        IReadOnlyList<GraniteInstall> installs = Array.Empty<GraniteInstall>();
        IReadOnlyList<string> attach = Array.Empty<string>();
        string? scanError = null;

        if (GraniteInstallScanner.IisInstalled)
        {
            try
            {
                var scan = await GraniteInstallScanner.ScanAsync(token);
                installs = scan.Installs;
                attach = GraniteInstallScanner.FindAttachSites(scan, File.Exists, Environment.ExpandEnvironmentVariables)
                    .Select(site => site.Bindings.Count == 0
                        ? site.Name
                        : $"{site.Name} ({string.Join(", ", site.Bindings.Select(b => $"{b.Protocol} {b.Port}"))})")
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                scanError = ex.Message;
            }
        }

        return new ServerSnapshot
        {
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            IisVersion = iisVersion,
            UrlRewrite = ServerPrerequisites.UrlRewriteInstalled,
            AspNetCore8 = ServerPrerequisites.AspNetCoreRuntime(8),
            AspNetCoreModule = ServerPrerequisites.AncmInstalled,
            SqlInstances = SqlInstanceDiscovery.GetLocalInstances(),
            Installs = installs,
            IisScanError = scanError,
            AttachSites = attach,
            BiSyncTasks = await ReadBiTasksAsync(token)
        };
    }

    private static async Task<IReadOnlyList<string>?> ReadBiTasksAsync(CancellationToken token)
    {
        string schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        if (!File.Exists(schtasks)) return null;
        try
        {
            var r = await ProcessRunner.RunAsync(schtasks, new[] { "/query", "/fo", "csv", "/nh" }, TimeSpan.FromSeconds(30), token);
            return r.ExitCode == 0 ? BiTaskParser.Parse(r.Output) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }
}
