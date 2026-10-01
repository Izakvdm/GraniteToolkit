using GraniteDbSwitcher.Models;
using Microsoft.Data.SqlClient;

namespace GraniteDbSwitcher.Core;

/// <summary>Lists the databases on a SQL Server and reads each one's Granite version.</summary>
public static class DatabaseCatalog
{
    public static async Task<IReadOnlyList<DatabaseInfo>> ListAsync(SqlAdminIdentity admin, CancellationToken token)
    {
        await using var c = await SqlAccess.OpenAsync(SqlAccess.Admin(admin), token);

        var names = await SqlAccess.ColumnAsync(c,
            "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' AND source_database_id IS NULL ORDER BY name;",
            token);

        var sizes = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT d.name, SUM(CAST(f.size AS bigint)) * 8 / 1024.0 FROM sys.master_files f JOIN sys.databases d ON d.database_id = f.database_id GROUP BY d.name;";
            await using var r = await cmd.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token)) sizes[r.GetString(0)] = Convert.ToDecimal(r.GetValue(1));
        }
        catch (SqlException) { /* needs VIEW ANY DEFINITION; sizes are optional */ }

        var restored = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT destination_database_name, MAX(restore_date) FROM msdb.dbo.restorehistory GROUP BY destination_database_name;";
            await using var r = await cmd.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
                if (!r.IsDBNull(0) && !r.IsDBNull(1)) restored[r.GetString(0)] = r.GetDateTime(1);
        }
        catch (SqlException) { /* no msdb access: skip */ }

        var list = new List<DatabaseInfo>();
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            var db = new DatabaseInfo
            {
                Name = name,
                SizeMb = sizes.TryGetValue(name, out var s) ? s : null,
                LastRestored = restored.TryGetValue(name, out var d) ? d : null
            };
            try
            {
                await ReadGraniteDetailsAsync(c, db, token);
            }
            catch (SqlException ex)
            {
                db.Problem = ex.Number == 916 ? "no access with this login" : ex.Message;
            }
            db.Version = GraniteVersionRules.Resolve(db);
            list.Add(db);
        }
        return list;
    }

    public static async Task ReadGraniteDetailsAsync(SqlConnection c, DatabaseInfo db, CancellationToken token)
    {
        string b = SqlAccess.Bracket(db.Name);
        bool Has(object? id) => id is not null;

        db.IsGranite = Has(await SqlAccess.ScalarAsync(c, $"SELECT OBJECT_ID(N'{b.Replace("'", "''")}.dbo.SystemSettings', 'U');", token));
        if (!db.IsGranite) return;

        db.DatabaseVersionSetting = (string?)await SqlAccess.ScalarInAsync(c, db.Name,
            "SELECT TOP (1) CAST([Value] AS nvarchar(100)) FROM dbo.SystemSettings WHERE [Application] = N'Granite' AND [Key] = N'DatabaseVersion';",
            token);

        db.HasV7Objects = Has(await SqlAccess.ScalarAsync(c, $"SELECT OBJECT_ID(N'{b.Replace("'", "''")}.dbo.ProcessFunction', 'U');", token));

        bool hasMigration = Has(await SqlAccess.ScalarAsync(c, $"SELECT OBJECT_ID(N'{b.Replace("'", "''")}.dbo.Migration', 'U');", token));
        if (hasMigration)
        {
            var migrations = await SqlAccess.ColumnAsync(c,
                $"SELECT [Name] FROM {b}.dbo.Migration WHERE [Name] LIKE 'SCHEMA[_]%';", token);
            db.SchemaMigration = GraniteVersionRules.HighestSchema(migrations);
        }
    }

    /// <summary>Reads one database again (after a switch, or for the header).</summary>
    public static async Task<DatabaseInfo> ReadOneAsync(SqlAdminIdentity admin, string name, CancellationToken token)
    {
        await using var c = await SqlAccess.OpenAsync(SqlAccess.Admin(admin), token);
        var db = new DatabaseInfo { Name = name };
        await ReadGraniteDetailsAsync(c, db, token);
        db.Version = GraniteVersionRules.Resolve(db);
        return db;
    }
}
