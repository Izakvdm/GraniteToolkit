/* ============================================================================
   GraniteWMS BI - Scheduled Sync Health Check
   Run this against the BI database (e.g. GraniteLive_BI) to confirm the
   Windows Scheduled Task is actually firing and bi.usp_RunSync is
   completing cleanly. Read-only -- safe to run any time, as often as you
   like.

   Covers what a SQL script can see: whether bi.SyncLog is getting regular,
   successful batch rows. It cannot see the Windows Scheduled Task itself
   (whether it's enabled, its own last-run result) -- see the PowerShell
   one-liner at the bottom of this file for that half.
   ============================================================================ */
USE GraniteLive_BI;   -- change to your actual BI database name
GO

-- Adjust to match whatever interval was chosen on Step 5 of the wizard
-- (5, 15, or 30 minutes). Used only to flag a run that's overdue below.
DECLARE @ExpectedIntervalMinutes int = 15;

/* ---- 1. Last 20 batches at a glance -------------------------------------
   One row per run of bi.usp_RunSync (ObjectName = 'usp_RunSync'), newest
   first. Status is 'Success', 'CompletedWithErrors', or 'Skipped' (another
   run was still in progress -- fine occasionally, a concern if constant). */
SELECT TOP (20)
    BatchTime,
    [Status]                                   AS BatchStatus,
    RowsInserted, RowsUpdated, RowsDeleted,
    DATEDIFF(second, StartTime, EndTime)       AS DurationSeconds,
    [Message]
FROM bi.SyncLog
WHERE ObjectName = N'usp_RunSync'
ORDER BY BatchTime DESC;

/* ---- 2. Is it overdue? ---------------------------------------------------
   Compares "now" to the last batch's start time against the expected
   interval, with a generous grace window (2x the interval, min 10 minutes)
   before calling it overdue, since a long-running batch can legitimately
   push the next trigger a little late. */
DECLARE @LastBatch datetime2(3) = (SELECT MAX(BatchTime) FROM bi.SyncLog WHERE ObjectName = N'usp_RunSync');
DECLARE @GraceMinutes int = CASE WHEN @ExpectedIntervalMinutes * 2 > 10 THEN @ExpectedIntervalMinutes * 2 ELSE 10 END;

SELECT
    @LastBatch                                                       AS LastBatchTime,
    DATEDIFF(minute, @LastBatch, SYSDATETIME())                      AS MinutesSinceLastBatch,
    @ExpectedIntervalMinutes                                         AS ExpectedIntervalMinutes,
    CASE
        WHEN @LastBatch IS NULL THEN 'NEVER RUN -- check the Scheduled Task exists and is enabled'
        WHEN DATEDIFF(minute, @LastBatch, SYSDATETIME()) > @GraceMinutes THEN 'OVERDUE -- check the Scheduled Task in Task Scheduler'
        ELSE 'OK'
    END                                                               AS HealthCheck;

/* ---- 3. Per-object detail for the most recent batch ----------------------
   Breaks the latest run down by which sync procedure did what -- useful
   when the batch summary shows CompletedWithErrors and you need to see
   which object(s) actually failed. */
DECLARE @LatestBatch datetime2(3) = (SELECT MAX(BatchTime) FROM bi.SyncLog);
SELECT
    ObjectName, [Status], RowsInserted, RowsUpdated, RowsDeleted,
    DATEDIFF(millisecond, StartTime, EndTime)  AS DurationMs,
    [Message]
FROM bi.SyncLog
WHERE BatchTime = @LatestBatch AND ObjectName <> N'usp_RunSync'
ORDER BY StartTime;

/* ---- 4. Any failures in the last 24 hours --------------------------------
   Every object-level failure across all recent batches, not just the
   latest one -- catches an intermittent failure that a healthy-looking
   latest batch would otherwise hide. */
SELECT
    BatchTime, ObjectName, [Status], [Message]
FROM bi.SyncLog
WHERE [Status] = 'Failed'
  AND BatchTime >= DATEADD(hour, -24, SYSDATETIME())
ORDER BY BatchTime DESC;

/* ============================================================================
   To also confirm the Windows Scheduled Task itself is enabled and firing
   on schedule (this SQL can't see that side), run this in PowerShell on
   the server that runs the sync:

       Get-ScheduledTaskInfo -TaskName "GraniteWMS BI Sync - GraniteLive_BI"

   LastTaskResult 0 = success, LastRunTime should be within one interval of
   now, and NextRunTime should be in the near future -- if NextRunTime is
   blank or in the past, the task is disabled or stuck.
   ============================================================================ */
