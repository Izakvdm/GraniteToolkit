using System.Text;

namespace GraniteDbSwitcher.Core;

/// <summary>
/// Changes one value in an ADO.NET connection string and leaves every other
/// part exactly as it was written: key spelling, order, quoting, pool
/// settings, the classic "TrustServerCertificate" keyword.
/// </summary>
/// <remarks>
/// Not SqlConnectionStringBuilder, for the same reason the install wizard's
/// ConnectionStringFormatter avoids it: that builder rewrites every keyword
/// in the modern spaced form ("Trust Server Certificate=True"), which the
/// System.Data.SqlClient copy shipped in the Business API folder rejects.
/// The releases also use different synonyms per app (V7's Custodian ships
/// Server=/Database=, the others Data Source=/Initial Catalog=), so the
/// switcher changes whichever synonym the file already uses.
/// </remarks>
public sealed class ConnectionStringEditor
{
    public static readonly string[] DatabaseKeys = { "Initial Catalog", "Database" };
    public static readonly string[] ServerKeys = { "Data Source", "Server", "Address", "Addr", "Network Address" };
    public static readonly string[] UserKeys = { "User ID", "User Id", "UID", "User" };
    public static readonly string[] PasswordKeys = { "Password", "PWD" };
    public static readonly string[] IntegratedKeys = { "Integrated Security", "Trusted_Connection" };

    private sealed class Part
    {
        public required string RawKey { get; init; }      // as written, trimmed
        public required string RawSegment { get; init; }  // "key=value" exactly as written
        public required string Value { get; set; }        // unquoted
        public bool Changed { get; set; }
    }

    private readonly List<Part> _parts = new();
    private readonly bool _trailingSemicolon;

    private ConnectionStringEditor(bool trailingSemicolon) => _trailingSemicolon = trailingSemicolon;

    public static ConnectionStringEditor Parse(string connectionString)
    {
        var editor = new ConnectionStringEditor(connectionString.TrimEnd().EndsWith(';'));
        int i = 0;
        string s = connectionString;
        while (i < s.Length)
        {
            // Skip separators and whitespace between pairs.
            while (i < s.Length && (s[i] == ';' || char.IsWhiteSpace(s[i]))) i++;
            if (i >= s.Length) break;

            int segmentStart = i;

            // Key: up to the first single '=' ("==" is a literal '=').
            var key = new StringBuilder();
            while (i < s.Length)
            {
                if (s[i] == '=')
                {
                    if (i + 1 < s.Length && s[i + 1] == '=') { key.Append('='); i += 2; continue; }
                    break;
                }
                key.Append(s[i]);
                i++;
            }
            if (i >= s.Length)
                throw new FormatException($"Connection string part \"{s[segmentStart..].Trim()}\" has no '='.");
            i++; // past '='

            while (i < s.Length && char.IsWhiteSpace(s[i]) && s[i] != ';') i++;

            string value;
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                char q = s[i++];
                var v = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new FormatException("Connection string has an unclosed quote.");
                    if (s[i] == q)
                    {
                        if (i + 1 < s.Length && s[i + 1] == q) { v.Append(q); i += 2; continue; }
                        i++;
                        break;
                    }
                    v.Append(s[i++]);
                }
                value = v.ToString();
                while (i < s.Length && s[i] != ';') i++; // trailing spaces after the quote
            }
            else
            {
                int start = i;
                while (i < s.Length && s[i] != ';') i++;
                value = s[start..i].Trim();
            }

            string rawKey = key.ToString().Trim();
            if (rawKey.Length == 0) throw new FormatException("Connection string has an empty key.");
            editor._parts.Add(new Part
            {
                RawKey = rawKey,
                RawSegment = s[segmentStart..i].Trim(),
                Value = value
            });
        }
        return editor;
    }

    private Part? Find(IEnumerable<string> synonyms) =>
        _parts.LastOrDefault(p => synonyms.Any(k => string.Equals(Normalise(p.RawKey), Normalise(k), StringComparison.OrdinalIgnoreCase)));

    private static string Normalise(string key) => string.Join(' ', key.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string? Get(IEnumerable<string> synonyms) => Find(synonyms)?.Value;

    public string? Database => Get(DatabaseKeys);
    public string? Server => Get(ServerKeys);
    public string? User => Get(UserKeys);
    public string? Password => Get(PasswordKeys);

    public bool IntegratedSecurity
    {
        get
        {
            string? v = Get(IntegratedKeys);
            return v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                     || v.Equals("sspi", StringComparison.OrdinalIgnoreCase)
                                     || v.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Sets the value under whichever synonym is already present, or appends <paramref name="keyIfMissing"/>.</summary>
    public void Set(IEnumerable<string> synonyms, string value, string keyIfMissing)
    {
        var part = Find(synonyms);
        if (part is null)
        {
            _parts.Add(new Part { RawKey = keyIfMissing, RawSegment = string.Empty, Value = value, Changed = true });
            return;
        }
        if (part.Value == value) return;
        part.Value = value;
        part.Changed = true;
    }

    public void SetDatabase(string database) => Set(DatabaseKeys, database, "Initial Catalog");

    /// <summary>The same connection string with only the database changed.</summary>
    public static string WithDatabase(string connectionString, string database)
    {
        var e = Parse(connectionString);
        e.SetDatabase(database);
        return e.ToString();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int n = 0; n < _parts.Count; n++)
        {
            var p = _parts[n];
            if (n > 0) sb.Append(';');
            sb.Append(p.Changed ? p.RawKey.Replace("=", "==") + "=" + Quote(p.Value) : p.RawSegment);
        }
        if (_trailingSemicolon || _parts.Any(p => p.Changed && p.RawSegment.Length == 0)) sb.Append(';');
        return sb.ToString();
    }

    /// <summary>ADO.NET value quoting, same rules as the install wizard's ConnectionStringFormatter.</summary>
    public static string Quote(string value)
    {
        bool needsQuotes = value.Contains(';') || value.Contains('\'') || value.Contains('"')
                           || value.Contains('=') || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        if (!needsQuotes) return value;
        if (!value.Contains('"')) return "\"" + value + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>The same string with the password hidden, for the log.</summary>
    public static string Mask(string connectionString)
    {
        try
        {
            var e = Parse(connectionString);
            if (e.Password is not null) e.Set(PasswordKeys, "*****", "Password");
            return e.ToString();
        }
        catch (FormatException)
        {
            return "(unreadable connection string)";
        }
    }
}
