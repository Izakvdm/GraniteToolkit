using Granite.Toolkit.Core.Security;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// C:\ProgramData\Granite BI Deploy\Tasks\&lt;BI database&gt;: where
/// Run_BI_Sync.bat lives, admin-only.
/// </summary>
/// <remarks>
/// Up to v1.6.0 the wrapper was written into the script folder, which can be
/// anywhere (a Desktop, a share). The scheduled task runs it with highest
/// privileges under a stored Windows account, so anyone able to edit it, or
/// to put a sqlcmd.exe in its working folder, could run code as that account.
/// </remarks>
public static class TaskFolder
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Granite BI Deploy", "Tasks");

    /// <summary>Creates (or locks down) the task folder for this BI database and returns it.</summary>
    public static string Prepare(DeploymentContext context)
    {
        string folder = Path.Combine(Root, SafeName(context.BiDb));
        SecureFolders.EnsureAdminOnly(Path.GetDirectoryName(Root)!);
        SecureFolders.EnsureAdminOnly(Root);
        if (SecureFolders.EnsureAdminOnly(folder) == FolderSecureResult.Tightened)
        {
            // Whatever was there could have been changed by someone else; start clean.
            foreach (var file in new DirectoryInfo(folder).GetFiles()) file.Delete();
        }
        return folder;
    }

    /// <summary>A database name as a folder name: anything Windows can't use in a file name becomes _.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 || safe is "." or ".." ? "_" : safe;
    }
}
