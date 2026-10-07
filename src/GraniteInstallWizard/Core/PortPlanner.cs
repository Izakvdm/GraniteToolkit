using System.Text.RegularExpressions;

namespace GraniteInstallWizard.Core;

/// <summary>A range of ports Windows has reserved (netsh "excludedportrange"); nothing else can bind inside it.</summary>
public readonly record struct PortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;
    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

/// <summary>What's already using ports on this server, from IIS, the TCP listener table and Windows' reserved ranges.</summary>
public sealed class PortUse
{
    private readonly Dictionary<int, string> _siteByPort = new();
    private readonly HashSet<int> _listening;
    private readonly HashSet<int> _replacedSitePorts = new();
    private readonly List<PortRange> _excluded;

    /// <param name="sites">Every IIS site on the server.</param>
    /// <param name="listening">Every port something is listening on (IIS sites included).</param>
    /// <param name="excluded">Windows' reserved port ranges.</param>
    /// <param name="replacedSiteNames">Sites this install will remove and recreate; their ports count as free.</param>
    public PortUse(IEnumerable<IisSite> sites, IEnumerable<int> listening, IEnumerable<PortRange> excluded, IEnumerable<string>? replacedSiteNames = null)
    {
        var replaced = (replacedSiteNames ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            foreach (var b in site.Bindings)
            {
                if (replaced.Contains(site.Name)) _replacedSitePorts.Add(b.Port);
                else _siteByPort.TryAdd(b.Port, site.Name);
            }
        }
        _listening = listening.ToHashSet();
        _excluded = excluded.ToList();
    }

    public IReadOnlyList<PortRange> Excluded => _excluded;

    /// <summary>
    /// Free means: in the safe range, not bound by an IIS site that's being
    /// kept, not inside a Windows reserved range, and not listened on by
    /// anything other than a site being replaced.
    /// </summary>
    public bool IsFree(int port) => WhyTaken(port) is null;

    /// <summary>Why a port isn't free, for the status line and pre-flight; null when it is free.</summary>
    public string? WhyTaken(int port)
    {
        if (port < PortPlanner.SafeMin || port > PortPlanner.SafeMax)
            return $"outside {PortPlanner.SafeMin}-{PortPlanner.SafeMax} (below is for system services, above is what Windows uses for outgoing connections)";
        if (_siteByPort.TryGetValue(port, out string? site)) return $"used by the IIS site \"{site}\"";
        foreach (var r in _excluded)
            if (r.Contains(port)) return $"reserved by Windows (excluded range {r}, usually Hyper-V, WSL or Docker)";
        if (_listening.Contains(port) && !_replacedSitePorts.Contains(port)) return "in use by another program";
        return null;
    }
}

/// <summary>
/// Picks HTTPS ports and site names for an install that has to sit next to
/// an existing Granite install (V7 beside V6, a test stack beside live).
/// Pure, so the LogicHarness can check it.
/// </summary>
/// <remarks>
/// Added in v0.7.0. Ports are kept as a block where possible: the defaults
/// shifted by 100, 200, ... (40080-40099 becomes 40180-40199), so the second
/// install is easy to recognise. Only if no shifted block is wholly free is
/// each port picked on its own, by counting up from its default.
///
/// Safe range 1024-49151: below 1024 is reserved for system services, and
/// 49152 and up is Windows' range for outgoing connections, so a site there
/// can clash at random. Windows also reserves blocks inside the safe range
/// for Hyper-V, WSL and Docker ("netsh int ipv4 show excludedportrange"),
/// which IIS can't bind to; those are read and avoided too.
/// </remarks>
public static class PortPlanner
{
    public const int SafeMin = 1024;
    public const int SafeMax = 49151;

    /// <summary>Ports for each component, keyed like the input. Throws if the safe range is somehow exhausted.</summary>
    public static Dictionary<string, int> Suggest(IReadOnlyList<(string Key, int Preferred)> wanted, Func<int, bool> isFree)
    {
        for (int offset = 0; offset <= 900; offset += 100)
        {
            var block = wanted.Select(w => (w.Key, Port: w.Preferred + offset)).ToList();
            if (block.Select(b => b.Port).Distinct().Count() == block.Count && block.All(b => isFree(b.Port)))
                return block.ToDictionary(b => b.Key, b => b.Port);
        }

        var result = new Dictionary<string, int>();
        var used = new HashSet<int>();
        foreach (var (key, preferred) in wanted)
        {
            int start = Math.Clamp(preferred, SafeMin, SafeMax);
            int? found = null;
            for (int p = start; p <= SafeMax && found is null; p++)
                if (!used.Contains(p) && isFree(p)) found = p;
            for (int p = SafeMin; p < start && found is null; p++)
                if (!used.Contains(p) && isFree(p)) found = p;
            if (found is null) throw new InvalidOperationException("No free port found between 1024 and 49151.");
            result[key] = found.Value;
            used.Add(found.Value);
        }
        return result;
    }

    /// <summary>
    /// Suffix for the site names, e.g. " V7". Empty when none of the base
    /// names exists yet. Otherwise the release's own version (from a folder
    /// or zip name like "Granite V7.0") if that makes every name unique, then
    /// " 2", " 3", ...
    /// </summary>
    public static string SuggestNameSuffix(IEnumerable<string> baseNames, IEnumerable<string> existingSiteNames, string? releaseName)
    {
        var existing = existingSiteNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = baseNames.ToList();
        bool Fits(string suffix) => names.All(n => !existing.Contains(n + suffix));
        if (Fits("")) return "";
        if (ReleaseVersion(releaseName) is string v && Fits($" V{v}")) return $" V{v}";
        for (int i = 2; i < 100; i++)
            if (Fits($" {i}")) return $" {i}";
        return $" {Guid.NewGuid().ToString("N")[..4]}";
    }

    /// <summary>"7" from "Granite V7.0", "Granite V7.0.zip" or "...\Granite V6.0"; null if there's no version in the name.</summary>
    public static string? ReleaseVersion(string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return null;
        string name = Path.GetFileName(releaseName.TrimEnd('\\', '/'));
        var m = Regex.Match(name, @"\bV\s?(?<v>\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>
    /// Reads "netsh interface ipv4 show excludedportrange protocol=tcp".
    /// Each data line is "start end" with an optional "*" for an
    /// administered exclusion; headers and dashes are skipped.
    /// </summary>
    public static List<PortRange> ParseExcludedRanges(string netshOutput)
    {
        var ranges = new List<PortRange>();
        foreach (string line in netshOutput.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?<s>\d{1,5})\s+(?<e>\d{1,5})\s*\*?\s*$");
            if (m.Success && int.TryParse(m.Groups["s"].Value, out int s) && int.TryParse(m.Groups["e"].Value, out int e) && s <= e && e <= 65535)
                ranges.Add(new PortRange(s, e));
        }
        return ranges;
    }
}
