using Granite.Toolkit.Core.Security;

namespace GraniteInstallWizard.Core;

/// <summary>
/// C:\ProgramData\Granite Install Wizard, locked to administrators before
/// anything is written to or read back from it.
/// </summary>
/// <remarks>
/// ProgramData lets any user create and change files by default. This
/// folder holds release extractions that get deployed into IIS, and an
/// extraction is reused when its marker file matches the zip. Left open,
/// an ordinary user on the server could change an extracted app (or plant
/// a matching extraction in advance) and have it installed with
/// administrator rights. So the folder is made admin-only first, and if it
/// had been open, every earlier extraction is thrown away and redone.
/// </remarks>
public static class WizardDataFolder
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite Install Wizard");

    /// <summary>The release extraction folder, safe to extract into and reuse from.</summary>
    public static string PrepareExtractionRoot()
    {
        if (SecureFolders.EnsureAdminOnly(Root) == FolderSecureResult.Tightened && Directory.Exists(ReleaseSource.ExtractionRoot))
            Directory.Delete(ReleaseSource.ExtractionRoot, recursive: true);
        SecureFolders.EnsureAdminOnly(ReleaseSource.ExtractionRoot);
        return ReleaseSource.ExtractionRoot;
    }

    /// <summary>The install log folder, admin-only. Throws if it can't be secured.</summary>
    public static string PrepareLogFolder()
    {
        SecureFolders.EnsureAdminOnly(Root);
        SecureFolders.EnsureAdminOnly(InstallRunner.LogFolder);
        return InstallRunner.LogFolder;
    }
}
