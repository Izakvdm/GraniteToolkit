using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace GraniteAttachInstaller.Core;

/// <summary>
/// Splits a script into GO-separated batches and runs each one. Granite
/// Attach's own .sql files (table/procedure scripts this project wrote) are
/// simple - no SQLCMD variables, no GO text inside comments or strings - so
/// this is a deliberately smaller tool than the GraniteWMS Install Wizard's
/// GraniteSqlScriptParser, which has to cope with SSDT-generated release
/// scripts. If a future script here needs SQLCMD variables or safer
/// comment-handling, port that parser instead of extending this one.
/// </summary>
public static class SqlScriptRunner
{
    private static readonly Regex GoLine = new(@"^\s*GO\s*(\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> SplitBatches(string scriptText)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (string rawLine in scriptText.Replace("\r\n", "\n").Split('\n'))
        {
            if (GoLine.IsMatch(rawLine))
            {
                if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.AppendLine(rawLine);
        }
        if (current.ToString().Trim().Length > 0) batches.Add(current.ToString());
        return batches;
    }

    public static async Task RunScriptAsync(SqlConnection connection, string scriptText, CancellationToken token)
    {
        foreach (string batch in SplitBatches(scriptText))
        {
            using var cmd = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(token);
        }
    }

    public static async Task RunFileAsync(SqlConnection connection, string path, CancellationToken token)
    {
        string text = await File.ReadAllTextAsync(path, token);
        await RunScriptAsync(connection, text, token);
    }
}
