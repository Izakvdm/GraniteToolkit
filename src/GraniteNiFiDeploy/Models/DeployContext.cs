using GraniteNiFiDeploy.Core;

namespace GraniteNiFiDeploy.Models;

/// <summary>
/// Everything the wizard's steps gather, carried to the install step. One
/// instance lives for the lifetime of the wizard (same pattern as the other
/// modules' InstallContext). Passwords live here only until the install
/// finishes; <see cref="ForgetSecrets"/> clears them.
/// </summary>
public sealed class DeployContext
{
    // ----- Step 1: install media ----------------------------------------------

    /// <summary>The folder (or bundle zip) the user picked.</summary>
    public string MediaSource { get; set; } = string.Empty;

    /// <summary>The four downloads, found and checked. Null until Step 1 has scanned.</summary>
    public MediaSet? Media { get; set; }

    // ----- Step 2: NiFi service ------------------------------------------------

    public string InstallRoot { get; set; } = @"C:\nifi";
    public string ServiceName { get; set; } = "NiFi";
    public int Port { get; set; } = 8443;
    public string HeapSize { get; set; } = "1g";
    public string AdminUser { get; set; } = "graniteadmin";
    public string AdminPassword { get; set; } = string.Empty;
    public string ImportRoot { get; set; } = @"C:\GraniteImport";

    /// <summary>Optional Windows account (DOMAIN\user) that drops CSV files. Gets Modify on Inbound only.</summary>
    public string DropAccount { get; set; } = string.Empty;

    // ----- Step 3: Granite database and feeds ------------------------------------

    public string SqlServerInstance { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public bool UseSqlAuth { get; set; }
    public string SqlUser { get; set; } = string.Empty;
    public string SqlPassword { get; set; } = string.Empty;
    public bool ConnectionTested { get; set; }

    /// <summary>What Test Connection found; null until it has run.</summary>
    public SqlServerCheck? SqlCheck { get; set; }

    /// <summary>The SQL login NiFi signs in with. Its password is generated at install time and never shown.</summary>
    public string NiFiSqlLogin { get; set; } = "svc_granite_nifi";

    /// <summary>Feed names (Feeds.All) to deploy.</summary>
    public HashSet<string> Feeds { get; } = new(Core.Feeds.All.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

    public OrderDefaults SalesOrderDefaults { get; set; } = OrderDefaults.ForSalesOrder;
    public OrderDefaults PurchaseOrderDefaults { get; set; } = OrderDefaults.ForPurchaseOrder;

    /// <summary>When false (the default, as in the other modules) NiFi encrypts the SQL connection but accepts a self-signed SQL Server certificate.</summary>
    public bool ValidateSqlCertificate { get; set; }

    // ----- Derived -----------------------------------------------------------------

    public string NiFiHome => Path.Combine(InstallRoot, Media?.NiFiFolderName ?? "nifi");
    public string DriverFolder => Path.Combine(InstallRoot, "drivers");
    public string InboundFolder => Path.Combine(ImportRoot, "Inbound");
    public string ArchiveFolder => Path.Combine(ImportRoot, "Archive");
    public string ErrorFolder => Path.Combine(ImportRoot, "Error");
    public string NiFiUrl => $"https://localhost:{Port}/nifi";

    /// <summary>Connection string for the wizard's own SQL work (deploying the scripts and the login).</summary>
    public string ConnectionString => SqlConnectionStrings.Build(SqlServerInstance, DatabaseName, UseSqlAuth, SqlUser, SqlPassword);

    public void ForgetSecrets()
    {
        AdminPassword = string.Empty;
        SqlPassword = string.Empty;
    }
}
