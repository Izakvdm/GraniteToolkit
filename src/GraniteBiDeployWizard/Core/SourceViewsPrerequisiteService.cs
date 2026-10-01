using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Handles a prerequisite this deployment kit assumes but never creates: the
/// dbo.vw_BI_* reporting views in the live SOURCE GraniteWMS database.
/// </summary>
/// <remarks>
/// Traced from a real deployment that failed with 95 "Invalid object name
/// 'GraniteLive.dbo.vw_BI_...'" errors, on every run, even after the source
/// database read-access grant (see BootstrapLoginService) was confirmed
/// working. Reading the actual scripts settled it:
/// <list type="bullet">
/// <item><c>02_Create_BI_Tables.sql</c> builds every bi.* table with
/// <c>SELECT TOP (0) * INTO bi.X FROM [$(SourceDb)].dbo.vw_BI_X</c> -- it
/// expects these views to already exist in the source database.</item>
/// <item><c>07_Superset_Compatibility_Views.sql</c>'s own header comment
/// says its dbo.vw_BI_* views (built inside the BI database) "mirror the
/// live GraniteWMS.dbo.vw_BI_* views exactly ... so the same datasets,
/// charts and dashboards keep working when you repoint the connection" --
/// confirming the source-side views are a separate, pre-existing artifact
/// this kit is designed to sit downstream of, not something it provisions.</item>
/// </list>
/// Neither of the six numbered scripts in the BI deployment kit folder
/// creates them. On a fresh/sandbox copy of the live database that never had
/// them deployed, the fix lives in a different script entirely --
/// Granite_Superset_Views.sql (or whatever the current name is at the
/// client), normally run once against the live database as part of the base
/// Superset reporting rollout, independent of this BI sync kit. This
/// service lets the wizard check for that prerequisite and run that script
/// itself, instead of the user having to leave the wizard and do it by hand
/// in SSMS.
/// </remarks>
public static class SourceViewsPrerequisiteService
{
    public sealed record CheckResult(bool Success, int ViewsFound, string Message);
    public sealed record DeployResult(int BatchesExecuted, int BatchesFailed, IReadOnlyList<string> Errors);

    /// <summary>
    /// Counts objects matching the vw_BI_% naming convention in the given
    /// database. A count of zero is the exact symptom this feature exists to
    /// catch; any positive count means the prerequisite is already met and
    /// nothing needs to be deployed (deploying again is harmless too --
    /// Granite_Superset_Views.sql uses CREATE OR ALTER VIEW throughout --
    /// but there is no point prompting for it).
    /// </summary>
    public static async Task<CheckResult> CheckAsync(string connectionString, string sourceDb, CancellationToken token = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            connection.ChangeDatabase(sourceDb);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.views WHERE name LIKE 'vw[_]BI[_]%';";
            command.CommandTimeout = 30;
            int count = Convert.ToInt32(await command.ExecuteScalarAsync(token));

            string message = count == 0
                ? $"No vw_BI_* views found in [{sourceDb}]. Deployment will fail with \"Invalid object name\" for " +
                  "every bi.* table until these are deployed to the source database first."
                : $"{count} vw_BI_* view(s) found in [{sourceDb}] -- this prerequisite is already met.";
            return new CheckResult(true, count, message);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, 0, $"Could not check [{sourceDb}] for vw_BI_* views: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the supplied base-reporting-views script against the source
    /// database. The script this targets is plain GO-batch T-SQL with no
    /// SQLCMD directives and no USE of its own (it is written to be opened
    /// in SSMS with the source database already selected as the query
    /// context) -- ChangeDatabase does that job here instead, exactly as
    /// DeploymentRunner does for the numbered BI kit scripts. If the file
    /// does contain a standalone USE, it is honored the same way rather than
    /// assumed away.
    /// </summary>
    public static async Task<DeployResult> DeployAsync(
        string connectionString, string sourceDb, string scriptPath, CancellationToken token = default)
    {
        string rawText = await File.ReadAllTextAsync(scriptPath, token);
        string normalized = ScriptBatchParser.NormalizeLineEndings(rawText);
        string uncommented = ScriptBatchParser.StripCommentsPreservingLayout(normalized);
        var batches = ScriptBatchParser.SplitIntoBatches(uncommented);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        connection.ChangeDatabase(sourceDb);

        int executed = 0, failed = 0;
        var errors = new List<string>();

        foreach (var (batchText, startLine) in batches)
        {
            token.ThrowIfCancellationRequested();
            if (ScriptBatchParser.IsEffectivelyEmpty(batchText))
                continue;

            string? useTarget = ScriptBatchParser.TryGetStandaloneUseTarget(batchText);
            if (useTarget is not null)
            {
                connection.ChangeDatabase(useTarget);
                continue;
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batchText;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(token);
                executed++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"line {startLine}: {ex.Message}");
            }
        }

        return new DeployResult(executed, failed, errors);
    }
}
