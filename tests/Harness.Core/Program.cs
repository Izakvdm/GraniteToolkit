int passed = 0, failed = 0;
void Check(bool ok, string what)
{
    if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + what); }
}

// Sites: one record for all modules, bindings and state both parsed.
const string sitesXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <appcmd>
      <SITE SITE.NAME="Default Web Site" SITE.ID="1" bindings="http/*:80:" state="Started" />
      <SITE SITE.NAME="Granite WebDesktop" SITE.ID="3" bindings="https/*:40099:,http/*:8080:myhost" state="Stopped" />
    </appcmd>
    """;
var sites = IisCommands.ParseSites(sitesXml);
Check(sites.Count == 2, "two sites parsed");
Check(sites[1].Id == 3 && sites[1].State == "Stopped", "site id and state parsed (Install needs Id, DB Switcher needs State)");
Check(sites[1].Bindings.Count == 2 && sites[1].Bindings[0].Port == 40099 && sites[1].Bindings[1].HostName == "myhost", "bindings parsed");
Check(new IisSite("x", 1, Array.Empty<IisBinding>()).State == "", "State defaults to empty, so the Install harness's 3-argument IisSite still works");

// Leading noise and appcmd error text.
Check(IisCommands.ParseSites("warning line\r\n" + sitesXml.Trim()).Count == 2, "text before the XML is skipped");
Check(IisCommands.ParseSites("ERROR ( message:Cannot find SITE object )").Count == 0, "appcmd error text gives no rows");
Check(IisCommands.ParseSites("").Count == 0, "empty output gives no rows");
bool threw = false;
try { IisCommands.ParseSites("<appcmd><SITE "); } catch (System.Xml.XmlException) { threw = true; }
Check(threw, "malformed XML still throws (Install relies on it; Attach catches it itself)");

// App pools: rows (DB Switcher) and names (Install, Attach).
const string poolsXml = """<appcmd><APPPOOL APPPOOL.NAME="DefaultAppPool" state="Started" /><APPPOOL APPPOOL.NAME="Granite Attach" state="Stopped" /><APPPOOL APPPOOL.NAME="" /></appcmd>""";
Check(IisCommands.ParseAppPools(poolsXml).Count == 2, "pools with no name are dropped");
Check(IisCommands.ParseAppPools(poolsXml)[1].State == "Stopped", "pool state parsed");
Check(IisCommands.ParseAppPoolNames(poolsXml).SequenceEqual(new[] { "DefaultAppPool", "Granite Attach" }), "pool names");

// Apps and vdirs (DB Switcher discovery).
var apps = IisCommands.ParseApps("""<appcmd><APP APP.NAME="Granite WebDesktop/" SITE.NAME="Granite WebDesktop" APPPOOL.NAME="GWD" path="/" /></appcmd>""");
Check(apps.Single().AppPool == "GWD" && apps.Single().SiteName == "Granite WebDesktop", "app parsed");
var vdirs = IisCommands.ParseVdirs("""<appcmd><VDIR APP.NAME="Granite WebDesktop/" path="/" physicalPath="C:\Granite\Web" /></appcmd>""");
Check(vdirs.Single().PhysicalPath == @"C:\Granite\Web", "vdir parsed");

// AddSite: https by default (Install), http for Attach.
Check(IisCommands.AddSite("S", 5, @"C:\p", 40099).Last() == "/bindings:https/*:40099:", "AddSite defaults to https");
Check(IisCommands.AddSite("S", 5, @"C:\p", 5080, protocol: "http").Last() == "/bindings:http/*:5080:", "AddSite http for Attach");
Check(IisCommands.FirewallRuleName("Web Desktop", 40099) == "Granite WMS - Web Desktop (40099)", "core firewall rule name unchanged");

// Commands only one module had before.
Check(IisCommands.ListApps().SequenceEqual(new[] { "list", "app", "/xml" }), "ListApps (from DB Switcher)");
Check(IisCommands.AddSslCert(40099, "AB", Guid.Empty).Contains("certstorename=MY"), "AddSslCert (from Install)");

// Merged log levels.
Check(new LogEntry(LogLevel.DryRun, "x").Prefix == "[DRY RUN]", "DryRun prefix");
Check(new LogEntry(LogLevel.Detail, "x").Prefix.Trim() == "", "Detail prefix blank (as Attach had it)");
Check(Enum.GetNames<LogLevel>().SequenceEqual(new[] { "Info", "Success", "Warning", "Error", "Detail", "DryRun", "Stage" }), "LogLevel members");

Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
