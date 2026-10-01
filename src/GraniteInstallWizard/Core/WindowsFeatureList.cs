namespace GraniteInstallWizard.Core;

/// <summary>
/// The Windows optional features the core stack needs, and parsing of
/// DISM's feature table.
/// </summary>
/// <remarks>
/// This is Scaffold's own list (GraniteScaffold\Prerequisites\IIS\
/// install-iis.ps1) plus seven it leaves out:
/// IIS-StaticContent and IIS-DefaultDocument (Web Desktop is a static
/// site; without them IIS cannot serve index.html and the .js files),
/// IIS-Security and IIS-RequestFiltering, IIS-HttpCompressionStatic, and
/// IIS-WebServerManagementTools with IIS-ManagementScriptingTools (the IIS
/// PowerShell module and WMI provider). The wizard itself only needs
/// appcmd.exe, which ships with the core web server; the management tools
/// are there for whoever supports the server afterwards.
/// <para/>
/// DISM is used (not Get-WindowsOptionalFeature or WMI) because it's
/// present on every Windows version the wizard targets, needs no extra
/// package reference, and returns a stable table with /English.
/// </remarks>
public static class WindowsFeatureList
{
    public static IReadOnlyList<string> Required { get; } = new[]
    {
        "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-StaticContent",
        "IIS-DefaultDocument", "IIS-HttpErrors", "IIS-HttpRedirect", "IIS-HttpCompressionStatic",
        "IIS-Security", "IIS-RequestFiltering", "IIS-ApplicationDevelopment", "NetFx4Extended-ASPNET45",
        "IIS-NetFxExtensibility45", "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-ASPNET45",
        "IIS-ApplicationInit", "IIS-WebServerManagementTools", "IIS-ManagementConsole",
        "IIS-ManagementScriptingTools"
    };

    public static string[] GetFeaturesArgs() => new[] { "/online", "/get-features", "/format:table", "/english" };

    /// <summary>One DISM call for all missing features; /all pulls in parent features.</summary>
    public static string[] EnableFeaturesArgs(IEnumerable<string> features)
    {
        var args = new List<string> { "/online", "/enable-feature" };
        foreach (string f in features) args.Add($"/featurename:{f}");
        args.AddRange(new[] { "/all", "/norestart", "/quiet" });
        return args.ToArray();
    }

    /// <summary>
    /// Parses "dism /get-features /format:table" output into feature name
    /// and state. "Enable Pending" counts as enabled: the feature is
    /// installed and only waiting on a restart.
    /// </summary>
    public static Dictionary<string, bool> ParseFeatureTable(string output)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            string name = line[..bar].Trim();
            string state = line[(bar + 1)..].Trim();
            if (name.Length == 0 || name.StartsWith("Feature Name", StringComparison.OrdinalIgnoreCase)) continue;
            result[name] = state.StartsWith("Enable", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, bool> table) =>
        Required.Where(f => !table.TryGetValue(f, out bool on) || !on).ToList();
}
