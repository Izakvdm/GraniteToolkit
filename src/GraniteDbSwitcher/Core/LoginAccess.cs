using GraniteDbSwitcher.Models;
using Microsoft.Data.SqlClient;

namespace GraniteDbSwitcher.Core;

/// <summary>
/// Makes sure the SQL login the Granite apps use can get into the target
/// database, which usually isn't the case straight after restoring a
/// client's backup: the backup's database user belongs to the client
/// server's login (different SID), so it's orphaned here.
/// </summary>
/// <remarks>
/// Order of preference, least invasive first:
/// <list type="number">
/// <item>The login is sysadmin, or already mapped to a user: nothing to fix
/// (other than db_owner, below).</item>
/// <item>An orphaned SQL user with the login's name exists (the normal
/// restore case): ALTER USER ... WITH LOGIN re-links it, keeping its
/// roles and schema.</item>
/// <item>Otherwise CREATE USER for the login. If the name is taken by a
/// different, non-orphaned user (e.g. the create script's GRANITE user,
/// which maps to BUILTIN\Users, versus a login called Granite on a
/// case-insensitive server) the new user is named login_DbSwitcher.</item>
/// </list>
/// The user is then made db_owner, the same role the Granite create script
/// gives its GRANITE user.
/// </remarks>
public static class LoginAccess
{
    public static async Task EnsureAsync(SqlConnection admin, string database, string login, Action<LogEntry> log, CancellationToken token)
    {
        void Log(LogLevel l, string m) => log(new LogEntry(l, m));

        object? sid = await SqlAccess.ScalarAsync(admin, "SELECT SUSER_SID(@login);", token, ("@login", login));
        if (sid is null)
            throw new InvalidOperationException($"The apps' SQL login \"{login}\" doesn't exist on this SQL Server.");

        object? sysadmin = await SqlAccess.ScalarAsync(admin, "SELECT IS_SRVROLEMEMBER('sysadmin', @login);", token, ("@login", login));
        if (sysadmin is int s && s == 1)
        {
            Log(LogLevel.Info, $"Login \"{login}\" is sysadmin, so it can use {database} as it is.");
            return;
        }

        string? user = (string?)await SqlAccess.ScalarInAsync(admin, database,
            "SELECT TOP (1) name FROM sys.database_principals WHERE sid = SUSER_SID(@login);", token, ("@login", login));

        if (user is not null)
        {
            Log(LogLevel.Info, $"Login \"{login}\" is already mapped to user \"{user}\" in {database}.");
        }
        else
        {
            string? orphan = (string?)await SqlAccess.ScalarInAsync(admin, database,
                @"SELECT TOP (1) dp.name FROM sys.database_principals dp
                  WHERE dp.type = 'S' AND dp.authentication_type_desc = 'INSTANCE' AND dp.name = @login
                    AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid);",
                token, ("@login", login));

            if (orphan is not null)
            {
                await SqlAccess.ExecuteInAsync(admin, database,
                    $"ALTER USER {SqlAccess.Bracket(orphan)} WITH LOGIN = {SqlAccess.Bracket(login)};", token);
                user = orphan;
                Log(LogLevel.Success, $"Re-linked the restored user \"{orphan}\" in {database} to login \"{login}\".");
            }
            else
            {
                object? taken = await SqlAccess.ScalarInAsync(admin, database,
                    "SELECT 1 FROM sys.database_principals WHERE name = @login;", token, ("@login", login));
                user = taken is null ? login : login + "_DbSwitcher";
                await SqlAccess.ExecuteInAsync(admin, database,
                    $"CREATE USER {SqlAccess.Bracket(user)} FOR LOGIN {SqlAccess.Bracket(login)} WITH DEFAULT_SCHEMA = [dbo];", token);
                Log(LogLevel.Success, $"Created user \"{user}\" in {database} for login \"{login}\".");
            }
        }

        object? owner = await SqlAccess.ScalarInAsync(admin, database,
            "SELECT IS_ROLEMEMBER('db_owner', @user);", token, ("@user", user));
        if (owner is int o && o == 1) return;

        // dbo itself reports 1 above, so this is a real user.
        await SqlAccess.ExecuteInAsync(admin, database,
            $"ALTER ROLE [db_owner] ADD MEMBER {SqlAccess.Bracket(user)};", token);
        Log(LogLevel.Success, $"Added \"{user}\" to db_owner in {database} (same as the Granite create script's user).");
    }

    /// <summary>
    /// A warning if the database uses SQLCLR assemblies but CLR is off on
    /// this server: V7's clr_ procedures (reversals, QC, repalletise) would fail.
    /// </summary>
    public static async Task<string?> ClrWarningAsync(SqlConnection admin, string database, CancellationToken token)
    {
        object? count = await SqlAccess.ScalarInAsync(admin, database,
            "SELECT COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1;", token);
        if (count is not int n || n == 0) return null;

        object? enabled = await SqlAccess.ScalarAsync(admin,
            "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'clr enabled';", token);
        return enabled is int e && e == 1
            ? null
            : $"{database} has {n} SQLCLR assembl{(n == 1 ? "y" : "ies")} but 'clr enabled' is off on this SQL Server, so Granite's CLR procedures will fail until it's turned on.";
    }
}
