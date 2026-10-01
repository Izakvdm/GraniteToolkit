using System.Text;
using System.Text.RegularExpressions;

namespace GraniteInstallWizard.Core;

/// <summary>One executable batch and the 1-based line it starts on in the source file.</summary>
public sealed record SqlBatch(string Text, int StartLine);

/// <summary>A SQLCMD-mode script turned into executable batches.</summary>
public sealed record ParsedSqlScript(
    IReadOnlyList<SqlBatch> Batches,
    IReadOnlyList<string> UnresolvedVariables,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// Turns the release's SQLCMD-mode scripts (GraniteDatabase_Create.sql,
/// Hotfix\Database\SQLCLR_Install.sql) into batches this wizard can run
/// itself over Microsoft.Data.SqlClient, so the target server needs
/// neither sqlcmd.exe nor the SqlServer PowerShell module.
/// </summary>
/// <remarks>
/// <para>
/// Differs from the BI Deployment Wizard's ScriptBatchParser in two
/// deliberate ways, both forced by what these scripts actually contain:
/// </para>
/// <list type="bullet">
/// <item><b>:setvar and :on error are honoured, not refused.</b>
/// GraniteDatabase_Create.sql is SSDT-generated and starts with
/// <c>:setvar DefaultDataPath ""</c>, <c>:on error exit</c> and
/// <c>:setvar __IsSqlCmdEnabled "True"</c> -- refusing directives (the BI
/// parser's rule, correct for its own hand-written scripts) would refuse
/// the one script this wizard exists to run. Variables passed in by the
/// caller (DatabaseName, DefaultFilePrefix) win over :setvar lines, which
/// is what the release expects: the file ships with those two lines
/// commented out precisely so a caller supplies them.
/// :r (include another file) is still refused -- nothing in V6.0 uses it,
/// and silently skipping an include would deploy half a database.</item>
/// <item><b>Comments are kept in what gets executed.</b>
/// The BI parser blanks comments before splitting (it had to: a GO inside
/// a /* */ block in one of its scripts became a real batch and ran a
/// sample CREATE LOGIN). This parser keeps that protection, since GO and
/// directive detection run against a comment-stripped copy, but it cuts
/// batches from the original text at the same character positions
/// (<see cref="StripCommentsPreservingLayout"/> keeps every character
/// offset identical). The difference matters here because these scripts
/// create hundreds of views and procedures: executing the stripped text
/// would store every Granite object definition with its comments
/// replaced by spaces, which support staff reading sp_helptext would
/// notice.</item>
/// </list>
/// </remarks>
public static partial class GraniteSqlScriptParser
{
    [GeneratedRegex(@"^[ \t]*GO[ \t]*(\d+)?[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^[ \t]*:setvar[ \t]+(?<name>\w+)[ \t]+(?<value>.*?)[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex SetVarLine();

    [GeneratedRegex(@"^[ \t]*:on[ \t]+error\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnErrorLine();

    [GeneratedRegex(@"^[ \t]*:(r|connect|out|error|perftrace|serverlist|list|quit|exit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedDirectiveLine();

    [GeneratedRegex(@"\$\((?<name>\w+)\)")]
    private static partial Regex VariableReference();

    [GeneratedRegex(@"\b(?<create>CREATE)(?<gap>\s+)(?<kind>VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateObject();

    /// <summary>
    /// Normalizes line endings to '\n' and drops a leading BOM character.
    /// The release mixes CRLF files with UTF-16 ones (SQLCLR_Install.sql);
    /// line-anchored matching needs one consistent ending.
    /// </summary>
    public static string NormalizeLineEndings(string scriptText) =>
        scriptText.TrimStart('﻿').Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Replaces every comment (line and nested block) with spaces, leaving
    /// string literals, line breaks, and every character offset exactly as
    /// they were. Same algorithm as the BI Deployment Wizard's parser.
    /// </summary>
    public static string StripCommentsPreservingLayout(string scriptText)
    {
        var sb = new StringBuilder(scriptText.Length);
        int i = 0;
        int n = scriptText.Length;

        while (i < n)
        {
            char c = scriptText[i];

            // Single-quoted string literal, with '' as an escaped quote.
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
                        if (i < n && scriptText[i] == '\'') { sb.Append('\''); i++; continue; }
                        break;
                    }
                }
                continue;
            }

            if (c == '-' && i + 1 < n && scriptText[i + 1] == '-')
            {
                while (i < n && scriptText[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && i + 1 < n && scriptText[i + 1] == '*')
            {
                int depth = 1;
                sb.Append(' ', 2);
                i += 2;
                while (i < n && depth > 0)
                {
                    if (i + 1 < n && scriptText[i] == '/' && scriptText[i + 1] == '*') { depth++; sb.Append(' ', 2); i += 2; continue; }
                    if (i + 1 < n && scriptText[i] == '*' && scriptText[i + 1] == '/') { depth--; sb.Append(' ', 2); i += 2; continue; }
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
    /// Parses a SQLCMD-mode script into executable batches.
    /// </summary>
    /// <param name="rawText">File contents as read from disk.</param>
    /// <param name="callerVariables">
    /// Values for $(name) references. These take precedence over :setvar
    /// lines in the file (see class remarks). Names are case-insensitive,
    /// as in sqlcmd.
    /// </param>
    /// <exception cref="NotSupportedException">The script uses :r or another directive this parser cannot honour.</exception>
    public static ParsedSqlScript Parse(string rawText, IReadOnlyDictionary<string, string>? callerVariables = null)
    {
        string original = NormalizeLineEndings(rawText);
        string stripped = StripCommentsPreservingLayout(original);

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (callerVariables is not null)
            foreach (var kv in callerVariables) variables[kv.Key] = kv.Value;

        // Work on a mutable copy of the original so directive lines can be
        // blanked out of the batch text without shifting any offsets.
        var working = new StringBuilder(original);
        var boundaries = new List<(int Start, int End)>(); // GO lines, [Start, End)

        int lineStart = 0;
        while (lineStart <= stripped.Length)
        {
            int lineEnd = stripped.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = stripped.Length;
            string line = stripped[lineStart..lineEnd];

            if (GoLine().IsMatch(line))
            {
                boundaries.Add((lineStart, lineEnd));
            }
            else if (SetVarLine().Match(line) is { Success: true } sv)
            {
                string name = sv.Groups["name"].Value;
                string value = sv.Groups["value"].Value;
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                if (!variables.ContainsKey(name)) variables[name] = value;
                Blank(working, lineStart, lineEnd);
            }
            else if (OnErrorLine().IsMatch(line))
            {
                // The runner already stops at the first failed batch, which
                // is what ":on error exit" asks for.
                Blank(working, lineStart, lineEnd);
            }
            else if (UnsupportedDirectiveLine().IsMatch(line))
            {
                int lineNumber = CountLines(stripped, lineStart);
                throw new NotSupportedException(
                    $"Line {lineNumber} uses a SQLCMD directive this wizard cannot run: {line.Trim()}");
            }

            if (lineEnd >= stripped.Length) break;
            lineStart = lineEnd + 1;
        }

        string text = working.ToString();
        var lineIndex = new LineIndex(original);
        var batches = new List<SqlBatch>();
        var unresolved = new List<string>();
        int segmentStart = 0;

        void AddSegment(int start, int end)
        {
            if (end <= start) return;
            string strippedSegment = stripped[start..end];
            if (strippedSegment.Trim().Length == 0) return; // only comments/whitespace
            // A segment that held nothing but directives is blank in the working copy too.
            string raw = text[start..end];
            if (StripCommentsPreservingLayout(raw).Trim().Length == 0) return;

            string substituted = VariableReference().Replace(raw, m =>
            {
                string name = m.Groups["name"].Value;
                if (variables.TryGetValue(name, out string? value)) return value;
                if (!unresolved.Contains(name, StringComparer.OrdinalIgnoreCase)) unresolved.Add(name);
                return m.Value;
            });
            batches.Add(new SqlBatch(substituted.Trim('\n'), lineIndex.LineOf(start)));
        }

        foreach (var (start, end) in boundaries)
        {
            AddSegment(segmentStart, start);
            segmentStart = Math.Min(end + 1, text.Length);
        }
        AddSegment(segmentStart, text.Length);

        return new ParsedSqlScript(batches, unresolved, variables);
    }

    /// <summary>
    /// Rewrites CREATE VIEW/PROCEDURE/FUNCTION/TRIGGER as CREATE OR ALTER so a
    /// Hotfix\Database script can run against a database that already has
    /// the object (the create script always makes it first). Needs SQL
    /// Server 2016 SP1 or later; pre-flight warns on older servers. Matches
    /// are found on the comment-stripped copy so a CREATE VIEW mentioned in
    /// a comment is left alone.
    /// </summary>
    public static string ToCreateOrAlter(string scriptText)
    {
        string original = NormalizeLineEndings(scriptText);
        string stripped = StripCommentsPreservingLayout(original);
        var sb = new StringBuilder(original);
        int shift = 0;
        foreach (Match m in CreateObject().Matches(stripped))
        {
            // Skip anything already written as CREATE OR ALTER.
            string before = stripped[..m.Index].TrimEnd();
            if (before.EndsWith("ALTER", StringComparison.OrdinalIgnoreCase)) continue;
            int at = m.Groups["gap"].Index + shift;
            sb.Insert(at, " OR ALTER");
            shift += " OR ALTER".Length;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the bodies of ```sql fenced blocks in a markdown file. The
    /// release's Hotfix\Custodian.md carries a required UPDATE of the
    /// Custodian token this way, as documentation rather than a .sql file.
    /// </summary>
    public static IReadOnlyList<string> ExtractMarkdownSqlBlocks(string markdown)
    {
        var blocks = new List<string>();
        foreach (Match m in Regex.Matches(NormalizeLineEndings(markdown), @"```[ \t]*sql[ \t]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            blocks.Add(m.Groups["body"].Value);
        return blocks;
    }

    private static void Blank(StringBuilder sb, int start, int end)
    {
        for (int i = start; i < end; i++) sb[i] = ' ';
    }

    private static int CountLines(string text, int charIndex) => new LineIndex(text).LineOf(charIndex);

    /// <summary>Character offset to 1-based line number, via binary search over newline positions.</summary>
    private sealed class LineIndex
    {
        private readonly List<int> _newlines = new();

        public LineIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') _newlines.Add(i);
        }

        public int LineOf(int charIndex)
        {
            int idx = _newlines.BinarySearch(charIndex);
            return (idx >= 0 ? idx : ~idx) + 1;
        }
    }
}
