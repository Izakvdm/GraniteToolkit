using Granite.Toolkit.Core.Security;
using GraniteToolkit.Logic;

int passed = 0, failed = 0;
void Check(bool ok, string what)
{
    if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + what); }
}

// ---- SignaturePolicy: every combination -------------------------------------
const string Pub = "CN=Example Ltd, O=Example Ltd, L=Somewhere, C=US";
var valid = new SignatureInfo(SignatureState.Valid, Pub, "Valid");
var validSpaces = new SignatureInfo(SignatureState.Valid, "CN=Example Ltd,O=Example Ltd,L=Somewhere,C=US", "Valid");
var otherPub = new SignatureInfo(SignatureState.Valid, "CN=Someone Else, O=Someone Else", "Valid");
var unsigned = SignatureInfo.Unsigned();
var broken = new SignatureInfo(SignatureState.Invalid, Pub, "the file was changed after it was signed");

LaunchDecision D(SignatureInfo l, SignatureInfo m) => SignaturePolicy.Decide(l, m).Decision;

Check(D(valid, valid) == LaunchDecision.Allow, "signed launcher + same publisher module: allowed");
Check(D(valid, validSpaces) == LaunchDecision.Allow, "publisher compared ignoring spaces after commas");
Check(D(valid, otherPub) == LaunchDecision.Block, "signed launcher + other publisher: blocked");
Check(D(valid, unsigned) == LaunchDecision.Block, "signed launcher + unsigned module: blocked (swapped file)");
Check(D(valid, broken) == LaunchDecision.Block, "signed launcher + tampered module: blocked");
Check(D(unsigned, unsigned) == LaunchDecision.AllowDevelopmentBuild, "dev build + unsigned module: allowed with warning");
Check(D(unsigned, valid) == LaunchDecision.AllowDevelopmentBuild, "dev build + signed module: allowed with warning");
Check(D(unsigned, broken) == LaunchDecision.Block, "dev build + tampered module: still blocked");
Check(D(broken, valid) == LaunchDecision.Block, "tampered launcher: blocks everything");
Check(D(broken, unsigned) == LaunchDecision.Block, "tampered launcher + unsigned: blocked");
Check(!SignaturePolicy.SamePublisher(null, Pub) && !SignaturePolicy.SamePublisher("", ""), "empty publisher never matches");
Check(!SignaturePolicy.SamePublisher("CN=Example", "CN=example"), "publisher match is case-sensitive (exact certificate name)");
Check(SignaturePolicy.Decide(valid, otherPub).Reason.Contains("Someone Else"), "block reason names the other publisher");

// ---- FolderAclPolicy ---------------------------------------------------------
const int Read = 0x1200A9, Modify = 0x1301BF, Full = 0x1F01FF, CreateFiles = 0x2, CreateFolders = 0x4;
Check(FolderAclPolicy.IsTrustedOwner(FolderAclPolicy.Administrators), "Administrators trusted owner");
Check(FolderAclPolicy.IsTrustedOwner(FolderAclPolicy.LocalSystem), "SYSTEM trusted owner");
Check(FolderAclPolicy.IsTrustedOwner(FolderAclPolicy.TrustedInstaller), "TrustedInstaller trusted owner");
Check(!FolderAclPolicy.IsTrustedOwner("S-1-5-21-1-2-3-1001"), "a user account isn't a trusted owner");
Check(!FolderAclPolicy.IsTrustedOwner(null), "unknown owner isn't trusted");

// Program Files default: Users read only.
var programFiles = new[]
{
    new AclRule("S-1-5-32-544", Full, true, false),
    new AclRule("S-1-5-18", Full, true, false),
    new AclRule("S-1-5-32-545", Read, true, false),
    new AclRule("S-1-15-2-1", Read, true, false) // ALL APPLICATION PACKAGES
};
Check(FolderAclPolicy.UntrustedWriters(programFiles).Count == 0, "Program Files ACL: nobody untrusted can write");

// ProgramData default: Users may create files and folders (inherit to subfolders).
var programData = new[]
{
    new AclRule("S-1-5-32-544", Full, true, false),
    new AclRule("S-1-5-32-545", Read, true, false),
    new AclRule("S-1-5-32-545", CreateFiles | CreateFolders, true, false)
};
Check(FolderAclPolicy.UntrustedWriters(programData).SequenceEqual(new[] { "Users" }), "ProgramData ACL: Users can write");

// A Downloads-style folder: Authenticated Users modify.
var shared = new[] { new AclRule("S-1-5-11", Modify, true, true), new AclRule("S-1-1-0", Read, true, false) };
Check(FolderAclPolicy.UntrustedWriters(shared).SequenceEqual(new[] { "Authenticated Users" }), "inherit-only write still counts");
Check(FolderAclPolicy.UntrustedWriters(new[] { new AclRule("S-1-1-0", Full, false, false) }).Count == 0, "deny rules aren't writers");
Check(FolderAclPolicy.UntrustedWriters(new[] { new AclRule("S-1-5-21-1-2-3-1001", Full, true, false) }).Count == 0, "a named account isn't a broad group");
Check(FolderAclPolicy.UntrustedWriters(new[] { new AclRule("S-1-1-0", 0x40000000, true, false) }).Count == 1, "GENERIC_WRITE counts");

// ---- ModuleCatalog ------------------------------------------------------------
Check(ModuleCatalog.All.Count(m => m.DeveloperOnly) == 1 && ModuleCatalog.All.Single(m => m.DeveloperOnly).Id == ModuleId.DbSwitcher, "only DB Switcher is developer-only");
Check(ModuleCatalog.All.All(m => m.ExeName == Path.GetFileName(m.ExeName) && m.ExeName.EndsWith(".exe")), "module names are bare exe names");
Check(ModuleCatalog.All.Select(m => m.ExeName).Distinct().Count() == ModuleCatalog.All.Count, "module exe names unique");
bool threw = false;
try { ModuleCatalog.ExePath("/app", new ModuleInfo(ModuleId.Install, "x", "x", "../evil.exe", false)); } catch (ArgumentException) { threw = true; }
Check(threw, "a module name with a folder part is refused");
Check(ModuleCatalog.ExePath("/app", ModuleCatalog.All[0]) == Path.Combine("/app", "GraniteInstallWizard.exe"), "module path is the launcher's folder + name");

// ---- Dashboard ----------------------------------------------------------------
var bare = new ServerSnapshot { MachineName = "SRV1", OsDescription = "Windows" };
var rows = Dashboard.StatusRows(bare);
Check(rows.Any(r => r.Area == "IIS" && r.State == HealthState.Missing), "no IIS: IIS missing");
Check(!rows.Any(r => r.Title == "URL Rewrite"), "no IIS: URL Rewrite not listed");
Check(rows.Any(r => r.Area == "GraniteWMS" && r.State == HealthState.Info), "no IIS: no Granite is information, not an error");
Check(Dashboard.ForModule(ModuleId.Install, bare).Suggested, "bare server: Install suggested");
Check(!Dashboard.ForModule(ModuleId.Bi, bare).Suggested && !Dashboard.ForModule(ModuleId.Attach, bare).Suggested, "bare server: only Install suggested");

var iisNoGranite = bare with { IisVersion = "10.0", UrlRewrite = false, AspNetCore8 = "8.0.11", AspNetCoreModule = false };
var r2 = Dashboard.StatusRows(iisNoGranite);
Check(r2.Any(r => r.Title == "URL Rewrite" && r.State == HealthState.Missing), "IIS without URL Rewrite flagged");
Check(r2.Any(r => r.Area == "Runtime" && r.State == HealthState.Attention), "runtime without IIS module: attention");
Check(r2.Any(r => r.Area == "GraniteWMS" && r.State == HealthState.Missing), "IIS but no Granite: missing");

var app = (GraniteAppKind k, string folder) => new Granite.Toolkit.Core.Discovery.GraniteApp(k, k.ToString(), k + "Pool", folder, Array.Empty<Granite.Toolkit.Core.Iis.IisBinding>());
var full = new GraniteInstall(@"C:\Program Files\GraniteWMS", new[]
{
    app(GraniteAppKind.WebDesktop, @"C:\Program Files\GraniteWMS\GraniteWebdesktop"),
    app(GraniteAppKind.BusinessApi, @"C:\Program Files\GraniteWMS\GraniteBusinessAPI"),
    app(GraniteAppKind.Custodian, @"C:\Program Files\GraniteWMS\GraniteCustodian"),
    app(GraniteAppKind.ProcessApp, @"C:\Program Files\GraniteWMS\GraniteProcessApp")
}) { AppVersion = new Version(6, 0, 0, 0) };
var partial = new GraniteInstall(@"D:\Granite", new[] { app(GraniteAppKind.BusinessApi, @"D:\Granite\GraniteBusinessAPI") });

var installed = iisNoGranite with { Installs = new[] { full, partial }, AttachSites = new[] { "Granite Attach (http 5080)" }, BiSyncTasks = new[] { "GraniteWMS BI Sync - GraniteLive_BI" } };
var r3 = Dashboard.StatusRows(installed);
Check(r3.Count(r => r.Area == "GraniteWMS") == 2, "one row per install");
Check(r3.Any(r => r.Area == "GraniteWMS" && r.State == HealthState.Ok && r.Title.Contains("V6.0.0.0")), "complete install OK with version");
Check(r3.Any(r => r.Area == "GraniteWMS" && r.State == HealthState.Attention && r.Detail.Contains("not all four")), "partial install flagged");
Check(r3.Any(r => r.Title == "Granite Attach" && r.State == HealthState.Ok), "Attach found");
Check(r3.Any(r => r.Title == "BI sync" && r.State == HealthState.Ok), "BI task found");
Check(!Dashboard.ForModule(ModuleId.Install, installed).Suggested, "with Granite installed, Install isn't suggested");
Check(Dashboard.ForModule(ModuleId.Attach, installed).Line.StartsWith("Already installed"), "Attach tile says already installed");
Check(Dashboard.ForModule(ModuleId.Bi, installed).Line.Contains("already scheduled"), "BI tile says already scheduled");

var unreadable = iisNoGranite with { IisScanError = "appcmd failed", BiSyncTasks = null };
var r4 = Dashboard.StatusRows(unreadable);
Check(r4.Any(r => r.Area == "GraniteWMS" && r.State == HealthState.Attention && r.Title == "Couldn't read IIS"), "IIS read error shown");
Check(r4.Any(r => r.Title == "BI sync" && r.Detail.Contains("Couldn't read")), "Task Scheduler read error shown");
Check(!Dashboard.ForModule(ModuleId.Install, unreadable).Suggested, "when IIS can't be read, Install isn't suggested blindly");

// ---- NiFi ----------------------------------------------------------------------
Check(rows.Any(r => r.Title == "Apache NiFi" && r.State == HealthState.Info && r.Detail == "Not installed"), "no NiFi: information row");
Check(!Dashboard.ForModule(ModuleId.NiFi, bare).Suggested && Dashboard.ForModule(ModuleId.NiFi, bare).Line.Contains("Needs a GraniteWMS database"), "bare server: NiFi needs Granite first");
Check(Dashboard.ForModule(ModuleId.NiFi, installed).Line.StartsWith("Not installed yet"), "Granite installed, no NiFi: not installed yet");
var withNiFi = installed with
{
    NiFiServices = new[]
    {
        new NiFiService("NiFi", @"C:\nifi\nifi-2.11.0", "2.11.0", "RUNNING"),
        new NiFiService("NiFiTest", @"D:\nifi-test\nifi-2.11.0", "2.11.0", "STOPPED")
    }
};
var r5 = Dashboard.StatusRows(withNiFi);
Check(r5.Any(r => r.Title == "Apache NiFi" && r.State == HealthState.Ok && r.Detail.Contains("NiFi 2.11.0, service NiFi, running")), "running NiFi service OK");
Check(r5.Any(r => r.Title == "Apache NiFi" && r.State == HealthState.Attention && r.Detail.Contains("stopped")), "stopped NiFi service flagged");
Check(!r5.Any(r => r.Title == "Apache NiFi" && r.Detail == "Not installed"), "no 'not installed' row once NiFi is found");
var nifiTile = Dashboard.ForModule(ModuleId.NiFi, withNiFi).Line;
Check(nifiTile.StartsWith("Already installed") && nifiTile.Contains("NiFiTest (not running)"), "NiFi tile lists the services");
Check(ModuleCatalog.All.Any(m => m.Id == ModuleId.NiFi && m.ExeName == "GraniteNiFiDeploy.exe" && !m.DeveloperOnly), "NiFi Deploy is a client-server module");

// ---- BiTaskParser -------------------------------------------------------------
const string csv = "\"\\GraniteWMS BI Sync - GraniteLive_BI\",\"02/10/2026 02:00:00\",\"Ready\"\r\n" +
                   "\"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\",\"N/A\",\"Ready\"\r\n" +
                   "\"\\Granite\\GraniteWMS BI Sync - Client2_BI\",\"N/A\",\"Disabled\"\r\n" +
                   "\"\\GraniteWMS BI Sync - GraniteLive_BI\",\"02/10/2026 02:00:00\",\"Ready\"\r\n" +
                   "INFO: something\r\n";
var tasks = BiTaskParser.Parse(csv);
Check(tasks.SequenceEqual(new[] { "GraniteWMS BI Sync - GraniteLive_BI", "GraniteWMS BI Sync - Client2_BI" }), "BI tasks found, in folders too, no duplicates");
Check(BiTaskParser.Parse("").Count == 0, "empty output");

Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
