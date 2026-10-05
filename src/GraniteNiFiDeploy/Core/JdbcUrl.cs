using System.Text.RegularExpressions;

namespace GraniteNiFiDeploy.Core;

/// <summary>
/// Turns the SQL Server name the operator typed (the SSMS form) into the
/// JDBC URL NiFi's connection pool uses. Pure, so the harness covers every
/// form: SERVER, SERVER\INSTANCE, SERVER,PORT, tcp:SERVER,PORT, ".", "(local)".
/// </summary>
public static class JdbcUrl
{
    public static string Build(string serverInstance, string database, bool validateCertificate)
    {
        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("Database name is blank.");
        if (database.IndexOfAny(new[] { ';', '{', '}', '=' }) >= 0)
            throw new ArgumentException("The database name has ; { } or = in it, which a JDBC URL can't carry.");

        var (host, instance, port) = Parse(serverInstance);
        string url = "jdbc:sqlserver://" + host + (port is null ? "" : ":" + port) + ";";
        if (instance is not null && port is null) url += "instanceName=" + instance + ";";
        url += "databaseName=" + database + ";encrypt=true;trustServerCertificate=" + (validateCertificate ? "false" : "true");
        return url;
    }

    /// <summary>Host, named instance and port from an SSMS-style server name.</summary>
    public static (string Host, string? Instance, int? Port) Parse(string serverInstance)
    {
        string s = (serverInstance ?? "").Trim();
        if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.Length == 0) throw new ArgumentException("SQL Server name is blank.");
        if (s.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NiFi connects over TCP. Named pipes, shared memory and LocalDB can't be used; use the server's name or address.");

        int? port = null;
        int comma = s.LastIndexOf(',');
        if (comma >= 0)
        {
            if (!int.TryParse(s[(comma + 1)..].Trim(), out int p) || p < 1 || p > 65535)
                throw new ArgumentException($"\"{s[(comma + 1)..]}\" isn't a port number.");
            port = p;
            s = s[..comma].Trim();
        }

        string? instance = null;
        int slash = s.IndexOf('\\');
        if (slash >= 0)
        {
            instance = s[(slash + 1)..].Trim();
            s = s[..slash].Trim();
            if (instance.Length == 0) instance = null;
            if (instance is not null && !Regex.IsMatch(instance, @"^[A-Za-z0-9_$#]{1,16}$"))
                throw new ArgumentException($"\"{instance}\" isn't a valid SQL Server instance name.");
        }

        string host = s is "." or "(local)" or "localhost" ? "localhost" : s;
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9._-]+$|^\[[0-9A-Fa-f:]+\]$"))
            throw new ArgumentException($"\"{host}\" isn't a server name NiFi can use.");
        return (host, instance, port);
    }
}
