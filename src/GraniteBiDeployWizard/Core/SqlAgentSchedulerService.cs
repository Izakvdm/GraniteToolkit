using GraniteBiDeployWizard.Models;
using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Registers the recurring background sync as a SQL Server Agent job,
/// instead of a Windows Scheduled Task -- the alternative Panel 5 offers on
/// Standard/Enterprise (Express has no Agent service; see
/// SqlConnectionFactory.DetectEngineEditionAsync). Requires no Windows
/// account: the job step runs as its owner, the Panel 1 SQL login that
/// deployed everything else.
/// </summary>
/// <remarks>
/// This mirrors the deployment kit's standalone 06_Scheduler_SqlAgent.sql
/// exactly (same job name, same job step, same schedule shape), but is a
/// second, independent implementation -- not a copy run at runtime --
/// because the standalone script hardcodes a 15-minute interval and this
/// path needs whatever interval Panel 5 actually chose. The kit script
/// stays as the reference for a manual, non-wizard deployment; this is the
/// one the wizard itself runs. If the job's shape ever needs to change,
/// change it in both places.
/// <para/>
/// This job step's owner needs EXECUTE on bi.usp_RunSync for as long as the
/// job exists, not just during this one deployment -- so this now grants it
/// explicitly, as its own last step, rather than assuming the owner will
/// still be db_owner (or otherwise broadly privileged) by the time the job
/// next fires. That assumption held while db_owner rights the deployment
/// login picked up along the way were simply never revoked, but
/// <see cref="ElevatedRightsService"/> can now scope a temporarily-elevated
/// login's rights back down once deployment finishes -- this explicit
/// grant is what keeps this specific mechanism working correctly either
/// way, whether or not elevation was used for this run.
/// </remarks>
public static class SqlAgentSchedulerService
{
    public static async Task RegisterOrUpdateSyncJobAsync(DeploymentContext context, CancellationToken token)
    {
        string connectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, "msdb", context.SqlUsername, context.SqlPassword);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        string jobName = context.SqlAgentJobName;

        // Re-runnable, same as the standalone script: drop any prior job of
        // this name first (e.g. from an earlier deployment with a different
        // interval) rather than leaving a stale schedule attached alongside
        // a new one.
        await ExecuteAsync(connection, token,
            "IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @jobName) " +
            "EXEC msdb.dbo.sp_delete_job @job_name = @jobName, @delete_unused_schedule = 1;",
            ("@jobName", jobName));

        await ExecuteAsync(connection, token,
            "EXEC msdb.dbo.sp_add_job @job_name = @jobName, @description = @description, @enabled = 1;",
            ("@jobName", jobName),
            ("@description", $"Refreshes the {context.BiDb} reporting tables from the vw_BI_ views. " +
                              "Created by the GraniteWMS BI Deployment Wizard."));

        await ExecuteAsync(connection, token,
            "EXEC msdb.dbo.sp_add_jobstep @job_name = @jobName, @step_name = N'Run bi.usp_RunSync', " +
            "@subsystem = N'TSQL', @database_name = @biDb, @command = N'EXEC bi.usp_RunSync;', " +
            "@retry_attempts = 0, @on_success_action = 1, @on_fail_action = 2;",
            ("@jobName", jobName),
            ("@biDb", context.BiDb));

        // @freq_subday_type = 4 is "minutes"; @freq_type = 4 is "daily",
        // i.e. every day, at the interval below, starting at midnight --
        // same shape as the standalone script, just with Panel 5's actual
        // interval instead of a hardcoded 15.
        await ExecuteAsync(connection, token,
            "EXEC msdb.dbo.sp_add_schedule @schedule_name = @scheduleName, @freq_type = 4, " +
            "@freq_interval = 1, @freq_subday_type = 4, @freq_subday_interval = @intervalMinutes, " +
            "@active_start_time = 0;",
            ("@scheduleName", $"Every {context.ScheduleIntervalMinutes} minutes"),
            ("@intervalMinutes", context.ScheduleIntervalMinutes));

        await ExecuteAsync(connection, token,
            "EXEC msdb.dbo.sp_attach_schedule @job_name = @jobName, @schedule_name = @scheduleName;",
            ("@jobName", jobName),
            ("@scheduleName", $"Every {context.ScheduleIntervalMinutes} minutes"));

        await ExecuteAsync(connection, token,
            "EXEC msdb.dbo.sp_add_jobserver @job_name = @jobName;",
            ("@jobName", jobName));

        await GrantOwnerExecuteRightsAsync(context, token);
    }

    /// <summary>
    /// Gives the job owner (the deployment login) an explicit, standing
    /// EXECUTE grant on bi.usp_RunSync in the BI database -- independent of
    /// whatever broader rights it may or may not still hold by the time the
    /// job next fires. See the class remarks for why this can no longer be
    /// assumed.
    /// </summary>
    private static async Task GrantOwnerExecuteRightsAsync(DeploymentContext context, CancellationToken token)
    {
        string bracketedLogin = "[" + context.SqlUsername.Replace("]", "]]") + "]";

        string connectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, context.BiDb, context.SqlUsername, context.SqlPassword);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        // Looked up by SID, not by matching the login name to the user
        // name. A real client run hit this exact collision in
        // GraniteSchedulerService's equivalent grant: when the deployment
        // login already owns [BiDb] (mapped as "dbo", from creating the
        // database or the db_owner grant), "SELECT ... WHERE name =
        // @name" doesn't see that existing mapping, and CREATE USER then
        // fails with "the login already has an account under a different
        // user name" -- a login can only be mapped to one user per
        // database. Resolving the login's actual existing user name first
        // (any name, dbo included) and granting to that avoids the
        // collision; dbo already has full rights, so the grant is a
        // harmless no-op in that case.
        string? existingUserName;
        await using (var checkUser = connection.CreateCommand())
        {
            checkUser.CommandText = @"
SELECT dp.name
FROM sys.database_principals dp
JOIN sys.server_principals sp ON dp.sid = sp.sid
WHERE sp.name = @loginName;";
            checkUser.Parameters.AddWithValue("@loginName", context.SqlUsername);
            existingUserName = await checkUser.ExecuteScalarAsync(token) as string;
        }
        if (existingUserName is null)
        {
            await using var createUser = connection.CreateCommand();
            createUser.CommandText = $"CREATE USER {bracketedLogin} FOR LOGIN {bracketedLogin};";
            await createUser.ExecuteNonQueryAsync(token);
            existingUserName = context.SqlUsername;
        }

        string bracketedUser = "[" + existingUserName.Replace("]", "]]") + "]";
        await using var grant = connection.CreateCommand();
        grant.CommandText = $"GRANT EXECUTE ON bi.usp_RunSync TO {bracketedUser};";
        await grant.ExecuteNonQueryAsync(token);
    }

    public static async Task<bool> JobExistsAsync(DeploymentContext context, CancellationToken token = default)
    {
        string connectionString = SqlConnectionFactory.BuildConnectionString(
            context.Server, "msdb", context.SqlUsername, context.SqlPassword);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @jobName;";
        command.Parameters.AddWithValue("@jobName", context.SqlAgentJobName);
        var result = await command.ExecuteScalarAsync(token);
        return result is not null;
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, CancellationToken token, string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 60;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        await command.ExecuteNonQueryAsync(token);
    }
}
