using Microsoft.Win32;

namespace GraniteAttachInstaller.Core;

/// <summary>
/// Finds SQL Server instances for Step 1's server dropdown. Copied from the
/// GraniteWMS Install Wizard's Core/SqlInstanceDiscovery.cs unchanged apart
/// from the namespace - see that file's remarks for why there are two
/// independent sources.
/// </summary>
public static class SqlInstanceDiscovery
{
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
            // Best-effort - registry access denied or key missing just
            // means this source contributes nothing; the field stays usable.
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

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
                // Best-effort - SQL Browser not running, UDP 1434 blocked, or no network.
            }

            return results;
        });
    }
}
