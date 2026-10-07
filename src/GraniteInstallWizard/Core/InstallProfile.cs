using System.Text.Json;
using System.Text.Json.Serialization;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Every wizard answer except passwords, saved as JSON so the next server
/// can start from the same answers (Step 1's "Load profile", Step 6's
/// "Save profile", and InstallProfile.json written to the install folder
/// after a real install).
/// </summary>
public sealed class InstallProfile
{
    public string WizardVersion { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }

    public string ReleaseSourcePath { get; set; } = string.Empty;
    public string ReleaseFolder { get; set; } = string.Empty;
    public string InstallRoot { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string DateFormat { get; set; } = string.Empty;

    public bool InstallIisFeatures { get; set; }
    public bool InstallUrlRewrite { get; set; }
    public bool InstallDotNet8Hosting { get; set; }
    public bool InstallDotNet6Hosting { get; set; }

    public string SqlServer { get; set; } = string.Empty;
    public SqlAuthMode SqlAuth { get; set; }
    public string SqlAdminUser { get; set; } = string.Empty;
    public DatabaseMode DatabaseMode { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string AppLogin { get; set; } = string.Empty;
    public bool ApplyHotfix { get; set; }
    /// <summary>Null = the database mode's default. ResetExistingAppLoginPassword is deliberately not saved.</summary>
    public bool? DatabaseHotfixChoice { get; set; }

    // DropExistingDatabase deliberately not saved: a profile replayed on
    // the next server must never carry "drop the database" with it.

    public string PublicHost { get; set; } = string.Empty;
    public Dictionary<string, SiteSettings> Sites { get; set; } = new();
    public bool OpenFirewall { get; set; }
    public bool InstallAlongside { get; set; }

    public CertificateMode CertMode { get; set; }
    public string CertFriendlyName { get; set; } = string.Empty;
    public List<string> CertDnsNames { get; set; } = new();
    public List<string> CertIpAddresses { get; set; } = new();
    public bool TrustCertificate { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static InstallProfile From(InstallContext c, string wizardVersion) => new()
    {
        WizardVersion = wizardVersion,
        SavedAt = DateTime.Now,
        ReleaseSourcePath = c.ReleaseSourcePath,
        ReleaseFolder = c.ReleaseFolder,
        InstallRoot = c.InstallRoot,
        CompanyName = c.CompanyName,
        DateFormat = c.DateFormat,
        InstallIisFeatures = c.InstallIisFeatures,
        InstallUrlRewrite = c.InstallUrlRewrite,
        InstallDotNet8Hosting = c.InstallDotNet8Hosting,
        InstallDotNet6Hosting = c.InstallDotNet6Hosting,
        SqlServer = c.SqlServer,
        SqlAuth = c.SqlAuth,
        SqlAdminUser = c.SqlAdminUser,
        DatabaseMode = c.DatabaseMode,
        DatabaseName = c.DatabaseName,
        AppLogin = c.AppLogin,
        ApplyHotfix = c.ApplyHotfix,
        DatabaseHotfixChoice = c.DatabaseHotfixChoice,
        PublicHost = c.PublicHost,
        Sites = c.Sites.ToDictionary(kv => kv.Key, kv => new SiteSettings { Enabled = kv.Value.Enabled, SiteName = kv.Value.SiteName, Port = kv.Value.Port }),
        OpenFirewall = c.OpenFirewall,
        InstallAlongside = c.InstallAlongside,
        CertMode = c.CertMode,
        CertFriendlyName = c.CertFriendlyName,
        CertDnsNames = c.CertDnsNames.ToList(),
        CertIpAddresses = c.CertIpAddresses.ToList(),
        TrustCertificate = c.TrustCertificate
    };

    /// <summary>Copies the profile onto a context. Passwords and the drop flag are left as they are.</summary>
    public void ApplyTo(InstallContext c)
    {
        if (!string.IsNullOrWhiteSpace(ReleaseFolder)) c.ReleaseFolder = ReleaseFolder;
        c.ReleaseSourcePath = !string.IsNullOrWhiteSpace(ReleaseSourcePath) ? ReleaseSourcePath : ReleaseFolder;
        if (!string.IsNullOrWhiteSpace(InstallRoot)) c.InstallRoot = InstallRoot;
        if (!string.IsNullOrWhiteSpace(CompanyName)) c.CompanyName = CompanyName;
        if (!string.IsNullOrWhiteSpace(DateFormat)) c.DateFormat = DateFormat;
        c.InstallIisFeatures = InstallIisFeatures;
        c.InstallUrlRewrite = InstallUrlRewrite;
        c.InstallDotNet8Hosting = InstallDotNet8Hosting;
        c.InstallDotNet6Hosting = InstallDotNet6Hosting;
        c.SqlServer = SqlServer;
        c.SqlAuth = SqlAuth;
        c.SqlAdminUser = SqlAdminUser;
        c.DatabaseMode = DatabaseMode;
        if (!string.IsNullOrWhiteSpace(DatabaseName)) c.DatabaseName = DatabaseName;
        if (!string.IsNullOrWhiteSpace(AppLogin)) c.AppLogin = AppLogin;
        c.ApplyHotfix = ApplyHotfix;
        c.DatabaseHotfixChoice = DatabaseHotfixChoice;
        c.ResetExistingAppLoginPassword = false;
        c.ReplaceExistingSites = false;
        c.InstallAlongside = InstallAlongside;
        c.PublicHost = PublicHost;
        foreach (var (key, s) in Sites)
        {
            if (!c.Sites.ContainsKey(key)) continue;
            c.Sites[key].Enabled = s.Enabled || key == GraniteComponent.BusinessApi;
            c.Sites[key].SiteName = s.SiteName;
            c.Sites[key].Port = s.Port;
        }
        c.OpenFirewall = OpenFirewall;
        c.CertMode = CertMode;
        if (!string.IsNullOrWhiteSpace(CertFriendlyName)) c.CertFriendlyName = CertFriendlyName;
        c.CertDnsNames = CertDnsNames.ToList();
        c.CertIpAddresses = CertIpAddresses.ToList();
        c.TrustCertificate = TrustCertificate;
        c.LastSqlCheck = null;
        c.LastSqlCheckKey = null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static InstallProfile FromJson(string json) =>
        JsonSerializer.Deserialize<InstallProfile>(json, Options) ?? throw new InvalidDataException("The profile file is empty.");
}
