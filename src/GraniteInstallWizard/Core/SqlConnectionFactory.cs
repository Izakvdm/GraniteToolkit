using GraniteInstallWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Connection strings for the wizard's own connections to SQL Server.
/// (The connection strings written into the apps' appsettings.json are a
/// separate thing -- see ConnectionStringFormatter.)
/// </summary>
/// <remarks>
/// Pooling is off for the same reason as in the BI Deployment Wizard:
/// every Open() should be a new session with exactly the credentials on
/// Step 3, never a pooled one. That also matters here for a second
/// reason: the create script drops and recreates the database, and a
/// pooled connection still sitting in it would block the DROP.
/// </remarks>
public static class SqlConnectionFactory
{
    private const string AppName = "GraniteWMS Install Wizard";

    /// <summary>The Step 3 admin identity (Windows or SQL login).</summary>
    public static string Admin(InstallContext c, string database = "master")
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = c.SqlServer,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = AppName,
            Pooling = false
        };
        if (c.SqlAuth == SqlAuthMode.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.IntegratedSecurity = false;
            b.UserID = c.SqlAdminUser;
            b.Password = c.SqlAdminPassword;
        }
        return b.ConnectionString;
    }

    /// <summary>The app login the Granite apps will use -- for checking it works.</summary>
    public static string AppLogin(InstallContext c, string database = "master") =>
        new SqlConnectionStringBuilder
        {
            DataSource = c.SqlServer,
            InitialCatalog = database,
            IntegratedSecurity = false,
            UserID = c.AppLogin,
            Password = c.AppPassword,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = AppName,
            Pooling = false
        }.ConnectionString;

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken token)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        object? result = await cmd.ExecuteScalarAsync(token);
        return result is DBNull ? null : result;
    }

    public static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(token);
    }
}
