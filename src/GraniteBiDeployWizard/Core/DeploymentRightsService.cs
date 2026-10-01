using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Grants the deployment login (Panel 1's SQL login) the rights it needs to
/// run the whole deployment, permanently -- once granted, they stay, so
/// every later redeploy against the same server just works without hitting
/// a permission wall again.
/// </summary>
/// <remarks>
/// Three real deployment attempts in a row each turned up a different
/// missing grant on the deployment login: db_datareader on the source
/// database first (Panel 2's "Invalid object name" fix), then CREATE
/// PROCEDURE in the live database (needed for Granite Scheduler's proxy
/// procedure). An earlier version of this service granted these
/// temporarily and revoked them again once each run finished -- but for
/// how this wizard is actually used, against servers this same login
/// redeploys to again and again, that just meant hitting the same
/// permission wall on every single run. What's wanted instead is simpler:
/// the deployment login should just have what it needs, standing, like any
/// other application login would.
/// <para/>
/// This runs at the start of every deployment, using a separate admin
/// identity supplied once, live, never stored (Windows Authentication as
/// the current user by default, or a different SQL login) -- the same
/// pattern <see cref="BootstrapLoginService"/> already uses for Panel 1's
/// "create a dedicated login" and Panel 5's Windows-account bootstrap.
/// Every grant here is the same idempotent shape used throughout this
/// codebase (<c>ADD MEMBER</c> / <c>GRANT</c> on something the login
/// already has is a harmless no-op), so running it again on a login that
/// already holds these rights costs nothing and changes nothing.
/// <para/>
/// A real run against a client server (2026-08-28) showed this isn't
/// enough by itself: the deployment login had just created
/// [BiDb] itself (via CREATE DATABASE, which normally makes the creator
/// its owner automatically), yet immediately failed switching into it with
/// "server principal ... is not able to access the database ... under the
/// current security context." That never happened locally, only against a
/// real client instance -- the most likely explanation is a
/// security-hardening policy (e.g. a DDL trigger) on that server that
/// reassigns a freshly created database's ownership away from its creator.
/// Since the deployment login can't grant itself access to a database it
/// no longer owns, <see cref="EnsureDatabaseAccessAsync"/> exists to do
/// that explicitly, using the same privileged admin identity as the method
/// above -- called by <see cref="DeploymentRunner"/> as a one-time repair
/// attempt exactly when a USE/ChangeDatabase into the BI database it just
/// created fails this way, rather than assuming creation always implies
/// access.
/// </remarks>
public static class DeploymentRightsService
{
    public sealed record GrantResult(bool Success, string Message);

    /// <summary>
    /// Grants dbcreator (server-level, so a not-yet-existing BI database
    /// can still be created) and db_owner on the source database.
    /// </summary>
    public static async Task<GrantResult> EnsureFullDeploymentRightsAsync(
        string adminConnectionString, string deploymentLoginName, string sourceDb, CancellationToken token)
    {
        try
        {
            await using var connection = new SqlConnection(adminConnectionString);
            await connection.OpenAsync(token);

            await using (var grantDbCreator = connection.CreateCommand())
            {
                grantDbCreator.CommandTimeout = 30;
                grantDbCreator.CommandText = $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {QuoteName(deploymentLoginName)};";
                await grantDbCreator.ExecuteNonQueryAsync(token);
            }

            await EnsureDatabaseUserAndOwnerAsync(connection, deploymentLoginName, sourceDb, token);

            return new GrantResult(true,
                $"\"{deploymentLoginName}\" now has dbcreator and db_owner on [{sourceDb}] -- standing, not just for this " +
                "run, so future deployments to this server won't need this step again.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GrantResult(false, $"Could not grant full deployment rights: {ex.Message}");
        }
    }

    /// <summary>
    /// Grants the deployment login db_owner on an existing database, using
    /// the same privileged admin identity as
    /// <see cref="EnsureFullDeploymentRightsAsync"/>. Unlike that method,
    /// this does not touch dbcreator (already granted, server-level) and is
    /// meant to be called again later, once a database that didn't exist
    /// yet at deployment start has since been created -- specifically, as a
    /// one-time repair when the deployment login's own automatic ownership
    /// of a database it just created turns out not to have taken effect
    /// (see the class remarks above for why that can happen).
    /// </summary>
    public static async Task<GrantResult> EnsureDatabaseAccessAsync(
        string adminConnectionString, string deploymentLoginName, string databaseName, CancellationToken token)
    {
        try
        {
            await using var connection = new SqlConnection(adminConnectionString);
            await connection.OpenAsync(token);

            await EnsureDatabaseUserAndOwnerAsync(connection, deploymentLoginName, databaseName, token);

            return new GrantResult(true, $"\"{deploymentLoginName}\" now has db_owner on [{databaseName}].");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GrantResult(false, $"Could not grant access to [{databaseName}]: {ex.Message}");
        }
    }

    private static async Task EnsureDatabaseUserAndOwnerAsync(
        SqlConnection connection, string deploymentLoginName, string databaseName, CancellationToken token)
    {
        string bracketedLogin = QuoteName(deploymentLoginName);

        connection.ChangeDatabase(databaseName);
        try
        {
            bool userExists;
            await using (var checkUser = connection.CreateCommand())
            {
                checkUser.CommandText = "SELECT 1 FROM sys.database_principals WHERE name = @name;";
                checkUser.Parameters.AddWithValue("@name", deploymentLoginName);
                userExists = await checkUser.ExecuteScalarAsync(token) is not null;
            }
            if (!userExists)
            {
                await using var createUser = connection.CreateCommand();
                createUser.CommandText = $"CREATE USER {bracketedLogin} FOR LOGIN {bracketedLogin};";
                await createUser.ExecuteNonQueryAsync(token);
            }

            await using var addRole = connection.CreateCommand();
            addRole.CommandText = $"ALTER ROLE [db_owner] ADD MEMBER {bracketedLogin};";
            await addRole.ExecuteNonQueryAsync(token);
        }
        finally
        {
            connection.ChangeDatabase("master");
        }
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";
}
