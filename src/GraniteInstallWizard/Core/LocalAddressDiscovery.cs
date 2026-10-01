using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GraniteInstallWizard.Core;

/// <summary>
/// This server's names and IPv4 addresses, for Step 4's address list and
/// Step 5's certificate names.
/// </summary>
public static class LocalAddressDiscovery
{
    public static List<string> HostNames()
    {
        var names = new List<string>();
        try
        {
            string fqdn = Dns.GetHostEntry(string.Empty).HostName;
            if (!string.IsNullOrWhiteSpace(fqdn)) names.Add(fqdn);
        }
        catch { /* no DNS: fall back to the machine name */ }
        if (!names.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
            names.Add(Environment.MachineName);
        return names;
    }

    /// <summary>IPv4 addresses on interfaces that are up, excluding loopback and APIPA (169.254.x.x).</summary>
    public static List<string> IPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal) || list.Contains(ip)) continue;
                    list.Add(ip);
                }
            }
        }
        catch { /* best effort */ }
        return list;
    }

    /// <summary>Ports something on this machine is already listening on.</summary>
    public static HashSet<int> ListeningTcpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        }
        catch { return new HashSet<int>(); }
    }
}
