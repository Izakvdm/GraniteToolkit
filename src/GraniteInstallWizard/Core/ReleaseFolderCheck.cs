using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Checks a folder really is a Granite release before anything relies on it.
/// Folder names are matched loosely (see ReleaseLayout), so V6.0 and V7.0
/// spellings both pass.
/// </summary>
public static class ReleaseFolderCheck
{
    public static List<string> Problems(string releaseFolder)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(releaseFolder) || !Directory.Exists(releaseFolder))
            return new List<string> { "The release folder doesn't exist." };
        foreach (var comp in GraniteComponent.CoreStack)
            if (!ReleaseLayout.DirectoryExists(releaseFolder, comp.ReleaseFolder)) missing.Add(comp.ReleaseFolder);
        if (!ReleaseLayout.FileExists(releaseFolder, "GraniteDatabase", "GraniteDatabase", "GraniteDatabase_Create.sql"))
            missing.Add(@"GraniteDatabase\GraniteDatabase\GraniteDatabase_Create.sql");
        if (!ReleaseLayout.DirectoryExists(releaseFolder, "GraniteScaffold", "Prerequisites"))
            missing.Add(@"GraniteScaffold\Prerequisites");
        return missing.Count == 0 ? missing : new List<string> { "The release folder is missing: " + string.Join(", ", missing) };
    }
}
