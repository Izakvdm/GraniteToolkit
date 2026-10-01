namespace GraniteBiDeployWizard.Core;

public static class PathHelper
{
    /// <summary>
    /// Programmatic path normalization: ensure the selected script folder
    /// string always ends in exactly one trailing backslash, the way the
    /// deployment scripts' own $(ScriptDir) convention expects.
    /// </summary>
    public static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return folder;

        string trimmed = folder.Trim();
        // Collapse any existing trailing slashes (either style) first, then
        // append exactly one backslash, so "C:\Foo", "C:\Foo\" and
        // "C:\Foo/" all normalize to the same "C:\Foo\".
        trimmed = trimmed.TrimEnd('\\', '/');
        return trimmed + "\\";
    }
}
