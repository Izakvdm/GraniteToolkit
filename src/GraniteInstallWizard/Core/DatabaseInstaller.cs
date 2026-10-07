using GraniteInstallWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteInstallWizard.Core;

/// <summary>
/// The database stage: app login, GraniteDatabase_Create.sql, the login's
/// database user, the Hotfix scripts, then the Custodian token.
/// </summary>
public sealed class DatabaseInstaller
{
    private readonly Action<LogEntry> _log;

    public DatabaseInstaller(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public async Task RunAsync(InstallContext c, CancellationToken token)
    {
        var variables = new Dictionary<string, string>
        {
            ["DatabaseName"] = c.DatabaseName,
            ["DefaultFilePrefix"] = c.DatabaseName
        };

        // Parse first: a script this wizard can't run should stop the
        // install before anything has been created, not halfway through.
        bool createNew = c.DatabaseMode == DatabaseMode.CreateNew;
        ParsedSqlScript? create = null;
        if (createNew)
        {
            string createText = await File.ReadAllTextAsync(c.CreateScriptPath, token);
            create = GraniteSqlScriptParser.Parse(createText, variables);
            if (create.UnresolvedVariables.Count > 0)
                throw new InvalidOperationException($"GraniteDatabase_Create.sql uses SQLCMD variables with no value: {string.Join(", ", create.UnresolvedVariables)}.");
        }

        var hotfixScripts = new List<(string Label, ParsedSqlScript Script)>();
        if (c.ApplyDatabaseHotfix)
        {
            var sources = HotfixScripts.Find(c);
            if (sources.Count == 0)
                Log(LogLevel.Info, "This release has no Hotfix database scripts; nothing to run.");
            foreach (var source in sources)
            {
                string text = await File.ReadAllTextAsync(source.Path, token);
                if (source.IsMarkdown)
                {
                    var blocks = GraniteSqlScriptParser.ExtractMarkdownSqlBlocks(text);
                    for (int i = 0; i < blocks.Count; i++)
                        hotfixScripts.Add(($"{source.Label} (SQL block {i + 1})", GraniteSqlScriptParser.Parse(blocks[i], variables)));
                }
                else
                {
                    hotfixScripts.Add((source.Label, GraniteSqlScriptParser.Parse(GraniteSqlScriptParser.ToCreateOrAlter(text), variables)));
                }
            }
        }

        // The Custodian token is read and checked here too, before anything
        // is created. See CustodianToken for why it isn't left to the hotfix.
        bool setToken = c.IsEnabled(GraniteComponent.Custodian);
        // A file picked on Step 3 is a deliberate replacement (a token GitHub
        // now refuses), so it overwrites; the release's copy only fills a gap.
        bool overwriteToken = createNew || !string.IsNullOrWhiteSpace(c.CustodianTokenFile);
        CustodianTokenValues? tokenValues = null;
        string tokenSource = "";
        if (setToken)
        {
            if (c.CustodianTokenSource is { } source)
            {
                tokenSource = source.Label;
                tokenValues = CustodianToken.Parse(File.ReadAllText(source.Path), tokenSource);
            }
            else
            {
                Log(LogLevel.Warning, CustodianToken.NoSourceText);
            }
        }

        if (c.DryRun)
        {
            Log(LogLevel.DryRun, c.ResetExistingAppLoginPassword
                ? $"Would create SQL login {c.AppLogin} if it doesn't exist, or change its password to the one entered if it exists with a different one."
                : $"Would create SQL login {c.AppLogin} if it doesn't exist.");
            if (create is not null)
                Log(LogLevel.DryRun, $"Would run GraniteDatabase_Create.sql as {c.DatabaseName} ({create.Batches.Count} batches).");
            else
                Log(LogLevel.DryRun, $"Would use the existing database {c.DatabaseName} as it is (create script not run, data kept).");
            Log(LogLevel.DryRun, $"Would map login {c.AppLogin} into {c.DatabaseName} as db_owner.");
            foreach (var (label, script) in hotfixScripts)
                Log(LogLevel.DryRun, $"Would run {label} ({script.Batches.Count} batch{(script.Batches.Count == 1 ? "" : "es")}).");
            if (tokenValues is not null)
                Log(LogLevel.DryRun, overwriteToken
                    ? $"Would set the Custodian token from {tokenSource} (version {tokenValues.Version})."
                    : $"Would add the Custodian token from {tokenSource} if {c.DatabaseName} has none (an existing token is kept).");
            return;
        }

        await using (SqlConnection master = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.Admin(c), token))
        {
            // 1. App login first, so a failure here comes before the long script.
            bool loginExists = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(master,
                "SELECT COUNT(*) FROM sys.server_principals WHERE name = @l;", token, ("@l", c.AppLogin)) ?? 0) > 0;
            if (!loginExists)
            {
                Log(LogLevel.Info, $"Creating SQL login {c.AppLogin}...");
                // Built with QUOTENAME inside T-SQL so neither the name nor the
                // password is ever concatenated into SQL on this side.
                await SqlConnectionFactory.ExecuteAsync(master,
                    "DECLARE @s nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@l) + N' WITH PASSWORD = ' + QUOTENAME(@p, '''') + N', CHECK_POLICY = OFF, DEFAULT_DATABASE = [master]'; EXEC (@s);",
                    token, ("@l", c.AppLogin), ("@p", c.AppPassword));
                Log(LogLevel.Success, $"Login {c.AppLogin} created.");
            }
            else if (c.ResetExistingAppLoginPassword && !(await SqlServerInspector.CheckExistingAppLoginAsync(c, token)).Ok)
            {
                // Only reached when the box on Step 3 is ticked AND the
                // entered password really doesn't work; a matching password
                // is left alone.
                Log(LogLevel.Warning, $"Changing the password of existing login {c.AppLogin} (\"change its password\" was ticked on Step 3)...");
                await SqlConnectionFactory.ExecuteAsync(master,
                    "DECLARE @s nvarchar(max) = N'ALTER LOGIN ' + QUOTENAME(@l) + N' WITH PASSWORD = ' + QUOTENAME(@p, '''') + N', CHECK_POLICY = OFF'; EXEC (@s);",
                    token, ("@l", c.AppLogin), ("@p", c.AppPassword));
                Log(LogLevel.Success, $"Password of login {c.AppLogin} changed. Anything else using this login needs the new password.");
            }
            else
            {
                Log(LogLevel.Info, $"Login {c.AppLogin} already exists and the password matches; reusing it.");
            }

            // 2. The create script. It starts in master and switches itself
            //    to the new database with USE, which carries over to every
            //    later batch on this connection.
            if (create is not null)
            {
                await RunScriptAsync(master, "GraniteDatabase_Create.sql", create, token);
            }
            else
            {
                Log(LogLevel.Info, $"Using the existing database {c.DatabaseName}: the create script is not run and its data is kept.");
            }
        }

        // 3. The login's user in the new database. The create script adds a
        //    user called GRANITE mapped to BUILTIN\Users, and SQL Server
        //    user names are case-insensitive, so a login called "Granite"
        //    can't get a user of the same name. Look the user up by the
        //    login's SID instead, and pick another name if needed.
        await using (SqlConnection db = await SqlConnectionFactory.OpenAsync(SqlConnectionFactory.Admin(c, c.DatabaseName), token))
        {
            string? user = (string?)await SqlConnectionFactory.ScalarAsync(db,
                "SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(@l);", token, ("@l", c.AppLogin));
            if (user is null)
            {
                user = c.AppLogin;
                bool taken = Convert.ToInt32(await SqlConnectionFactory.ScalarAsync(db,
                    "SELECT COUNT(*) FROM sys.database_principals WHERE name = @n;", token, ("@n", user)) ?? 0) > 0;
                if (taken) user = c.AppLogin + "_App";
                Log(LogLevel.Info, $"Creating database user {user} for login {c.AppLogin}...");
                await SqlConnectionFactory.ExecuteAsync(db,
                    "DECLARE @s nvarchar(max) = N'CREATE USER ' + QUOTENAME(@u) + N' FOR LOGIN ' + QUOTENAME(@l) + N' WITH DEFAULT_SCHEMA = [dbo]'; EXEC (@s);",
                    token, ("@u", user), ("@l", c.AppLogin));
            }
            await SqlConnectionFactory.ExecuteAsync(db,
                "DECLARE @s nvarchar(max) = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@u); EXEC (@s);",
                token, ("@u", user));
            Log(LogLevel.Success, $"Login {c.AppLogin} is db_owner of {c.DatabaseName} (database user {user}).");

            // 4. Hotfix scripts (if chosen), each starting in the Granite database.
            foreach (var (label, script) in hotfixScripts)
            {
                if (!string.Equals(db.Database, c.DatabaseName, StringComparison.OrdinalIgnoreCase))
                    db.ChangeDatabase(c.DatabaseName);
                await RunScriptAsync(db, label, script, token);
            }

            // 5. The Custodian token, last, so it wins over anything the
            //    create script or a hotfix seeded.
            if (tokenValues is not null)
            {
                if (!string.Equals(db.Database, c.DatabaseName, StringComparison.OrdinalIgnoreCase))
                    db.ChangeDatabase(c.DatabaseName);
                Log(LogLevel.Info, $"Setting the Custodian token from {tokenSource}...");
                await using var cmd = db.CreateCommand();
                cmd.CommandText = CustodianToken.UpsertSql;
                cmd.CommandTimeout = 120;
                cmd.Parameters.Add("@token", System.Data.SqlDbType.NVarChar, -1).Value = tokenValues.Token;
                cmd.Parameters.Add("@key", System.Data.SqlDbType.NVarChar, -1).Value = tokenValues.EncryptionKey;
                cmd.Parameters.Add("@version", System.Data.SqlDbType.Int).Value = tokenValues.Version;
                cmd.Parameters.Add("@user", System.Data.SqlDbType.NVarChar, 50).Value = CustodianToken.AuditUser;
                cmd.Parameters.Add("@overwrite", System.Data.SqlDbType.Bit).Value = overwriteToken;
                await using var reader = await cmd.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    throw new InvalidOperationException("Setting the Custodian token returned no result.");
                string message = CustodianToken.DescribeResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), overwriteToken);
                Log(LogLevel.Success, message);
            }
        }
    }

    /// <summary>
    /// Runs batches in order and stops at the first failure (what the
    /// script's own ":on error exit" asks for). PRINT output goes to the log
    /// as Detail lines; a progress line every 25% keeps the long create
    /// script from looking stuck.
    /// </summary>
    private async Task RunScriptAsync(SqlConnection conn, string label, ParsedSqlScript script, CancellationToken token)
    {
        int total = script.Batches.Count;
        Log(LogLevel.Info, $"Running {label} ({total} batch{(total == 1 ? "" : "es")})...");

        void OnInfo(object? sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
            {
                string text = err.Message.Trim();
                if (text.Length > 0) Log(LogLevel.Detail, "   " + text);
            }
        }

        conn.InfoMessage += OnInfo;
        try
        {
            int nextReport = 25;
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                SqlBatch batch = script.Batches[i];
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = batch.Text;
                cmd.CommandTimeout = 0;
                try
                {
                    await cmd.ExecuteNonQueryAsync(token);
                }
                catch (SqlException ex)
                {
                    string firstLines = string.Join(Environment.NewLine, batch.Text.Split('\n').Take(4)).Trim();
                    throw new InvalidOperationException(
                        $"{label}: batch {i + 1} of {total} (line {batch.StartLine}) failed: {ex.Message}{Environment.NewLine}Batch starts with:{Environment.NewLine}{firstLines}", ex);
                }

                int pct = (int)(100.0 * (i + 1) / total);
                if (total >= 20 && pct >= nextReport && pct < 100)
                {
                    Log(LogLevel.Info, $"{label}: {pct}% ({i + 1}/{total} batches)");
                    nextReport += 25;
                }
            }
        }
        finally
        {
            conn.InfoMessage -= OnInfo;
        }
        Log(LogLevel.Success, $"{label} completed.");
    }
}
