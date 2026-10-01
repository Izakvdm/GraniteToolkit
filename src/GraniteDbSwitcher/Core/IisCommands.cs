using System.Xml.Linq;
using GraniteDbSwitcher.Models;

namespace GraniteDbSwitcher.Core;

public sealed record IisSiteRow(string Name, IReadOnlyList<IisBinding> Bindings, string State);
public sealed record IisAppRow(string AppName, string SiteName, string AppPool, string Path);
public sealed record IisVdirRow(string AppName, string Path, string PhysicalPath);
public sealed record IisPoolRow(string Name, string State);

/// <summary>
/// appcmd.exe argument lists and parsers for its /xml output. Pure, so the
/// LogicHarness can test them. appcmd rather than
/// Microsoft.Web.Administration for the same reasons as the install wizard
/// (nothing to bundle; ships with every IIS install).
/// </summary>
public static class IisCommands
{
    public static string AppCmdPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");

    public static string[] ListSites() => new[] { "list", "site", "/xml" };
    public static string[] ListApps() => new[] { "list", "app", "/xml" };
    public static string[] ListVdirs() => new[] { "list", "vdir", "/xml" };
    public static string[] ListAppPools() => new[] { "list", "apppool", "/xml" };
    public static string[] RecycleAppPool(string pool) => new[] { "recycle", "apppool", $"/apppool.name:{pool}" };
    public static string[] StartAppPool(string pool) => new[] { "start", "apppool", $"/apppool.name:{pool}" };

    private static IEnumerable<XElement> Rows(string xml, string element)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<XElement>();
        int start = xml.IndexOf('<');
        if (start < 0) return Array.Empty<XElement>();
        return XDocument.Parse(xml[start..]).Descendants(element);
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? string.Empty;

    public static IReadOnlyList<IisSiteRow> ParseSites(string xml) =>
        Rows(xml, "SITE")
            .Select(e => new IisSiteRow(Attr(e, "SITE.NAME"), ParseBindings(Attr(e, "bindings")), Attr(e, "state")))
            .ToList();

    public static IReadOnlyList<IisAppRow> ParseApps(string xml) =>
        Rows(xml, "APP")
            .Select(e => new IisAppRow(Attr(e, "APP.NAME"), Attr(e, "SITE.NAME"), Attr(e, "APPPOOL.NAME"), Attr(e, "path")))
            .ToList();

    public static IReadOnlyList<IisVdirRow> ParseVdirs(string xml) =>
        Rows(xml, "VDIR")
            .Select(e => new IisVdirRow(Attr(e, "APP.NAME"), Attr(e, "path"), Attr(e, "physicalPath")))
            .ToList();

    public static IReadOnlyList<IisPoolRow> ParseAppPools(string xml) =>
        Rows(xml, "APPPOOL")
            .Select(e => new IisPoolRow(Attr(e, "APPPOOL.NAME"), Attr(e, "state")))
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
