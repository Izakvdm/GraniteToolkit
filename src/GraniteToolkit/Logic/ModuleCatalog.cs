namespace GraniteToolkit.Logic;

public enum ModuleId { Install, Bi, NiFi, DbSwitcher }

/// <summary>One toolkit module: an exe that ships next to the launcher.</summary>
/// <param name="ExeName">File name only. The launcher only ever starts it from its own folder.</param>
/// <param name="DeveloperOnly">Not installed on client servers by default (MSI feature off).</param>
public sealed record ModuleInfo(ModuleId Id, string Title, string Description, string ExeName, bool DeveloperOnly);

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleInfo> All = new[]
    {
        new ModuleInfo(ModuleId.Install, "Install GraniteWMS",
            "The core stack on this server: Web Desktop, Business API, Custodian and Process App, with the database, IIS sites, certificate and firewall rules.",
            "GraniteInstallWizard.exe", DeveloperOnly: false),
        new ModuleInfo(ModuleId.Bi, "Deploy BI reporting",
            "The BI database, sync engine, reporting views and the sync schedule (SQL Agent, Granite Scheduler or Windows Task Scheduler).",
            "GraniteBiDeployWizard.exe", DeveloperOnly: false),
        new ModuleInfo(ModuleId.NiFi, "Deploy NiFi integration",
            "Apache NiFi as a Windows service with the Granite CSV import: a client system drops a CSV in a folder and it lands in Granite, validated and audited.",
            "GraniteNiFiDeploy.exe", DeveloperOnly: false),
        new ModuleInfo(ModuleId.DbSwitcher, "Switch database",
            "Developer tool: points a local install at another client's database for testing. Not for client servers.",
            "GraniteDbSwitcher.exe", DeveloperOnly: true)
    };

    /// <summary>
    /// The full path the launcher may start a module from: its own folder and
    /// the catalog's file name, nothing else. Never a PATH search, never a
    /// name with folder parts in it.
    /// </summary>
    public static string ExePath(string appFolder, ModuleInfo module)
    {
        if (module.ExeName != Path.GetFileName(module.ExeName) || module.ExeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Module file name \"{module.ExeName}\" must be a bare file name.");
        return Path.Combine(appFolder, module.ExeName);
    }
}
