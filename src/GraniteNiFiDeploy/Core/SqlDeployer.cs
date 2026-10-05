using Microsoft.Data.SqlClient;

namespace GraniteNiFiDeploy.Core;

public static class SqlConnectionStrings
{
    /// <summary>
    /// The wizard's own connection, built with SqlConnectionStringBuilder so
    /// a password with ; or = in it can't change the connection string.
    /// </summary>
    public static string Build(string server, string database, bool sqlAuth, string user, string password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            ApplicationName = "Granite NiFi Deploy",
            ConnectTimeout = 15
        };
        if (sqlAuth)
        {
            b.UserID = user;
            b.Password = password;
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }
}

/// <summary>What Test Connection found out about the server and database.</summary>
public sealed record SqlServerCheck(
    string Version,
    bool MixedModeAuth,
    bool CanCreateLogins,
    bool CanCreateObjects,
    IReadOnlyList<string> MissingGraniteTables,
    bool FrameworkAlreadyDeployed)
{
    /// <summary>Problems that stop the install.</summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var list = new List<string>();
            if (!MixedModeAuth) list.Add("SQL Server only allows Windows sign-in. NiFi signs in with a SQL login, so turn on \"SQL Server and Windows Authentication mode\" (server properties, Security) and restart SQL Server.");
            if (!CanCreateLogins) list.Add("This sign-in can't create SQL logins (needs ALTER ANY LOGIN, e.g. the securityadmin or sysadmin role).");
            if (!CanCreateObjects) list.Add("This sign-in can't create tables and procedures in the database (needs db_owner or sysadmin).");
            if (MissingGraniteTables.Count > 0) list.Add("This doesn't look like a GraniteWMS database: no " + string.Join(", ", MissingGraniteTables) + ".");
            return list;
        }
    }
}

/// <summary>
/// Everything NiFi Deploy does in SQL Server: checks, the embedded
/// framework and feed scripts, and NiFi's own least-privilege login.
/// </summary>
public sealed class SqlDeployer
{
    private static readonly string[] GraniteTables = { "MasterItem", "TradingPartner", "Document", "DocumentDetail", "Audit" };
    private readonly Action<LogEntry> _log;

    public SqlDeployer(Action<LogEntry> log) => _log = log;

    public async Task<(SqlServerCheck? Check, string? Error)> CheckAsync(string connectionString, CancellationToken token)
    {
        try
        {
            await using var cn = new SqlConnection(connectionString);
            await cn.OpenAsync(token);
            const string sql = @"
SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
       CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS int),
       HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN'),
       CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_MEMBER('db_owner') = 1 THEN 1 ELSE 0 END,
       CASE WHEN OBJECT_ID(N'dbo.Custom_NiFi_RunImport', N'P') IS NULL THEN 0 ELSE 1 END;";
            await using var cmd = new SqlCommand(sql, cn);
            await using var r = await cmd.ExecuteReaderAsync(token);
            await r.ReadAsync(token);
            string version = r.GetString(0);
            bool mixed = r.GetInt32(1) == 0;
            bool logins = !r.IsDBNull(2) && r.GetInt32(2) == 1;
            bool objects = r.GetInt32(3) == 1;
            bool framework = r.GetInt32(4) == 1;
            await r.CloseAsync();

            var missing = new List<string>();
            foreach (string table in GraniteTables)
            {
                await using var t = new SqlCommand("SELECT OBJECT_ID(@name, N'U')", cn);
                t.Parameters.AddWithValue("@name", "dbo." + table);
                if (await t.ExecuteScalarAsync(token) is null or DBNull) missing.Add("dbo." + table);
            }
            return (new SqlServerCheck(version, mixed, logins, objects, missing, framework), null);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionStringToMaster, CancellationToken token)
    {
        try
        {
            await using var cn = new SqlConnection(connectionStringToMaster);
            await cn.OpenAsync(token);
            await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", cn);
            await using var r = await cmd.ExecuteReaderAsync(token);
            var names = new List<string>();
            while (await r.ReadAsync(token)) names.Add(r.GetString(0));
            return names;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log(new LogEntry(LogLevel.Warning, "Couldn't list databases: " + ex.Message));
            return Array.Empty<string>();
        }
    }

    /// <summary>Distinct Type, Status and Site values already used on documents, for the order defaults dropdowns.</summary>
    public async Task<(IReadOnlyList<string> Types, IReadOnlyList<string> Statuses, IReadOnlyList<string> Sites)> DocumentValuesAsync(string connectionString, CancellationToken token)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(token);
        async Task<IReadOnlyList<string>> Distinct(string column)
        {
            await using var cmd = new SqlCommand($"SELECT DISTINCT TOP (50) CAST([{column}] AS nvarchar(30)) FROM dbo.[Document] WHERE [{column}] IS NOT NULL ORDER BY 1", cn);
            await using var r = await cmd.ExecuteReaderAsync(token);
            var list = new List<string>();
            while (await r.ReadAsync(token)) list.Add(r.GetString(0));
            return list;
        }
        return (await Distinct("Type"), await Distinct("Status"), await Distinct("Site"));
    }

    /// <summary>Runs the framework and the chosen feed scripts, batch by batch, in one connection.</summary>
    public async Task DeployScriptsAsync(string connectionString, IReadOnlyCollection<string> feeds,
        OrderDefaults salesOrder, OrderDefaults purchaseOrder, CancellationToken token)
    {
        await using var cn = new SqlConnection(connectionString);
        cn.InfoMessage += (_, e) => { foreach (SqlError m in e.Errors) _log(new LogEntry(LogLevel.Detail, m.Message)); };
        await cn.OpenAsync(token);

        foreach (string script in DeployScripts.ScriptsFor(feeds))
        {
            var feed = Feeds.All.FirstOrDefault(f => f.Script == script);
            string text = feed is null
                ? DeployScripts.ReadSql(script)
                : DeployScripts.FeedScript(feed, feed.Name == "SalesOrder" ? salesOrder : feed.Name == "PurchaseOrder" ? purchaseOrder : null);
            var batches = DeployScripts.SplitBatches(text);
            _log(new LogEntry(LogLevel.Info, $"Running {script} ({batches.Count} batches)"));
            int n = 0;
            foreach (string batch in batches)
            {
                n++;
                try
                {
                    await using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 300 };
                    await cmd.ExecuteNonQueryAsync(token);
                }
                catch (SqlException ex)
                {
                    throw new InvalidOperationException($"{script}, batch {n} of {batches.Count}: {ex.Message}", ex);
                }
            }
            _log(new LogEntry(LogLevel.Success, script));
        }
    }

    /// <summary>
    /// Creates NiFi's SQL login (or resets its password if it exists), its
    /// database user, and adds it to the Custom_NiFiImport role. Names and
    /// password are passed as parameters and quoted by SQL Server itself.
    /// </summary>
    public async Task EnsureNiFiLoginAsync(string connectionString, string login, string password, CancellationToken token)
    {
        if (!InputRules.IsValidSqlLogin(login, out string error)) throw new ArgumentException(error);
        const string sql = @"
SET NOCOUNT ON;
DECLARE @Sql nvarchar(max), @Existed bit = 0;
IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @Login)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @Login AND type = 'S')
        THROW 50000, 'A login with that name exists but is not a SQL login. Choose another name for NiFi.', 1;
    SET @Existed = 1;
    SET @Sql = N'ALTER LOGIN ' + QUOTENAME(@Login) + N' WITH PASSWORD = ' + QUOTENAME(@Password, N'''') + N', CHECK_POLICY = ON; ALTER LOGIN ' + QUOTENAME(@Login) + N' ENABLE;';
    EXEC sys.sp_executesql @Sql;
END
ELSE
BEGIN
    SET @Sql = N'CREATE LOGIN ' + QUOTENAME(@Login) + N' WITH PASSWORD = ' + QUOTENAME(@Password, N'''')
             + N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = ' + QUOTENAME(DB_NAME()) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF DATABASE_PRINCIPAL_ID(@Login) IS NULL
BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@Login) + N' FOR LOGIN ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @Sql;
END;

IF ISNULL(IS_ROLEMEMBER(N'Custom_NiFiImport', @Login), 0) = 0
BEGIN
    SET @Sql = N'ALTER ROLE Custom_NiFiImport ADD MEMBER ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @Sql;
END;

SELECT @Existed;";
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add(new SqlParameter("@Login", System.Data.SqlDbType.NVarChar, 128) { Value = login });
        cmd.Parameters.Add(new SqlParameter("@Password", System.Data.SqlDbType.NVarChar, 128) { Value = password });
        bool existed = (await cmd.ExecuteScalarAsync(token)) is bool b && b;
        _log(new LogEntry(LogLevel.Success, existed
            ? $"SQL login {login} already existed: password reset to a new random one, kept in NiFi only."
            : $"SQL login {login} created with a random password, kept in NiFi only."));
        _log(new LogEntry(LogLevel.Info, $"{login} is in role Custom_NiFiImport: insert into staging and run the import procs, nothing else."));
    }

    /// <summary>Checks the deployed objects are there.</summary>
    public async Task<IReadOnlyList<string>> MissingObjectsAsync(string connectionString, IEnumerable<string> feeds, CancellationToken token)
    {
        var expected = new List<(string Name, string Type)>
        {
            ("dbo.Custom_NiFiImportLog", "U"), ("dbo.Custom_NiFi_RunImport", "P"),
            ("dbo.Custom_NiFi_ClearStaging", "P"), ("dbo.Custom_NiFi_LogFailure", "P")
        };
        foreach (var f in feeds.Select(Feeds.Get))
        {
            expected.Add(("dbo." + f.StagingTable, "U"));
            expected.Add(("dbo." + f.ImportProc, "P"));
        }
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(token);
        var missing = new List<string>();
        foreach (var (name, type) in expected)
        {
            await using var cmd = new SqlCommand("SELECT OBJECT_ID(@n, @t)", cn);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@t", type);
            if (await cmd.ExecuteScalarAsync(token) is null or DBNull) missing.Add(name);
        }
        return missing;
    }
}
