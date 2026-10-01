using GraniteBiDeployWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Registers the recurring background sync with Granite's own Scheduler
/// service, via a row in the live Granite database's <c>dbo.ScheduledJobs</c>
/// table -- the third Panel 5 mechanism, alongside Windows Task Scheduler
/// and SQL Server Agent.
/// </summary>
/// <remarks>
/// Confirmed 2026-08-28 against real rows from a working Granite install
/// (screenshots of the Scheduled Jobs WebDesktop screen, the underlying
/// grid, and a direct SQL query against dbo.ScheduledJobs / ScheduledJobsHistory):
/// <list type="bullet">
/// <item>Works against a SQLEXPRESS instance -- unlike SQL Server Agent,
/// this mechanism is not edition-restricted.</item>
/// <item><c>Type = 'STOREDPROCEDURE'</c>, <c>InjectJob = NULL</c> for a
/// stored-procedure job (the alternative, unconfirmed <c>InjectJob</c> path
/// is for a different, compiled kind of job this wizard doesn't use).</item>
/// <item><c>IntervalFormat = 'MINUTES'</c> with <c>Interval</c> as a plain
/// integer number of minutes is the confirmed, working combination for a
/// fixed-cadence job -- confirmed twice now: first negatively, when a real
/// run against USA-GRANITEHCO-\SQLEXPRESS registered fine under the
/// originally-assumed <c>IntervalFormat = 'SECONDS'</c> but came back
/// <c>LastExecutionResult = "Interval value not valid"</c> once the
/// Scheduler service tried to process it; then positively, pulling every
/// row from that same install's <c>dbo.ScheduledJobs</c> -- of 8 real jobs,
/// none use SECONDS at all (six use <c>IntervalFormat = 'CRON'</c>, which
/// remains the better fit for a job that only needs to run during business
/// hours or on a non-uniform cadence), and one,
/// <c>AssignRouteNumbers-Automatically</c> (<c>Interval = 10</c>,
/// <c>IntervalFormat = 'MINUTES'</c>), is scheduled and running with
/// <c>LastExecutionResult = SUCCESS</c> -- a direct working precedent for
/// exactly this job's shape, a plain fixed-minutes interval.</item>
/// <item><c>StoredProcedure</c> holds a bare, unqualified procedure name
/// with no schema or database prefix in every real example seen -- the
/// service evidently executes it in whatever database <c>ScheduledJobs</c>
/// itself lives in (the live Granite database), not a separate one.</item>
/// </list>
/// <c>bi.usp_RunSync</c> lives in the separate BI database, so a bare name
/// can't reach it directly. This registers a thin proxy procedure in the
/// live database instead -- <c>dbo.usp_RunGraniteBiSync</c>, which just
/// does <c>EXEC [BiDb].bi.usp_RunSync;</c> -- and points <c>ScheduledJobs</c>
/// at that. Creating an object in the live database this way isn't a new
/// category of risk for this wizard: Step 2 already does the same thing
/// for the <c>dbo.vw_BI_*</c> reporting views.
/// <para/>
/// The SQL login the Granite Scheduler service itself connects as --
/// needed to grant it rights to call into the BI database -- is confirmed
/// 2026-08-28 from a real <c>appsettings.json</c> on a live install: its
/// <c>ConnectionStrings:GraniteConnection</c> uses SQL login
/// <c>Granite</c> (the password was visible in that file too, but is
/// never read, logged, or stored anywhere by this wizard or this
/// service -- only the login name is used). This grants EXECUTE on
/// <c>bi.usp_RunSync</c> to that specific login when it exists on the
/// target instance (creating a database user for it in the BI database
/// first, if one doesn't exist yet), and falls back to the broader
/// <c>public</c> role only if no <c>Granite</c> login is found -- a
/// different install may run the Scheduler service under a different
/// account, and a job that silently never fires (because it can't reach
/// the procedure) is worse than a slightly broader grant.
/// </remarks>
public static class GraniteSchedulerService
{
    public static async Task RegisterOrUpdateSyncJobAsync(DeploymentContext context, CancellationToken token)
    {
        string proxyProcedure = context.GraniteSchedulerProxyProcedureName;

        // T-SQL bracket-identifier escaping: a literal "]" in the database
        // name would otherwise close the bracket early.
        string biDbEscaped = context.BiDb.Replace("]", "]]");

        // ----- 1. Create the proxy procedure in the live (source) database -----
        string sourceConnectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, context.SourceDb, context.SqlUsername, context.SqlPassword);

        await using (var sourceConnection = new SqlConnection(sourceConnectionString))
        {
            await sourceConnection.OpenAsync(token);

            await using var createProxy = sourceConnection.CreateCommand();
            createProxy.CommandTimeout = 60;
            createProxy.CommandText =
                $"CREATE OR ALTER PROCEDURE dbo.{proxyProcedure} AS " +
                "BEGIN SET NOCOUNT ON; " +
                $"EXEC [{biDbEscaped}].bi.usp_RunSync; " +
                "END;";
            await createProxy.ExecuteNonQueryAsync(token);
        }

        // ----- 2. Register (or update) the job row --------------------------
        string liveConnectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, context.SourceDb, context.SqlUsername, context.SqlPassword);

        await using (var liveConnection = new SqlConnection(liveConnectionString))
        {
            await liveConnection.OpenAsync(token);

            await using var upsert = liveConnection.CreateCommand();
            upsert.CommandTimeout = 60;
            // IntervalFormat = 'SECONDS' (with Interval as a computed number
            // of seconds) was assumed working from an earlier, thinner look
            // at a real install and shipped as the default -- a real run
            // against USA-GRANITEHCO-\SQLEXPRESS proved that assumption
            // wrong: the job registered fine, but LastExecutionResult came
            // back "Interval value not valid" once the Scheduler service
            // actually tried to process it. Pulling every row from that
            // same install's dbo.ScheduledJobs settled it properly: of 8
            // real jobs, none use SECONDS at all -- six use CRON, and one,
            // AssignRouteNumbers-Automatically (Interval = 10, IntervalFormat
            // = MINUTES), is scheduled and running with LastExecutionResult
            // = SUCCESS. That's a direct, working precedent for exactly this
            // shape -- a plain interval in minutes -- so this now writes
            // IntervalFormat = 'MINUTES' with context.ScheduleIntervalMinutes
            // itself as Interval, no seconds conversion needed.
            upsert.CommandText = @"
IF EXISTS (SELECT 1 FROM dbo.ScheduledJobs WHERE JobName = @jobName)
    UPDATE dbo.ScheduledJobs
    SET isActive = 1, JobDescription = @description, [Type] = N'STOREDPROCEDURE',
        StoredProcedure = @proc, InjectJob = NULL, Interval = @interval,
        IntervalFormat = N'MINUTES', [Status] = N'SCHEDULED',
        AuditDate = SYSDATETIME(), AuditUser = @auditUser
    WHERE JobName = @jobName;
ELSE
    INSERT INTO dbo.ScheduledJobs
        (isActive, JobName, JobDescription, [Type], StoredProcedure, InjectJob,
         Interval, IntervalFormat, [Status], AuditDate, AuditUser)
    VALUES
        (1, @jobName, @description, N'STOREDPROCEDURE', @proc, NULL,
         @interval, N'MINUTES', N'SCHEDULED', SYSDATETIME(), @auditUser);";

            // dbo.ScheduledJobs.JobDescription is a short, fixed-width column --
            // confirmed 2026-08-28 from a real truncation error against a live
            // install (the error's own truncated-value text was exactly 100
            // characters long). The full sentence below can run past that for a
            // longer BiDb name, so it's defensively clamped to 100 chars here
            // rather than relying on the wording always fitting.
            string jobDescription =
                $"Refreshes the {context.BiDb} reporting tables from the vw_BI_ views. " +
                "Created by the GraniteWMS BI Deployment Wizard.";
            if (jobDescription.Length > 100)
            {
                jobDescription = jobDescription.Substring(0, 100);
            }

            upsert.Parameters.AddWithValue("@jobName", context.GraniteSchedulerJobName);
            upsert.Parameters.AddWithValue("@description", jobDescription);
            upsert.Parameters.AddWithValue("@proc", proxyProcedure);
            upsert.Parameters.AddWithValue("@interval", context.ScheduleIntervalMinutes.ToString());
            upsert.Parameters.AddWithValue("@auditUser", "GraniteBiWizard");

            await upsert.ExecuteNonQueryAsync(token);
        }

        // ----- 3. Grant EXECUTE on bi.usp_RunSync in the BI database --------
        // Prefers the confirmed real Scheduler service login ("Granite") --
        // creating a database user for it here if one doesn't already exist
        // -- and falls back to `public` only if that login isn't present on
        // this instance. See the class remarks above for the evidence and
        // the reasoning.
        string biConnectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, context.BiDb, context.SqlUsername, context.SqlPassword);

        await using (var biConnection = new SqlConnection(biConnectionString))
        {
            await biConnection.OpenAsync(token);

            // Looked up by SID, not by matching the login name to the user
            // name -- a real client run showed why that matters. On that
            // server the deployment login itself was named "Granite" (the
            // same login the Scheduler service connects as), and by the
            // time this step runs it already owns [BiDb] (mapped as "dbo",
            // via CREATE DATABASE / the db_owner grant), not as a user
            // literally named "Granite". "IF NOT EXISTS (... name =
            // N'Granite')" didn't see that existing "dbo" mapping, so it
            // tried CREATE USER [Granite] FOR LOGIN [Granite] anyway and
            // hit "The login already has an account under a different user
            // name" -- a login can only be mapped to one user per database.
            // Resolving the login's *actual* existing user name first (any
            // name, dbo included) and granting to that avoids the
            // collision; dbo already has full rights, so the grant is a
            // harmless no-op in that case.
            //
            // A first pass at this fix built the dynamic GRANT text
            // entirely in T-SQL (DECLARE + QUOTENAME() concatenated inside
            // an EXEC(...) call) and a real run against this same client
            // server hit "Incorrect syntax near 'QUOTENAME'" from it. Never
            // fully pinned down which part of that batch the parser
            // tripped on, so rather than keep guessing, this does the
            // lookup and identifier-bracketing in C# instead -- the same
            // ExecuteScalar-then-plain-GRANT shape already used (and
            // working) in SqlAgentSchedulerService's equivalent method and
            // DeploymentRightsService -- and never asks the server to parse
            // a GRANT it had to assemble dynamically at all.
            bool graniteLoginExists;
            await using (var checkLogin = biConnection.CreateCommand())
            {
                checkLogin.CommandText =
                    "SELECT 1 FROM sys.server_principals WHERE name = N'Granite' AND type IN ('S','U');";
                graniteLoginExists = await checkLogin.ExecuteScalarAsync(token) is not null;
            }

            if (graniteLoginExists)
            {
                string? existingUser;
                await using (var lookup = biConnection.CreateCommand())
                {
                    lookup.CommandText = @"
SELECT dp.name
FROM sys.database_principals dp
JOIN sys.server_principals sp ON dp.sid = sp.sid
WHERE sp.name = N'Granite';";
                    existingUser = await lookup.ExecuteScalarAsync(token) as string;
                }

                if (existingUser is null)
                {
                    await using var createUser = biConnection.CreateCommand();
                    createUser.CommandTimeout = 60;
                    createUser.CommandText = "CREATE USER [Granite] FOR LOGIN [Granite];";
                    await createUser.ExecuteNonQueryAsync(token);
                    existingUser = "Granite";
                }

                string bracketedUser = "[" + existingUser.Replace("]", "]]") + "]";
                await using var grant = biConnection.CreateCommand();
                grant.CommandTimeout = 60;
                grant.CommandText = $"GRANT EXECUTE ON bi.usp_RunSync TO {bracketedUser};";
                await grant.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var grantPublic = biConnection.CreateCommand();
                grantPublic.CommandTimeout = 60;
                grantPublic.CommandText = "GRANT EXECUTE ON bi.usp_RunSync TO public;";
                await grantPublic.ExecuteNonQueryAsync(token);
            }
        }
    }

    public static async Task<bool> JobExistsAsync(DeploymentContext context, CancellationToken token = default)
    {
        string connectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, context.SourceDb, context.SqlUsername, context.SqlPassword);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM dbo.ScheduledJobs WHERE JobName = @jobName;";
        command.Parameters.AddWithValue("@jobName", context.GraniteSchedulerJobName);
        var result = await command.ExecuteScalarAsync(token);
        return result is not null;
    }
}
