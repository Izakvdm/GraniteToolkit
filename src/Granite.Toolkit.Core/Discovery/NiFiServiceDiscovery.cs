using Granite.Toolkit.Core.Processes;
using Microsoft.Win32;

namespace Granite.Toolkit.Core.Discovery;

/// <summary>
/// Finds NiFi services the way the NiFi Deploy module (and the Granite
/// install guide) sets them up: an NSSM service whose application is
/// <c>&lt;NiFi home&gt;\bin\nifi.cmd</c>. Read-only: the registry and
/// <c>sc query</c>, nothing else. The parsing is in NiFiServiceInfo (pure,
/// checked by the harnesses).
/// </summary>
public static class NiFiServiceDiscovery
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Every NSSM service that runs a nifi.cmd, with its current state.</summary>
    public static async Task<IReadOnlyList<NiFiService>> FindAsync(CancellationToken token)
    {
        var found = new List<NiFiService>();
        foreach (var (name, application) in ReadNssmServices())
        {
            string? home = NiFiServiceInfo.HomeFromApplication(application);
            if (home is null) continue;
            string? state = await QueryStateAsync(name, token);
            found.Add(new NiFiService(name, home, NiFiServiceInfo.VersionFromHome(home), state));
        }
        return found.OrderBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when a Windows service with this name exists (any program, not only NiFi).</summary>
    public static bool ServiceExists(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<(string Name, string Application)> ReadNssmServices()
    {
        var result = new List<(string, string)>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesKey);
            if (services is null) return result;
            foreach (string name in services.GetSubKeyNames())
            {
                try
                {
                    using var parameters = services.OpenSubKey($@"{name}\Parameters");
                    if (parameters?.GetValue("Application") is string application)
                        result.Add((name, application));
                }
                catch
                {
                    // One unreadable service key doesn't stop the rest.
                }
            }
        }
        catch
        {
            // Registry not readable: report nothing rather than guess.
        }
        return result;
    }

    private static async Task<string?> QueryStateAsync(string serviceName, CancellationToken token)
    {
        string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        if (!File.Exists(sc)) return null;
        try
        {
            var r = await ProcessRunner.RunAsync(sc, new[] { "query", serviceName }, TimeSpan.FromSeconds(20), token);
            return r.ExitCode == 0 ? NiFiServiceInfo.ParseScState(r.Output) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }
}
