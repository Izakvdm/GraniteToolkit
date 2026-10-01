using System.Xml.Linq;

namespace Granite.Toolkit.Core.Iis;

/// <summary>An existing IIS site, as reported by appcmd list site /xml. State is "Started", "Stopped" or empty.</summary>
public sealed record IisSite(string Name, int Id, IReadOnlyList<IisBinding> Bindings, string State = "");

/// <summary>One site binding, e.g. https/*:40080:</summary>
public sealed record IisBinding(string Protocol, string Address, int Port, string HostName);

/// <summary>An IIS application, as reported by appcmd list app /xml.</summary>
public sealed record IisApp(string AppName, string SiteName, string AppPool, string Path);

/// <summary>A virtual directory, as reported by appcmd list vdir /xml.</summary>
public sealed record IisVdir(string AppName, string Path, string PhysicalPath);

/// <summary>An app pool, as reported by appcmd list apppool /xml.</summary>
public sealed record IisAppPool(string Name, string State);

/// <summary>
/// Builds the appcmd.exe / netsh.exe / icacls.exe argument lists every
/// toolkit module runs, and parses appcmd's XML output. Pure, so the
/// LogicHarness projects can check every command without touching IIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why appcmd and not Microsoft.Web.Administration.</b> The skeleton's
/// csproj planned to reference Microsoft.Web.Administration.dll from
/// %windir%\system32\inetsrv (the API Granite.Scaffolding.exe uses). That
/// DLL only exists once IIS is installed, and on a fresh server installing
/// IIS is this wizard's own job, two stages before the IIS stage. It's
/// also not a supported NuGet package, so a copy would have to be bundled
/// and kept version-matched by hand. appcmd.exe ships with every IIS
/// install, so the wizard picks it up at run time with nothing to bundle.
/// </para>
/// <para>
/// Every command is an argument list, never one concatenated string, so
/// paths with spaces (C:\Program Files\...) are quoted by
/// ProcessStartInfo.ArgumentList rather than by hand.
/// </para>
/// </remarks>
public static class IisCommands
{
    public static string AppCmdPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");

    public static string[] ListSites() => new[] { "list", "site", "/xml" };

    public static bool AppCmdExists() => File.Exists(AppCmdPath);

    public static string[] ListAppPools() => new[] { "list", "apppool", "/xml" };

    public static string[] ListApps() => new[] { "list", "app", "/xml" };

    public static string[] ListVdirs() => new[] { "list", "vdir", "/xml" };

    /// <summary>
    /// ASP.NET Core apps run through the ASP.NET Core Module, so the pool
    /// loads no CLR ("No Managed Code" = empty managedRuntimeVersion).
    /// AlwaysRunning plus idleTimeout 0 stops the pool unloading after 20
    /// idle minutes, which scanners notice as a slow first scan each morning.
    /// </summary>
    public static string[] AddAppPool(string pool) => new[]
    {
        "add", "apppool",
        $"/name:{pool}",
        "/managedRuntimeVersion:",
        "/startMode:AlwaysRunning",
        "/processModel.idleTimeout:00:00:00",
        "/processModel.identityType:ApplicationPoolIdentity"
    };

    /// <summary>Re-applies the pool settings to a pool that already existed.</summary>
    public static string[] ConfigureAppPool(string pool) => new[]
    {
        "set", "apppool", pool,
        "/managedRuntimeVersion:",
        "/startMode:AlwaysRunning",
        "/processModel.idleTimeout:00:00:00",
        "/processModel.identityType:ApplicationPoolIdentity"
    };

    /// <summary>
    /// Explicit /id avoids IIS picking one itself (the WebAdministration
    /// PowerShell equivalent fails outright on a server with no sites).
    /// </summary>
    /// <remarks>
    /// <paramref name="protocol"/> is https for the core Granite sites (the
    /// Install module binds a certificate) and http for Attach, which stays
    /// on the LAN with no certificate step.
    /// </remarks>
    public static string[] AddSite(string site, int id, string physicalPath, int port, string protocol = "https") => new[]
    {
        "add", "site",
        $"/name:{site}",
        $"/id:{id}",
        $"/physicalPath:{physicalPath}",
        $"/bindings:{protocol}/*:{port}:"
    };

    public static string[] SetAppPoolForRootApp(string site, string pool) => new[]
    {
        "set", "app", $"{site}/", $"/applicationPool:{pool}"
    };

    public static string[] EnablePreload(string site) => new[]
    {
        "set", "site", $"/site.name:{site}", "/applicationDefaults.preloadEnabled:true"
    };

    public static string[] StartAppPool(string pool) => new[] { "start", "apppool", $"/apppool.name:{pool}" };

    public static string[] StopSite(string site) => new[] { "stop", "site", $"/site.name:{site}" };

    public static string[] DeleteSite(string site) => new[] { "delete", "site", $"/site.name:{site}" };

    public static string[] StopAppPool(string pool) => new[] { "stop", "apppool", $"/apppool.name:{pool}" };

    public static string[] DeleteAppPool(string pool) => new[] { "delete", "apppool", $"/apppool.name:{pool}" };

    public static string[] DeleteFirewallRule(string ruleName) => new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleName}" };

    public static string[] RecycleAppPool(string pool) => new[] { "recycle", "apppool", $"/apppool.name:{pool}" };

    public static string[] StartSite(string site) => new[] { "start", "site", $"/site.name:{site}" };

    /// <summary>
    /// IIS https bindings get their certificate from http.sys's SSL
    /// binding table for ip:port, not from the site. appcmd can't set it;
    /// netsh is the standard way. The appid only identifies who registered
    /// the binding.
    /// </summary>
    public static string[] AddSslCert(int port, string thumbprint, Guid appId) => new[]
    {
        "http", "add", "sslcert",
        $"ipport=0.0.0.0:{port}",
        $"certhash={thumbprint}",
        $"appid={{{appId}}}",
        "certstorename=MY"
    };

    public static string[] ShowSslCert(int port) => new[] { "http", "show", "sslcert", $"ipport=0.0.0.0:{port}" };

    public static string[] DeleteSslCert(int port) => new[] { "http", "delete", "sslcert", $"ipport=0.0.0.0:{port}" };

    /// <summary>
    /// Modify rights for the pool identity on the app folder, inherited by
    /// every file: NLog and the ASP.NET Core stdout log write under the app
    /// folder, and ApplicationPoolIdentity has no write access there by default.
    /// </summary>
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

    public static string FirewallRuleName(string title, int port) => $"Granite WMS - {title} ({port})";

    /// <summary>
    /// The XML part of appcmd's output. Anything before the first '<' (a
    /// stray warning line) is skipped; output with no XML at all (appcmd's
    /// "ERROR ( message:... )") gives no rows. Malformed XML still throws,
    /// so a caller never mistakes a broken listing for an empty server.
    /// </summary>
    private static IEnumerable<XElement> Rows(string xml, string element)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<XElement>();
        int start = xml.IndexOf('<');
        if (start < 0) return Array.Empty<XElement>();
        return XDocument.Parse(xml[start..]).Descendants(element);
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? string.Empty;

    /// <summary>Parses appcmd list site /xml.</summary>
    public static IReadOnlyList<IisSite> ParseSites(string xml) =>
        Rows(xml, "SITE")
            .Select(e => new IisSite(
                Attr(e, "SITE.NAME"),
                int.TryParse(Attr(e, "SITE.ID"), out int id) ? id : 0,
                ParseBindings(Attr(e, "bindings")),
                Attr(e, "state")))
            .ToList();

    /// <summary>Parses appcmd list apppool /xml into pools with their state.</summary>
    public static IReadOnlyList<IisAppPool> ParseAppPools(string xml) =>
        Rows(xml, "APPPOOL")
            .Select(e => new IisAppPool(Attr(e, "APPPOOL.NAME"), Attr(e, "state")))
            .Where(p => p.Name.Length > 0)
            .ToList();

    /// <summary>Parses appcmd list apppool /xml into pool names only.</summary>
    public static IReadOnlyList<string> ParseAppPoolNames(string xml) =>
        ParseAppPools(xml).Select(p => p.Name).ToList();

    /// <summary>Parses appcmd list app /xml.</summary>
    public static IReadOnlyList<IisApp> ParseApps(string xml) =>
        Rows(xml, "APP")
            .Select(e => new IisApp(Attr(e, "APP.NAME"), Attr(e, "SITE.NAME"), Attr(e, "APPPOOL.NAME"), Attr(e, "path")))
            .ToList();

    /// <summary>Parses appcmd list vdir /xml.</summary>
    public static IReadOnlyList<IisVdir> ParseVdirs(string xml) =>
        Rows(xml, "VDIR")
            .Select(e => new IisVdir(Attr(e, "APP.NAME"), Attr(e, "path"), Attr(e, "physicalPath")))
            .ToList();

    /// <summary>"https/*:40080:,http/*:80:" into bindings. IPv6 addresses contain colons, so the port is the second-last field.</summary>
    public static IReadOnlyList<IisBinding> ParseBindings(string bindings)
    {
        var list = new List<IisBinding>();
        foreach (string raw in bindings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int slash = raw.IndexOf('/');
            if (slash < 0) continue;
            string protocol = raw[..slash];
            string info = raw[(slash + 1)..];
            int lastColon = info.LastIndexOf(':');
            if (lastColon < 0) continue;
            string host = info[(lastColon + 1)..];
            string addrPort = info[..lastColon];
            int portColon = addrPort.LastIndexOf(':');
            if (portColon < 0) continue;
            if (!int.TryParse(addrPort[(portColon + 1)..], out int port)) continue;
            list.Add(new IisBinding(protocol, addrPort[..portColon], port, host));
        }
        return list;
    }
}
