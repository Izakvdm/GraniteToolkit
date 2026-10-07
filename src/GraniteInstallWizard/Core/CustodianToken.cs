using System.Text.RegularExpressions;

namespace GraniteInstallWizard.Core;

/// <summary>The three values Custodian.md sets on the Token row.</summary>
/// <param name="Token">The encrypted token (base64).</param>
/// <param name="EncryptionKey">The key it was encrypted with (base64).</param>
/// <param name="Version">The settings version the file records.</param>
public sealed record CustodianTokenValues(string Token, string EncryptionKey, int Version);

/// <summary>
/// Custodian's licence token: the SystemSettings row with Key 'Token' under
/// Application 'Granite.Custodian' (older databases: 'GRANITECUSTODIAN').
/// Without it Custodian starts but can't authenticate its calls.
/// </summary>
/// <remarks>
/// V6.0 ships the token as Hotfix\Custodian.md, which only reached the
/// database when the Hotfix database scripts were ticked. V7.0 doesn't ship
/// it at all. So the wizard sets the token as its own step whenever
/// Custodian is installed, from a Custodian.md the person installing picks
/// on Step 3, or else the release's Hotfix\Custodian.md.
///
/// The wizard deliberately carries no copy of its own. The token opens a
/// shared GitHub repository: compiled into a signed exe it would ship to
/// every server, outlive its revocation, and go stale (the V6.0
/// release's Version 6 token is already refused with "Bad credentials"). Without a file the
/// install still completes and verification says Custodian has no working
/// token.
///
/// The file's SQL is not run as-is. It's a plain UPDATE, which silently
/// does nothing when the row doesn't exist yet. Instead the three values are
/// read out of it, checked, and written by <see cref="UpsertSql"/> with
/// parameters, so nothing from the file is ever executed as SQL.
/// </remarks>
public static class CustodianToken
{
    /// <summary>
    /// One parameterised batch. Inserts the row when neither application name
    /// has one; otherwise updates it, either always (@overwrite = 1, a new
    /// database) or only where the value is empty (an existing database keeps
    /// the token it already has). Updating both spellings matters: Custodian
    /// renames GRANITECUSTODIAN rows to Granite.Custodian on start-up, so a
    /// second inserted row would become a duplicate. Returns
    /// (rows found, rows inserted, rows updated).
    /// The column list matches Custodian's own SystemSettings inserts.
    /// </summary>
    public const string UpsertSql = """
        SET NOCOUNT ON;
        DECLARE @found int = (SELECT COUNT(*) FROM [dbo].[SystemSettings]
                              WHERE [Key] = N'Token' AND [Application] IN (N'GRANITECUSTODIAN', N'Granite.Custodian'));
        DECLARE @inserted int = 0, @updated int = 0;
        IF @found = 0
        BEGIN
            INSERT INTO [dbo].[SystemSettings] ([Application], [Key], [Value], [Description], [ValueDataType], [isActive], [isEncrypted], [EncryptionKey], [AuditDate], [AuditUser], [Version])
            VALUES (N'Granite.Custodian', N'Token', @token, N'Custodian API token', 'string', 1, 1, @key, GETDATE(), @user, @version);
            SET @inserted = @@ROWCOUNT;
        END
        ELSE
        BEGIN
            UPDATE [dbo].[SystemSettings]
            SET [Value] = @token, [ValueDataType] = 'string', [isEncrypted] = 1, [isActive] = 1,
                [EncryptionKey] = @key, [AuditDate] = GETDATE(), [AuditUser] = @user, [Version] = @version
            WHERE [Key] = N'Token' AND [Application] IN (N'GRANITECUSTODIAN', N'Granite.Custodian')
              AND (@overwrite = 1 OR ISNULL(CAST([Value] AS nvarchar(max)), N'') = N'');
            SET @updated = @@ROWCOUNT;
        END
        SELECT @found, @inserted, @updated;
        """;

    /// <summary>The AuditUser written on the row, so it's clear later where the token came from.</summary>
    public const string AuditUser = "INSTALLWIZARD";

    private static readonly Regex TokenPattern = new(@"\bValue\s*=\s*'(?<v>[^']*)'", RegexOptions.IgnoreCase);
    private static readonly Regex KeyPattern = new(@"\bEncryptionKey\s*=\s*'(?<v>[^']*)'", RegexOptions.IgnoreCase);
    private static readonly Regex VersionPattern = new(@"\bVersion\s*=\s*'?(?<v>\d+)'?", RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads the token values out of a Custodian.md. Throws
    /// <see cref="InvalidDataException"/> with a readable reason when the file
    /// doesn't look like one, so a bad file stops the install before the
    /// database is touched.
    /// </summary>
    public static CustodianTokenValues Parse(string markdown, string sourceLabel)
    {
        var blocks = GraniteSqlScriptParser.ExtractMarkdownSqlBlocks(markdown);
        string? sql = blocks.FirstOrDefault(b =>
            b.Contains("SystemSettings", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(b, @"'Token'", RegexOptions.IgnoreCase));
        if (sql is null)
            throw new InvalidDataException($"{sourceLabel} has no SQL block that sets the Custodian Token setting.");

        string token = TokenPattern.Match(sql) is { Success: true } t ? t.Groups["v"].Value.Trim() : "";
        string key = KeyPattern.Match(sql) is { Success: true } k ? k.Groups["v"].Value.Trim() : "";
        int version = VersionPattern.Match(sql) is { Success: true } v && int.TryParse(v.Groups["v"].Value, out int n) ? n : 0;

        if (!IsBase64(token, minBytes: 32))
            throw new InvalidDataException($"{sourceLabel}: the token value is missing or isn't base64.");
        if (!IsBase64(key, minBytes: 16))
            throw new InvalidDataException($"{sourceLabel}: the EncryptionKey is missing or isn't base64.");
        if (version <= 0)
            throw new InvalidDataException($"{sourceLabel}: no Version number found.");

        return new CustodianTokenValues(token, key, version);
    }

    /// <summary>
    /// Picks the source: the file chosen on Step 3 if there is one, otherwise
    /// the release's Hotfix\Custodian.md, otherwise none.
    /// </summary>
    public static (string Label, string Path)? ChooseSource(string? chosenPath, string? releaseCustodianMdPath)
    {
        if (!string.IsNullOrWhiteSpace(chosenPath))
            return File.Exists(chosenPath) ? ($"{System.IO.Path.GetFileName(chosenPath)} (chosen on Step 3)", chosenPath)
                : throw new FileNotFoundException($"The Custodian.md chosen on Step 3 is no longer at {chosenPath}.");
        if (releaseCustodianMdPath is not null && File.Exists(releaseCustodianMdPath))
            return ($"Hotfix\\{System.IO.Path.GetFileName(releaseCustodianMdPath)} (from the release)", releaseCustodianMdPath);
        return null;
    }

    /// <summary>What Step 3, the review and the log say when there's no token file.</summary>
    public const string NoSourceText =
        "No Custodian.md: an existing token in the database is kept; otherwise Custodian installs without one and can't open the process repository until a current Custodian.md from Granite is run.";

    /// <summary>The log line for what <see cref="UpsertSql"/> did.</summary>
    public static string DescribeResult(int found, int inserted, int updated, bool overwrite) =>
        inserted > 0 ? "Custodian token added (there was no Token setting yet)."
        : updated > 0 ? $"Custodian token set ({updated} Token setting{(updated == 1 ? "" : "s")} updated)."
        : found > 0 && !overwrite ? "Custodian token already set in this existing database; left as it is."
        : "Custodian token: nothing changed.";

    private static bool IsBase64(string s, int minBytes)
    {
        if (s.Length == 0) return false;
        var buffer = new byte[s.Length];
        return Convert.TryFromBase64String(s, buffer, out int written) && written >= minBytes;
    }
}
