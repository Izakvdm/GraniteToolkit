using System.Text.RegularExpressions;
using GraniteDbSwitcher.Models;

namespace GraniteDbSwitcher.Core;

public enum Compatibility
{
    Match,
    Mismatch,
    Unknown
}

/// <summary>
/// Works out a database's Granite version and whether it suits an install.
/// </summary>
/// <remarks>
/// Findings from the V6.0 and V7.0 release scripts (2026-09-30):
/// <list type="bullet">
/// <item>SystemSettings Granite/DatabaseVersion is written as '6.0.0.0' by
/// BOTH create scripts, and only when missing, so it can't tell V6 from V7.</item>
/// <item>dbo.Migration is the reliable marker: the V6.0 create script adds
/// SCHEMA_500 .. SCHEMA_600, V7.0 adds SCHEMA_700 on top.</item>
/// <item>V7.0 adds dbo.ProcessFunction / ProcessFunctionMapping, which V6.0
/// doesn't have: the fallback when Migration is missing.</item>
/// <item>Install side: Granite.Business.API.dll is FileVersion 6.0.0.0 in
/// V6.0 and 7.2.0.0 in V7.0.</item>
/// </list>
/// Only the major version is compared: a V7.2 Business API runs on a
/// SCHEMA_700 database.
/// </remarks>
public static class GraniteVersionRules
{
    private static readonly Regex SchemaName = new(@"^SCHEMA_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>SCHEMA_700 -> 700; null if the name isn't a schema migration.</summary>
    public static int? SchemaNumber(string migrationName)
    {
        var m = SchemaName.Match(migrationName.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }

    /// <summary>The highest SCHEMA_nnn among the Migration names, or null.</summary>
    public static string? HighestSchema(IEnumerable<string> migrationNames) =>
        migrationNames
            .Select(n => (Name: n, Number: SchemaNumber(n)))
            .Where(x => x.Number is not null)
            .OrderByDescending(x => x.Number)
            .Select(x => x.Name.Trim())
            .FirstOrDefault();

    public static GraniteDbVersion? Resolve(DatabaseInfo db)
    {
        if (!db.IsGranite) return null;

        if (db.SchemaMigration is not null && SchemaNumber(db.SchemaMigration) is int n)
        {
            // 700 -> 7.0, 510 -> 5.1, 600 -> 6.0
            return new GraniteDbVersion(n / 100, n % 100 / 10, db.SchemaMigration.ToUpperInvariant());
        }

        if (db.HasV7Objects)
            return new GraniteDbVersion(7, 0, "has V7 tables (no Migration rows)");

        if (db.DatabaseVersionSetting is not null && System.Version.TryParse(db.DatabaseVersionSetting, out var v))
            return new GraniteDbVersion(v.Major, v.Minor, "SystemSettings DatabaseVersion");

        return null;
    }

    public static Compatibility Check(GraniteInstall install, GraniteDbVersion? dbVersion)
    {
        if (install.AppVersion is null || dbVersion is null) return Compatibility.Unknown;
        return install.AppVersion.Major == dbVersion.Major ? Compatibility.Match : Compatibility.Mismatch;
    }
}
