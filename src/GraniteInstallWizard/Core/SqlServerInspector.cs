using GraniteInstallWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Step 3's Test Connection, and the same checks again at pre-flight:
/// what this SQL Server is, and whether the install can go ahead on it.
/// </summary>
public static class SqlServerInspector
{
    public static async Task<SqlServerInfo> InspectAsync(InstallContext c, CancellationToken token)
    {
        await using SqlConnection conn = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.Admin(c), token);

        string version = (string?)await SqlConnectionFactory.ScalarAsync(conn,
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + N' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100));", token) ?? "(unknown)";
        int major = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(conn,
            "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), 4) AS int);", token) ?? 0);
        string login = (string?)await SqlConnectionFactory.ScalarAsync(conn, "SELECT SUSER_SNAME();", token) ?? "(unknown)";
        bool sysadmin = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(conn, "SELECT IS_SRVROLEMEMBER('sysadmin');", token) ?? 0) == 1;
        bool integratedOnly = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(conn,
            "SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS int);", token) ?? 0) == 1;
        bool dbExists = await SqlConnectionFactory.ScalarAsync(conn, "SELECT DB_ID(@db);", token, ("@db", c.DatabaseName)) is not null;
        bool looksGranite = dbExists && await LooksLikeGraniteAsync(conn, c.DatabaseName, token);
        bool loginExists = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(conn,
            "SELECT COUNT(*) FROM sys.server_principals WHERE name = @l;", token, ("@l", c.AppLogin)) ?? 0) > 0;

        return new SqlServerInfo(version, major, login, sysadmin, integratedOnly, dbExists, loginExists, looksGranite);
    }

    /// <summary>Tables every Granite database has; used to tell a Granite database from any other.</summary>
    public static IReadOnlyList<string> SignatureTables { get; } = new[] { "SystemSettings", "Process", "MasterItem" };

    /// <summary>
    /// True when the database has all of <see cref="SignatureTables"/>.
    /// OBJECT_ID takes a three-part name, so this needs no USE and no
    /// dynamic SQL; the database name is only ever a parameter.
    /// </summary>
    private static async Task<bool> LooksLikeGraniteAsync(SqlConnection conn, string database, CancellationToken token)
    {
        foreach (string table in SignatureTables)
        {
            object? id = await SqlConnectionFactory.ScalarAsync(conn,
                "SELECT OBJECT_ID(QUOTENAME(@db) + N'.dbo.' + QUOTENAME(@t), N'U');", token, ("@db", database), ("@t", table));
            if (id is null) return false;
        }
        return true;
    }

    /// <summary>
    /// Online user databases on the server that look like Granite
    /// databases, for Step 3's database dropdown in "use existing" mode.
    /// Databases the connecting account can't open are skipped.
    /// </summary>
    public static async Task<List<string>> ListGraniteDatabasesAsync(InstallContext c, CancellationToken token)
    {
        await using SqlConnection conn = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.Admin(c), token);
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        }
        var result = new List<string>();
        foreach (string name in names)
        {
            try { if (await LooksLikeGraniteAsync(conn, name, token)) result.Add(name); }
            catch (SqlException) { /* no rights in that database: skip it */ }
        }
        return result;
    }

    /// <summary>
    /// Every reason the install can't proceed on this server, as sentences
    /// ready to show. Empty means go.
    /// </summary>
    public static List<string> Blockers(SqlServerInfo info, InstallContext c)
    {
        var problems = new List<string>();
        if (!info.IsSysadmin)
            problems.Add(c.DatabaseMode == DatabaseMode.CreateNew
                ? $"The SQL account {info.ConnectedAs} is not sysadmin. The install needs sysadmin to create the database, the app login, and the SQLCLR assembly (the create script runs sp_configure 'clr enabled' and creates a certificate login in master)."
                : $"The SQL account {info.ConnectedAs} is not sysadmin. The install needs sysadmin to create the app login and make it db_owner of {c.DatabaseName}.");
        if (info.IntegratedSecurityOnly)
            problems.Add("SQL Server is set to Windows Authentication mode only. The Granite apps log in with a SQL login, so switch the server to \"SQL Server and Windows Authentication mode\" (SSMS: server Properties > Security) and restart the SQL Server service.");

        if (c.DatabaseMode == DatabaseMode.CreateNew)
        {
            if (info.DatabaseExists && !c.DropExistingDatabase)
                problems.Add($"Database {c.DatabaseName} already exists. GraniteDatabase_Create.sql drops and recreates its database, so the wizard won't run it over an existing one. Choose \"Use an existing Granite database\" to keep it, pick another name, or tick \"Drop and recreate\" if you really mean to replace it.");
        }
        else
        {
            if (!info.DatabaseExists)
                problems.Add($"Database {c.DatabaseName} doesn't exist on this server. Pick one from the list after Test Connection, or choose \"Create a new, clean database\".");
            else if (!info.DatabaseLooksLikeGranite)
                problems.Add($"Database {c.DatabaseName} doesn't look like a Granite database (it has no dbo.{string.Join(", dbo.", SignatureTables)}). The wizard won't point the apps at it.");
        }
        return problems;
    }

    /// <summary>Warnings that don't block the install.</summary>
    public static List<string> Warnings(SqlServerInfo info, InstallContext c)
    {
        var warnings = new List<string>();
        var hotfixScripts = HotfixScripts.Find(c);
        bool hotfixSql = c.ApplyDatabaseHotfix && hotfixScripts.Count > 0;
        if (info.MajorVersion > 0 && info.MajorVersion < 13 && hotfixSql)
            warnings.Add("SQL Server is older than 2016. The Hotfix database scripts are run as CREATE OR ALTER, which needs SQL Server 2016 SP1 or later.");
        if (c.DatabaseMode == DatabaseMode.CreateNew && info.DatabaseExists && c.DropExistingDatabase)
            warnings.Add($"Database {c.DatabaseName} exists and WILL BE DROPPED and recreated. Every row in it will be lost.");
        if (c.DatabaseMode == DatabaseMode.UseExisting && info.DatabaseExists && info.DatabaseLooksLikeGranite)
        {
            warnings.Add($"Using the existing database {c.DatabaseName}: the create script is not run and its data is kept.");
            if (hotfixSql)
                warnings.Add($"The Hotfix database scripts ({HotfixScripts.Describe(hotfixScripts)}) will be run against {c.DatabaseName} and change objects in it. Take a backup first if this is a live database.");
        }
        return warnings;
    }

    /// <summary>
    /// Only meaningful when the app login already exists: checks the
    /// password typed on Step 3 actually works for it, so the apps aren't
    /// configured with a password SQL Server will reject. The wizard only
    /// changes an existing login's password when "change its password" is
    /// ticked on Step 3 (<see cref="InstallContext.ResetExistingAppLoginPassword"/>).
    /// </summary>
    public static async Task<(bool Ok, string Message)> CheckExistingAppLoginAsync(InstallContext c, CancellationToken token)
    {
        try
        {
            await using var conn = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.AppLogin(c), token);
            return (true, $"Login {c.AppLogin} already exists and the password matches; it will be reused.");
        }
        catch (SqlException ex)
        {
            return (false, ExistingLoginMismatchMessage(c, ex.Message.TrimEnd('.')));
        }
    }

    /// <summary>What to say when the existing app login rejects the Step 3 password.</summary>
    public static string ExistingLoginMismatchMessage(InstallContext c, string sqlError) => c.ResetExistingAppLoginPassword
        ? $"Login {c.AppLogin} already exists with a different password ({sqlError}). Its password WILL BE CHANGED to the one entered; anything else using {c.AppLogin} will need the new password."
        : $"Login {c.AppLogin} already exists, but the password entered doesn't work for it ({sqlError}). Enter its current password, tick \"change its password\" to replace it, or use a different login name.";
}
