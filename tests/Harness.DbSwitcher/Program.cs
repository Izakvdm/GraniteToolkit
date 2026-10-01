using System.Data.Common;
using System.Text;
using System.Text.Json;
using GraniteDbSwitcher.Core;
using GraniteDbSwitcher.Models;

namespace LogicHarness;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static void Check(bool condition, string what)
    {
        if (condition) _passed++;
        else { Failures.Add(what); Console.WriteLine("FAIL: " + what); }
    }

    private static int Main(string[] args)
    {
        ConnectionStrings();
        AppSettingsSynthetic();
        VersionRules();
        IisAndDiscovery();
        ServerNames();
        if (args.Length > 0) RealFiles(args[0]);
        else Console.WriteLine("(no test-data folder given: real appsettings files skipped)");

        Console.WriteLine();
        Console.WriteLine($"{_passed} checks passed, {Failures.Count} failed.");
        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------

    private static Dictionary<string, string> Ado(string cs)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k])!, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertOnlyDatabaseChanged(string before, string after, string newDb, string label)
    {
        var a = Ado(before);
        var b = Ado(after);
        string dbKey = a.Keys.FirstOrDefault(k => k.Equals("initial catalog", StringComparison.OrdinalIgnoreCase) || k.Equals("database", StringComparison.OrdinalIgnoreCase))
                       ?? "initial catalog";
        Check(b.TryGetValue(dbKey, out var got) && got == newDb, $"{label}: database is \"{newDb}\" (got \"{got}\")");
        foreach (var (k, v) in a)
        {
            if (k.Equals(dbKey, StringComparison.OrdinalIgnoreCase)) continue;
            Check(b.TryGetValue(k, out var v2) && v2 == v, $"{label}: {k} unchanged");
        }
        Check(b.Count == Math.Max(a.Count, a.ContainsKey(dbKey) ? a.Count : a.Count + 1), $"{label}: no keys added or lost");
    }

    private static void ConnectionStrings()
    {
        string v6Api = @"Data Source=.\SQL2022;Initial Catalog=GraniteDatabase;Persist Security Info=True;User ID=Granite;Password=Example-Pa55;TrustServerCertificate=True;Pooling=true;Min Pool Size=5;Max Pool Size=25;";
        string v7Cust = "Server=172.28.208.1;Database=Granite_V6;User Id=sa;Password=x;TrustServerCertificate=True;";
        string noTrail = "Data Source=.;Initial Catalog=GraniteDatabase;Persist Security Info=True;User ID=Granite;Password=Example-Pa55";
        string quoted = "Data Source=ULTRA\\SQLEXPRESS;Initial Catalog=GraniteDatabase;User ID=Granite_App;Password=\"p;a'ss\";TrustServerCertificate=True";
        string noDb = "Data Source=.;User ID=Granite;Password=abc";
        string spaced = " Data Source = . ; Initial Catalog = Old ;User ID=u;Password=p ";

        foreach (var (cs, label) in new[] { (v6Api, "V6 Business API"), (v7Cust, "V7 Custodian"), (noTrail, "Process App (no trailing ;)"), (quoted, "quoted password"), (noDb, "no database key"), (spaced, "spaced") })
        {
            foreach (string db in new[] { "ClientA_Granite", "Client;Semi", "O'Brien DB", "Name \"Q\"", " Lead" })
            {
                string after = ConnectionStringEditor.WithDatabase(cs, db);
                AssertOnlyDatabaseChanged(cs, after, db, $"{label} -> [{db}]");
            }
        }

        // Unchanged parts stay byte-identical, including classic keywords.
        string swapped = ConnectionStringEditor.WithDatabase(v6Api, "ClientA");
        Check(swapped == v6Api.Replace("Initial Catalog=GraniteDatabase", "Initial Catalog=ClientA"), "V6 Business API: only the catalog text changes");
        Check(swapped.Contains("TrustServerCertificate=True") && !swapped.Contains("Trust Server Certificate"), "classic TrustServerCertificate keyword kept");
        Check(ConnectionStringEditor.WithDatabase(v7Cust, "X") == v7Cust.Replace("Database=Granite_V6", "Database=X"), "V7 Custodian: Database= synonym reused");
        Check(ConnectionStringEditor.WithDatabase(noTrail, "X") == noTrail.Replace("Initial Catalog=GraniteDatabase", "Initial Catalog=X"), "no trailing ; stays without one");
        Check(ConnectionStringEditor.WithDatabase(noDb, "X") == noDb + ";Initial Catalog=X;", "missing database key appended");
        Check(ConnectionStringEditor.WithDatabase(v6Api, "GraniteDatabase") == v6Api, "same database: string untouched");

        var p = ConnectionStringEditor.Parse(quoted);
        Check(p.Password == "p;a'ss", "quoted password read");
        Check(p.Server == "ULTRA\\SQLEXPRESS" && p.User == "Granite_App" && p.Database == "GraniteDatabase", "server/user/database read");
        Check(!p.IntegratedSecurity, "SQL auth detected");
        Check(ConnectionStringEditor.Parse("Server=.;Database=x;Trusted_Connection=True").IntegratedSecurity, "Trusted_Connection detected");
        Check(ConnectionStringEditor.Parse("Data Source=.;Integrated Security=SSPI").IntegratedSecurity, "Integrated Security=SSPI detected");
        Check(!ConnectionStringEditor.Mask(quoted).Contains("p;a'ss") && ConnectionStringEditor.Mask(quoted).Contains("*****"), "Mask hides the password");

        bool threw = false;
        try { ConnectionStringEditor.Parse("Data Source=.;Password=\"unclosed"); } catch (FormatException) { threw = true; }
        Check(threw, "unclosed quote rejected");
    }

    // ------------------------------------------------------------------

    private static void AssertEditPreservesFile(byte[] before, string expectedName, string newDb, string label)
    {
        var found = AppSettingsConnectionEditor.Find(before);
        var target = AppSettingsConnectionEditor.Pick(found, expectedName);
        Check(target is not null, $"{label}: connection \"{expectedName}\" found");
        if (target is null) return;

        string newValue = ConnectionStringEditor.WithDatabase(target.Value, newDb);
        byte[] after = AppSettingsConnectionEditor.Replace(before, target, newValue);

        // Bytes before and after the edited token are identical.
        int head = (int)target.ByteOffset;
        Check(before.AsSpan(0, head).SequenceEqual(after.AsSpan(0, head)), $"{label}: bytes before the value unchanged");
        int tail = before.Length - head - target.ByteLength;
        Check(before.AsSpan(before.Length - tail).SequenceEqual(after.AsSpan(after.Length - tail)), $"{label}: bytes after the value unchanged");
        Check(AppSettingsConnectionEditor.HasBom(before) == AppSettingsConnectionEditor.HasBom(after), $"{label}: BOM kept as it was");

        // Re-read: the edited entry has the new value, every other entry is identical.
        var reFound = AppSettingsConnectionEditor.Find(after);
        Check(reFound.Count == found.Count, $"{label}: same number of connection strings");
        foreach (var f in found)
        {
            var r = reFound.FirstOrDefault(x => x.Name == f.Name);
            string expected = f.Name == target.Name ? newValue : f.Value;
            Check(r is not null && r.Value == expected, $"{label}: \"{f.Name}\" is as expected after the edit");
        }

        // Still valid JSON (comments allowed, as the apps read it), all other settings equal.
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        int bom = AppSettingsConnectionEditor.HasBom(before) ? 3 : 0;
        using var d1 = JsonDocument.Parse(before.AsMemory(bom), opts);
        using var d2 = JsonDocument.Parse(after.AsMemory(bom), opts);
        foreach (var prop in d1.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("ConnectionStrings")) continue;
            Check(d2.RootElement.TryGetProperty(prop.Name, out var p2) && p2.GetRawText() == prop.Value.GetRawText(),
                $"{label}: \"{prop.Name}\" unchanged");
        }

        string text = Encoding.UTF8.GetString(after);
        Check(text.Split('\n').Count(l => l.TrimStart().StartsWith("//")) == Encoding.UTF8.GetString(before).Split('\n').Count(l => l.TrimStart().StartsWith("//")),
            $"{label}: // comment lines kept");
    }

    private static void AppSettingsSynthetic()
    {
        string json = "{\r\n  // Business API\r\n  \"ConnectionStrings\": {\r\n    // main\r\n    \"CONNECTION\": \"Data Source=.\\\\SQLEXPRESS;Initial Catalog=Old;User ID=Granite;Password=a\\\"b;TrustServerCertificate=True;\",\r\n    \"Other\": \"Data Source=x;Initial Catalog=Keep\",\r\n  },\r\n  \"Nested\": { \"ConnectionStrings\": { \"CONNECTION\": \"decoy\" } },\r\n  \"CompanyName\": \"Granite WMS\"\r\n}\r\n";
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(plain).ToArray();

        var found = AppSettingsConnectionEditor.Find(plain);
        Check(found.Count == 2, "synthetic: two top-level connection strings, nested decoy ignored");
        Check(found.FirstOrDefault(f => f.Name == "CONNECTION")?.Value == "Data Source=.\\SQLEXPRESS;Initial Catalog=Old;User ID=Granite;Password=a\"b;TrustServerCertificate=True;",
            "synthetic: escaped backslash and quote decoded");

        AssertEditPreservesFile(plain, "CONNECTION", "NewDb", "synthetic (no BOM)");
        AssertEditPreservesFile(withBom, "CONNECTION", "NewDb", "synthetic (BOM)");
        AssertEditPreservesFile(plain, "connection", "New\\Db\"Q", "synthetic (odd name, case-insensitive key)");

        var one = AppSettingsConnectionEditor.Find(Encoding.UTF8.GetBytes("{\"connectionStrings\":{\"Whatever\":\"Data Source=.;Database=a\"}}"));
        Check(AppSettingsConnectionEditor.Pick(one, "ConnectionString")?.Name == "Whatever", "single entry used when the expected name is missing");
        var two = AppSettingsConnectionEditor.Find(Encoding.UTF8.GetBytes("{\"ConnectionStrings\":{\"A\":\"x=1\",\"B\":\"x=2\"}}"));
        Check(AppSettingsConnectionEditor.Pick(two, "CONNECTION") is null, "ambiguous: no guess when several and none match");
        Check(AppSettingsConnectionEditor.Find(Encoding.UTF8.GetBytes("{\"Business_API_Endpoint\":\"https::5001/\"}")).Count == 0, "Web Desktop file: no connection strings");
    }

    // ------------------------------------------------------------------

    private static void VersionRules()
    {
        string[] v6 = { "SCHEMA_500", "DATA_500", "CLR_500", "SCHEMA_510", "DATA_510", "CLR_510", "SCHEMA_600", "DATA_600", "CLR_600" };
        string[] v7 = v6.Concat(new[] { "SCHEMA_700", "DATA_700" }).ToArray();
        Check(GraniteVersionRules.HighestSchema(v6) == "SCHEMA_600", "V6 create script -> SCHEMA_600");
        Check(GraniteVersionRules.HighestSchema(v7) == "SCHEMA_700", "V7 create script -> SCHEMA_700");
        Check(GraniteVersionRules.HighestSchema(new[] { "SCHEMA_90", "SCHEMA_1000" }) == "SCHEMA_1000", "numeric, not text, ordering");
        Check(GraniteVersionRules.HighestSchema(new[] { "DATA_700" }) is null, "no schema rows -> null");

        DatabaseInfo Db(string? schema, string? setting, bool v7Objects, bool granite = true) => new()
        {
            Name = "x", IsGranite = granite, SchemaMigration = schema, DatabaseVersionSetting = setting, HasV7Objects = v7Objects
        };

        var r7 = GraniteVersionRules.Resolve(Db("SCHEMA_700", "6.0.0.0", true));
        Check(r7 is { Major: 7, Minor: 0 }, "V7 db: Migration wins over the 6.0.0.0 setting");
        var r6 = GraniteVersionRules.Resolve(Db("SCHEMA_600", "6.0.0.0", false));
        Check(r6 is { Major: 6, Minor: 0 }, "V6 db -> 6.0");
        Check(GraniteVersionRules.Resolve(Db("SCHEMA_510", null, false)) is { Major: 5, Minor: 1 }, "SCHEMA_510 -> 5.1");
        Check(GraniteVersionRules.Resolve(Db(null, "6.0.0.0", true)) is { Major: 7 }, "no Migration + V7 tables -> 7");
        Check(GraniteVersionRules.Resolve(Db(null, "6.0.0.0", false)) is { Major: 6 }, "no Migration, no V7 tables -> setting 6");
        Check(GraniteVersionRules.Resolve(Db(null, null, false)) is null, "nothing to go on -> unknown");
        Check(GraniteVersionRules.Resolve(Db("SCHEMA_700", null, true, granite: false)) is null, "non-Granite -> null");

        var install6 = new GraniteInstall(@"C:\Program Files\GraniteWMS", Array.Empty<GraniteApp>()) { AppVersion = new Version(6, 0, 0, 0) };
        var install7 = new GraniteInstall(@"C:\Program Files\GraniteWMS V7", Array.Empty<GraniteApp>()) { AppVersion = new Version(7, 2, 0, 0) };
        var unknown = new GraniteInstall(@"C:\x", Array.Empty<GraniteApp>());
        Check(GraniteVersionRules.Check(install6, r6) == Compatibility.Match, "V6 install + V6 db: match");
        Check(GraniteVersionRules.Check(install7, r7) == Compatibility.Match, "V7.2 install + SCHEMA_700 db: match");
        Check(GraniteVersionRules.Check(install6, r7) == Compatibility.Mismatch, "V6 install + V7 db: mismatch");
        Check(GraniteVersionRules.Check(install7, r6) == Compatibility.Mismatch, "V7 install + V6 db: mismatch");
        Check(GraniteVersionRules.Check(unknown, r6) == Compatibility.Unknown, "install version unknown");
        Check(GraniteVersionRules.Check(install6, null) == Compatibility.Unknown, "db version unknown");
    }

    // ------------------------------------------------------------------

    private static void IisAndDiscovery()
    {
        string sitesXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <appcmd>
                <SITE SITE.NAME="Default Web Site" SITE.ID="1" bindings="http/*:80:" state="Started" />
                <SITE SITE.NAME="GraniteWebDesktop" SITE.ID="2" bindings="https/*:40099:" state="Started" />
                <SITE SITE.NAME="GraniteBusinessAPI" SITE.ID="3" bindings="https/*:40081:" state="Started" />
                <SITE SITE.NAME="GraniteCustodian" SITE.ID="4" bindings="https/*:40082:" state="Started" />
                <SITE SITE.NAME="GraniteProcessApp" SITE.ID="5" bindings="https/*:40080:,http/*:8080:" state="Started" />
                <SITE SITE.NAME="V7 Business API" SITE.ID="6" bindings="https/*:41081:v7.localhost" state="Started" />
                <SITE SITE.NAME="V7 Process App" SITE.ID="7" bindings="https/[::1]:41080:" state="Stopped" />
            </appcmd>
            """;
        string appsXml = """
            <appcmd>
                <APP APP.NAME="Default Web Site/" APPPOOL.NAME="DefaultAppPool" SITE.NAME="Default Web Site" path="/" />
                <APP APP.NAME="GraniteWebDesktop/" APPPOOL.NAME="GraniteWebDesktop" SITE.NAME="GraniteWebDesktop" path="/" />
                <APP APP.NAME="GraniteBusinessAPI/" APPPOOL.NAME="GraniteBusinessAPI" SITE.NAME="GraniteBusinessAPI" path="/" />
                <APP APP.NAME="GraniteCustodian/" APPPOOL.NAME="GraniteCustodian" SITE.NAME="GraniteCustodian" path="/" />
                <APP APP.NAME="GraniteProcessApp/" APPPOOL.NAME="GraniteProcessApp" SITE.NAME="GraniteProcessApp" path="/" />
                <APP APP.NAME="GraniteProcessApp/sub" APPPOOL.NAME="Other" SITE.NAME="GraniteProcessApp" path="/sub" />
                <APP APP.NAME="V7 Business API/" APPPOOL.NAME="V7BusinessAPI" SITE.NAME="V7 Business API" path="/" />
                <APP APP.NAME="V7 Process App/" APPPOOL.NAME="V7ProcessApp" SITE.NAME="V7 Process App" path="/" />
            </appcmd>
            """;
        string vdirsXml = """
            <appcmd>
                <VDIR VDIR.NAME="Default Web Site/" APP.NAME="Default Web Site/" path="/" physicalPath="%SystemDrive%\inetpub\wwwroot" />
                <VDIR VDIR.NAME="GraniteWebDesktop/" APP.NAME="GraniteWebDesktop/" path="/" physicalPath="C:\Program Files\GraniteWMS\GraniteWebdesktop" />
                <VDIR VDIR.NAME="GraniteBusinessAPI/" APP.NAME="GraniteBusinessAPI/" path="/" physicalPath="C:\Program Files\GraniteWMS\GraniteBusinessAPI\" />
                <VDIR VDIR.NAME="GraniteCustodian/" APP.NAME="GraniteCustodian/" path="/" physicalPath="c:\program files\graniteWMS\GraniteCustodian" />
                <VDIR VDIR.NAME="GraniteProcessApp/" APP.NAME="GraniteProcessApp/" path="/" physicalPath="C:\Program Files\GraniteWMS\GraniteProcessApp" />
                <VDIR VDIR.NAME="GraniteProcessApp/sub" APP.NAME="GraniteProcessApp/sub" path="/" physicalPath="C:\Elsewhere\Sub" />
                <VDIR VDIR.NAME="V7 Business API/" APP.NAME="V7 Business API/" path="/" physicalPath="%GRANITE7%\GraniteBusinessAPI" />
                <VDIR VDIR.NAME="V7 Process App/" APP.NAME="V7 Process App/" path="/" physicalPath="D:\GraniteV7\GraniteProcessApp" />
            </appcmd>
            """;

        var sites = IisCommands.ParseSites(sitesXml);
        var apps = IisCommands.ParseApps(appsXml);
        var vdirs = IisCommands.ParseVdirs(vdirsXml);
        Check(sites.Count == 7 && apps.Count == 8 && vdirs.Count == 8, "appcmd XML parsed");
        Check(sites.First(s => s.Name == "GraniteProcessApp").Bindings.Count == 2, "two bindings parsed");
        Check(sites.First(s => s.Name == "V7 Process App").Bindings[0].Address == "[::1]", "IPv6 binding address");
        Check(IisCommands.ParseAppPools("<appcmd><APPPOOL APPPOOL.NAME=\"A\" state=\"Stopped\" /></appcmd>").Single().State == "Stopped", "app pool state parsed");
        Check(IisCommands.ParseSites("ERROR ( message:... )").Count == 0, "non-XML output -> empty");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\Program Files\GraniteWMS\GraniteBusinessAPI", "Granite.Business.API.dll"),
            Path.Combine(@"c:\program files\graniteWMS\GraniteCustodian", "Granite.Custodian.dll"),
            Path.Combine(@"C:\Program Files\GraniteWMS\GraniteProcessApp", "Granite.Process.App.dll"),
            Path.Combine(@"C:\Program Files\GraniteWMS\GraniteWebdesktop", "index.html"),
            Path.Combine(@"D:\GraniteV7\GraniteBusinessAPI", "Granite.Business.API.dll"),
            Path.Combine(@"D:\GraniteV7\GraniteProcessApp", "Granite.Process.App.dll"),
            Path.Combine(@"C:\Elsewhere\Sub", "Granite.Business.API.dll"),
            Path.Combine(@"C:\inetpub\wwwroot", "index.html")
        };
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(@"C:\Program Files\GraniteWMS\GraniteWebdesktop", "appsettings.json")] = "{ \"Business_API_Endpoint\": \"https::5001/\" }",
            [Path.Combine(@"C:\inetpub\wwwroot", "appsettings.json")] = "{ }"
        };
        string Expand(string p) => p.Replace("%SystemDrive%", "C:").Replace("%GRANITE7%", @"D:\GraniteV7");

        var installs = InstallDiscovery.Build(sites, apps, vdirs, files.Contains, p => texts.TryGetValue(p, out var t) ? t : null, Expand);
        Check(installs.Count == 2, $"two installs found (got {installs.Count})");
        var v6 = installs.FirstOrDefault(i => i.RootFolder.Equals(@"C:\Program Files\GraniteWMS", StringComparison.OrdinalIgnoreCase));
        Check(v6 is not null && v6.Apps.Count == 4, "V6 install: all four apps, despite folder-name case differences and a trailing slash");
        Check(v6?.Get(GraniteAppKind.WebDesktop) is not null, "Web Desktop recognised by index.html + Business_API_Endpoint");
        Check(v6?.DatabaseApps.Count() == 3, "three apps with a database connection");
        Check(v6?.Get(GraniteAppKind.ProcessApp)?.LocalUrl == "https://localhost:40080/", "https binding preferred for the URL");
        var v7 = installs.FirstOrDefault(i => i.RootFolder == @"D:\GraniteV7");
        Check(v7 is not null && v7.Apps.Count == 2, "V7 install via an expanded env-var path");
        Check(v7?.Get(GraniteAppKind.BusinessApi)?.LocalUrl == "https://v7.localhost:41081/", "host header used in the URL");
        Check(v7?.Get(GraniteAppKind.BusinessApi)?.AppPool == "V7BusinessAPI", "pool name comes from the app, not the site");
        Check(!installs.Any(i => i.Apps.Any(a => a.SiteName == "Default Web Site")), "Default Web Site ignored");
        Check(!installs.Any(i => i.RootFolder == @"C:\Elsewhere"), "non-root application ignored");

        Check(InstallDiscovery.ParentOf(@"C:\Program Files\GraniteWMS\GraniteBusinessAPI\") == @"C:\Program Files\GraniteWMS", "ParentOf with a trailing slash");
    }

    private static void ServerNames()
    {
        Check(AppConnectionReader.NormaliseServer(".\\SQLEXPRESS") == AppConnectionReader.NormaliseServer("localhost\\sqlexpress"), ". = localhost");
        Check(AppConnectionReader.NormaliseServer("(local)") == AppConnectionReader.NormaliseServer("."), "(local) = .");
        Check(AppConnectionReader.NormaliseServer("tcp:127.0.0.1\\SQL2022") == AppConnectionReader.NormaliseServer(".\\SQL2022"), "tcp:127.0.0.1 = .");
        Check(AppConnectionReader.NormaliseServer("ULTRA\\SQLEXPRESS") != AppConnectionReader.NormaliseServer(".\\SQL2022"), "different instances differ");
    }

    // ------------------------------------------------------------------

    private static void RealFiles(string folder)
    {
        var files = Directory.GetFiles(folder, "*.json").OrderBy(f => f).ToList();
        Console.WriteLine($"Real appsettings files: {files.Count} in {folder}");
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            byte[] bytes = File.ReadAllBytes(file);
            string? expected =
                name.Contains("BusinessApi", StringComparison.OrdinalIgnoreCase) ? "CONNECTION" :
                name.Contains("Custodian", StringComparison.OrdinalIgnoreCase) ? "CONNECTION" :
                name.Contains("ProcessApp", StringComparison.OrdinalIgnoreCase) ? "ConnectionString" : null;

            if (expected is null)
            {
                Check(AppSettingsConnectionEditor.Find(bytes).Count == 0, $"{name}: no connection strings (Web Desktop)");
                Check(InstallDiscovery.Identify("x", p => p.EndsWith("index.html"), _ => Encoding.UTF8.GetString(bytes)) == GraniteAppKind.WebDesktop,
                    $"{name}: recognised as Web Desktop");
                continue;
            }

            var kind = expected == "ConnectionString" ? GraniteAppKind.ProcessApp
                     : name.Contains("Custodian", StringComparison.OrdinalIgnoreCase) ? GraniteAppKind.Custodian : GraniteAppKind.BusinessApi;
            var state = AppConnectionReader.FromBytes(new GraniteApp(kind, "s", "p", "x", Array.Empty<IisBinding>()), bytes);
            Check(state.Problem is null && state.Database is not null, $"{name}: reads current database ({state.Database} on {state.Server}, user {state.User})");
            Check(state.ConnectionName == expected, $"{name}: picked \"{expected}\" (got \"{state.ConnectionName}\")");

            AssertEditPreservesFile(bytes, expected, "ClientA_Test", name);
            AssertOnlyDatabaseChanged(state.ConnectionString!, ConnectionStringEditor.WithDatabase(state.ConnectionString!, "ClientA_Test"), "ClientA_Test", name + " connection string");
        }
    }
}
