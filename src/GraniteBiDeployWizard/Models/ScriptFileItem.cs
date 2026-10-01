namespace GraniteBiDeployWizard.Models;

/// <summary>
/// One .sql file discovered in the selected script folder, and whether the
/// wizard will include it in the deployment run.
/// </summary>
public sealed class ScriptFileItem
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }

    /// <summary>Checked/unchecked state in the Panel 4 script list.</summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// True when the scanner detected SQLCMD scripting directives
    /// (:setvar, :r, :on error, ...) that this engine cannot execute
    /// natively -- e.g. an orchestration/master script. These are always
    /// listed but start unchecked and disabled.
    /// </summary>
    public bool UnsupportedDirectives { get; set; }

    /// <summary>
    /// Human-readable reason shown next to the file when it was
    /// auto-unchecked or disabled, so the user understands why without
    /// having to open the file.
    /// </summary>
    public string? Note { get; set; }
}
