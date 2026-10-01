using System.Collections.Generic;

namespace GraniteBiDeployWizard.Models;

/// <summary>
/// The mechanism Panel 5 registers to run <c>bi.usp_RunSync</c> on a
/// recurring interval. Exactly one is registered per deployment -- Panel 6
/// never sets up more than one, even if the other mechanism's script is
/// separately ticked on Panel 4.
/// </summary>
public enum SchedulerType
{
    /// <summary>
    /// A native Windows Scheduled Task running the generated Run_BI_Sync.bat.
    /// Works identically on SQL Server Express and Standard/Enterprise --
    /// the default, and the only option on Express (no Agent service).
    /// </summary>
    WindowsTaskScheduler,

    /// <summary>
    /// A SQL Server Agent job calling bi.usp_RunSync directly. Requires
    /// Standard or Enterprise edition -- Express has no Agent service --
    /// see <see cref="DeploymentContext.SqlEngineEdition"/>.
    /// </summary>
    SqlServerAgent,

    /// <summary>
    /// A row in Granite's own <c>dbo.ScheduledJobs</c> table, polled by the
    /// Granite Scheduler service. Confirmed 2026-08-28 against a real
    /// working install: works on Express (unlike SQL Server Agent), and
    /// <c>StoredProcedure</c> takes a bare, unqualified name -- the service
    /// evidently calls it in whatever database <c>ScheduledJobs</c> itself
    /// lives in (the live Granite database), not the separate BI database
    /// <c>bi.usp_RunSync</c> lives in. So this mechanism creates a thin
    /// proxy procedure in the live database first (see
    /// <see cref="GraniteSchedulerProxyProcedureName"/>) that does the
    /// cross-database call, and registers that instead. See
    /// <see cref="GraniteSchedulerService"/> for the full mechanics and the
    /// EXECUTE-grant simplification it documents.
    /// </summary>
    GraniteScheduler,

    /// <summary>
    /// Nothing is registered by the wizard. Run_BI_Sync.bat is still
    /// generated so it's ready to wire in by hand later -- into Task
    /// Scheduler, SQL Server Agent, or Granite Scheduler -- but the BI
    /// tables will not stay up to date on their own until something does.
    /// For a client that isn't ready to decide yet, or wants to schedule it
    /// themselves.
    /// </summary>
    None
}

/// <summary>
/// Everything gathered from the six wizard panels, carried forward so later
/// steps (and the final deployment run) can act on it. One instance lives for
/// the lifetime of the wizard and is passed to each step control.
/// </summary>
public sealed class DeploymentContext
{
    // ----- Panel 1: DB credentials -----------------------------------------
    public string Server { get; set; } = string.Empty;
    public string SqlUsername { get; set; } = string.Empty;
    public string SqlPassword { get; set; } = string.Empty;

    /// <summary>
    /// True once the "create a dedicated login" bootstrap flow on Panel 1
    /// has swapped SqlUsername/SqlPassword above for a login it created
    /// itself. Purely cosmetic -- lets Panel 6 mention it in the log -- the
    /// deployment run treats this exactly like any other Panel 1 login.
    /// </summary>
    public bool UsedDedicatedBootstrapLogin { get; set; }

    // ----- Panel 2: live database reporting-views prerequisite --------------

    /// <summary>
    /// True once Panel 2's "deploy the live database's reporting views"
    /// action has run successfully this session -- lets Panel 6 mention it
    /// in the log, same rationale as <see cref="UsedDedicatedBootstrapLogin"/>.
    /// </summary>
    public bool SourceViewsDeployedThisSession { get; set; }

    /// <summary>
    /// The source database name Panel 2's reporting-views check last ran
    /// against, and how many vw_BI_* views it found there -- null until
    /// Panel 2 has actually been passed. Panel 3 (Database Names) compares
    /// its own source database field against this, so changing the name
    /// there can't silently invalidate what Panel 2 already confirmed --
    /// the user gets sent back to re-check instead.
    /// </summary>
    public string? SourceViewsCheckedForDb { get; set; }
    public int? SourceViewsFoundCount { get; set; }

    // ----- Panel 3: database name resolution --------------------------------
    public string SourceDb { get; set; } = "GraniteLive";
    public string BiDb { get; set; } = "GraniteLive_BI";

    // ----- Panel 4: script folder + selected script array -------------------
    public string ScriptFolder { get; set; } = string.Empty;
    public List<ScriptFileItem> ScriptFiles { get; } = new();

    // ----- Panel 5: background sync schedule + execution account -----------
    public int ScheduleIntervalMinutes { get; set; } = 15;
    public string WindowsAccountName { get; set; } = string.Empty;
    public string WindowsAccountPassword { get; set; } = string.Empty;

    /// <summary>Which mechanism Panel 6 registers. See <see cref="SchedulerType"/>.</summary>
    public SchedulerType Scheduler { get; set; } = SchedulerType.WindowsTaskScheduler;

    /// <summary>
    /// SERVERPROPERTY('EngineEdition') from the last time Panel 5 checked
    /// (4 = Express, no Agent service; anything else -- Standard,
    /// Enterprise, Developer, Azure variants -- has one). Null until the
    /// check has run, or if it failed (e.g. transient connection issue) --
    /// treated as "unknown", not as "not Express", so SQL Server Agent is
    /// never wrongly ruled out on a check that simply didn't complete.
    /// </summary>
    public int? SqlEngineEdition { get; set; }

    // ----- Derived --------------------------------------------------------
    public string TaskName => $"GraniteWMS BI Sync - {BiDb}";
    public string BatchFileName => "Run_BI_Sync.bat";
    public string SqlAgentJobName => $"{BiDb} - Sync";

    /// <summary>Fixed, not per-BiDb -- matches the plain, unsuffixed naming of Granite's own example jobs (HandleLabelPrintQueue, InventorySnapshot).</summary>
    public string GraniteSchedulerJobName => "GraniteBiSync";

    /// <summary>Created in the live (source) database by <see cref="GraniteSchedulerService"/>; registered in dbo.ScheduledJobs as the bare name this mechanism needs.</summary>
    public string GraniteSchedulerProxyProcedureName => "usp_RunGraniteBiSync";

    public string BuildBatchFilePath() => Path.Combine(ScriptFolder, BatchFileName);
}
