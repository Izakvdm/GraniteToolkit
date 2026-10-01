using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

// Point this at any folder of GraniteWMS BI deployment scripts to verify
// against a different script set; this path is where this session staged a
// copy of the real Source Files/Granite BI DB folder for testing.
const string scriptFolder = "/tmp/harness-scripts/";
const string sourceDb = "GraniteLive";
const string biDb = "GraniteLive_BI";

int failures = 0;

void Check(bool condition, string description)
{
    if (condition)
    {
        Console.WriteLine($"  [PASS] {description}");
    }
    else
    {
        Console.WriteLine($"  [FAIL] {description}");
        failures++;
    }
}

// Mirrors DeploymentRunner's exact pipeline: normalize -> strip comments -> substitute.
string RunPipeline(string raw) => ScriptBatchParser.ReplaceVariables(
    ScriptBatchParser.StripCommentsPreservingLayout(ScriptBatchParser.NormalizeLineEndings(raw)),
    sourceDb, biDb);

Console.WriteLine("=== 1. Panel 3 scan + classification against the real script folder ===");
var items = ScriptFileScanner.Scan(scriptFolder);
foreach (var item in items)
{
    string flag = item.UnsupportedDirectives ? "UNSUPPORTED" : item.Included ? "included" : "unticked";
    Console.WriteLine($"  {item.FileName,-32} -> {flag,-12} {item.Note}");
}

Check(items.Count == 10, $"found all 10 .sql files (found {items.Count})");
Check(items.First(i => i.FileName == "00_Deploy_All.sql").UnsupportedDirectives,
    "00_Deploy_All.sql detected as unsupported (:setvar/:r) and locked out");
Check(!items.First(i => i.FileName == "00_Fix_Collation.sql").Included,
    "00_Fix_Collation.sql starts unticked (remediation script, not in the deploy order)");
Check(!items.First(i => i.FileName == "06_Scheduler_SqlAgent.sql").Included,
    "06_Scheduler_SqlAgent.sql starts unticked (superseded by the wizard's own Task Scheduler step)");
foreach (var name in new[] { "01_Create_BI_Database.sql", "02_Create_BI_Tables.sql", "03_Sync_Engine.sql",
                             "04_Client_Views_rpt.sql", "05_Security_Roles_Logins.sql",
                             "07_Superset_Compatibility_Views.sql", "08_Indexing.sql" })
{
    var match = items.First(i => i.FileName == name);
    Check(match.Included && !match.UnsupportedDirectives, $"{name} starts ticked and runnable");
}

var orderedNames = items.Select(i => i.FileName).ToList();
var expectedOrder = new[]
{
    "00_Deploy_All.sql", "00_Fix_Collation.sql", "01_Create_BI_Database.sql", "02_Create_BI_Tables.sql",
    "03_Sync_Engine.sql", "04_Client_Views_rpt.sql", "05_Security_Roles_Logins.sql",
    "06_Scheduler_SqlAgent.sql", "07_Superset_Compatibility_Views.sql", "08_Indexing.sql"
};
Check(orderedNames.SequenceEqual(expectedOrder), "natural sort puts 00..08 in the correct numeric order");

Console.WriteLine();
Console.WriteLine("=== 2. GO-batch splitting on the real files -- exercises the line-ending and comment-stripping fixes ===");
int totalBatchesAllFiles = 0;
int totalUseSwitches = 0;
foreach (var item in items.Where(i => i.Included && !i.UnsupportedDirectives))
{
    string raw = File.ReadAllText(item.FullPath);
    // The real folder turns out to have mixed line endings -- most files are
    // CRLF, but 03 and 08 are plain LF on disk. That's exactly why
    // NormalizeLineEndings has to handle both: report which style each file
    // actually uses rather than assuming one.
    Console.WriteLine($"  {item.FileName,-32} line endings: {(raw.Contains("\r\n") ? "CRLF" : raw.Contains('\r') ? "CR" : "LF")}");

    string substituted = RunPipeline(raw);
    var batches = ScriptBatchParser.SplitIntoBatches(substituted);

    int useSwitches = 0, empty = 0, real = 0;
    foreach (var (text, _) in batches)
    {
        if (ScriptBatchParser.IsEffectivelyEmpty(text)) { empty++; continue; }
        if (ScriptBatchParser.TryGetStandaloneUseTarget(text) is string target) { useSwitches++; totalUseSwitches++; continue; }
        real++;

        Check(!text.Contains("$(SourceDb)") && !text.Contains("$(BiDb)"),
            $"{item.FileName}: no leftover $(SourceDb)/$(BiDb) tokens in an executable batch");
    }

    totalBatchesAllFiles += batches.Count;
    Console.WriteLine($"  {item.FileName,-32} {batches.Count,3} batch(es)  ({real} executable, {useSwitches} USE-switch, {empty} empty)");

    // The core regression this proves: on the old (pre-fix) code, every CRLF
    // file would report exactly 1 batch here, because "GO\r\n" never matched
    // the separator regex and the whole file was treated as one blob.
    Check(batches.Count > 1, $"{item.FileName}: GO separators were actually found (not collapsed into a single batch)");
}
Console.WriteLine($"  Total: {totalBatchesAllFiles} batches, {totalUseSwitches} USE context-switches, across 7 files.");

Console.WriteLine();
Console.WriteLine("=== 3. Standalone USE detection -- confirms ChangeDatabase targets resolve correctly ===");
{
    string raw = File.ReadAllText(Path.Combine(scriptFolder, "01_Create_BI_Database.sql"));
    string substituted = RunPipeline(raw);
    var useTargets = ScriptBatchParser.SplitIntoBatches(substituted)
        .Select(b => ScriptBatchParser.TryGetStandaloneUseTarget(b.Text))
        .Where(t => t is not null)
        .ToList();
    Console.WriteLine($"  01_Create_BI_Database.sql USE targets found: {string.Join(", ", useTargets)}");
    Check(useTargets.Contains(biDb), $"USE [$(BiDb)] correctly resolved to '{biDb}' after substitution");
}
{
    // 05_Security_Roles_Logins.sql has a commented-out sample block --
    //   /* USE [master]; GO CREATE LOGIN [client_bi] WITH PASSWORD =
    //      N'CHANGE_ME_Strong!Passw0rd', CHECK_POLICY = ON; GO
    //      USE [$(BiDb)]; GO CREATE USER [client_bi] ... */
    // -- with several GO lines *inside* the comment. A GO-splitter that
    // isn't comment-aware turns each of those into its own "batch",
    // including one that is a real, executable CREATE LOGIN statement with
    // a hardcoded sample password. This is the regression StripCommentsPreservingLayout
    // exists to prevent.
    string raw = File.ReadAllText(Path.Combine(scriptFolder, "05_Security_Roles_Logins.sql"));
    string substituted = RunPipeline(raw);
    var batches = ScriptBatchParser.SplitIntoBatches(substituted);

    bool anyMaster = batches
        .Select(b => ScriptBatchParser.TryGetStandaloneUseTarget(b.Text))
        .Any(t => string.Equals(t, "master", StringComparison.OrdinalIgnoreCase));
    Check(!anyMaster, "the commented-out USE [master] sample line in 05 is not picked up as a real context switch");

    var executableBatches = batches
        .Select(b => b.Text)
        .Where(t => !ScriptBatchParser.IsEffectivelyEmpty(t) && ScriptBatchParser.TryGetStandaloneUseTarget(t) is null)
        .ToList();
    Check(executableBatches.All(t => !t.Contains("CREATE LOGIN", StringComparison.OrdinalIgnoreCase)),
        "the commented-out sample CREATE LOGIN never appears as an executable batch");
    Check(executableBatches.All(t => !t.Contains("CHANGE_ME_Strong", StringComparison.Ordinal)),
        "the hardcoded sample password never appears in an executable batch");
    Console.WriteLine($"  05_Security_Roles_Logins.sql: {executableBatches.Count} executable batch(es), none from the commented-out sample.");
}

Console.WriteLine();
Console.WriteLine("=== 4. Run_BI_Sync.bat generation ===");
var context = new DeploymentContext
{
    Server = "SQL01\\GRANITE",
    SqlUsername = "sa",
    SqlPassword = "not-written-anywhere",
    SourceDb = sourceDb,
    BiDb = biDb,
    ScriptFolder = scriptFolder,
    ScheduleIntervalMinutes = 15,
    WindowsAccountName = @".\svc-granitebi",
    WindowsAccountPassword = "also-not-written-anywhere"
};
string batchContent = BatchFileGenerator.Generate(context);
Console.WriteLine(string.Join(Environment.NewLine, batchContent.Split('\n').Select(l => "  | " + l.TrimEnd('\r'))));
Check(batchContent.Contains("-E"), "generated .bat uses trusted (-E) auth");
Check(batchContent.Contains($"-d \"{biDb}\""), "generated .bat targets the BI database");
Check(batchContent.Contains("EXEC bi.usp_RunSync;"), "generated .bat calls bi.usp_RunSync");
Check(!batchContent.Contains(context.SqlPassword), "generated .bat does not contain the SQL password");
Check(!batchContent.Contains(context.WindowsAccountPassword), "generated .bat does not contain the Windows account password");

string writtenPath = BatchFileGenerator.WriteToScriptFolder(context);
Check(File.Exists(writtenPath), $"WriteToScriptFolder actually wrote {writtenPath}");

Console.WriteLine();
Console.WriteLine("=== 5. Path normalization ===");
Check(PathHelper.NormalizeFolder(@"C:\Foo\Bar") == @"C:\Foo\Bar\", "adds a trailing backslash when missing");
Check(PathHelper.NormalizeFolder(@"C:\Foo\Bar\") == @"C:\Foo\Bar\", "leaves an existing trailing backslash alone");
Check(PathHelper.NormalizeFolder(@"C:\Foo\Bar/") == @"C:\Foo\Bar\", "normalizes a trailing forward slash to backslash");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"ALL CHECKS PASSED against the real GraniteWMS BI script set."
    : $"{failures} CHECK(S) FAILED -- see [FAIL] lines above.");

return failures == 0 ? 0 : 1;
