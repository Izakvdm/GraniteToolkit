using System.Text.RegularExpressions;

namespace Granite.Toolkit.Core.Addressing;

/// <summary>Pure checks on certificate names and netsh output, for the Change address module and the harness.</summary>
public static class CertificateCoverage
{
    /// <summary>
    /// Whether a certificate with these Subject Alternative Names is valid
    /// for <paramref name="host"/>: an exact match, or a single-level
    /// wildcard (*.example.com covers a.example.com, not a.b.example.com or
    /// example.com). IP addresses only ever match exactly.
    /// </summary>
    public static bool Covers(IEnumerable<string> names, string host)
    {
        foreach (string raw in names)
        {
            string name = raw.Trim();
            if (name.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("*.", StringComparison.Ordinal) && !GraniteAddress.IsIPv4(host))
            {
                int dot = host.IndexOf('.');
                if (dot > 0 && host[(dot + 1)..].Equals(name[2..], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static readonly Regex Thumbprint = new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{40}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);

    /// <summary>
    /// The certificate thumbprint in "netsh http show sslcert" output, or
    /// null. Found as the only 40-hex-digit value in the output rather than
    /// by its label, which Windows translates ("Certificate Hash",
    /// "Zertifikathash", ...). The application ID is a GUID, so never matches.
    /// </summary>
    public static string? ThumbprintFromNetsh(string output)
    {
        var matches = Thumbprint.Matches(output).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// The names a replacement self-signed certificate should carry: the old
    /// ones, minus IPs this machine no longer has, plus the new address.
    /// DNS names and IPs are returned separately (the certificate needs them
    /// as different SAN types).
    /// </summary>
    public static (IReadOnlyList<string> Dns, IReadOnlyList<string> Ips) NamesForReissue(IEnumerable<string> oldNames, string newHost, MachineAddresses machine)
    {
        var dns = new List<string>();
        var ips = new List<string>();
        void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (GraniteAddress.IsIPv4(name))
            {
                if (GraniteAddress.Check(name, machine) == AddressHealth.NotThisMachine) return; // stale
                if (!ips.Contains(name)) ips.Add(name);
            }
            else if (!dns.Contains(name, StringComparer.OrdinalIgnoreCase)) dns.Add(name);
        }
        // The new address leads (it becomes the certificate's CN), then the rest.
        Add(newHost);
        foreach (string n in oldNames) Add(n);
        foreach (string n in machine.Names) Add(n);
        Add("localhost");
        foreach (string ip in machine.AllIPv4) Add(ip);
        // A certificate needs at least one DNS name for its subject.
        if (dns.Count == 0) dns.AddRange(machine.Names.Take(1).DefaultIfEmpty("localhost"));
        return (dns, ips);
    }
}
