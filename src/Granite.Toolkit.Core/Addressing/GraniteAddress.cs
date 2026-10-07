using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Granite.Toolkit.Core.Addressing;

/// <summary>What's wrong (if anything) with the address an app uses to reach the Business API.</summary>
public enum AddressHealth
{
    /// <summary>A name or fixed IP of this machine.</summary>
    Ok,

    /// <summary>A DNS name that isn't this machine's own name: fine if DNS points it here, not checked.</summary>
    NameNotChecked,

    /// <summary>An IP of this machine that DHCP handed out, so it can change.</summary>
    DhcpAddress,

    /// <summary>localhost or 127.x: works on the server only, never from a PC or scanner.</summary>
    Localhost,

    /// <summary>An IP this machine doesn't have (any more). Web Desktop and Process App can't reach the API.</summary>
    NotThisMachine,

    /// <summary>Empty or not a URL.</summary>
    Missing
}

/// <summary>This machine's addresses, as the checks need them.</summary>
/// <param name="Names">Computer name and DNS names (FQDN).</param>
/// <param name="FixedIPv4">Manually configured IPv4 addresses.</param>
/// <param name="DhcpIPv4">IPv4 addresses handed out by DHCP.</param>
public sealed record MachineAddresses(IReadOnlyList<string> Names, IReadOnlyList<string> FixedIPv4, IReadOnlyList<string> DhcpIPv4)
{
    public IEnumerable<string> AllIPv4 => FixedIPv4.Concat(DhcpIPv4);
}

/// <summary>
/// Pure rules for the address Web Desktop and Process App use to reach the
/// Business API: checking it, validating a new one, and rewriting a URL
/// to use it. Used by the dashboard and the Change address module.
/// </summary>
public static class GraniteAddress
{
    // One DNS label: letters, digits and hyphens, not starting or ending with a hyphen.
    private static readonly Regex Label = new("^(?!-)[A-Za-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant);

    /// <summary>The host part of a URL like https://host:40081/, or null.</summary>
    public static string? HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    public static bool IsIPv4(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3;

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IsIPv4(host) && host.StartsWith("127.", StringComparison.Ordinal));

    /// <summary>
    /// A new address someone may choose: an IPv4 address or a DNS name,
    /// nothing else (no ports, paths, spaces or quotes), and not localhost,
    /// which Web Desktop and the API refuse. The value ends up in config
    /// files and a certificate, so it's checked strictly.
    /// </summary>
    public static bool IsValidNewHost(string? host, out string error)
    {
        error = "";
        host = host?.Trim() ?? "";
        if (host.Length == 0) { error = "Enter an address."; return false; }
        if (IsLoopback(host)) { error = "localhost only works on the server itself. Use this computer's name or an IP address."; return false; }
        if (IsIPv4(host)) return true;
        if (host.Any(char.IsDigit) && host.All(c => char.IsDigit(c) || c == '.')) { error = $"\"{host}\" isn't a valid IPv4 address."; return false; }
        if (host.Length > 253) { error = "That name is too long for DNS (253 characters at most)."; return false; }
        if (!host.Split('.').All(l => Label.IsMatch(l)))
        {
            error = "Use a computer name, a DNS name (letters, digits, hyphens and dots) or an IPv4 address, with no https://, port or slash.";
            return false;
        }
        return true;
    }

    /// <summary>How healthy an API address is on this machine.</summary>
    public static AddressHealth Check(string? host, MachineAddresses machine)
    {
        if (string.IsNullOrWhiteSpace(host)) return AddressHealth.Missing;
        if (IsLoopback(host)) return AddressHealth.Localhost;
        if (IsIPv4(host))
        {
            if (machine.DhcpIPv4.Contains(host)) return AddressHealth.DhcpAddress;
            return machine.FixedIPv4.Contains(host) ? AddressHealth.Ok : AddressHealth.NotThisMachine;
        }
        bool own = machine.Names.Any(n => n.Equals(host, StringComparison.OrdinalIgnoreCase)
                                          || n.Split('.')[0].Equals(host, StringComparison.OrdinalIgnoreCase));
        return own ? AddressHealth.Ok : AddressHealth.NameNotChecked;
    }

    /// <summary>The same URL with another host: scheme, port, path and trailing slash kept.</summary>
    public static string WithHost(string url, string newHost)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException($"\"{url}\" isn't a URL.");
        string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        string path = uri.PathAndQuery;
        if (path == "/" && !url.TrimEnd().EndsWith('/')) path = "";
        return $"{uri.Scheme}://{newHost}{port}{path}";
    }

    /// <summary>
    /// An origin the way a browser sends it: scheme and host in lower case,
    /// the port, no trailing slash ("https://ultra:40099"). The Granite APIs
    /// compare AllowedOrigins letter for letter, so "https://Ultra:40099" or
    /// "https://ultra:40099/" in the list never matches what Chrome or Edge
    /// sends and the call is refused. Anything that isn't a URL is returned
    /// trimmed, unchanged.
    /// </summary>
    public static string NormalizeOrigin(string origin)
    {
        string trimmed = origin.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return trimmed;
        string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
    }

    /// <summary>The choices offered for a new address: names first, then fixed IPs, then DHCP IPs.</summary>
    public static IReadOnlyList<string> Suggestions(MachineAddresses machine) =>
        machine.Names.Concat(machine.FixedIPv4).Concat(machine.DhcpIPv4)
            .Where(h => !IsLoopback(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The address to suggest by default: a fixed IP if the machine has
    /// one (works for scanners with no DNS), otherwise the computer's name,
    /// which survives a DHCP change.
    /// </summary>
    public static string? DefaultChoice(MachineAddresses machine) =>
        machine.FixedIPv4.FirstOrDefault() ?? machine.Names.LastOrDefault(n => !n.Contains('.')) ?? machine.Names.FirstOrDefault();
}
