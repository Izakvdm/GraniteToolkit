using System.Text;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Writes the connection strings that go into each app's appsettings.json.
/// </summary>
/// <remarks>
/// Built by hand rather than with Microsoft.Data.SqlClient's
/// SqlConnectionStringBuilder on purpose. That builder (5.x) writes the
/// modern spaced keywords -- "Trust Server Certificate=True",
/// "Application Name=..." -- and the Business API folder ships
/// System.Data.SqlClient.dll and ServiceStack.OrmLite.SqlServer alongside
/// Microsoft.Data.SqlClient. System.Data.SqlClient only knows
/// "TrustServerCertificate" and throws "Keyword not supported" on the
/// spaced form, so whichever of the app's data layers uses it would fail
/// at startup. The keywords here are the classic ones the release's own
/// appsettings files use, which every SqlClient version accepts.
/// <para/>
/// Values containing ';', quotes, or leading/trailing spaces are quoted
/// the way both SqlClients parse them, so a password like p@ss;w"rd
/// round-trips intact.
/// </remarks>
public static class ConnectionStringFormatter
{
    public static string ForApp(string server, string database, string user, string password, IEnumerable<(string Key, string Value)>? extra = null)
    {
        var parts = new List<(string, string)>
        {
            ("Data Source", server),
            ("Initial Catalog", database),
            ("Persist Security Info", "True"),
            ("User ID", user),
            ("Password", password),
            ("TrustServerCertificate", "True")
        };
        if (extra is not null) parts.AddRange(extra);
        return Join(parts);
    }

    public static string Join(IEnumerable<(string Key, string Value)> parts)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parts)
        {
            sb.Append(key).Append('=').Append(Quote(value)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>ADO.NET connection-string value quoting.</summary>
    public static string Quote(string value)
    {
        bool needsQuotes = value.Contains(';') || value.Contains('\'') || value.Contains('"')
                           || value.Contains('=') || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        if (!needsQuotes) return value;
        if (!value.Contains('"')) return "\"" + value + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
