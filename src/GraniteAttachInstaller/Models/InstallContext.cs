namespace GraniteAttachInstaller.Models;

/// <summary>
/// Everything gathered from the wizard's panels, carried forward so the
/// install step can act on it. One instance lives for the lifetime of the
/// wizard - same pattern as the GraniteWMS Install Wizard's InstallContext.
/// </summary>
public sealed class InstallContext
{
    // ----- Step 1: SQL Server & database -----------------------------------
    public string SqlServerInstance { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public bool UseSqlAuth { get; set; }
    public string SqlUser { get; set; } = string.Empty;
    public string SqlPassword { get; set; } = string.Empty;

    /// <summary>Set by Step 1's "Test Connection" - Step 1 won't advance until this is true for the settings currently entered.</summary>
    public bool ConnectionTested { get; set; }

    // ----- Step 2: app, IIS site, process-step kit --------------------------

    /// <summary>Path to GraniteAttach.csproj - auto-detected relative to the installer, or browsed to.</summary>
    public string ProjectPath { get; set; } = string.Empty;

    public string SiteName { get; set; } = "Granite Attach";
    public string AppPoolName { get; set; } = "GraniteAttachAppPool";
    public int Port { get; set; } = 5080;
    public string PhysicalPath { get; set; } = @"C:\inetpub\GraniteAttach";

    /// <summary>
    /// Optional. When set, the install step also resolves and runs the
    /// three process-step kit stored procedures for this process name, and
    /// writes the resolved WebTemplate HTML file. Leave blank to install
    /// just the app and the storage tables, and run this installer again
    /// later once you've decided the process name.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Where phones/scanners on the network reach the app - auto-detected LAN IP, editable.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    // ----- Filled in during install ------------------------------------------
    public string EncryptionKeyBase64 { get; set; } = string.Empty;
    public string CaptureLinkSecret { get; set; } = string.Empty;
    public string? ResolvedWebTemplatePath { get; set; }
    public string SqlFolder { get; set; } = string.Empty;

    public string ConnectionString =>
        UseSqlAuth
            ? $"Server={SqlServerInstance};Database={DatabaseName};User Id={SqlUser};Password={SqlPassword};TrustServerCertificate=True;"
            : $"Server={SqlServerInstance};Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;";
}
