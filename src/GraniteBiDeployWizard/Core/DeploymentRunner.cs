using GraniteBiDeployWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Executes the selected .sql script array against the target server: reads
/// each file, substitutes $(SourceDb)/$(BiDb), splits on GO boundaries, and
/// runs each batch over one long-lived connection -- honoring the scripts'
/// USE statements by switching the connection's database via
/// SqlConnection.ChangeDatabase rather than sending the USE text itself.
/// </summary>
public sealed class DeploymentRunner
{
    private readonly Action<LogEntry> _log;
    private readonly Func<SyncSkipPromptContext, bool>? _confirmResync;

    /// <param name="log">Appends one line to the Step 6 console.</param>
    /// <param name="confirmResync">
    /// Asked only when bi.SyncLog shows the BI database already has data
    /// from a prior sync -- lets the caller (Step5DeployLogControl, via a
    /// MessageBox) offer to skip the initial sync on a redeploy and let
    /// the scheduled task catch the tables up instead, rather than always
    /// re-running a potentially multi-minute full sync. Return true to run
    /// the sync now, false to skip it. Null (the default) always runs it,
    /// same as before this existed -- useful for any non-interactive
    /// caller.
    /// </param>
    public DeploymentRunner(Action<LogEntry> log, Func<SyncSkipPromptContext, bool>? confirmResync = null)
    {
        _log = log;
        _confirmResync = confirmResync;
    }

    public sealed record RunResult(int BatchesExecuted, int BatchesFailed, int FilesSkipped, bool Aborted);

    /// <summary>
    /// What <see cref="DeploymentRunner"/> knows about a prior completed
    /// sync, handed to <c>confirmResync</c> so it can show a meaningful
    /// message -- how stale the existing data is, and whether a scheduler
    /// is even in place to catch it up if the sync is skipped.
    /// </summary>
    public sealed record SyncSkipPromptContext(
        DateTime LastSyncTime, int MinutesAgo, string LastSyncStatus,
        SchedulerType Scheduler, int ScheduleIntervalMinutes);

    public async Task<RunResult> RunAsync(
        DeploymentContext context, string rightsAdminConnectionString, CancellationToken token)
    {
        int executed = 0, failed = 0, skipped = 0;

        var filesToRun = context.ScriptFiles.Where(f => f.Included && !f.UnsupportedDirectives).ToList();
        if (filesToRun.Count == 0)
        {
            Log(LogLevel.Warning, "No script files are selected -- nothing to deploy.");
            return new RunResult(0, 0, 0, Aborted: false);
        }

        // Connect to "master" initially: several scripts run administrative
        // statements (CREATE DATABASE, msdb job setup) before their first
        // USE switches context, and master always exists.
        string connectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, "master", context.SqlUsername, context.SqlPassword);

        await using var connection = new SqlConnection(connectionString);
        Log(LogLevel.Info, $"Connecting to {context.Server} ...");
        await connection.OpenAsync(token);
        Log(LogLevel.Success, "Connected.");

        // Runs before any numbered script: a BI database created by an
        // earlier attempt at this same deployment (before some other
        // prerequisite was in place) can be stuck on the wrong collation
        // forever otherwise -- 01_Create_BI_Database.sql only sets it at
        // the moment it creates the database, never on a database that
        // already exists. Left unrepaired, this surfaces several scripts
        // later as a much less obvious "Cannot resolve the collation
        // conflict" failure in 03_Sync_Engine.sql. See CollationRepairService.
        var collationResult = await CollationRepairService.EnsureBiDatabaseCollationMatchesSourceAsync(
            connection, context.SourceDb, context.BiDb, token);
        Log(collationResult.Success ? (collationResult.Repaired ? LogLevel.Success : LogLevel.Info) : LogLevel.Warning,
            $"Collation check: {collationResult.Message}");

        foreach (var file in filesToRun)
        {
            token.ThrowIfCancellationRequested();
            Log(LogLevel.Info, $"--- {file.FileName} ---");

            string rawText;
            try
            {
                rawText = await File.ReadAllTextAsync(file.FullPath, token);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Could not read {file.FileName}: {ex.Message}");
                skipped++;
                continue;
            }

            string normalized = ScriptBatchParser.NormalizeLineEndings(rawText);
            string uncommented = ScriptBatchParser.StripCommentsPreservingLayout(normalized);
            string substituted = ScriptBatchParser.ReplaceVariables(uncommented, context.SourceDb, context.BiDb);
            var batches = ScriptBatchParser.SplitIntoBatches(substituted);

            foreach (var (batchText, startLine) in batches)
            {
                token.ThrowIfCancellationRequested();

                if (ScriptBatchParser.IsEffectivelyEmpty(batchText))
                    continue;

                string? useTarget = ScriptBatchParser.TryGetStandaloneUseTarget(batchText);
                if (useTarget is not null)
                {
                    if (!string.Equals(connection.Database, useTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            connection.ChangeDatabase(useTarget);
                            Log(LogLevel.Info, $"Switched context to [{useTarget}].");
                        }
                        catch (Exception ex)
                        {
                            // A real run against a client server showed this can
                            // happen even right after the deployment login itself
                            // created [useTarget] (which normally makes the
                            // creator its owner automatically): some SQL Server
                            // instances reassign a freshly created database's
                            // ownership away from its creator as a
                            // security-hardening policy, before this login ever
                            // gets to use it. If this is the BI database (the one
                            // this wizard itself just created, as opposed to the
                            // pre-existing source database), try one explicit
                            // repair -- grant the login access back via the
                            // privileged admin identity -- before giving up. See
                            // DeploymentRightsService's remarks for the evidence.
                            bool healed = false;
                            if (string.Equals(useTarget, context.BiDb, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(rightsAdminConnectionString))
                            {
                                Log(LogLevel.Warning,
                                    $"{file.FileName} line {startLine}: could not switch to [{useTarget}]: " +
                                    $"{ex.Message.TrimEnd('.', ' ')}. Attempting to grant access back explicitly and retry once.");

                                var healResult = await DeploymentRightsService.EnsureDatabaseAccessAsync(
                                    rightsAdminConnectionString, context.SqlUsername, useTarget, token);

                                if (healResult.Success)
                                {
                                    try
                                    {
                                        connection.ChangeDatabase(useTarget);
                                        Log(LogLevel.Success, $"Switched context to [{useTarget}] after granting access.");
                                        healed = true;
                                    }
                                    catch (Exception retryEx)
                                    {
                                        Log(LogLevel.Error,
                                            $"Still could not switch to [{useTarget}] after granting access: {retryEx.Message}");
                                    }
                                }
                                else
                                {
                                    Log(LogLevel.Error, $"Could not grant access to [{useTarget}]: {healResult.Message}");
                                }
                            }

                            if (!healed)
                            {
                                // This is not "one statement failed" -- it means we no
                                // longer know which database subsequent statements would
                                // land in. Continuing would risk silently creating the
                                // rest of the BI schema (tables, views, roles, procs)
                                // inside whatever database the connection is still
                                // sitting in -- most dangerously, master. Stop the whole
                                // run here rather than guess.
                                Log(LogLevel.Error,
                                    $"{file.FileName} line {startLine}: could not switch to [{useTarget}]: {ex.Message}");
                                Log(LogLevel.Error,
                                    $"Aborting deployment: without a working [{useTarget}], nothing after this point can " +
                                    "be trusted to run in the right database. No further scripts will be executed, and " +
                                    "the scheduled task will not be registered. Fix the issue above (commonly: the SQL " +
                                    "login doesn't have permission to create/access this database) and run the wizard again.");
                                return new RunResult(executed, failed + 1, skipped, Aborted: true);
                            }
                        }
                    }
                    continue; // standalone USE line is not sent to the server
                }

                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batchText;
                    command.CommandTimeout = 600; // schema/index builds on a large BI table can run long
                    await command.ExecuteNonQueryAsync(token);
                    executed++;
                }
                catch (SqlException ex)
                {
                    // Keep going: the scripts are written to be re-runnable,
                    // and one optional batch failing (e.g. SQL Agent objects
                    // on an Express instance with no Agent service) shouldn't
                    // abort the rest of the deployment. This is safe precisely
                    // because we know we're in the right database -- unlike a
                    // failed USE above, this can't land work in the wrong place.
                    failed++;
                    Log(LogLevel.Error, $"{file.FileName} line {startLine}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Log(LogLevel.Error, $"{file.FileName} line {startLine}: unexpected error: {ex.Message}");
                }
            }
        }

        // The master $(ScriptDir) orchestrator (00_Deploy_All.sql, not run
        // natively -- see ScriptBatchParser.ContainsUnsupportedDirectives)
        // triggers this same initial sync between building the views and
        // building the indexes. Run it once here too, so the BI tables are
        // populated immediately rather than sitting empty until the first
        // scheduled task tick. Non-fatal: if 03_Sync_Engine.sql was
        // unticked, bi.usp_RunSync won't exist yet, and that's fine -- the
        // scheduled task will pick the sync up on its own once it does.
        try
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(connection.Database, context.BiDb, StringComparison.OrdinalIgnoreCase))
                connection.ChangeDatabase(context.BiDb);

            // A redeploy -- adding a new view, rerunning after a schema
            // tweak -- hits this same block every time, even though the BI
            // tables are usually already fully populated and only minutes
            // stale. A full bi.usp_RunSync pass can take several minutes on
            // a large source database, and if a scheduler is registered
            // it'll bring the tables current on its own within one
            // interval anyway -- so ask rather than always paying that
            // cost. bi.SyncLog is the source of truth for "has this ever
            // actually synced data": a genuinely first-ever deployment (no
            // prior completed batch) always runs the sync, no prompt.
            bool runSync = true;
            var lastSync = await SyncStatusService.GetLastCompletedSyncAsync(connection, token);
            if (lastSync is not null)
            {
                string ageText = lastSync.MinutesAgo <= 0 ? "less than a minute ago" : $"{lastSync.MinutesAgo} minute(s) ago";
                Log(LogLevel.Info, $"The BI database already has synced data (last completed sync: {ageText}, {lastSync.Status}).");

                if (_confirmResync is not null)
                {
                    runSync = _confirmResync(new SyncSkipPromptContext(
                        lastSync.LastBatchTime, lastSync.MinutesAgo, lastSync.Status,
                        context.Scheduler, context.ScheduleIntervalMinutes));
                }
            }

            if (runSync)
            {
                Log(LogLevel.Info, "Running initial sync (EXEC bi.usp_RunSync) to populate the BI tables...");
                await using var syncCommand = connection.CreateCommand();
                syncCommand.CommandText = "EXEC bi.usp_RunSync;";
                syncCommand.CommandTimeout = 600;
                await syncCommand.ExecuteNonQueryAsync(token);
                Log(LogLevel.Success, "Initial sync complete.");
            }
            else
            {
                string scheduleNote = context.Scheduler == SchedulerType.None
                    ? "No scheduler is registered for this deployment, so the BI tables will stay as they are until bi.usp_RunSync is run again (manually, or via Run_BI_Sync.bat)."
                    : $"The scheduled task (every {context.ScheduleIntervalMinutes} min) will bring the BI tables fully current on its own.";
                Log(LogLevel.Warning, $"Skipped the initial sync, as chosen. {scheduleNote}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log(LogLevel.Warning,
                $"Initial sync did not run yet: {ex.Message.TrimEnd('.', ' ')}. " +
                "The scheduled task will pick it up on its next tick.");
        }

        // Give the Panel 5 scheduled-task account (Windows/trusted auth via
        // -E in Run_BI_Sync.bat) a database user and EXECUTE rights on
        // bi.usp_RunSync, so the scheduled sync can actually run without
        // any further manual SQL. Non-fatal: if the account doesn't have a
        // server-level login yet -- Panel 5's "Create/Verify SQL Login"
        // step wasn't run, or wasn't run against this server -- this just
        // logs a clear pointer back to that step. The scheduled task still
        // gets registered either way; it just won't succeed until this is
        // fixed.
        if (!string.IsNullOrWhiteSpace(context.WindowsAccountName))
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(connection.Database, context.BiDb, StringComparison.OrdinalIgnoreCase))
                {
                    Log(LogLevel.Warning,
                        $"Skipping SQL access setup for '{context.WindowsAccountName}': not currently connected to [{context.BiDb}].");
                }
                else
                {
                    await BootstrapLoginService.GrantSyncExecutionRightsAsync(connection, context.WindowsAccountName, token);
                    Log(LogLevel.Success, $"Granted '{context.WindowsAccountName}' rights to run bi.usp_RunSync.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(LogLevel.Warning,
                    $"Could not grant '{context.WindowsAccountName}' rights to run bi.usp_RunSync: {ex.Message.TrimEnd('.', ' ')}. " +
                    "If Panel 5's \"Create/Verify SQL Login\" step wasn't run, that's likely why -- go back and run " +
                    "it, or ask your DBA to run CREATE LOGIN [account] FROM WINDOWS; on this server, then re-run " +
                    "this deployment.");
            }
        }

        int skippedForDirectives = context.ScriptFiles.Count(f => f.Included == false && f.UnsupportedDirectives);
        skipped += skippedForDirectives;

        Log(failed == 0 ? LogLevel.Success : LogLevel.Warning,
            $"Deployment run finished: {executed} batch(es) executed, {failed} failed, {skipped} file(s) skipped.");

        return new RunResult(executed, failed, skipped, Aborted: false);
    }

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));
}
