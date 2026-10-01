using System.Xml.Linq;

namespace GraniteAttachInstaller.Core;

public sealed record IisSite(string Name, int Id);

/// <summary>
/// Builds the appcmd.exe / icacls.exe / netsh.exe argument lists this
/// installer runs. Trimmed from the GraniteWMS Install Wizard's
/// Core/IisCommands.cs: same "why appcmd, not Microsoft.Web.Administration"
/// reasoning (see that file's remarks), http instead of https (no
/// certificate step here - this stays on the LAN, matching how the
/// GraniteAttach app has run so far), and only the commands this installer
/// actually needs.
/// </summary>
public static class IisCommands
{
    public static string AppCmdPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");

    public static bool AppCmdExists() => File.Exists(AppCmdPath);

    public static string[] ListSites() => new[] { "list", "site", "/xml" };

    public static string[] ListAppPools() => new[] { "list", "apppool", "/xml" };

    /// <summary>No Managed Code - ASP.NET Core runs through the ASP.NET Core Module, not the classic CLR pool.</summary>
    public static string[] AddAppPool(string pool) => new[]
    {
        "add", "apppool",
        $"/name:{pool}",
        "/managedRuntimeVersion:",
        "/startMode:AlwaysRunning",
        "/processModel.idleTimeout:00:00:00",
        "/processModel.identityType:ApplicationPoolIdentity"
    };

    public static string[] ConfigureAppPool(string pool) => new[]
    {
        "set", "apppool", pool,
        "/managedRuntimeVersion:",
        "/startMode:AlwaysRunning",
        "/processModel.idleTimeout:00:00:00",
        "/processModel.identityType:ApplicationPoolIdentity"
    };

    public static string[] AddSite(string site, int id, string physicalPath, int port) => new[]
    {
        "add", "site",
        $"/name:{site}",
        $"/id:{id}",
        $"/physicalPath:{physicalPath}",
        $"/bindings:http/*:{port}:"
    };

    public static string[] SetAppPoolForRootApp(string site, string pool) => new[]
    {
        "set", "app", $"{site}/", $"/applicationPool:{pool}"
    };

    public static string[] StartAppPool(string pool) => new[] { "start", "apppool", $"/apppool.name:{pool}" };

    public static string[] StartSite(string site) => new[] { "start", "site", $"/site.name:{site}" };

    public static string[] StopSite(string site) => new[] { "stop", "site", $"/site.name:{site}" };

    public static string[] DeleteSite(string site) => new[] { "delete", "site", $"/site.name:{site}" };

    public static string[] StopAppPool(string pool) => new[] { "stop", "apppool", $"/apppool.name:{pool}" };

    public static string[] DeleteAppPool(string pool) => new[] { "delete", "apppool", $"/apppool.name:{pool}" };

    /// <summary>NLog / ASP.NET Core stdout logging writes under the app folder; ApplicationPoolIdentity has no write access there by default.</summary>
    public static string[] GrantFolderModify(string path, string pool) => new[]
    {
        path, "/grant", $"IIS AppPool\\{pool}:(OI)(CI)M", "/Q"
    };

    public static string[] AddFirewallRule(string ruleName, int port) => new[]
    {
        "advfirewall", "firewall", "add", "rule",
        $"name={ruleName}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=any"
    };

    public static string[] ShowFirewallRule(string ruleName) => new[]
    {
        "advfirewall", "firewall", "show", "rule", $"name={ruleName}"
    };

    public static string FirewallRuleName(int port) => $"Granite Attach ({port})";

    /// <summary>Parses appcmd list site /xml into name + id pairs.</summary>
    public static IReadOnlyList<IisSite> ParseSites(string xml)
    {
        var result = new List<IisSite>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var el in doc.Descendants("SITE"))
            {
                string name = (string?)el.Attribute("SITE.NAME") ?? string.Empty;
                int.TryParse((string?)el.Attribute("SITE.ID"), out int id);
                result.Add(new IisSite(name, id));
            }
        }
        catch { /* malformed/empty xml - caller treats this as "no sites found" */ }
        return result;
    }

    public static IReadOnlyList<string> ParseAppPools(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<string>();
        try
        {
            return XDocument.Parse(xml).Descendants("APPPOOL")
                .Select(e => (string?)e.Attribute("APPPOOL.NAME") ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
