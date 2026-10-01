using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Enumerates the .sql script array in a folder for Panel 4's checklist.
/// </summary>
public static class ScriptFileScanner
{
    /// <summary>
    /// Names that, historically, are orchestration or manual-remediation
    /// scripts rather than part of the automated deploy order (the master
    /// $(ScriptDir) runner already excludes them from its own :r list, and
    /// the SQL Agent scheduler script is for a manual, non-wizard
    /// deployment -- choosing "SQL Server Agent" on Panel 5 registers the
    /// job itself, at the interval actually chosen there, without needing
    /// this file). They are still listed and can be ticked back on -- this
    /// only sets the wizard's starting suggestion.
    /// </summary>
    private static readonly string[] DefaultUncheckedHints =
    {
        "deploy_all", "fix_collation", "schedulersqlagent"
    };

    public static List<ScriptFileItem> Scan(string folder)
    {
        var items = new List<ScriptFileItem>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return items;

        var files = Directory.GetFiles(folder, "*.sql", SearchOption.TopDirectoryOnly)
                              .OrderBy(Path.GetFileName, new NaturalFileNameComparer())
                              .ToList();

        foreach (var path in files)
        {
            string name = Path.GetFileName(path);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                items.Add(new ScriptFileItem
                {
                    FullPath = path,
                    FileName = name,
                    Included = false,
                    UnsupportedDirectives = true,
                    Note = $"Could not read file: {ex.Message}"
                });
                continue;
            }

            bool unsupported = ScriptBatchParser.ContainsUnsupportedDirectives(text);
            string lowerName = name.ToLowerInvariant();

            if (unsupported)
            {
                items.Add(new ScriptFileItem
                {
                    FullPath = path,
                    FileName = name,
                    Included = false,
                    UnsupportedDirectives = true,
                    Note = "Orchestration script (:setvar / :r) -- only sqlcmd.exe can run this; not supported natively."
                });
                continue;
            }

            string normalizedName = lowerName.Replace("_", "");
            bool defaultUnchecked = DefaultUncheckedHints.Any(h => normalizedName.Contains(h.Replace("_", "")));
            items.Add(new ScriptFileItem
            {
                FullPath = path,
                FileName = name,
                Included = !defaultUnchecked,
                UnsupportedDirectives = false,
                Note = defaultUnchecked
                    ? "Not part of the standard deploy order -- tick to include if you need it."
                    : null
            });
        }

        return items;
    }
}
