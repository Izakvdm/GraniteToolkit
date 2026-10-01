using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Looks at <c>bi.SyncLog</c> to tell whether the BI database already has
/// synced data from a previous deployment or scheduled run, so
/// <see cref="DeploymentRunner"/> can offer to skip the (often multi-minute)
/// initial <c>bi.usp_RunSync</c> pass on a redeploy rather than always
/// re-running it.
/// </summary>
public static class SyncStatusService
{
    public sealed record LastSyncInfo(DateTime LastBatchTime, string Status, int MinutesAgo);

    /// <summary>
    /// Returns the most recent completed <c>usp_RunSync</c> batch (Success
    /// or CompletedWithErrors -- either one means data actually landed;
    /// 'Skipped' rows, where another run was already in progress, don't
    /// count as evidence the tables are populated). Returns null when
    /// <c>bi.SyncLog</c> doesn't exist yet (a genuinely first-ever
    /// deployment, or 03_Sync_Engine.sql wasn't ticked) or has no such row
    /// -- both cases where the caller should just run the sync, same as
    /// before this check existed.
    /// </summary>
    public static async Task<LastSyncInfo?> GetLastCompletedSyncAsync(SqlConnection connection, CancellationToken token)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;
            // MinutesAgo is computed server-side (DATEDIFF against
            // SYSDATETIME()) rather than in C# against BatchTime, so it
            // can't be thrown off by any clock skew between this machine
            // and the SQL Server instance.
            command.CommandText = @"
IF OBJECT_ID(N'bi.SyncLog', N'U') IS NULL
    SELECT CAST(NULL AS datetime2(3)) AS LastBatchTime, CAST(NULL AS nvarchar(30)) AS Status, CAST(NULL AS int) AS MinutesAgo;
ELSE
    SELECT TOP (1) BatchTime AS LastBatchTime, [Status], DATEDIFF(minute, BatchTime, SYSDATETIME()) AS MinutesAgo
    FROM bi.SyncLog
    WHERE ObjectName = N'usp_RunSync' AND [Status] IN (N'Success', N'CompletedWithErrors')
    ORDER BY BatchTime DESC;";

            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token) && !await reader.IsDBNullAsync(0, token))
            {
                return new LastSyncInfo(
                    reader.GetDateTime(0),
                    reader.GetString(1),
                    reader.GetInt32(2));
            }
            return null;
        }
        catch (SqlException)
        {
            // If bi.SyncLog is somehow unreadable, the safe default is to
            // let the caller run the full sync rather than silently skip
            // it on a guess.
            return null;
        }
    }
}
