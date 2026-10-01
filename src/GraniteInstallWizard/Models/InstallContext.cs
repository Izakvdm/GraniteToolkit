using GraniteInstallWizard.Core;
namespace GraniteInstallWizard.Models;

public enum SqlAuthMode
{
    /// <summary>The Windows account this (elevated) wizard is running as.</summary>
    Windows,

    /// <summary>A SQL login, typically sa.</summary>
    SqlLogin
}

/// <summary>What Step 3 does with the Granite database.</summary>
public enum DatabaseMode
{
    /// <summary>Run GraniteDatabase_Create.sql to make a clean database.</summary>
    CreateNew,

    /// <summary>
    /// Point the apps at a Granite database that already exists (a restored
    /// backup, a copy of live, a database moved from another server). The
    /// create script is never run against it and no data is changed.
    /// </summary>
    UseExisting
}

public enum CertificateMode
{
    CreateSelfSigned,
    UseExisting
}

/// <summary>IIS settings for one component, from Step 4.</summary>
public sealed class SiteSettings
{
    public bool Enabled { get; set; } = true;
    public string SiteName { get; set; } = string.Empty;
    public int Port { get; set; }
}

/// <summary>
/// What Step 3's Test Connection found out about the SQL Server, kept so
/// Step 3's validation can insist the test actually passed for the
/// settings currently entered, not an earlier set.
/// </summary>
public sealed record SqlServerInfo(
    string Version,
    int MajorVersion,
    string ConnectedAs,
    bool IsSysadmin,
    bool IntegratedSecurityOnly,
    bool DatabaseExists,
    bool AppLoginExists,
    bool DatabaseLooksLikeGranite);

/// <summary>
/// Everything gathered from the six wizard panels, carried forward so later
/// steps (and the install run) can act on it. One instance lives for the
/// lifetime of the wizard and is passed to each step control -- the same
/// pattern as the BI Deployment Wizard's DeploymentContext.
/// </summary>
public sealed class InstallContext
{
    // ----- Step 1: release and install folder ------------------------------
    /// <summary>What Step 1 points at: a release folder or a release .zip (v0.4.0).</summary>
    public string ReleaseSourcePath { get; set; } = string.Empty;

    /// <summary>The release folder actually read from: ReleaseSourcePath itself, or where its zip was extracted.</summary>
    public string ReleaseFolder { get; set; } = string.Empty;

    /// <summary>Default C:\Program Files\GraniteWMS (Izak, 2026-09-29). Created on Step 1 if it doesn't exist.</summary>
    public string InstallRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : @"C:\Program Files", "GraniteWMS");
    public string CompanyName { get; set; } = "Granite WMS";

    /// <summary>
    /// Web Desktop's (moment.js) spelling, e.g. DD/MM/YYYY. The Business
    /// API's .NET spelling is derived from it -- see DateFormatConverter.
    /// </summary>
    public string DateFormat { get; set; } = "DD/MM/YYYY";

    // ----- Step 2: prerequisites --------------------------------------------
    public bool InstallIisFeatures { get; set; } = true;
    public bool InstallUrlRewrite { get; set; } = true;
    public bool InstallDotNet8Hosting { get; set; } = true;
    public bool InstallDotNet6Hosting { get; set; }

    // ----- Step 3: SQL Server -------------------------------------------------
    public string SqlServer { get; set; } = string.Empty;
    public SqlAuthMode SqlAuth { get; set; } = SqlAuthMode.Windows;
    public string SqlAdminUser { get; set; } = "sa";
    public string SqlAdminPassword { get; set; } = string.Empty;
    public DatabaseMode DatabaseMode { get; set; } = DatabaseMode.CreateNew;
    public string DatabaseName { get; set; } = "GraniteDatabase";
    public string AppLogin { get; set; } = "Granite";
    public string AppPassword { get; set; } = string.Empty;
    public bool DropExistingDatabase { get; set; }
    /// <summary>Hotfix binaries (Hotfix\BusinessAPI, Hotfix\ProcessApp) over the copied apps.</summary>
    public bool ApplyHotfix { get; set; } = true;

    /// <summary>
    /// Whether to run Hotfix\Database\*.sql and the SQL in Hotfix\*.md.
    /// Separate from <see cref="ApplyHotfix"/> because against an existing
    /// database they change live objects (a view, the SQLCLR assembly, the
    /// Custodian token). Defaults by mode -- on for a new database, off for
    /// an existing one -- unless the user has ticked or unticked the box,
    /// which <see cref="DatabaseHotfixChoice"/> records. (v0.3.0 stored a
    /// plain bool, and re-entering Step 3 in "existing" mode put the
    /// new-database default back; see the v0.3.1 README entry.)
    /// </summary>
    public bool ApplyDatabaseHotfix => DatabaseHotfixChoice ?? DatabaseMode == DatabaseMode.CreateNew;

    /// <summary>The user's explicit choice for the hotfix database scripts, or null for the mode's default.</summary>
    public bool? DatabaseHotfixChoice { get; set; }

    /// <summary>
    /// Opt-in: if the app login already exists and the Step 3 password
    /// doesn't work for it, change the login's password rather than
    /// stopping. Off by default and never saved in a profile, like
    /// <see cref="DropExistingDatabase"/>: whatever else uses the login
    /// (an older install, an integration) stops working until it gets
    /// the new password too.
    /// </summary>
    public bool ResetExistingAppLoginPassword { get; set; }

    /// <summary>Result of the last Test Connection, and the settings it ran against.</summary>
    public SqlServerInfo? LastSqlCheck { get; set; }
    public string? LastSqlCheckKey { get; set; }

    /// <summary>Identifies the Step 3 settings a SqlServerInfo belongs to.</summary>
    public string SqlCheckKey =>
        string.Join("|", SqlServer, SqlAuth, SqlAdminUser, SqlAdminPassword, DatabaseMode, DatabaseName, AppLogin);

    // ----- Step 4: websites ---------------------------------------------------
    public string PublicHost { get; set; } = string.Empty;
    public Dictionary<string, SiteSettings> Sites { get; } = GraniteComponent.CoreStack.ToDictionary(
        c => c.Key,
        c => new SiteSettings { Enabled = true, SiteName = c.DefaultSiteName, Port = c.DefaultPort });
    public bool OpenFirewall { get; set; } = true;

    /// <summary>
    /// Opt-in (v0.3.2): IIS sites that already have the Step 4 names are
    /// removed (with their app pools, http.sys certificate bindings and
    /// firewall rules) and recreated, so the wizard can reinstall over its
    /// own earlier install. Off by default and never saved in a profile.
    /// </summary>
    public bool ReplaceExistingSites { get; set; }

    // ----- Step 5: certificate --------------------------------------------------
    public CertificateMode CertMode { get; set; } = CertificateMode.CreateSelfSigned;
    public string CertFriendlyName { get; set; } = "Granite WMS";
    public List<string> CertDnsNames { get; set; } = new();
    public List<string> CertIpAddresses { get; set; } = new();
    public bool TrustCertificate { get; set; } = true;
    public string? ExistingCertThumbprint { get; set; }

    /// <summary>Names on the existing certificate picked on Step 5, used for CORS origins.</summary>
    public List<string> ExistingCertNames { get; set; } = new();

    // ----- Step 6 ----------------------------------------------------------------
    public bool DryRun { get; set; }

    // ----- Derived -----------------------------------------------------------------
    public IEnumerable<GraniteComponent> EnabledComponents =>
        GraniteComponent.CoreStack.Where(c => Sites[c.Key].Enabled);

    public bool IsEnabled(string key) => Sites.TryGetValue(key, out var s) && s.Enabled;

    public string InstallPathFor(GraniteComponent c) => Path.Combine(InstallRoot, c.ReleaseFolder);

    /// <summary>The component's folder in the release, however that release spells it (GraniteBusinessAPI or GraniteBusinessApi).</summary>
    public string ReleasePathFor(GraniteComponent c) => ReleaseLayout.Resolve(ReleaseFolder, c.ReleaseFolder);

    /// <summary>
    /// The component's Hotfix folder, or null when the release has none.
    /// V6.0 calls it Hotfix\BusinessAPI, V7.0 Hotfix\Business Api.
    /// </summary>
    public string? HotfixPathFor(GraniteComponent c) =>
        c.HotfixFolder.Length == 0 || !Directory.Exists(HotfixRoot) ? null : ReleaseLayout.FindDirectory(HotfixRoot, c.HotfixFolder);

    /// <summary>https://host:port/ for a component; defaults to the Step 4 address.</summary>
    public string UrlFor(string key, string? host = null) =>
        $"https://{host ?? PublicHost}:{Sites[key].Port}/";

    /// <summary>
    /// Every host name a browser could use to reach this server: the Step 4
    /// address, every name and IP on the certificate, and localhost.
    /// </summary>
    public IReadOnlyList<string> AllHostNames()
    {
        var names = new List<string>();
        void Add(string? h)
        {
            if (!string.IsNullOrWhiteSpace(h) && !names.Contains(h, StringComparer.OrdinalIgnoreCase))
                names.Add(h.Trim());
        }
        Add(PublicHost);
        var certNames = CertMode == CertificateMode.UseExisting ? ExistingCertNames : CertDnsNames.Concat(CertIpAddresses).ToList();
        foreach (var n in certNames) Add(n);
        Add("localhost");
        return names;
    }

    /// <summary>
    /// CORS origins for the given components, one per host name (see
    /// <see cref="AllHostNames"/>). A browser sends whichever origin the
    /// user actually typed -- IP on a scanner, hostname on a PC -- and the
    /// API only answers origins it lists, so listing just one breaks the
    /// others with an opaque CORS error.
    /// </summary>
    public string[] OriginsFor(params string[] keys)
    {
        var origins = new List<string>();
        foreach (string key in keys)
        {
            if (!IsEnabled(key)) continue;
            foreach (string host in AllHostNames())
                origins.Add($"https://{host}:{Sites[key].Port}");
        }
        return origins.ToArray();
    }

    public string CreateScriptPath =>
        ReleaseLayout.Resolve(ReleaseFolder, "GraniteDatabase", "GraniteDatabase", "GraniteDatabase_Create.sql");

    public string HotfixRoot => ReleaseLayout.Resolve(ReleaseFolder, "Hotfix");

    public string PrerequisitesRoot => ReleaseLayout.Resolve(ReleaseFolder, "GraniteScaffold", "Prerequisites");
}
