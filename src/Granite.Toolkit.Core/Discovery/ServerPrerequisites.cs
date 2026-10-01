using Microsoft.Win32;

namespace Granite.Toolkit.Core.Discovery;

/// <summary>
/// Quick, read-only checks for what the Granite core stack needs on a
/// server. Moved here from the Install Wizard's PrerequisiteService so the
/// launcher shows the same answers. The full IIS feature list (dism) is
/// slow, so it stays in the Install Wizard's Step 2.
/// </summary>
public static class ServerPrerequisites
{
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>The ASP.NET Core Module (V2), which the Hosting Bundle registers into IIS.</summary>
    public static bool AncmInstalled =>
        File.Exists(Path.Combine(ProgramFiles, "IIS", "Asp.Net Core Module", "V2", "aspnetcorev2.dll"));

    public static bool UrlRewriteInstalled =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "rewrite.dll"));

    /// <summary>Highest installed Microsoft.AspNetCore.App runtime for a major version, or null.</summary>
    public static string? AspNetCoreRuntime(int major)
    {
        string shared = Path.Combine(ProgramFiles, "dotnet", "shared", "Microsoft.AspNetCore.App");
        if (!Directory.Exists(shared)) return null;
        return HighestVersion(Directory.GetDirectories(shared).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!), major);
    }

    /// <summary>Of folder names like "8.0.11" and "8.0.30-rc.1", the highest with the given major version. Pure, for the harness.</summary>
    public static string? HighestVersion(IEnumerable<string> names, int major) =>
        names
            .Where(n => n.StartsWith(major + ".", StringComparison.Ordinal))
            .OrderByDescending(n => Version.TryParse(n.Split('-')[0], out var v) ? v : new Version(0, 0))
            .FirstOrDefault();

    /// <summary>IIS version from HKLM\SOFTWARE\Microsoft\InetStp, e.g. "10.0", or null when IIS isn't installed.</summary>
    public static string? IisVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
            if (key?.GetValue("MajorVersion") is int major)
                return key.GetValue("MinorVersion") is int minor ? $"{major}.{minor}" : major.ToString();
        }
        catch { /* treat as unknown */ }
        return null;
    }
}
