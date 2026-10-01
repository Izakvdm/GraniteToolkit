using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Keeps the BI database's collation aligned with the live source database's,
/// automatically, on every deployment run -- not just the first.
/// </summary>
/// <remarks>
/// Traced from a real run against Ultra\SQLEXPRESS: with the source-views
/// prerequisite (see <see cref="SourceViewsPrerequisiteService"/>) finally in
/// place, deployment got past the 95 "Invalid object name" errors and hit a
/// new one -- three failures in 03_Sync_Engine.sql, all
/// <c>Msg 468 "Cannot resolve the collation conflict between
/// Latin1_General_CI_AS and SQL_Latin1_General_CP1_CI_AS"</c>.
///
/// <para>
/// 01_Create_BI_Database.sql already knows about this: when it creates the BI
/// database, it reads the source database's collation and applies it with
/// <c>CREATE DATABASE ... COLLATE ...</c>. But that logic sits inside an
/// <c>IF DB_ID($(BiDb)) IS NULL</c> guard -- it only ever runs at the moment
/// the database is first created. On this environment, GraniteLive_BI was
/// created by an earlier deployment attempt (made before the source-side
/// vw_BI_* views existed, several runs before this fix), so it locked in
/// whatever collation the server default happened to be at the time. Every
/// later run finds the database already exists, skips 01's guard entirely,
/// and the mismatch persists no matter how many times the wizard is re-run.
/// </para>
///
/// <para>
/// The deployment kit ships a standalone fix for exactly this
/// (00_Fix_Collation.sql), but it hardcodes GraniteWMS/GraniteWMS_BI, has to
/// be run by hand outside the wizard, and its own header says to then
/// manually re-run 02, 03, 04 and 07 in order. This service does the same
/// three statements (SINGLE_USER WITH ROLLBACK IMMEDIATE, ALTER DATABASE
/// COLLATE, MULTI_USER) using the actual configured database names, and
/// DeploymentRunner calls it once per run, before the numbered scripts
/// execute -- so the same re-run of 02/03/04/07 the manual fix calls for
/// happens automatically as part of the very run that just repaired the
/// collation, with no separate step for the user to remember.
/// </para>
///
/// <para>
/// A second real run, 2026-08-28, hit a further wrinkle: the ALTER DATABASE
/// COLLATE itself failed with "The column 'SyncLog.DurationMs' is dependent
/// on database collation. The database collation cannot be changed if a
/// schema-bound object depends on it." <c>bi.SyncLog.DurationMs</c> is a
/// computed column (<c>DATEDIFF(millisecond, StartTime, EndTime)</c>) --
/// purely numeric, no string expression anywhere in it -- but SQL Server
/// blocks ALTER DATABASE COLLATE while *any* computed column exists in the
/// database, regardless of its type, because the column's definition is
/// parsed and bound under the database's current collation. The standard
/// workaround is to drop the column (and anything indexed on it) before the
/// COLLATE and recreate it after, so this service now does exactly that --
/// using the same definitions 01_Create_BI_Database.sql creates them with --
/// whenever a collation mismatch actually needs repairing. Guarded by an
/// existence check either way, so it's a no-op on a BI database that
/// doesn't have bi.SyncLog yet (a genuinely first-ever run never reaches
/// this branch at all -- see the "does not exist yet" case below).
/// </para>
///
/// <para>
/// That SyncLog fix immediately exposed a second, worse bug on the very next
/// run: the fix's own existence check (a cross-database query against
/// <c>{BiDb}.sys.indexes</c> / <c>sys.columns</c>) ran *before* SINGLE_USER
/// was claimed, and by then the previous run had already left the BI
/// database stuck permanently in single-user mode -- its COLLATE had failed
/// partway through the old SINGLE_USER -&gt; COLLATE -&gt; MULTI_USER sequence
/// (on the SyncLog dependency above), so MULTI_USER never ran, and the
/// database stayed exclusively locked (eventually to some other session --
/// the wizard's own connection for that run had long since closed) for
/// every run after. The observed symptom was blunt: "Database ... is
/// already open and can only have one user at a time" on the very next
/// collation check, then the same failure again from
/// 01_Create_BI_Database.sql's own <c>ALTER DATABASE ... SET RECOVERY
/// SIMPLE</c>. Two changes fix this for good: <c>SET SINGLE_USER WITH
/// ROLLBACK IMMEDIATE</c> now runs *first*, before any other statement in
/// the repair branch -- it forcibly reclaims the database even if something
/// else (another run's leftover session, someone's open SSMS tab) currently
/// holds it, exactly like it already did for the COLLATE statement itself --
/// and everything from the SyncLog check through COLLATE to the SyncLog
/// rebuild is now wrapped in a <c>try/finally</c> that unconditionally runs
/// <c>SET MULTI_USER</c> afterward, so a failure anywhere in that sequence
/// can never again leave the database stuck.
/// </para>
///
/// <para>
/// Reported again later: a real deployment left the BI database in
/// single-user mode after the run. The <c>SET SINGLE_USER WITH ROLLBACK
/// IMMEDIATE</c> statement itself was still running *before* the
/// try/finally added above, not inside it -- so the one failure mode the
/// previous fix didn't cover was the claim statement itself throwing
/// (most plausibly a client-side command timeout on an ALTER DATABASE that
/// had already completed server-side, but any exception at that point has
/// the same effect): the database ends up single-user server-side while
/// the .NET exception jumps straight past the try/finally to the outer
/// catch, which only returns an error message and never attempts
/// MULTI_USER. Fixed by moving the SINGLE_USER statement inside the same
/// try the SyncLog/COLLATE work already runs in, so the finally's
/// <c>SET MULTI_USER</c> now covers it too. This is safe even when
/// SINGLE_USER never actually took (the ordinary case, on any run that
/// wasn't erroring): <c>SET MULTI_USER</c> on an already-multi-user
/// database is a no-op.
/// </para>
///
/// <para>
/// A real deployment (2026-09-10) confirmed the actual cause of that kind
/// of failure directly: an SSMS query window was sitting open, connected
/// to the BI database, from a prior manual ALTER script. <c>SET
/// SINGLE_USER WITH ROLLBACK IMMEDIATE</c> is supposed to forcibly
/// disconnect exactly that kind of session, but SQL Server can still
/// refuse the request outright with <c>Msg 5061 "ALTER DATABASE failed
/// because a lock could not be placed ... Try again later"</c> -- its own
/// wording says this is meant to be retried, not treated as fatal. Before
/// this fix, one such refusal failed the whole collation check
/// immediately, which then let the real collation-mismatch errors this
/// class exists to prevent resurface in 03_Sync_Engine.sql. The SINGLE_USER
/// claim now retries a few times, a few seconds apart, specifically on
/// Msg 5061 before giving up -- enough for SQL Server to actually process
/// the forced disconnect in most cases. If every attempt still fails, the
/// error message returned now says plainly that another connection (an
/// open SSMS tab, Superset, another copy of this wizard) is holding the
/// BI database open and needs to be closed, rather than just echoing SQL
/// Server's generic wording.
/// </para>
/// </remarks>
public static class CollationRepairService
{
    public sealed record RepairResult(bool Success, bool Repaired, string Message);

    // Msg 5061: "ALTER DATABASE failed because a lock could not be placed
    // on database '%.*ls'. Try again later." -- SQL Server's own transient/
    // retry-suggested error for SET SINGLE_USER contention. See the class
    // remarks above for the real run (an idle SSMS window) this was traced
    // from.
    private const int LockNotPlaceableErrorNumber = 5061;
    private const int SingleUserMaxAttempts = 4;
    private static readonly TimeSpan SingleUserRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Compares the source and BI database collations using the supplied
    /// connection (temporarily switched to master, then restored to whatever
    /// database it was pointed at before returning) and realigns the BI
    /// database's collation to the source's if they differ.
    /// </summary>
    public static async Task<RepairResult> EnsureBiDatabaseCollationMatchesSourceAsync(
        SqlConnection connection, string sourceDb, string biDb, CancellationToken token)
    {
        string originalDatabase = connection.Database;
        try
        {
            if (!string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase))
                connection.ChangeDatabase("master");

            string? sourceCollation = await GetCollationAsync(connection, sourceDb, token);
            if (sourceCollation is null)
                return new RepairResult(false, false, $"Could not read the collation of [{sourceDb}] -- check the source database name.");

            string? biCollation = await GetCollationAsync(connection, biDb, token);
            if (biCollation is null)
            {
                // Normal on a first-ever run: 01_Create_BI_Database.sql is
                // about to create it with the right collation from the start.
                return new RepairResult(true, false,
                    $"[{biDb}] does not exist yet -- it will be created with [{sourceDb}]'s collation.");
            }

            if (string.Equals(sourceCollation, biCollation, StringComparison.OrdinalIgnoreCase))
                return new RepairResult(true, false, $"[{biDb}] collation already matches [{sourceDb}] ({biCollation}).");

            string quotedBi = QuoteName(biDb);

            bool hadSyncLogIndex = false;
            bool hadSyncLogColumn = false;
            try
            {
                // Claimed first, unconditionally, before anything else
                // touches the database -- WITH ROLLBACK IMMEDIATE forcibly
                // reclaims it even if some other session (a stuck leftover
                // from an earlier failed run, someone's open SSMS tab)
                // currently holds it. See the class remarks above for the
                // real run that made this ordering necessary. This
                // statement now runs *inside* the try/finally below rather
                // than before it (see the newest class remarks) so that a
                // failure here -- most plausibly a client-side timeout on
                // an ALTER DATABASE that actually completed server-side --
                // still reaches the finally's SET MULTI_USER instead of
                // leaving the database claimed with nothing left to
                // release it.
                //
                // Retries a few times on Msg 5061 specifically -- SQL
                // Server's own wording for that error is "Try again
                // later," and a real run traced it to an idle SSMS window
                // still connected to the BI database, which WITH ROLLBACK
                // IMMEDIATE should clear but doesn't always manage to on
                // the first attempt. See the class remarks above.
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        await using var singleUserCommand = connection.CreateCommand();
                        singleUserCommand.CommandTimeout = 60;
                        singleUserCommand.CommandText = $"ALTER DATABASE {quotedBi} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;";
                        await singleUserCommand.ExecuteNonQueryAsync(token);
                        break;
                    }
                    catch (SqlException ex) when (ex.Number == LockNotPlaceableErrorNumber && attempt < SingleUserMaxAttempts)
                    {
                        await Task.Delay(SingleUserRetryDelay, token);
                    }
                }

                // bi.SyncLog.DurationMs (and its index) block ALTER DATABASE
                // COLLATE if present -- see the class remarks above. Check
                // first so we only drop/recreate what's actually there. Safe
                // to run now: single-user mode above guarantees this
                // connection is the database's only user.
                await using (var checkCommand = connection.CreateCommand())
                {
                    checkCommand.CommandTimeout = 30;
                    checkCommand.CommandText = $@"
SELECT
    CASE WHEN EXISTS (
        SELECT 1 FROM {quotedBi}.sys.indexes i
        JOIN {quotedBi}.sys.objects o ON i.object_id = o.object_id
        JOIN {quotedBi}.sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'bi' AND o.name = N'SyncLog' AND i.name = N'IX_bi_SyncLog_BatchTime'
    ) THEN 1 ELSE 0 END,
    CASE WHEN EXISTS (
        SELECT 1 FROM {quotedBi}.sys.columns c
        JOIN {quotedBi}.sys.objects o ON c.object_id = o.object_id
        JOIN {quotedBi}.sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'bi' AND o.name = N'SyncLog' AND c.name = N'DurationMs'
    ) THEN 1 ELSE 0 END;";
                    await using var reader = await checkCommand.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token))
                    {
                        hadSyncLogIndex = reader.GetInt32(0) == 1;
                        hadSyncLogColumn = reader.GetInt32(1) == 1;
                    }
                }

                if (hadSyncLogIndex)
                {
                    await using var dropIndex = connection.CreateCommand();
                    dropIndex.CommandTimeout = 60;
                    dropIndex.CommandText = $"DROP INDEX IX_bi_SyncLog_BatchTime ON {quotedBi}.bi.SyncLog;";
                    await dropIndex.ExecuteNonQueryAsync(token);
                }
                if (hadSyncLogColumn)
                {
                    await using var dropColumn = connection.CreateCommand();
                    dropColumn.CommandTimeout = 60;
                    dropColumn.CommandText = $"ALTER TABLE {quotedBi}.bi.SyncLog DROP COLUMN DurationMs;";
                    await dropColumn.ExecuteNonQueryAsync(token);
                }

                await using (var collateCommand = connection.CreateCommand())
                {
                    collateCommand.CommandTimeout = 60;
                    collateCommand.CommandText = $"ALTER DATABASE {quotedBi} COLLATE {sourceCollation};";
                    await collateCommand.ExecuteNonQueryAsync(token);
                }

                if (hadSyncLogColumn)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.CommandTimeout = 60;
                    addColumn.CommandText =
                        $"ALTER TABLE {quotedBi}.bi.SyncLog ADD DurationMs AS DATEDIFF(millisecond, StartTime, EndTime);";
                    await addColumn.ExecuteNonQueryAsync(token);
                }
                if (hadSyncLogIndex)
                {
                    await using var addIndex = connection.CreateCommand();
                    addIndex.CommandTimeout = 60;
                    addIndex.CommandText =
                        $"CREATE INDEX IX_bi_SyncLog_BatchTime ON {quotedBi}.bi.SyncLog (BatchTime DESC, ObjectName);";
                    await addIndex.ExecuteNonQueryAsync(token);
                }
            }
            finally
            {
                // Unconditional: whatever happened above -- success, a
                // cancellation, any exception, including one thrown by the
                // SET SINGLE_USER statement itself now that it runs inside
                // this try -- the database must never be left single-user,
                // or every later connection (this deployment's own later
                // scripts included, per the real 01_Create_BI_Database.sql
                // failure this was traced from) starts failing with
                // "already open and can only have one user at a time."
                // Safe to always attempt even when SINGLE_USER never
                // actually took: SET MULTI_USER on a database that's
                // already multi-user is a harmless no-op. Best-effort: if
                // this itself fails, the original exception (if any) is
                // what gets reported below, not this one.
                try
                {
                    await using var multiUserCommand = connection.CreateCommand();
                    multiUserCommand.CommandTimeout = 60;
                    multiUserCommand.CommandText = $"ALTER DATABASE {quotedBi} SET MULTI_USER;";
                    await multiUserCommand.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch { /* best-effort; see remark above */ }
            }

            string syncLogNote = hadSyncLogColumn
                ? " (also rebuilt bi.SyncLog's DurationMs computed column and index, which block a collation change while present)"
                : string.Empty;

            return new RepairResult(true, true,
                $"[{biDb}] was {biCollation} but [{sourceDb}] is {sourceCollation} -- realigned [{biDb}] to match{syncLogNote}. " +
                "Re-run 02/03/04/07 below to rebuild anything created under the old collation.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number == LockNotPlaceableErrorNumber)
        {
            // Every retry above was exhausted -- something is still
            // holding the BI database open. Say so plainly instead of
            // just relaying SQL Server's generic "try again later"
            // wording; see the class remarks for the real run (an idle
            // SSMS window) this message is written for.
            return new RepairResult(false, false,
                $"Could not repair [{biDb}]'s collation: another connection is holding it open, and SQL Server " +
                $"would not release it even after {SingleUserMaxAttempts} attempts with ROLLBACK IMMEDIATE. Close any " +
                "other tool connected to the BI database (an SSMS query window, Superset, another copy of this " +
                "wizard) and re-run deployment.");
        }
        catch (Exception ex)
        {
            return new RepairResult(false, false, $"Could not check/repair the BI database's collation: {ex.Message}");
        }
        finally
        {
            if (!string.Equals(connection.Database, originalDatabase, StringComparison.OrdinalIgnoreCase))
            {
                try { connection.ChangeDatabase(originalDatabase); }
                catch { /* best-effort restore; the caller's next ChangeDatabase/USE will correct this anyway */ }
            }
        }
    }

    private static async Task<string?> GetCollationAsync(SqlConnection connection, string databaseName, CancellationToken token)
    {
        // Was DATABASEPROPERTYEX(@db, 'Collation') -- its own documentation
        // says it returns NULL not only when the database doesn't exist,
        // but also when the caller lacks permission to view it. A real run
        // against a client server (2026-08-28) hit exactly that: this
        // returned NULL for [GraniteLive_BI], logging "does not exist yet"
        // and silently skipping the mismatch-repair branch below, even
        // though the database genuinely existed with real synced data --
        // 01_Create_BI_Database.sql's own DB_ID() guard, running moments
        // later in the same connection, correctly found it and skipped
        // CREATE DATABASE. Likely the same server-side ownership/permission
        // hardening already worked around elsewhere in this project (see
        // DeploymentRightsService's remarks) making this specific database
        // invisible to DATABASEPROPERTYEX for this login, while sys.databases
        // -- instance-level metadata, visible to every login by default,
        // which is what DB_ID() itself resolves against -- was not. Querying
        // sys.databases directly here brings this check back in line with
        // 01's own, more reliable existence check.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT collation_name FROM sys.databases WHERE database_id = DB_ID(@db);";
        command.CommandTimeout = 30;
        command.Parameters.AddWithValue("@db", databaseName);
        return (await command.ExecuteScalarAsync(token)) as string;
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";
}
