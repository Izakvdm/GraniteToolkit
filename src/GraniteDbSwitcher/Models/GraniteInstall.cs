using Granite.Toolkit.Core.Iis;

namespace GraniteDbSwitcher.Models;

public enum GraniteAppKind
{
    WebDesktop,
    BusinessApi,
    Custodian,
    ProcessApp
}

/// <summary>One Granite app found in IIS: its site, pool and folder.</summary>
public sealed record GraniteApp(
    GraniteAppKind Kind,
    string SiteName,
    string AppPool,
    string PhysicalPath,
    IReadOnlyList<IisBinding> Bindings)
{
    public string AppSettingsPath => Path.Combine(PhysicalPath, "appsettings.json");

    /// <summary>Which connection string this app reads (as named in the V6.0 and V7.0 releases).</summary>
    public string? ConnectionName => Kind switch
    {
        GraniteAppKind.BusinessApi => "CONNECTION",
        GraniteAppKind.Custodian => "CONNECTION",
        GraniteAppKind.ProcessApp => "ConnectionString",
        _ => null
    };

    public bool HasDatabaseConnection => ConnectionName is not null;

    /// <summary>A browsable URL on this machine: https first, localhost unless the binding has a host name.</summary>
    public string? LocalUrl
    {
        get
        {
            var b = Bindings.FirstOrDefault(x => x.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                    ?? Bindings.FirstOrDefault(x => x.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase));
            if (b is null) return null;
            string host = string.IsNullOrWhiteSpace(b.HostName) ? "localhost" : b.HostName;
            return $"{b.Protocol.ToLowerInvariant()}://{host}:{b.Port}/";
        }
    }

    public string Title => Kind switch
    {
        GraniteAppKind.WebDesktop => "Web Desktop",
        GraniteAppKind.BusinessApi => "Business API",
        GraniteAppKind.Custodian => "Custodian",
        GraniteAppKind.ProcessApp => "Process App",
        _ => Kind.ToString()
    };
}

/// <summary>
/// One Granite install: the Granite sites whose folders share a parent
/// folder, e.g. C:\Program Files\GraniteWMS\GraniteBusinessAPI and
/// ...\GraniteProcessApp are one install. Two installs (V6 and V7 side by
/// side) are simply two parent folders.
/// </summary>
public sealed class GraniteInstall
{
    public GraniteInstall(string rootFolder, IReadOnlyList<GraniteApp> apps)
    {
        RootFolder = rootFolder;
        Apps = apps;
    }

    public string RootFolder { get; }
    public IReadOnlyList<GraniteApp> Apps { get; }

    /// <summary>File version of Granite.Business.API.dll, e.g. 6.0.0.0 or 7.2.0.0.</summary>
    public Version? AppVersion { get; set; }

    public GraniteApp? Get(GraniteAppKind kind) => Apps.FirstOrDefault(a => a.Kind == kind);

    public IEnumerable<GraniteApp> DatabaseApps => Apps.Where(a => a.HasDatabaseConnection);

    public string DisplayName
    {
        get
        {
            string version = AppVersion is null ? "version unknown" : $"V{AppVersion.Major}.{AppVersion.Minor}";
            return $"{RootFolder}  ({version}, {Apps.Count} site{(Apps.Count == 1 ? "" : "s")})";
        }
    }

    public override string ToString() => DisplayName;
}
