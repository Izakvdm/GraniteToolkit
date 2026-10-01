using System.Text.RegularExpressions;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Turns raw T-SQL script text into a sequence of executable batches:
/// SQLCMD variable substitution, GO-boundary splitting, and stripping the
/// standalone USE statements that scripts use to switch database context
/// (the wizard switches context itself via SqlConnection.ChangeDatabase
/// instead of sending literal USE text -- see DeploymentRunner).
/// </summary>
public static partial class ScriptBatchParser
{
    // GO on its own line, optionally followed by a repeat count (GO 3) and/or
    // a trailing comment, case-insensitive, tolerant of surrounding whitespace.
    [GeneratedRegex(@"^[ \t]*GO[ \t]*(\d+)?[ \t]*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoSeparator();

    // A batch that, once trimmed of whitespace/comments, is nothing but a
    // single USE statement, e.g. USE [$(BiDb)];  or  USE master
    [GeneratedRegex(@"^\s*USE\s+\[?(?<db>[^\];\s]+)\]?\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneUse();

    // SQLCMD scripting commands (:setvar, :r, :on error, :connect, ...) that
    // this engine does not implement -- these mark an orchestration/master
    // script meant to be run through sqlcmd itself, not parsed natively.
    [GeneratedRegex(@"^[ \t]*:(setvar|r\s|on\s+error|connect|out|error|perftrace|list|serverlist|quit|exit)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SqlCmdDirective();

    /// <summary>
    /// True when the script contains SQLCMD directives that only sqlcmd.exe
    /// itself (or SSMS in SQLCMD mode) can interpret. Lines that are
    /// commented out (start with -- before the colon) do not count.
    /// </summary>
    public static bool ContainsUnsupportedDirectives(string scriptText)
    {
        foreach (Match m in SqlCmdDirective().Matches(scriptText))
        {
            // Re-check the whole line: a commented-out directive like
            // "--:setvar SourceDb "x"" is fine, it's inert T-SQL comment text.
            int lineStart = scriptText.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            int lineEnd = scriptText.IndexOf('\n', m.Index);
            if (lineEnd < 0) lineEnd = scriptText.Length;
            string line = scriptText[lineStart..lineEnd];
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("--", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Normalizes all line endings to '\n'. The deployment scripts are
    /// Windows-authored (CRLF), and the GO-boundary regex anchors on '$'
    /// per line -- without this, a trailing '\r' before every line break
    /// would stop "GO\r\n" from ever matching as a standalone separator
    /// line, silently collapsing an entire file into a single batch.
    /// </summary>
    public static string NormalizeLineEndings(string scriptText) =>
        scriptText.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Blanks out every comment (line and block, nesting-aware) to spaces,
    /// leaving string literals untouched and every line break exactly where
    /// it was -- so line numbers used for error reporting stay accurate.
    /// </summary>
    /// <remarks>
    /// This must run before <see cref="SplitIntoBatches"/>. Without it, a GO
    /// separator regex has no way to know it's looking at a line inside an
    /// open /* ... */ block: the deployment scripts include a commented-out
    /// sample login block in 05_Security_Roles_Logins.sql with several GO
    /// lines inside it, and naive splitting turns each of those into its own
    /// "batch" -- one of which is a real, executable
    /// <c>CREATE LOGIN ... WITH PASSWORD = 'CHANGE_ME_Strong!Passw0rd'</c>
    /// statement that was only ever meant to be edited and uncommented by
    /// hand. Stripping comments first means GO/USE text inside them is just
    /// blank space by the time batches are cut, so it can never be sent to
    /// the server. String literals are preserved because $(SourceDb) and
    /// $(BiDb) sometimes appear inside one (e.g. N'$(SourceDb)' passed to
    /// DATABASEPROPERTYEX), and those still need to be substituted.
    /// </remarks>
    public static string StripCommentsPreservingLayout(string scriptText)
    {
        var sb = new System.Text.StringBuilder(scriptText.Length);
        int i = 0;
        int n = scriptText.Length;

        while (i < n)
        {
            char c = scriptText[i];

            // Single-quoted string literal, with '' as an escaped quote.
            // Left untouched (including any -- /* */ text inside it).
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    sb.Append(scriptText[i]);
                    bool isQuote = scriptText[i] == '\'';
                    i++;
                    if (isQuote)
                    {
                        if (i < n && scriptText[i] == '\'') { sb.Append('\''); i++; continue; } // escaped ''
                        break;
                    }
                }
                continue;
            }

            // Line comment: blank to end of line, keep the newline itself.
            if (c == '-' && i + 1 < n && scriptText[i + 1] == '-')
            {
                while (i < n && scriptText[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            // Block comment: T-SQL allows nesting, so track depth.
            if (c == '/' && i + 1 < n && scriptText[i + 1] == '*')
            {
                int depth = 1;
                sb.Append(' ', 2);
                i += 2;
                while (i < n && depth > 0)
                {
                    if (i + 1 < n && scriptText[i] == '/' && scriptText[i + 1] == '*')
                    {
                        depth++; sb.Append(' ', 2); i += 2; continue;
                    }
                    if (i + 1 < n && scriptText[i] == '*' && scriptText[i + 1] == '/')
                    {
                        depth--; sb.Append(' ', 2); i += 2; continue;
                    }
                    sb.Append(scriptText[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Replaces every literal occurrence of $(SourceDb) and $(BiDb) with the
    /// user-supplied database names from Panel 3.
    /// </summary>
    public static string ReplaceVariables(string scriptText, string sourceDb, string biDb)
    {
        return scriptText
            .Replace("$(SourceDb)", sourceDb, StringComparison.Ordinal)
            .Replace("$(BiDb)", biDb, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits already-normalized, variable-substituted script text into
    /// batches on GO boundaries. Returns the batches in file order,
    /// including their original starting line number (1-based) for error
    /// reporting.
    /// </summary>
    public static List<(string Text, int StartLine)> SplitIntoBatches(string scriptText)
    {
        var results = new List<(string, int)>();

        int StartLineOf(int charIndex)
        {
            int startLine = 1;
            for (int i = 0; i < charIndex && i < scriptText.Length; i++)
                if (scriptText[i] == '\n') startLine++;
            return startLine;
        }

        int lastIndex = 0;
        foreach (Match m in GoSeparator().Matches(scriptText))
        {
            string raw = scriptText[lastIndex..m.Index];
            results.Add((raw, StartLineOf(lastIndex)));
            lastIndex = m.Index + m.Length;
        }

        // trailing text after the final GO (or the whole script, if no GO at all)
        results.Add((scriptText[lastIndex..], StartLineOf(lastIndex)));

        return results;
    }

    /// <summary>
    /// If <paramref name="batchText"/>, once trimmed, is nothing but a single
    /// USE statement, returns the target database name it names; otherwise
    /// null. Used so the runner can honor the context switch via
    /// SqlConnection.ChangeDatabase instead of executing the USE text.
    /// </summary>
    public static string? TryGetStandaloneUseTarget(string batchText)
    {
        string trimmed = StripLineComments(batchText).Trim();
        if (trimmed.Length == 0) return null;

        var match = StandaloneUse().Match(trimmed);
        return match.Success ? match.Groups["db"].Value : null;
    }

    /// <summary>
    /// True when a batch has nothing left to execute once comments and
    /// whitespace are removed (e.g. a lone "GO" with a preceding blank
    /// section, or a block that is only a header comment).
    /// </summary>
    public static bool IsEffectivelyEmpty(string batchText)
    {
        string noBlockComments = Regex.Replace(batchText, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        string noLineComments = StripLineComments(noBlockComments);
        return noLineComments.Trim().Length == 0;
    }

    private static string StripLineComments(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int idx = lines[i].IndexOf("--", StringComparison.Ordinal);
            if (idx >= 0) lines[i] = lines[i][..idx];
        }
        return string.Join('\n', lines);
    }
}
