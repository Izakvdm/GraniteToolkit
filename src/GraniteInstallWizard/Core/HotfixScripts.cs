using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>One SQL source the "Run the Hotfix database scripts" option would run.</summary>
/// <param name="Label">How it's shown in the log and on Step 3, e.g. Hotfix\Database\SQLCLR_Install.sql.</param>
/// <param name="Path">The file on disk.</param>
/// <param name="IsMarkdown">True for a .md file whose ```sql blocks are run.</param>
public sealed record HotfixSqlSource(string Label, string Path, bool IsMarkdown);

/// <summary>
/// Lists the Hotfix database scripts in a release. Shared by
/// DatabaseInstaller (which runs them) and Step 3 (which says what they are).
/// </summary>
/// <remarks>
/// Only two places count, both from the V6.0 layout: Hotfix\Database\*.sql
/// and the SQL blocks in Hotfix\*.md.
///
/// V7.0 has neither, and that's deliberate. Its Hotfix\SQLCLR_Install.sql
/// sits loose in the Hotfix root and carries a different (older, August)
/// build of the Granite SQLCLR assembly than the one already in V7.0's
/// GraniteDatabase_Create.sql and GraniteSQLCLR.zip. Running it after the
/// create script could swap in the older assembly, so loose .sql files are
/// not picked up. GraniteDatabase\hotfix\Integration_Accpac_MasterItem.sql
/// is an Accpac integration view (it needs $(AccpacDatabase)) and isn't
/// part of the core stack either.
/// </remarks>
public static class HotfixScripts
{
    public static List<HotfixSqlSource> Find(InstallContext c)
    {
        var found = new List<HotfixSqlSource>();
        string root = c.HotfixRoot;
        if (!Directory.Exists(root)) return found;

        string? dbDir = ReleaseLayout.FindDirectory(root, "Database");
        if (dbDir is not null)
        {
            foreach (string file in Directory.GetFiles(dbDir, "*.sql").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                found.Add(new HotfixSqlSource($"Hotfix\\{System.IO.Path.GetFileName(dbDir)}\\{System.IO.Path.GetFileName(file)}", file, false));
        }
        // Custodian.md is never run as a script: its plain UPDATE overwrote a
        // working token with the release's older one (Ultra, 7 October). The
        // token has its own step (CustodianToken), which never lets the
        // release copy replace a token that's already set.
        foreach (string md in Directory.GetFiles(root, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            if (!IsCustodianToken(md))
                found.Add(new HotfixSqlSource($"Hotfix\\{System.IO.Path.GetFileName(md)}", md, true));
        return found;
    }

    /// <summary>True for Custodian.md, or any .md whose SQL sets the Custodian Token setting.</summary>
    public static bool IsCustodianToken(string mdPath)
    {
        if (System.IO.Path.GetFileName(mdPath).Equals("Custodian.md", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string text = File.ReadAllText(mdPath);
            return text.Contains("SystemSettings", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(text, @"\[?Key\]?\s*=\s*'Token'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (IOException) { return false; }
    }

    /// <summary>Short list for Step 3 and the review: "SQLCLR_Install.sql, Custodian.md".</summary>
    public static string Describe(IReadOnlyList<HotfixSqlSource> scripts) =>
        scripts.Count == 0 ? "none in this release" : string.Join(", ", scripts.Select(s => System.IO.Path.GetFileName(s.Path)));
}
