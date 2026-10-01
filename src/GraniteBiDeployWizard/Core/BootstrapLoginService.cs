using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Handles two related "I don't have the right SQL identity" escape hatches:
/// Panel 1's "I don't have a login with database-creation rights", and
/// Panel 5's "the scheduled-task account needs its own SQL login". Many
/// real-world targets for this wizard are client-owned servers where the
/// only credentials handed to the installer are a limited application
/// login -- not sa, and not necessarily a login with the dbcreator/sysadmin
/// server role CREATE DATABASE requires, and the Windows account the
/// scheduled sync runs as almost certainly has no SQL login at all yet.
/// Both flows work the same way: use a separate admin identity the user
/// *does* have (their own Windows login, or a different SQL login) purely
/// to run the one privileged statement needed, live, once -- never stored.
/// </summary>
public static class BootstrapLoginService
{
    // Server login names are also used unquoted in a few places downstream
    // (and echoed back into the UI/log), so keep them to a safe, boring
    // identifier shape rather than trusting T-SQL bracket-quoting alone.
    private static readonly Regex ValidLoginNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,60}$", RegexOptions.Compiled);

    // Windows account names are always DOMAIN\user, .\user, or COMPUTER\user
    // -- never a plain identifier -- so they get their own, separately
    // validated shape rather than reusing IsValidLoginName.
    private static readonly Regex ValidWindowsAccountPattern = new(@"^[A-Za-z0-9_.\-]+\\[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    public static bool IsValidLoginName(string name) => ValidLoginNamePattern.IsMatch(name);

    public static bool IsValidWindowsAccountName(string name) => ValidWindowsAccountPattern.IsMatch(name);

    /// <summary>
    /// SQL Server's Windows-login machinery does not accept the ".\" "this
    /// computer" shorthand the way LogonUser does -- see
    /// <see cref="WindowsAccountNameResolver"/> for why. Every place this
    /// service touches a SQL-visible Windows principal name resolves it
    /// through there first, so the name actually created on the server and
    /// the name later looked up always match.
    /// </summary>
    private static string ResolveForSqlServer(string windowsAccountName) =>
        WindowsAccountNameResolver.ResolveLocalShorthand(windowsAccountName);

    public sealed record RoleCheckResult(bool Success, bool IsSysAdmin, bool IsDbCreator, bool IsSecurityAdmin, string Message)
    {
        /// <summary>True if this login can run CREATE DATABASE on its own.</summary>
        public bool CanCreateDatabases => IsSysAdmin || IsDbCreator;

        /// <summary>True if this login can create/alter other SQL logins and grant them server roles.</summary>
        public bool CanManageLogins => IsSysAdmin || IsSecurityAdmin;
    }

    /// <summary>
    /// Opens the given connection and reports which of the three server
    /// roles relevant to this wizard the login belongs to.
    /// </summary>
    public static async Task<RoleCheckResult> CheckRolesAsync(string connectionString, CancellationToken token = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator'), IS_SRVROLEMEMBER('securityadmin');";

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return new RoleCheckResult(false, false, false, false, "Server did not return role membership.");

            bool sysadmin = reader.GetInt32(0) == 1;
            bool dbcreator = reader.GetInt32(1) == 1;
            bool securityadmin = reader.GetInt32(2) == 1;

            return new RoleCheckResult(true, sysadmin, dbcreator, securityadmin, "Connected.");
        }
        catch (SqlException ex)
        {
            return new RoleCheckResult(false, false, false, false, $"SQL Server rejected the connection: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new RoleCheckResult(false, false, false, false, $"Could not connect: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates (or repairs, if it already exists) a dedicated SQL login and
    /// grants it dbcreator, using an already-privileged admin connection.
    /// Once this login goes on to run CREATE DATABASE for the BI database,
    /// SQL Server automatically makes it db_owner of that database too --
    /// no further per-database grant is needed.
    /// </summary>
    public static async Task CreateOrRepairDeploymentLoginAsync(
        string adminConnectionString, string loginName, string loginPassword, CancellationToken token = default)
    {
        if (!IsValidLoginName(loginName))
            throw new ArgumentException(
                "Login name must start with a letter or underscore and contain only letters, digits, and underscores.",
                nameof(loginName));
        if (string.IsNullOrEmpty(loginPassword))
            throw new ArgumentException("Password cannot be empty.", nameof(loginPassword));

        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(token);

        // The login name is validated above against a plain identifier
        // shape, and the password can't be passed as a bind parameter --
        // CREATE LOGIN / ALTER LOGIN require it as a literal -- so it is
        // quote-escaped (doubled single quotes) rather than concatenated
        // raw.
        string bracketedName = "[" + loginName.Replace("]", "]]") + "]";
        string escapedPassword = loginPassword.Replace("'", "''");

        bool exists;
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT 1 FROM sys.server_principals WHERE name = @name;";
            checkCommand.Parameters.AddWithValue("@name", loginName);
            exists = await checkCommand.ExecuteScalarAsync(token) is not null;
        }

        await using (var ddlCommand = connection.CreateCommand())
        {
            ddlCommand.CommandText = exists
                ? $"ALTER LOGIN {bracketedName} WITH PASSWORD = N'{escapedPassword}' UNLOCK, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF; " +
                  $"ALTER LOGIN {bracketedName} ENABLE;"
                : $"CREATE LOGIN {bracketedName} WITH PASSWORD = N'{escapedPassword}', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;";
            await ddlCommand.ExecuteNonQueryAsync(token);
        }

        await using (var roleCommand = connection.CreateCommand())
        {
            // Idempotent: ADD MEMBER on a role the login already belongs to
            // is a harmless no-op, so re-running this against an existing
            // Granite_BI_User login (e.g. a redeploy) is always safe.
            roleCommand.CommandText = $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {bracketedName};";
            await roleCommand.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>
    /// Grants a login read access (db_datareader) inside an existing
    /// database, using an already-privileged admin connection. This is the
    /// piece a brand-new dedicated deployment login is missing that
    /// dbcreator alone does not provide: dbcreator only covers creating,
    /// altering, and dropping databases -- it says nothing about what the
    /// login can see inside a database it didn't create, like the existing
    /// live/source database the BI scripts read from via three-part names
    /// (<c>GraniteLive.dbo.vw_BI_...</c>). Without this, every script that
    /// reads from the source database fails with "Invalid object name",
    /// even though CREATE DATABASE for the new BI database worked fine.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CreateOrRepairDeploymentLoginAsync"/> (server-level
    /// DDL, servicable by sysadmin or securityadmin), granting rights
    /// *inside* an existing database needs database-level authority there --
    /// sysadmin (bypasses all checks), or db_owner of that specific
    /// database. A securityadmin-only admin account can create the login
    /// fine but will get "permission denied" here; the caller should treat
    /// that as a distinct, non-fatal outcome to report clearly rather than
    /// folding it into the login-creation failure path.
    /// </remarks>
    public static async Task GrantSourceDatabaseReadAccessAsync(
        string adminConnectionString, string loginName, string sourceDatabase, CancellationToken token = default)
    {
        if (!IsValidLoginName(loginName))
            throw new ArgumentException(
                "Login name must start with a letter or underscore and contain only letters, digits, and underscores.",
                nameof(loginName));
        if (string.IsNullOrWhiteSpace(sourceDatabase))
            throw new ArgumentException("Source database name cannot be empty.", nameof(sourceDatabase));

        string bracketedName = "[" + loginName.Replace("]", "]]") + "]";

        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(token);
        // CREATE USER / ALTER ROLE must run inside the source database
        // itself, not master -- the admin connection string is built
        // against master so the same login creation flow above also works
        // before any specific database is known.
        connection.ChangeDatabase(sourceDatabase);

        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT 1 FROM sys.database_principals WHERE name = @name;";
            checkCommand.Parameters.AddWithValue("@name", loginName);
            if (await checkCommand.ExecuteScalarAsync(token) is null)
            {
                await using var createUserCommand = connection.CreateCommand();
                createUserCommand.CommandText = $"CREATE USER {bracketedName} FOR LOGIN {bracketedName};";
                await createUserCommand.ExecuteNonQueryAsync(token);
            }
        }

        await using var roleCommand = connection.CreateCommand();
        // Idempotent, like the dbcreator grant above -- safe to re-run.
        roleCommand.CommandText = $"ALTER ROLE [db_datareader] ADD MEMBER {bracketedName};";
        await roleCommand.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Creates a server-level SQL login mapped to a Windows account (or
    /// verifies one already exists), for the Panel 5 "the scheduled-task
    /// account needs its own SQL login" flow -- Run_BI_Sync.bat calls
    /// sqlcmd with -E (trusted/integrated authentication) as this Windows
    /// account, and SQL Server has no idea who that is until a login for it
    /// exists. Uses an already-privileged admin connection, the same way
    /// <see cref="CreateOrRepairDeploymentLoginAsync"/> does for Panel 1.
    /// A no-op if the login is already there, so this is safe to re-run.
    /// </summary>
    public static async Task CreateOrVerifyWindowsLoginAsync(
        string adminConnectionString, string windowsAccountName, CancellationToken token = default)
    {
        if (!IsValidWindowsAccountName(windowsAccountName))
            throw new ArgumentException(
                "That doesn't look like a Windows account name (expected DOMAIN\\user, .\\user, or COMPUTER\\user).",
                nameof(windowsAccountName));

        string resolvedName = ResolveForSqlServer(windowsAccountName);

        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(token);

        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT 1 FROM sys.server_principals WHERE name = @name;";
            checkCommand.Parameters.AddWithValue("@name", resolvedName);
            if (await checkCommand.ExecuteScalarAsync(token) is not null)
                return; // already mapped -- nothing to do
        }

        // Validated above against a plain DOMAIN\user shape (no brackets,
        // quotes, or semicolons possible), so bracket-escaping it here is
        // just standard identifier quoting, not injection defense on its own.
        string bracketedName = "[" + resolvedName.Replace("]", "]]") + "]";

        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE LOGIN {bracketedName} FROM WINDOWS;";
        await createCommand.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Gives a Windows account (mapped to a login by
    /// <see cref="CreateOrVerifyWindowsLoginAsync"/>) a database user in
    /// whatever database the given connection is currently pointed at, and
    /// EXECUTE on bi.usp_RunSync -- exactly what the scheduled sync needs
    /// and nothing more. Runs on the caller's own already-open connection
    /// (the deployment connection from <see cref="DeploymentRunner"/>,
    /// already sitting in the BI database with db_owner-equivalent rights
    /// there) rather than opening a new one, since this must execute in
    /// that exact database.
    /// </summary>
    public static async Task GrantSyncExecutionRightsAsync(
        SqlConnection connection, string windowsAccountName, CancellationToken token = default)
    {
        if (!IsValidWindowsAccountName(windowsAccountName))
            throw new ArgumentException(
                "That doesn't look like a Windows account name (expected DOMAIN\\user, .\\user, or COMPUTER\\user).",
                nameof(windowsAccountName));

        // Must match whatever CreateOrVerifyWindowsLoginAsync actually
        // created on the server -- a ".\name" login doesn't exist there
        // under that literal name, only under "COMPUTERNAME\name".
        string resolvedName = ResolveForSqlServer(windowsAccountName);
        string bracketedName = "[" + resolvedName.Replace("]", "]]") + "]";

        bool userExists;
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT 1 FROM sys.database_principals WHERE name = @name;";
            checkCommand.Parameters.AddWithValue("@name", resolvedName);
            userExists = await checkCommand.ExecuteScalarAsync(token) is not null;
        }

        if (!userExists)
        {
            // Fails with a clear "Windows NT user or group ... not found"
            // style error if CreateOrVerifyWindowsLoginAsync was never run
            // for this account -- DeploymentRunner logs that as a pointer
            // back to Panel 5 rather than treating it as fatal.
            await using var createUserCommand = connection.CreateCommand();
            createUserCommand.CommandText = $"CREATE USER {bracketedName} FOR LOGIN {bracketedName};";
            await createUserCommand.ExecuteNonQueryAsync(token);
        }

        await using var grantCommand = connection.CreateCommand();
        grantCommand.CommandText = $"GRANT EXECUTE ON [bi].[usp_RunSync] TO {bracketedName};";
        await grantCommand.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Generates a random password that satisfies SQL Server's default
    /// complexity policy (CHECK_POLICY = ON needs at least 3 of: upper,
    /// lower, digit, symbol) by construction, using a cryptographic RNG.
    /// Ambiguous-looking characters (I, O, 0, 1, l) are excluded so a user
    /// who has to retype it by hand won't be tripped up.
    /// </summary>
    public static string GenerateStrongPassword(int length = 20)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = upper + lower + digits + symbols;

        byte[] randomBytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = all[randomBytes[i] % all.Length];

        // Force the first four characters to one of each class so the
        // policy check always passes regardless of what the random draw
        // above happened to land on.
        chars[0] = upper[randomBytes[0] % upper.Length];
        chars[1] = lower[randomBytes[1] % lower.Length];
        chars[2] = digits[randomBytes[2] % digits.Length];
        chars[3] = symbols[randomBytes[3] % symbols.Length];

        return new string(chars);
    }
}
