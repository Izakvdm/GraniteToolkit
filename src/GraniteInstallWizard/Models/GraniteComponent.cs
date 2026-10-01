namespace GraniteInstallWizard.Models;

/// <summary>
/// One Granite application this wizard can install into IIS, and where it
/// lives in the V6.0 release folder.
/// </summary>
/// <param name="Key">Stable identifier used in the context and saved profiles.</param>
/// <param name="ReleaseFolder">Folder name inside the release, and inside the install folder.</param>
/// <param name="Title">Human name shown in the UI and log.</param>
/// <param name="DefaultSiteName">Default IIS site and app pool name.</param>
/// <param name="DefaultPort">Default HTTPS port.</param>
/// <param name="HotfixFolder">Folder under the release's Hotfix\ folder with replacement binaries, if any.</param>
/// <param name="VerifyPath">
/// Relative URL the post-install check requests. Web Desktop checks
/// appsettings.json specifically because the Vue app fetches it from its
/// own origin at startup (confirmed in js/app.*.js) -- if IIS can't serve
/// that file, Web Desktop loads as a blank page with no obvious error.
/// </param>
public sealed record GraniteComponent(
    string Key,
    string ReleaseFolder,
    string Title,
    string DefaultSiteName,
    int DefaultPort,
    string HotfixFolder,
    string VerifyPath)
{
    public const string WebDesktop = "WebDesktop";
    public const string BusinessApi = "BusinessAPI";
    public const string Custodian = "Custodian";
    public const string ProcessApp = "ProcessApp";

    /// <summary>
    /// The core stack, in install order. Ports: 40099 for Web Desktop is the
    /// standard across Granite installs (Izak, 2026-09-28; the release's own
    /// sample files don't agree on any set), 40080 for Process App (the
    /// scanner app) is what the first real install used, and 40081/40082
    /// are this wizard's defaults. Every port can be changed on Step 4.
    /// Names follow the release folders, so the app in GraniteProcessApp
    /// stays "Process App".
    /// </summary>
    public static IReadOnlyList<GraniteComponent> CoreStack { get; } = new[]
    {
        new GraniteComponent(WebDesktop,  "GraniteWebdesktop",  "Web Desktop",   "Granite WebDesktop",   40099, "WebDesktop",  "appsettings.json"),
        new GraniteComponent(BusinessApi, "GraniteBusinessAPI", "Business API",  "Granite Business API", 40081, "BusinessAPI", ""),
        new GraniteComponent(Custodian,   "GraniteCustodian",   "Custodian API", "Granite Custodian",    40082, "Custodian",   ""),
        new GraniteComponent(ProcessApp,  "GraniteProcessApp",  "Process App",   "Granite Process App",  40080, "ProcessApp",  "")
    };

    public static GraniteComponent Get(string key) => CoreStack.First(c => c.Key == key);
}
