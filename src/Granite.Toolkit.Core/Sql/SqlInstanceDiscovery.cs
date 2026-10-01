using Microsoft.Win32;

namespace Granite.Toolkit.Core.Sql;

/// <summary>
/// Shared by every toolkit module that asks for a SQL Server.
/// Finds SQL Server instances so a wizard's server field can offer a
/// dropdown instead of asking the installer to already know (or go look
/// up) the exact instance name. Two independent sources, since neither
/// alone is reliable:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="GetLocalInstances"/> reads the registry key every SQL
/// Server setup writes on the machine it installs to. Instant, and finds
/// the local instance even when the SQL Browser service (which the other
/// source depends on) is stopped -- the common case for a default-instance
/// SQL Server Express install used entirely locally.</item>
/// <item><see cref="DiscoverNetworkInstancesAsync"/> uses
/// <c>Microsoft.Data.Sql.SqlDataSourceEnumerator</c> -- the same broadcast
/// discovery SQL Server Management Studio's server dropdown uses -- to find
/// instances elsewhere on the network. Best-effort: it depends on the SQL
/// Browser service running on each target and UDP broadcast reaching it,
/// so a firewalled or Browser-less instance won't show up here even though
/// it's perfectly reachable by name.</item>
/// </list>
/// Either source failing (permissions, no network, Browser service off) is
/// swallowed -- this only ever adds convenience items to an editable
/// dropdown; a server that doesn't show up here can always be typed in by
/// hand, exactly as before this existed.
/// </remarks>
public static class SqlInstanceDiscovery
{
    /// <summary>
    /// Instances installed on this machine, read directly from the registry
    /// key SQL Server setup maintains
    /// (HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL).
    /// Returns names in the "MACHINE\INSTANCE" form this wizard's server
    /// field expects, or bare "MACHINE" for a default instance.
    /// </summary>
    public static List<string> GetLocalInstances()
    {
        var results = new List<string>();
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");

            if (key is not null)
            {
                string machine = Environment.MachineName;
                foreach (string valueName in key.GetValueNames())
                {
                    results.Add(string.Equals(valueName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                        ? machine
                        : $@"{machine}\{valueName}");
                }
            }
        }
        catch
        {
            // Best-effort -- registry access denied or key missing just
            // means this source contributes nothing; the field stays usable.
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Instances discoverable on the network via SQL Server's broadcast
    /// enumeration. Can take a few seconds (it waits out a broadcast
    /// timeout), so this always runs off the UI thread.
    /// </summary>
    public static Task<List<string>> DiscoverNetworkInstancesAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<string>();
            try
            {
                System.Data.DataTable table = Microsoft.Data.Sql.SqlDataSourceEnumerator.Instance.GetDataSources();
                foreach (System.Data.DataRow row in table.Rows)
                {
                    string server = row["ServerName"]?.ToString() ?? string.Empty;
                    string instance = row["InstanceName"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(server)) continue;

                    results.Add(string.IsNullOrWhiteSpace(instance) ? server : $@"{server}\{instance}");
                }
            }
            catch
            {
                // Best-effort -- SQL Browser not running, UDP 1434 blocked,
                // or no network at all. The field stays usable either way.
            }

            return results;
        });
    }
}
