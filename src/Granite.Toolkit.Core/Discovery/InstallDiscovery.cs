using Granite.Toolkit.Core.Iis;

namespace Granite.Toolkit.Core.Discovery;

/// <summary>
/// Finds the Granite installs in IIS: every site whose root folder holds a
/// Granite app, grouped by the folder those app folders sit in.
/// </summary>
/// <remarks>
/// Apps are recognised by what's in the folder rather than by site name,
/// since site names differ per install (the wizard's defaults, Scaffolding's,
/// or whatever was typed for a second V7 install):
/// Granite.Business.API.dll, Granite.Custodian.dll, Granite.Process.App.dll,
/// and for Web Desktop (a static site) index.html next to an appsettings.json
/// that has Business_API_Endpoint. The filesystem is reached through the
/// two delegates so the LogicHarness can feed it a fake one.
/// </remarks>
public static class InstallDiscovery
{
    public static IReadOnlyList<GraniteInstall> Build(
        IReadOnlyList<IisSite> sites,
        IReadOnlyList<IisApp> apps,
        IReadOnlyList<IisVdir> vdirs,
        Func<string, bool> fileExists,
        Func<string, string?> readText,
        Func<string, string>? expandPath = null)
    {
        expandPath ??= p => p;
        var found = new List<GraniteApp>();

        foreach (var app in apps.Where(a => a.Path == "/"))
        {
            var vdir = vdirs.FirstOrDefault(v =>
                v.AppName.Equals(app.AppName, StringComparison.OrdinalIgnoreCase) && v.Path == "/");
            if (vdir is null || string.IsNullOrWhiteSpace(vdir.PhysicalPath)) continue;

            string folder = expandPath(vdir.PhysicalPath).TrimEnd('\\', '/');
            var kind = Identify(folder, fileExists, readText);
            if (kind is null) continue;

            var site = sites.FirstOrDefault(s => s.Name.Equals(app.SiteName, StringComparison.OrdinalIgnoreCase));
            found.Add(new GraniteApp(kind.Value, app.SiteName, app.AppPool, folder,
                site?.Bindings ?? Array.Empty<IisBinding>()));
        }

        return found
            .GroupBy(a => ParentOf(a.PhysicalPath), StringComparer.OrdinalIgnoreCase)
            .Select(g => new GraniteInstall(g.Key, g.OrderBy(a => a.Kind).ToList()))
            .OrderBy(i => i.RootFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static GraniteAppKind? Identify(string folder, Func<string, bool> fileExists, Func<string, string?> readText)
    {
        if (fileExists(Path.Combine(folder, "Granite.Business.API.dll"))) return GraniteAppKind.BusinessApi;
        if (fileExists(Path.Combine(folder, "Granite.Custodian.dll"))) return GraniteAppKind.Custodian;
        if (fileExists(Path.Combine(folder, "Granite.Process.App.dll"))) return GraniteAppKind.ProcessApp;
        if (fileExists(Path.Combine(folder, "index.html")))
        {
            string? settings = readText(Path.Combine(folder, "appsettings.json"));
            if (settings is not null && settings.Contains("Business_API_Endpoint", StringComparison.OrdinalIgnoreCase))
                return GraniteAppKind.WebDesktop;
        }
        return null;
    }

    /// <summary>Parent folder, Windows-style whatever OS the harness runs on.</summary>
    public static string ParentOf(string folder)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut <= 0 ? trimmed : trimmed[..cut];
    }
}
