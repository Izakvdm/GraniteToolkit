using System.Text.RegularExpressions;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Custodian's SSRSWebServiceUrl setting: the Report Server web service it
/// adds /ReportExecution2005.asmx and /ReportService2010.asmx to. Left empty,
/// Custodian logs two errors every time Web Desktop asks for its /config.
/// </summary>
/// <remarks>
/// Reporting Services on this server is found from its http.sys URL
/// reservation (netsh http show urlacl), which every SSRS and Power BI
/// Report Server version registers for its web service, whatever the
/// instance or port. Only this server is looked at: SSRS on another server
/// is left for the person installing to set. The value is only written when
/// the setting is empty, so a URL someone already set is never replaced.
/// </remarks>
public static class ReportServerSetting
{
    public const string Key = "SSRSWebServiceUrl";

    private static readonly Regex Reservation = new(@"(?<scheme>https?)://(?<host>[^:/\s]+):(?<port>\d+)/(?<path>ReportServer[^/\s]*)/?", RegexOptions.IgnoreCase);

    /// <summary>
    /// The web service URL from netsh's urlacl list, as Custodian wants it
    /// ("http://ULTRA/ReportServer", no trailing slash), or null when this
    /// server has no Report Server. HTTP is preferred: Custodian calls it from
    /// this server, and an HTTPS reservation's certificate may not name it.
    /// </summary>
    public static string? FromUrlAcl(string netshOutput, string machineName)
    {
        var found = Reservation.Matches(netshOutput)
            .Select(m => (Scheme: m.Groups["scheme"].Value.ToLowerInvariant(), Host: m.Groups["host"].Value, Port: int.Parse(m.Groups["port"].Value), Path: m.Groups["path"].Value))
            .OrderBy(r => r.Scheme == "http" ? 0 : 1)
            .ToList();
        if (found.Count == 0) return null;
        var r = found[0];
        string host = r.Host is "+" or "*" ? machineName : r.Host;
        bool defaultPort = r.Scheme == "http" ? r.Port == 80 : r.Port == 443;
        return $"{r.Scheme}://{host}{(defaultPort ? "" : ":" + r.Port)}/{r.Path}";
    }

    /// <summary>
    /// Inserts the setting when there's no row, or fills it when it's empty.
    /// Never replaces a URL already set. Returns (rows found, inserted, updated).
    /// </summary>
    public const string UpsertSql = """
        SET NOCOUNT ON;
        DECLARE @found int = (SELECT COUNT(*) FROM [dbo].[SystemSettings]
                              WHERE [Key] = N'SSRSWebServiceUrl' AND [Application] IN (N'GRANITECUSTODIAN', N'Granite.Custodian'));
        DECLARE @inserted int = 0, @updated int = 0;
        IF @found = 0
        BEGIN
            INSERT INTO [dbo].[SystemSettings] ([Application], [Key], [Value], [Description], [ValueDataType], [isActive], [isEncrypted], [EncryptionKey], [AuditDate], [AuditUser], [Version])
            VALUES (N'Granite.Custodian', N'SSRSWebServiceUrl', @url, N'Report Server web service URL', 'string', 1, 0, NULL, GETDATE(), @user, 1);
            SET @inserted = @@ROWCOUNT;
        END
        ELSE
        BEGIN
            UPDATE [dbo].[SystemSettings]
            SET [Value] = @url, [AuditDate] = GETDATE(), [AuditUser] = @user
            WHERE [Key] = N'SSRSWebServiceUrl' AND [Application] IN (N'GRANITECUSTODIAN', N'Granite.Custodian')
              AND ISNULL(CAST([Value] AS nvarchar(max)), N'') = N'';
            SET @updated = @@ROWCOUNT;
        END
        SELECT @found, @inserted, @updated;
        """;

    public static string DescribeResult(string url, int found, int inserted, int updated) =>
        inserted > 0 ? $"Custodian {Key} set to {url}."
        : updated > 0 ? $"Custodian {Key} set to {url} (it was empty)."
        : found > 0 ? $"Custodian {Key} already set; left as it is."
        : $"Custodian {Key}: nothing changed.";

    public const string NotFoundText =
        "No Reporting Services web service found on this server, so Custodian's SSRSWebServiceUrl is left as it is. If reports run on another server, set it to that server's Report Server URL (for example http://SERVER/ReportServer).";
}
