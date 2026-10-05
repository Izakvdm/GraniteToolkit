namespace Granite.Toolkit.Core.Discovery;

/// <summary>An Apache NiFi install running as a Windows service under NSSM.</summary>
/// <param name="ServiceName">The Windows service name.</param>
/// <param name="NiFiHome">The NiFi folder (the one holding bin and conf).</param>
/// <param name="Version">From the folder name (nifi-2.11.0), or null when the folder was renamed.</param>
/// <param name="State">RUNNING, STOPPED, START_PENDING and so on (sc.exe's words), or null when it couldn't be read.</param>
public sealed record NiFiService(string ServiceName, string NiFiHome, string? Version, string? State)
{
    public bool IsRunning => string.Equals(State, "RUNNING", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pure parsing for NiFiServiceDiscovery: no registry, no processes, so
/// the harnesses link this file and check it anywhere.
/// </summary>
public static class NiFiServiceInfo
{
    /// <summary>
    /// The NiFi home for an NSSM Application value, or null when it isn't
    /// NiFi. Accepts <c>C:\nifi\nifi-2.11.0\bin\nifi.cmd</c> (with or without
    /// quotes) and nothing else.
    /// </summary>
    public static string? HomeFromApplication(string? application)
    {
        if (string.IsNullOrWhiteSpace(application)) return null;
        string path = application.Trim().Trim('"').Replace('/', '\\');
        if (!path.EndsWith(@"\bin\nifi.cmd", StringComparison.OrdinalIgnoreCase)) return null;
        string home = path[..^@"\bin\nifi.cmd".Length];
        return home.Length == 0 ? null : home;
    }

    /// <summary>"2.11.0" from "C:\nifi\nifi-2.11.0", or null for any other folder name.</summary>
    public static string? VersionFromHome(string home)
    {
        string leaf = home.TrimEnd('\\', '/');
        int cut = leaf.LastIndexOfAny(new[] { '\\', '/' });
        if (cut >= 0) leaf = leaf[(cut + 1)..];
        var m = System.Text.RegularExpressions.Regex.Match(leaf, @"^nifi-(\d+\.\d+\.\d+(?:[.-][0-9A-Za-z]+)*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The STATE word from <c>sc query</c> output ("RUNNING"), or null.</summary>
    public static string? ParseScState(string output)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
            // "STATE              : 4  RUNNING"
            string[] parts = line.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) return parts[2].ToUpperInvariant();
        }
        return null;
    }

    /// <summary>
    /// The Description of each event in <c>wevtutil qe ... /f:text</c> output,
    /// in the order given, whitespace collapsed to one line each.
    /// </summary>
    public static IReadOnlyList<string> ParseEventDescriptions(string output)
    {
        var result = new List<string>();
        System.Text.StringBuilder? current = null;
        void Flush()
        {
            if (current is null) return;
            string text = System.Text.RegularExpressions.Regex.Replace(current.ToString(), @"\s+", " ").Trim();
            if (text.Length > 0) result.Add(text);
            current = null;
        }
        foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^Event\[\d+\]:")) { Flush(); continue; }
            if (current is null)
            {
                string t = line.TrimStart();
                if (t.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                    current = new System.Text.StringBuilder(t["Description:".Length..]).Append(' ');
                continue;
            }
            current.Append(line).Append(' ');
        }
        Flush();
        return result;
    }
}
