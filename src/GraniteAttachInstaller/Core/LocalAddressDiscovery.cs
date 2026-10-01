using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GraniteAttachInstaller.Core;

/// <summary>
/// This machine's LAN IPv4 addresses, for pre-filling PublicBaseUrl on
/// Step 2. Trimmed from the GraniteWMS Install Wizard's
/// Core/LocalAddressDiscovery.cs (only the address list is needed here).
/// </summary>
public static class LocalAddressDiscovery
{
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
}
