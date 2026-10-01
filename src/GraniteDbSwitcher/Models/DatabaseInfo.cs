namespace GraniteDbSwitcher.Models;

/// <summary>One database on the SQL Server, as the switcher lists it.</summary>
public sealed class DatabaseInfo
{
    public required string Name { get; init; }

    /// <summary>Has dbo.SystemSettings: treated as a Granite database.</summary>
    public bool IsGranite { get; set; }

    /// <summary>Highest SCHEMA_nnn row in dbo.Migration, e.g. SCHEMA_700.</summary>
    public string? SchemaMigration { get; set; }

    /// <summary>SystemSettings Granite/DatabaseVersion. Not reliable on its own (V7 still writes 6.0.0.0).</summary>
    public string? DatabaseVersionSetting { get; set; }

    /// <summary>dbo.ProcessFunction exists (added in V7).</summary>
    public bool HasV7Objects { get; set; }

    public decimal? SizeMb { get; set; }
    public DateTime? LastRestored { get; set; }

    /// <summary>Why it couldn't be read, if it couldn't.</summary>
    public string? Problem { get; set; }

    /// <summary>The Granite version worked out from the above (see GraniteVersionRules).</summary>
    public GraniteDbVersion? Version { get; set; }
}

/// <summary>A database's Granite version and how it was worked out.</summary>
public sealed record GraniteDbVersion(int Major, int Minor, string Source)
{
    public string Label => $"{Major}.{Minor}";
}
