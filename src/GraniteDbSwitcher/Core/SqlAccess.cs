using Microsoft.Data.SqlClient;

namespace GraniteDbSwitcher.Core;

public enum SqlAuthMode
{
    Windows,
    SqlLogin
}

/// <summary>How the switcher itself signs in to SQL Server (not the apps' login).</summary>
public sealed record SqlAdminIdentity(string Server, SqlAuthMode Auth, string? User, string? Password);

/// <summary>Small SQL helpers. Pooling off so every Open() uses exactly the identity given.</summary>
public static class SqlAccess
{
    private const string AppName = "GraniteWMS DB Switcher";

    public static string Admin(SqlAdminIdentity id, string database = "master")
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = id.Server,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = AppName,
            Pooling = false
        };
        if (id.Auth == SqlAuthMode.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = id.User ?? string.Empty;
            b.Password = id.Password ?? string.Empty;
        }
        return b.ConnectionString;
    }

    /// <summary>An app's own connection string, tightened for a one-off check (no pooling, short timeout).</summary>
    public static string ForCheck(string appConnectionString)
    {
        var b = new SqlConnectionStringBuilder(appConnectionString)
        {
            Pooling = false,
            ConnectTimeout = 15,
            ApplicationName = AppName
        };
        return b.ConnectionString;
    }

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

    public static async Task<object?> ScalarAsync(SqlConnection c, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        object? result = await cmd.ExecuteScalarAsync(token);
        return result is DBNull ? null : result;
    }

    public static async Task ExecuteAsync(SqlConnection c, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(token);
    }

    public static async Task<List<string>> ColumnAsync(SqlConnection c, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (!reader.IsDBNull(0)) list.Add(Convert.ToString(reader.GetValue(0))!);
        return list;
    }

    /// <summary>[name] with ] doubled, like QUOTENAME.</summary>
    public static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Runs a statement in another database's context through [db].sys.sp_executesql.</summary>
    public static async Task<object?> ScalarInAsync(SqlConnection c, string database, string innerSql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = BuildExec(database, innerSql, parameters, cmd);
        object? result = await cmd.ExecuteScalarAsync(token);
        return result is DBNull ? null : result;
    }

    public static async Task ExecuteInAsync(SqlConnection c, string database, string innerSql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = BuildExec(database, innerSql, parameters, cmd);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static string BuildExec(string database, string innerSql, (string Name, object Value)[] parameters, SqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("@__sql", innerSql);
        string declarations = string.Join(", ", parameters.Select(p => $"{p.Name} nvarchar(4000)"));
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        string passThrough = string.Join(", ", parameters.Select(p => $"{p.Name} = {p.Name}"));
        return parameters.Length == 0
            ? $"EXEC {Bracket(database)}.sys.sp_executesql @__sql;"
            : $"EXEC {Bracket(database)}.sys.sp_executesql @__sql, N'{declarations}', {passThrough};";
    }
}
