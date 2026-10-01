using System.Text.Json.Nodes;
using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;
using SqlClientBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder;

// Usage:  dotnet run -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V6.0"
//         dotnet run -c Release -- "C:\Users\izakm\Documents\Granite WMS\Granite V7.0.zip"
// The release only needs the four apps' appsettings.json,
// GraniteDatabase\GraniteDatabase\GraniteDatabase_Create.sql and the
// Hotfix folder for every check to run; a full release works too. A zip,
// or a V7-style folder whose apps are inner zips, is unpacked first with
// the wizard's own ReleaseSource, so that path is tested for real too.
string releaseArg = args.Length > 0 ? args[0] : @"C:\Users\izakm\Documents\Granite WMS\Granite V6.0";
string harnessExtract = Path.Combine(Path.GetTempPath(), "gw-harness-releases");
string release;
string releaseKind;
if (ReleaseSource.IsZip(releaseArg))
{
    release = ReleaseSource.ExtractZip(releaseArg, harnessExtract, null, CancellationToken.None);
    releaseKind = "zip, unpacked with ReleaseSource.ExtractZip";
}
else if (!Directory.Exists(releaseArg)) { Console.WriteLine($"Release folder not found: {releaseArg}"); return 2; }
else if (ReleaseSource.FindReleaseRoot(releaseArg) is string plain)
{
    release = plain;
    releaseKind = "folder";
}
else if (ReleaseSource.FindPackedReleaseRoot(releaseArg) is string packedRoot)
{
    release = ReleaseSource.PreparePackedFolder(packedRoot, harnessExtract, null, CancellationToken.None);
    releaseKind = "folder of inner zips, unpacked with ReleaseSource.PreparePackedFolder";
}
else
{
    release = releaseArg; // a trimmed copy (appsettings, create script, Hotfix) is enough for the checks
    releaseKind = "folder, partial release";
}
Console.WriteLine($"Release: {release} ({releaseKind})");
Console.WriteLine();

int failures = 0, passes = 0;
void Check(bool condition, string description)
{
    if (condition) { passes++; Console.WriteLine($"  [PASS] {description}"); }
    else { failures++; Console.WriteLine($"  [FAIL] {description}"); }
}
string P(params string[] parts) => ReleaseLayout.Resolve(release, parts);

var vars = new Dictionary<string, string> { ["DatabaseName"] = "GraniteTest1", ["DefaultFilePrefix"] = "GraniteTest1" };

Console.WriteLine("=== 1. GraniteDatabase_Create.sql, parsed for real ===");
string createRaw = File.ReadAllText(P("GraniteDatabase", "GraniteDatabase", "GraniteDatabase_Create.sql"));
var create = GraniteSqlScriptParser.Parse(createRaw, vars);
Console.WriteLine($"  {create.Batches.Count} batches, line endings: {(createRaw.Contains("\r\n") ? "CRLF" : "LF")}");
Check(create.Batches.Count > 500, $"splits into hundreds of batches ({create.Batches.Count})");
Check(create.UnresolvedVariables.Count == 0, "no unresolved $(...) variables");
Check(create.Batches.Count(b => b.Text.Contains("CREATE DATABASE [GraniteTest1]")) == 1, "CREATE DATABASE uses the caller's DatabaseName");
Check(create.Batches.All(b => !b.Text.Contains("$(DatabaseName)")), "no $(DatabaseName) token survives into any batch");
Check(create.Variables["DatabaseName"] == "GraniteTest1", "caller's DatabaseName wins over any :setvar");
Check(create.Variables["__IsSqlCmdEnabled"] == "True", ":setvar __IsSqlCmdEnabled \"True\" honoured (quotes stripped)");
Check(create.Variables["DefaultDataPath"] == string.Empty, ":setvar DefaultDataPath \"\" gives an empty value");
Check(create.Batches.Count(b => b.Text.Contains("N'True' NOT LIKE N'True'")) == 1, "the SQLCMD-mode guard batch resolves to 'True' NOT LIKE 'True' (so NOEXEC is never switched on)");
Check(create.Batches.All(b => !System.Text.RegularExpressions.Regex.IsMatch(GraniteSqlScriptParser.StripCommentsPreservingLayout(b.Text), @"(?im)^\s*:(setvar|on\s+error|r)\b")), "no SQLCMD directive left in any batch");
Check(create.Batches.All(b => !System.Text.RegularExpressions.Regex.IsMatch(GraniteSqlScriptParser.StripCommentsPreservingLayout(b.Text), @"(?im)^\s*GO\s*$")), "no GO separator left inside any batch");
Check(create.Batches.Any(b => b.Text.Contains("--") || b.Text.Contains("/*")), "comments are kept in executed text (object definitions keep them)");
Check(create.Batches.All(b => b.StartLine >= 1 && b.StartLine <= createRaw.Split('\n').Length), "every batch has a valid start line");
int useBatches = create.Batches.Count(b => b.Text.TrimStart().StartsWith("USE [GraniteTest1]", StringComparison.OrdinalIgnoreCase));
Check(useBatches >= 1, $"USE [GraniteTest1] present ({useBatches}x) so later batches run in the new database");
string lf = createRaw.Replace("\r\n", "\n");
var createLf = GraniteSqlScriptParser.Parse(lf, vars);
Check(createLf.Batches.Count == create.Batches.Count, "same batch count whether the file is CRLF or LF");

Console.WriteLine();
Console.WriteLine("=== 2. Hotfix database scripts ===");
string clrPath = P("Hotfix", "Database", "SQLCLR_Install.sql");
if (File.Exists(clrPath))
{
    string clrRaw = File.ReadAllText(clrPath); // UTF-16 with BOM on disk
    var clr = GraniteSqlScriptParser.Parse(GraniteSqlScriptParser.ToCreateOrAlter(clrRaw), vars);
    Console.WriteLine($"  SQLCLR_Install.sql: {clr.Batches.Count} batches");
    Check(clr.Batches.Count > 50, "SQLCLR_Install.sql (UTF-16) decodes and splits");
    Check(clr.UnresolvedVariables.Count == 0, "SQLCLR_Install.sql has no unresolved variables");
    Check(clr.Batches.Any(b => b.Text.Contains("USE [GraniteTest1]")), "SQLCLR_Install.sql switches to the Granite database");
}
else Console.WriteLine("  (SQLCLR_Install.sql not present, skipped)");

string viewPath = P("Hotfix", "Database", "dbo.API_QueryDocumentProgress.sql");
if (File.Exists(viewPath))
{
    string converted = GraniteSqlScriptParser.ToCreateOrAlter(File.ReadAllText(viewPath));
    Check(converted.Contains("CREATE OR ALTER VIEW [dbo].[API_QueryDocumentProgress]"), "hotfix view rewritten as CREATE OR ALTER");
    Check(!GraniteSqlScriptParser.ToCreateOrAlter(converted).Contains("OR ALTER OR ALTER"), "rewriting twice does nothing more");
}
string mdPath = P("Hotfix", "Custodian.md");
if (File.Exists(mdPath))
{
    var blocks = GraniteSqlScriptParser.ExtractMarkdownSqlBlocks(File.ReadAllText(mdPath));
    Check(blocks.Count == 1 && blocks[0].Contains("UPDATE SystemSettings"), "Custodian.md yields its one UPDATE SystemSettings block");
    Check(GraniteSqlScriptParser.Parse(blocks[0], vars).Batches.Count == 1, "that block is one batch");
}

Console.WriteLine();
Console.WriteLine("=== 3. Parser edge cases (synthetic) ===");
var p1 = GraniteSqlScriptParser.Parse("SELECT 1\nGO\n/*\nGO\nCREATE LOGIN x WITH PASSWORD='p'\nGO\n*/\nSELECT 2\n-- GO\nGO\n");
Check(p1.Batches.Count == 2, $"GO inside a block comment or line comment is not a separator ({p1.Batches.Count} batches)");
Check(!p1.Batches.Any(b => b.Text.Trim().StartsWith("CREATE LOGIN")), "commented-out CREATE LOGIN never becomes its own batch");
var p2 = GraniteSqlScriptParser.Parse("--:setvar DatabaseName \"Wrong\"\n:setvar Other \"x\"\nSELECT '$(Other)', '$(Missing)'\nGO 3\n");
Check(p2.Batches.Count == 1 && p2.Batches[0].Text.Contains("'x'"), "commented :setvar ignored, real :setvar applied, GO n accepted");
Check(p2.UnresolvedVariables.SequenceEqual(new[] { "Missing" }), "unknown $(Missing) reported as unresolved");
bool threw = false;
try { GraniteSqlScriptParser.Parse(":r other.sql\nGO\n"); } catch (NotSupportedException) { threw = true; }
Check(threw, ":r include is refused rather than silently skipped");
Check(!GraniteSqlScriptParser.ToCreateOrAlter("-- CREATE VIEW x\nSELECT 1").Contains("OR ALTER"), "CREATE VIEW inside a comment is not rewritten");
Check(GraniteSqlScriptParser.ToCreateOrAlter("create procedure dbo.p as select 1").StartsWith("create OR ALTER procedure"), "lower-case create procedure rewritten");

Console.WriteLine();
Console.WriteLine("=== 4. appsettings.json rewrite, against the real shipped files ===");
var ctx = new InstallContext
{
    ReleaseFolder = release,
    SqlServer = @"SRV01\SQL2022",
    DatabaseName = "GraniteLive",
    AppLogin = "Granite",
    AppPassword = "p@ss;w\"rd",
    CompanyName = "Acme \"Test\"; Ltd",
    DateFormat = "DD/MM/YYYY",
    PublicHost = "192.168.1.50",
    CertDnsNames = new List<string> { "srv01.acme.local", "SRV01", "localhost" },
    CertIpAddresses = new List<string> { "192.168.1.50" }
};
JsonObject Rewrite(string key, out AppSettingsResult result)
{
    var comp = GraniteComponent.Get(key);
    string shipped = File.ReadAllText(ReleaseLayout.Resolve(release, comp.ReleaseFolder, "appsettings.json"));
    result = AppSettingsWriter.Apply(comp, shipped, ctx);
    return JsonNode.Parse(result.Json)!.AsObject();
}
string ConnStr(JsonObject o, string name) => o["ConnectionStrings"]![name]!.GetValue<string>();

var api = Rewrite(GraniteComponent.BusinessApi, out var apiResult);
var apiConn = new SqlClientBuilder(ConnStr(api, "CONNECTION"));
Check(apiConn.Password == "p@ss;w\"rd", "Business API: password with ; and \" survives the round trip through SqlClient");
Check(apiConn.DataSource == @"SRV01\SQL2022" && apiConn.InitialCatalog == "GraniteLive" && apiConn.UserID == "Granite", "Business API: server, database, login");
Check(apiConn.MaxPoolSize == 25 && apiConn.MinPoolSize == 5, "Business API: shipped pool sizes kept");
Check(apiConn.TrustServerCertificate, "Business API: TrustServerCertificate on");
Check(!ConnStr(api, "CONNECTION").Contains("Trust Server Certificate"), "Business API: classic TrustServerCertificate keyword (System.Data.SqlClient accepts it)");
var apiOrigins = api["AllowedOrigins"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
Check(apiOrigins.Contains("https://192.168.1.50:40099") && apiOrigins.Contains("https://SRV01:40099") && apiOrigins.Contains("https://localhost:40080"), $"Business API: CORS covers Web Desktop and Process App by IP, name and localhost ({apiOrigins.Count} origins)");
Check(api["DateTimeFormat"]!.GetValue<string>() == "dd'/'MM'/'yyyy", "Business API: DateTimeFormat dd'/'MM'/'yyyy");
Check(apiResult.Json.Contains("dd'/'MM'/'yyyy"), "Business API: apostrophes written literally, not \\u0027");
Check(api["servicestack"]?["license"]?.GetValue<string>().Length > 50, "Business API: ServiceStack licence carried over");
Check(api["Telemetry"]?["ServiceName"]?.GetValue<string>() == "business-api", "Business API: Telemetry section carried over");

var cus = Rewrite(GraniteComponent.Custodian, out _);
Check(new SqlClientBuilder(ConnStr(cus, "CONNECTION")).InitialCatalog == "GraniteLive", "Custodian: CONNECTION points at the Granite database");
var test = new SqlClientBuilder(ConnStr(cus, "Granite_Test"));
Check(test.InitialCatalog == "GraniteDatabaseTest" && test.DataSource == @"SRV01\SQL2022" && test.UserID == "Granite", "Custodian: Granite_Test keeps its catalog but uses this server and login");
Check(!ConnStr(cus, "Granite_Test").Contains(@".\SQL2022"), "Custodian: developer's .\\SQL2022 server gone");
var cusOrigins = cus["AllowedOrigins"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
Check(cusOrigins.All(o => o.EndsWith(":40099")) && cusOrigins.Contains("https://localhost:40099"), "Custodian: CORS allows Web Desktop only");

var pa = Rewrite(GraniteComponent.ProcessApp, out _);
Check(pa["BusinessApiEndPoint"]!.GetValue<string>() == "https://192.168.1.50:40081/", "Process App: BusinessApiEndPoint uses the Step 4 address");
Check(new SqlClientBuilder(ConnStr(pa, "ConnectionString")).TrustServerCertificate, "Process App: connection string now trusts the server certificate (shipped one didn't)");
Check(pa["Company"]!.GetValue<string>() == "Acme \"Test\"; Ltd", "Process App: company name with quotes and ; kept exactly");
bool paHasDate = JsonNode.Parse(File.ReadAllText(ReleaseLayout.Resolve(release, "GraniteProcessApp", "appsettings.json")),
    documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject().ContainsKey("DateTimeFormat");
Check(paHasDate ? pa["DateTimeFormat"]!.GetValue<string>() == "dd'/'MM'/'yyyy" : !pa.ContainsKey("DateTimeFormat"),
      paHasDate ? "Process App: DateTimeFormat (new in V7) set to dd'/'MM'/'yyyy" : "Process App: no DateTimeFormat key added where the release has none (V6.0)");

var wd = Rewrite(GraniteComponent.WebDesktop, out _);
Check(wd["Business_API_Endpoint"]!.GetValue<string>() == "https://192.168.1.50:40081/", "Web Desktop: Business_API_Endpoint");
Check(wd["URL_Custodian"]!.GetValue<string>() == "https://192.168.1.50:40082/", "Web Desktop: URL_Custodian");
Check(wd["URL_LabelPrint"]!.GetValue<string>() == "" && wd["URL_Integration"]!.GetValue<string>() == "", "Web Desktop: malformed label/integration placeholders cleared");
Check(wd["DateTimeFormat"]!.GetValue<string>() == "DD/MM/YYYY", "Web Desktop: DateTimeFormat DD/MM/YYYY");

ctx.Sites[GraniteComponent.Custodian].Enabled = false;
var wd2 = Rewrite(GraniteComponent.WebDesktop, out _);
Check(wd2["URL_Custodian"]!.GetValue<string>() == "", "Web Desktop: URL_Custodian blank when Custodian isn't installed");
var api2 = Rewrite(GraniteComponent.BusinessApi, out _);
Check(api2["AllowedOrigins"]!.AsArray().Count == apiOrigins.Count, "Business API: origins unchanged by Custodian being off");
ctx.Sites[GraniteComponent.Custodian].Enabled = true;

Console.WriteLine();
Console.WriteLine("=== 5. Date formats and connection-string quoting ===");
Check(DateFormatConverter.ToDotNet("DD/MM/YYYY") == "dd'/'MM'/'yyyy", "DD/MM/YYYY");
Check(DateFormatConverter.ToDotNet("MM/DD/YYYY") == "MM'/'dd'/'yyyy", "MM/DD/YYYY");
Check(DateFormatConverter.ToDotNet("YYYY-MM-DD") == "yyyy'-'MM'-'dd", "YYYY-MM-DD");
foreach (string pwd in new[] { "simple", "a;b", "a'b", "a\"b", "a'b\"c;d", " lead", "trail ", "x=y" })
{
    string cs = ConnectionStringFormatter.ForApp("S", "D", "U", pwd);
    Check(new SqlClientBuilder(cs).Password == pwd, $"password {pwd.Replace("\"", "\\\"")} round-trips");
}

Console.WriteLine();
Console.WriteLine("=== 6. IIS / netsh commands and appcmd output parsing ===");
Check(IisCommands.AddSite("Granite WebDesktop", 3, @"C:\Granite WMS\GraniteWebdesktop", 40080)
        .SequenceEqual(new[] { "add", "site", "/name:Granite WebDesktop", "/id:3", @"/physicalPath:C:\Granite WMS\GraniteWebdesktop", "/bindings:https/*:40080:" }),
      "add site: one argument per switch, paths with spaces kept whole");
Check(IisCommands.AddAppPool("Granite Business API").Contains("/managedRuntimeVersion:") && IisCommands.AddAppPool("x").Contains("/startMode:AlwaysRunning"), "add apppool: No Managed Code, AlwaysRunning");
var guid = Guid.Parse("5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61");
Check(IisCommands.AddSslCert(40081, "ABCDEF", guid).SequenceEqual(new[] { "http", "add", "sslcert", "ipport=0.0.0.0:40081", "certhash=ABCDEF", "appid={5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61}", "certstorename=MY" }), "netsh sslcert: appid in braces");
Check(IisCommands.GrantFolderModify(@"C:\G\X", "Granite Process App")[2] == @"IIS AppPool\Granite Process App:(OI)(CI)M", "icacls grant for the pool identity");
const string sitesXml = """
<?xml version="1.0" encoding="UTF-8"?>
<appcmd>
    <SITE SITE.NAME="Default Web Site" SITE.ID="1" bindings="http/*:80:,https/*:443:" state="Started" />
    <SITE SITE.NAME="Granite Old" SITE.ID="7" bindings="https/192.168.1.5:40081:,http/[fe80::1]:8080:host.local" state="Stopped" />
</appcmd>
""";
var sites = IisCommands.ParseSites(sitesXml);
Check(sites.Count == 2 && sites.Max(s => s.Id) == 7, "appcmd list site parsed; next id would be 8");
Check(sites[1].Bindings.Any(b => b.Port == 40081) && sites[1].Bindings.Any(b => b.Port == 8080 && b.HostName == "host.local"), "bindings parsed, including an IPv6 address");
Check(IisCommands.ParseAppPools("<appcmd><APPPOOL APPPOOL.NAME=\"DefaultAppPool\" /><APPPOOL APPPOOL.NAME=\"Granite WebDesktop\" /></appcmd>").Count == 2, "appcmd list apppool parsed");
Check(IisCommands.ParseSites(string.Empty).Count == 0, "no IIS yet: empty site list");

Console.WriteLine();
Console.WriteLine("=== 7. DISM feature table ===");
const string dism = """
Deployment Image Servicing and Management tool

------------------------------------------- | --------
Feature Name                                | State
------------------------------------------- | --------
IIS-WebServerRole                           | Enabled
IIS-WebServer                               | Enable Pending
IIS-StaticContent                           | Disabled
IIS-DefaultDocument                         | Disabled with Payload Removed

The operation completed successfully.
""";
var table = WindowsFeatureList.ParseFeatureTable(dism);
Check(table["IIS-WebServerRole"] && table["IIS-WebServer"] && !table["IIS-StaticContent"] && !table["IIS-DefaultDocument"], "Enabled / Enable Pending / Disabled / Payload Removed read correctly");
var missing = WindowsFeatureList.Missing(table);
Check(missing.Contains("IIS-StaticContent") && !missing.Contains("IIS-WebServerRole") && missing.Contains("IIS-ManagementConsole"), "missing = required minus enabled (features not listed count as missing)");
Check(WindowsFeatureList.EnableFeaturesArgs(new[] { "A", "B" }).SequenceEqual(new[] { "/online", "/enable-feature", "/featurename:A", "/featurename:B", "/all", "/norestart", "/quiet" }), "one DISM call enables all missing features");

Console.WriteLine();
Console.WriteLine("=== 8. Profiles, origins, release check, hotfix exclusions ===");
ctx.SqlAdminPassword = "sa-secret";
ctx.DropExistingDatabase = true;
string json = InstallProfile.From(ctx, "v0.2.0").ToJson();
Check(!json.Contains("p@ss") && !json.Contains("sa-secret"), "profile holds no passwords");
Check(!json.Contains("DropExistingDatabase"), "profile never carries \"drop the database\"");
Check(json.Contains("\"SqlAuth\": \"Windows\""), "enums saved by name");
var restored = new InstallContext();
InstallProfile.FromJson(json).ApplyTo(restored);
Check(restored.SqlServer == ctx.SqlServer && restored.Sites[GraniteComponent.ProcessApp].Port == 40080 && restored.CertIpAddresses.SequenceEqual(ctx.CertIpAddresses), "profile round-trips");
Check(restored.AppPassword == string.Empty && !restored.DropExistingDatabase, "loading a profile leaves passwords blank and drop off");
Check(ctx.AllHostNames().SequenceEqual(new[] { "192.168.1.50", "srv01.acme.local", "SRV01", "localhost" }), "host names: Step 4 address first, then certificate names, localhost once");
Check(ReleaseFolderCheck.Problems(Path.GetTempPath()).Count == 1, "a folder that isn't a release is rejected with one clear message");
Check(ReleaseFolderCheck.Problems(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).Single() == "The release folder doesn't exist.", "a missing release folder is reported as such");
foreach (var (name, excluded) in new[] { ("appsettings.json", true), ("appsettings.Production.json", true), ("web.config", true), ("nlog.config", true), ("README.md", true), ("Granite.Process.App.dll", false), ("Granite.Process.App.deps.json", false) })
    Check(FileDeployer.IsHotfixExcluded(name) == excluded, $"hotfix overlay {(excluded ? "skips" : "copies")} {name}");

Console.WriteLine();
Console.WriteLine("=== 9. New vs existing database: what blocks, what warns ===");
SqlServerInfo Info(bool exists, bool granite, bool sysadmin = true, bool winOnly = false) =>
    new("16.0 Express", 16, "SRV\\admin", sysadmin, winOnly, exists, false, granite);
var db = new InstallContext { DatabaseName = "GraniteLive" };

db.DatabaseMode = DatabaseMode.CreateNew; db.DropExistingDatabase = false;
Check(SqlServerInspector.Blockers(Info(false, false), db).Count == 0, "new: database absent -> go");
Check(SqlServerInspector.Blockers(Info(true, true), db).Single().Contains("already exists"), "new: database exists, no drop -> blocked, and the message points at \"Use an existing\"");
Check(SqlServerInspector.Blockers(Info(true, true), db).Single().Contains("Use an existing Granite database"), "new: blocked message offers the existing-database option");
db.DropExistingDatabase = true;
Check(SqlServerInspector.Blockers(Info(true, true), db).Count == 0 && SqlServerInspector.Warnings(Info(true, true), db).Any(w => w.Contains("WILL BE DROPPED")), "new: exists + drop ticked -> go, with a drop warning");

db.DatabaseMode = DatabaseMode.UseExisting; db.DropExistingDatabase = false; db.DatabaseHotfixChoice = null;
Check(SqlServerInspector.Blockers(Info(false, false), db).Single().Contains("doesn't exist"), "existing: database absent -> blocked");
Check(SqlServerInspector.Blockers(Info(true, false), db).Single().Contains("doesn't look like a Granite database"), "existing: not a Granite database -> blocked");
Check(SqlServerInspector.Blockers(Info(true, true), db).Count == 0, "existing: Granite database -> go");
var existingWarnings = SqlServerInspector.Warnings(Info(true, true), db);
Check(existingWarnings.Any(w => w.Contains("data is kept")) && !existingWarnings.Any(w => w.Contains("Hotfix")), "existing, hotfix scripts off: says data is kept, no hotfix warning");
db.DatabaseHotfixChoice = true;
db.ReleaseFolder = release;
if (HotfixScripts.Find(db).Count > 0)
    Check(SqlServerInspector.Warnings(Info(true, true), db).Any(w => w.Contains("backup")), "existing, hotfix scripts on: warns to take a backup, naming the scripts");
else
    Check(!SqlServerInspector.Warnings(Info(true, true), db).Any(w => w.Contains("Hotfix")), "existing, hotfix scripts on but the release has none: no hotfix warning");
db.DropExistingDatabase = true;
Check(!SqlServerInspector.Warnings(Info(true, true), db).Any(w => w.Contains("DROPPED")), "existing: a stray drop flag never produces a drop");
Check(SqlServerInspector.Blockers(Info(true, true, sysadmin: false, winOnly: true), db).Count == 2, "existing: not sysadmin and Windows-only auth both still block");
Check(db.SqlCheckKey != new InstallContext { DatabaseName = "GraniteLive", DatabaseMode = DatabaseMode.CreateNew }.SqlCheckKey, "switching mode invalidates an earlier Test Connection");

var modeProfile = new InstallContext();
InstallProfile.FromJson(InstallProfile.From(db, "v0.2.1").ToJson()).ApplyTo(modeProfile);
Check(modeProfile.DatabaseMode == DatabaseMode.UseExisting && modeProfile.ApplyDatabaseHotfix, "profile carries the database mode and the hotfix-scripts choice");
var defaults = new InstallContext();
Check(defaults.ApplyDatabaseHotfix, "hotfix scripts default ON for a new database");
defaults.DatabaseMode = DatabaseMode.UseExisting;
Check(!defaults.ApplyDatabaseHotfix, "hotfix scripts default OFF for an existing database (v0.3.1: re-entering Step 3 can't flip it back on)");
defaults.DatabaseHotfixChoice = true;
Check(defaults.ApplyDatabaseHotfix, "an explicit tick wins over the existing-database default");
defaults.DatabaseMode = DatabaseMode.CreateNew; defaults.DatabaseHotfixChoice = false;
Check(!defaults.ApplyDatabaseHotfix, "an explicit untick wins over the new-database default");

var loginCtx = new InstallContext { AppLogin = "Granite" };
Check(SqlServerInspector.ExistingLoginMismatchMessage(loginCtx, "Login failed").Contains("change its password"), "login mismatch without reset: message points at the \"change its password\" option");
loginCtx.ResetExistingAppLoginPassword = true;
Check(SqlServerInspector.ExistingLoginMismatchMessage(loginCtx, "Login failed").Contains("WILL BE CHANGED"), "login mismatch with reset: message says the password will change");
string resetJson = InstallProfile.From(loginCtx, "v0.3.1").ToJson();
Check(!resetJson.Contains("ResetExistingAppLoginPassword"), "profile never carries \"change its password\"");
var resetBack = new InstallContext { ResetExistingAppLoginPassword = true };
InstallProfile.FromJson(resetJson).ApplyTo(resetBack);
Check(!resetBack.ResetExistingAppLoginPassword, "loading a profile switches \"change its password\" off");

Check(IisCommands.RecycleAppPool("Granite Business API").SequenceEqual(new[] { "recycle", "apppool", "/apppool.name:Granite Business API" }), "recycle apppool command (v0.2.1 clean start)");

Console.WriteLine();
Console.WriteLine("=== 10. Reinstalling over an earlier install: IIS site and port conflicts ===");
// The first real install on Ultra used Web Desktop 40099 and Process App 40080, now the defaults.
IisSite Site(string name, int id, int port) => new(name, id, new[] { new IisBinding("https", "*", port, "") });
var ultra = new List<IisSite>
{
    new("Default Web Site", 1, new[] { new IisBinding("http", "*", 80, "") }),
    Site("Granite WebDesktop", 2, 40099), Site("Granite Business API", 3, 40081),
    Site("Granite Custodian", 4, 40082), Site("Granite Process App", 5, 40080)
};
var listeningNow = new HashSet<int> { 80, 40099, 40081, 40082, 40080 };
var re = new InstallContext(); // Step 4 defaults: WD 40099, API 40081, Custodian 40082, PA 40080
var noReplace = SiteConflictCheck.Evaluate(ultra, listeningNow, re);
Check(noReplace.Errors.Count == 4, $"replace off: one problem per existing site, no duplicate port errors for the same site ({noReplace.Errors.Count})");
Check(!noReplace.Errors.Any(e => e.StartsWith("Port ")), "replace off: ports held by same-named sites are covered by the name message");
Check(noReplace.Errors.Where(e => e.Contains("already exists")).All(e => e.Contains("Replace existing IIS sites")), "replace off: name clashes point at the Replace option");
var moved = new InstallContext();
moved.Sites[GraniteComponent.WebDesktop].Port = 40080; // clashes with the old Process App site
Check(SiteConflictCheck.Evaluate(ultra, listeningNow, moved).Errors.Any(e => e.Contains("Port 40080 (Web Desktop)") && e.Contains("Granite Process App")), "replace off: a port held by a DIFFERENT existing site is still reported");
re.ReplaceExistingSites = true;
var withReplace = SiteConflictCheck.Evaluate(ultra, listeningNow, re);
Check(withReplace.Errors.Count == 0, "replace on: no errors (ports held by sites being replaced are free)");
Check(withReplace.Warnings.Count == 4 && withReplace.SitesToReplace.Count == 4, "replace on: 4 sites to replace, 4 warnings");
Check(!withReplace.SitesToReplace.Any(x => x.Name == "Default Web Site"), "replace on: sites with other names are never touched");
re.Sites[GraniteComponent.ProcessApp].Port = 80;
Check(SiteConflictCheck.Evaluate(ultra, listeningNow, re).Errors.Single().Contains("\"Default Web Site\", which isn't being replaced"), "replace on: a port held by an unrelated site still blocks");
re.Sites[GraniteComponent.ProcessApp].Port = 5000;
Check(SiteConflictCheck.Evaluate(ultra, new HashSet<int>(listeningNow) { 5000 }, re).Errors.Single().Contains("another program"), "replace on: a port held by a non-IIS program still blocks");
re.Sites[GraniteComponent.ProcessApp].Port = 40080;
Check(SiteConflictCheck.Evaluate(Array.Empty<IisSite>(), new HashSet<int>(), new InstallContext()).Errors.Count == 0, "fresh server (no IIS yet): nothing to clash with");
var replaceProfile = new InstallContext { ReplaceExistingSites = true };
InstallProfile.FromJson(InstallProfile.From(re, "v0.3.2").ToJson()).ApplyTo(replaceProfile);
Check(!replaceProfile.ReplaceExistingSites && !InstallProfile.From(re, "v0.3.2").ToJson().Contains("ReplaceExistingSites"), "profile never carries \"Replace existing IIS sites\"");
Check(IisCommands.DeleteSite("Granite WebDesktop").SequenceEqual(new[] { "delete", "site", "/site.name:Granite WebDesktop" })
      && IisCommands.DeleteAppPool("Granite WebDesktop").SequenceEqual(new[] { "delete", "apppool", "/apppool.name:Granite WebDesktop" })
      && IisCommands.DeleteFirewallRule("Granite WMS - Process App (40080)").SequenceEqual(new[] { "advfirewall", "firewall", "delete", "rule", "name=Granite WMS - Process App (40080)" }),
      "delete site / apppool / firewall rule commands");

Console.WriteLine();
Console.WriteLine("=== 11. Release from a folder or a .zip ===");
string zipWork = Path.Combine(Path.GetTempPath(), "gw-zip-" + Guid.NewGuid().ToString("N"));
void MakeFakeRelease(string root)
{
    foreach (var comp in GraniteComponent.CoreStack) Directory.CreateDirectory(Path.Combine(root, comp.ReleaseFolder));
    Directory.CreateDirectory(Path.Combine(root, "GraniteDatabase", "GraniteDatabase"));
    File.WriteAllText(Path.Combine(root, "GraniteDatabase", "GraniteDatabase", "GraniteDatabase_Create.sql"), "SELECT 1");
    Directory.CreateDirectory(Path.Combine(root, "GraniteScaffold", "Prerequisites"));
    File.WriteAllText(Path.Combine(root, "GraniteBusinessAPI", "appsettings.json"), "{}");
}
string staging = Path.Combine(zipWork, "staging");
MakeFakeRelease(Path.Combine(staging, "Granite V7.0"));
string zipPath = Path.Combine(zipWork, "Granite V7.0.zip");
System.IO.Compression.ZipFile.CreateFromDirectory(staging, zipPath);
string extractRoot = Path.Combine(zipWork, "Releases");
var pcts = new List<int>();
string extracted = ReleaseSource.ExtractZip(zipPath, extractRoot, new SyncProgress(pcts.Add), CancellationToken.None);
Check(extracted.EndsWith(Path.Combine("Granite V7.0", "Granite V7.0")) && ReleaseFolderCheck.Problems(extracted).Count == 0, "zip with a top-level folder: release found inside it");
Check(pcts.Count > 0 && pcts[^1] == 100, "extraction reports progress up to 100%");
File.WriteAllText(Path.Combine(extracted, "touched.txt"), "x");
string again = ReleaseSource.ExtractZip(zipPath, extractRoot, null, CancellationToken.None);
Check(again == extracted && File.Exists(Path.Combine(extracted, "touched.txt")), "same zip again: earlier extraction reused, not re-extracted");
File.SetLastWriteTimeUtc(zipPath, DateTime.UtcNow.AddMinutes(5));
ReleaseSource.ExtractZip(zipPath, extractRoot, null, CancellationToken.None);
Check(!File.Exists(Path.Combine(extracted, "touched.txt")), "changed zip: extracted fresh");
Check(ReleaseSource.IsZip(zipPath) && !ReleaseSource.IsZip(staging), "zip vs folder detection");
Check(ReleaseSource.FindReleaseRoot(staging) == Path.Combine(staging, "Granite V7.0"), "folder: a release one level down is found");

string evilZip = Path.Combine(zipWork, "evil.zip");
using (var z = System.IO.Compression.ZipFile.Open(evilZip, System.IO.Compression.ZipArchiveMode.Create))
    using (var w = new StreamWriter(z.CreateEntry("../../outside.txt").Open())) w.Write("x");
bool refused = false;
try { ReleaseSource.ExtractZip(evilZip, extractRoot, null, CancellationToken.None); } catch (InvalidDataException) { refused = true; }
Check(refused && !File.Exists(Path.Combine(zipWork, "outside.txt")), "zip slip: an entry pointing outside the extraction folder is refused");

// Granite V6.0.zip stores its root as an entry named "/" (v0.5.1 fix).
string rootEntryZip = Path.Combine(zipWork, "rootentry.zip");
using (var z = System.IO.Compression.ZipFile.Open(rootEntryZip, System.IO.Compression.ZipArchiveMode.Create))
{
    z.CreateEntry("/");
    foreach (string entryDir in Directory.GetDirectories(Path.Combine(staging, "Granite V7.0"), "*", SearchOption.AllDirectories))
        z.CreateEntry(Path.GetRelativePath(Path.Combine(staging, "Granite V7.0"), entryDir).Replace('\\', '/') + "/");
    foreach (string entryFile in Directory.GetFiles(Path.Combine(staging, "Granite V7.0"), "*", SearchOption.AllDirectories))
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, entryFile, Path.GetRelativePath(Path.Combine(staging, "Granite V7.0"), entryFile).Replace('\\', '/'));
    using (var w = new StreamWriter(z.CreateEntry("/leading-slash.txt").Open())) w.Write("x");
}
string fromRootEntry = ReleaseSource.ExtractZip(rootEntryZip, extractRoot, null, CancellationToken.None);
Check(ReleaseFolderCheck.Problems(fromRootEntry).Count == 0 && fromRootEntry == Path.Combine(extractRoot, "rootentry"), "zip with a \"/\" root entry (like Granite V6.0.zip): extracted, not refused");
Check(File.Exists(Path.Combine(fromRootEntry, "leading-slash.txt")), "entry with a leading slash lands inside the extraction folder");

string notRelease = Path.Combine(zipWork, "notrelease.zip");
using (var z = System.IO.Compression.ZipFile.Open(notRelease, System.IO.Compression.ZipArchiveMode.Create))
    using (var w = new StreamWriter(z.CreateEntry("readme.txt").Open())) w.Write("x");
bool rejected = false;
try { ReleaseSource.ExtractZip(notRelease, extractRoot, null, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "a zip with no Granite release in it is rejected");

string two = Path.Combine(zipWork, "two");
MakeFakeRelease(Path.Combine(two, "A"));
MakeFakeRelease(Path.Combine(two, "B"));
Check(ReleaseSource.FindReleaseRoot(two) is null, "two releases side by side: ambiguous, not guessed");
Check(new InstallContext().InstallRoot.EndsWith("GraniteWMS") && new InstallContext().InstallRoot.Contains("Program Files"), $"default install folder is Program Files\\GraniteWMS ({new InstallContext().InstallRoot})");
Directory.Delete(zipWork, recursive: true);

Console.WriteLine();
Console.WriteLine("=== 12. V7-style releases: apps packed as inner zips ===");
string v7Work = Path.Combine(Path.GetTempPath(), "gw-v7-" + Guid.NewGuid().ToString("N"));
// Build a release shaped like the real V7.0 zip: each app is its own zip
// with its own top folder, spelled the V7 way.
string packed = Path.Combine(v7Work, "src", "Granite V7.0");
Directory.CreateDirectory(packed);
void InnerZip(string zipPath, string topFolder, params (string Name, string Text)[] files)
{
    Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
    using var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
    z.CreateEntry(topFolder + "/");
    foreach (var (name, text) in files)
        using (var w = new StreamWriter(z.CreateEntry($"{topFolder}/{name}").Open())) w.Write(text);
}
InnerZip(Path.Combine(packed, "GraniteBusinessApi.zip"), "GraniteBusinessApi", ("appsettings.json", "{}"), ("Granite.Business.API.dll", "v7"));
InnerZip(Path.Combine(packed, "GraniteCustodian.zip"), "GraniteCustodian", ("appsettings.json", "{}"));
InnerZip(Path.Combine(packed, "GraniteWebDesktop.zip"), "GraniteWebDesktop", ("appsettings.json", "{}"), ("web.config", "<configuration/>"));
InnerZip(Path.Combine(packed, "GraniteProcessApp.zip"), "GraniteProcessApp", ("appsettings.json", "{}"));
InnerZip(Path.Combine(packed, "GraniteScaffold.zip"), "GraniteScaffold", ("Prerequisites/.NET/dotnet-hosting-8.0.30-win.exe", ""));
InnerZip(Path.Combine(packed, "GraniteTelemetry.zip"), "GraniteTelemetry", ("big.bin", "not needed"));
InnerZip(Path.Combine(packed, "GraniteDatabase", "GraniteDatabase.zip"), "GraniteDatabase", ("GraniteDatabase_Create.sql", "SELECT 1"));
InnerZip(Path.Combine(packed, "GraniteDatabase", "Accpac.zip"), "Accpac", ("x.sql", "SELECT 1"));
InnerZip(Path.Combine(packed, "Hotfix", "ProcessApp.zip"), "ProcessApp", ("Granite.Process.App.dll", "hotfix"));
Directory.CreateDirectory(Path.Combine(packed, "Hotfix", "Business Api"));
File.WriteAllText(Path.Combine(packed, "Hotfix", "Business Api", "Granite.Business.API.ServiceInterface.dll"), "hotfix");
File.WriteAllText(Path.Combine(packed, "Hotfix", "SQLCLR_Install.sql"), "SELECT 1");
File.WriteAllText(Path.Combine(packed, "GraniteDatabase", "7.2 Upgrade.sql"), "SELECT 1");

Check(ReleaseFolderCheck.Problems(packed).Count == 1 && ReleaseSource.IsPackedRelease(packed), "packed folder: not a release as it stands, but recognised as a packed one");
Check(ReleaseSource.FindPackedReleaseRoot(Path.Combine(v7Work, "src")) == packed, "packed folder one level down is found");
foreach (var (name, needed) in new[] { ("GraniteBusinessApi.zip", true), ("GraniteBusinessAPI.zip", true), ("GraniteWebDesktop.zip", true), ("GraniteScaffold.zip", true),
                                      ("GraniteDatabase.zip", true), ("ProcessApp.zip", true), ("Business Api.zip", true), ("GraniteTelemetry.zip", false),
                                      ("GraniteScheduler.zip", false), ("Accpac.zip", false), ("GraniteSQLCLR.zip", false), ("agroserve-intacct-config.zip", false) })
    Check(ReleaseLayout.IsNeededInnerZip(name) == needed, $"inner zip {name}: {(needed ? "unpacked" : "left alone")}");

string v7Out = Path.Combine(v7Work, "Releases");
string fromFolder = ReleaseSource.PreparePackedFolder(packed, v7Out, null, CancellationToken.None);
Check(ReleaseFolderCheck.Problems(fromFolder).Count == 0, "packed folder: unpacked copy is a complete release");
Check(File.Exists(Path.Combine(packed, "GraniteBusinessApi.zip")) && !Directory.Exists(Path.Combine(packed, "GraniteBusinessApi")), "packed folder: the user's own folder is left untouched");
Check(!Directory.Exists(Path.Combine(fromFolder, "GraniteTelemetry")) && !File.Exists(Path.Combine(fromFolder, "GraniteTelemetry.zip")), "packed folder: Telemetry neither copied nor unpacked");
Check(!Directory.Exists(Path.Combine(fromFolder, "GraniteDatabase", "Accpac")), "packed folder: ERP database packs left alone");
Check(File.Exists(Path.Combine(fromFolder, "Hotfix", "ProcessApp", "Granite.Process.App.dll")), "packed folder: Hotfix\\ProcessApp.zip unpacked to Hotfix\\ProcessApp");
Check(ReleaseSource.PreparePackedFolder(packed, v7Out, null, CancellationToken.None) == fromFolder, "packed folder: unchanged folder reuses the earlier unpack");

var v7ctx = new InstallContext { ReleaseFolder = fromFolder };
Check(File.Exists(Path.Combine(v7ctx.ReleasePathFor(GraniteComponent.Get(GraniteComponent.BusinessApi)), "Granite.Business.API.dll")), "GraniteBusinessAPI resolves to V7's GraniteBusinessApi folder");
Check(File.Exists(v7ctx.CreateScriptPath), "create script found in the unpacked GraniteDatabase.zip");
Check(v7ctx.HotfixPathFor(GraniteComponent.Get(GraniteComponent.BusinessApi))?.EndsWith("Business Api") == true, "Hotfix\\BusinessAPI matches V7's Hotfix\\Business Api");
Check(v7ctx.HotfixPathFor(GraniteComponent.Get(GraniteComponent.Custodian)) is null, "no Hotfix folder for Custodian: nothing to apply");
Check(HotfixScripts.Find(v7ctx).Count == 0, "loose Hotfix\\SQLCLR_Install.sql and 7.2 Upgrade.sql are NOT picked up as hotfix database scripts");

string packedZip = Path.Combine(v7Work, "Granite V7.0.zip");
System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(v7Work, "src"), packedZip);
var v7pcts = new List<int>();
string fromZip = ReleaseSource.ExtractZip(packedZip, v7Out, new SyncProgress(v7pcts.Add), CancellationToken.None);
Check(ReleaseFolderCheck.Problems(fromZip).Count == 0, "packed zip: extracted and unpacked to a complete release");
Check(!Directory.GetFiles(fromZip, "*.zip", SearchOption.AllDirectories).Any(f => ReleaseLayout.IsNeededInnerZip(Path.GetFileName(f))), "packed zip: needed inner zips removed after unpacking");
Check(!File.Exists(Path.Combine(fromZip, "GraniteTelemetry.zip")), "packed zip: Telemetry never written out");
Check(v7pcts.Count > 0 && v7pcts[^1] == 100 && v7pcts.SequenceEqual(v7pcts.OrderBy(x => x)), "packed zip: progress climbs to 100% without going backwards");

// V6.0 layout still resolves the same way.
var v6ctx = new InstallContext { ReleaseFolder = release };
Console.WriteLine($"  This release: hotfix database scripts = {HotfixScripts.Describe(HotfixScripts.Find(v6ctx))}; hotfix app folders = "
    + string.Join(", ", GraniteComponent.CoreStack.Where(c => v6ctx.HotfixPathFor(c) is not null).Select(c => Path.GetFileName(v6ctx.HotfixPathFor(c)!))));
Check(GraniteComponent.CoreStack.All(c => Directory.Exists(v6ctx.ReleasePathFor(c))), "this release: every core app folder resolves");
Directory.Delete(v7Work, recursive: true);

Console.WriteLine();
Console.WriteLine($"{passes} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool, so a check right after could miss values).</summary>
sealed class SyncProgress : IProgress<int>
{
    private readonly Action<int> _report;
    public SyncProgress(Action<int> report) => _report = report;
    public void Report(int value) => _report(value);
}
