using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Builds the connection strings the wizard uses to talk to SQL Server.
/// </summary>
public static class SqlConnectionFactory
{
    /// <summary>
    /// Builds a connection string for the given server/database using the
    /// SQL login gathered on Panel 1.
    /// </summary>
    /// <remarks>
    /// The deployment brief for this wizard calls for the connection string
    /// to explicitly carry <c>Context Connection=False</c> as a "bypass
    /// session caching" rule. That keyword is real, but it belongs to the
    /// legacy, SQL-CLR-hosted <c>System.Data.SqlClient</c> in-process
    /// provider (a stored-procedure-assembly scenario) -- it has no meaning
    /// for an external client, and <c>Microsoft.Data.SqlClient</c> (the
    /// driver this project uses, per spec) does not define it at all.
    /// Setting it here would either fail to compile against
    /// <see cref="SqlConnectionStringBuilder"/> or throw
    /// "Keyword not supported" at runtime if forced in as raw text.
    /// <para/>
    /// The actual goal behind the rule -- guarantee every connection this
    /// wizard opens is a brand new, explicitly SQL-authenticated session
    /// using exactly the Panel 1 credentials, never one silently reused
    /// from a pool or from ambient Windows identity -- is implemented
    /// instead with two settings that really do that on this driver:
    /// <c>IntegratedSecurity = false</c> (never falls back to Windows auth)
    /// and <c>Pooling = false</c> (every Open() is a genuinely new physical
    /// connection, never a pooled one with residual session state).
    /// <para/>
    /// This "never Windows auth" guarantee is about this one connection
    /// path -- the credentials that actually run the deployment. It does
    /// not rule out Windows auth everywhere: <see cref="BuildIntegratedConnectionString"/>
    /// below is a separate, explicitly opt-in path used only by the Panel 1
    /// "create a dedicated login" bootstrap flow (<see cref="BootstrapLoginService"/>),
    /// to authenticate as an admin identity the user already has on the box
    /// purely to run CREATE LOGIN. Its output is never used to run the
    /// deployment itself -- the new SQL login it creates is what gets used
    /// for that, through this method, like any other Panel 1 credential.
    /// </remarks>
    public static string BuildConnectionString(string server, string? database, string username, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            UserID = username,
            Password = password,
            IntegratedSecurity = false, // never fall back to Windows auth
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = "GraniteWMS BI Deployment Wizard",
            Pooling = false // every Open() is a fresh, uncached physical connection -- see remarks above
        };

        if (!string.IsNullOrWhiteSpace(database))
            builder.InitialCatalog = database;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Builds a connection string authenticated as whatever Windows account
    /// this process is running as, for the Panel 1 "create a dedicated
    /// login" bootstrap flow only -- see the remarks on
    /// <see cref="BuildConnectionString"/>. Never used for the deployment
    /// connection itself.
    /// </summary>
    public static string BuildIntegratedConnectionString(string server, string? database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = "GraniteWMS BI Deployment Wizard (bootstrap)",
            Pooling = false
        };

        if (!string.IsNullOrWhiteSpace(database))
            builder.InitialCatalog = database;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Opens a throwaway connection to validate the Panel 1 credentials.
    /// Connects to "master" so it works before the BI database exists.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        string server, string username, string password, CancellationToken token = default)
    {
        string connectionString = BuildConnectionString(server, "master", username, password);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT @@VERSION;";
            var result = await command.ExecuteScalarAsync(token);

            string version = result?.ToString() ?? "(unknown version)";
            string shortVersion = version.Split('\n')[0].Trim();
            return (true, $"Connected successfully. {shortVersion}");
        }
        catch (SqlException ex)
        {
            return (false, $"SQL Server rejected the connection: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Could not connect: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads SERVERPROPERTY('EngineEdition') so Panel 5 can tell whether
    /// SQL Server Agent is available (4 = Express does not have the Agent
    /// service; every other edition -- Standard, Enterprise, Developer, the
    /// Azure variants -- does). This is the numeric, locale-independent
    /// property; SERVERPROPERTY('Edition') returns a display string like
    /// "Express Edition (64-bit)" that would need string matching instead.
    /// </summary>
    public static async Task<(bool Success, int? EngineEdition, string Message)> DetectEngineEditionAsync(
        string server, string username, string password, CancellationToken token = default)
    {
        string connectionString = BuildConnectionString(server, "master", username, password);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
            var result = await command.ExecuteScalarAsync(token);

            if (result is int edition)
                return (true, edition, "OK");

            return (false, null, "SERVERPROPERTY('EngineEdition') returned no value.");
        }
        catch (Exception ex)
        {
            // Best-effort: Panel 5 treats a failed check as "unknown", not
            // as "not Express" -- see DeploymentContext.SqlEngineEdition.
            return (false, null, ex.Message);
        }
    }
}
