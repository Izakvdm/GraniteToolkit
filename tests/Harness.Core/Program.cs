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

// ---- JsonTextEditor: byte-preserving edits ------------------------------------
{
    var enc = new System.Text.UTF8Encoding(false);
    string wd = "﻿{\r\n  // Web Desktop settings\r\n  \"Business_API_Endpoint\": \"https://192.168.68.63:40081/\",\r\n  \"URL_Custodian\": \"https://192.168.68.63:40082/\", // custodian\r\n  \"CompanyName\": \"Granite WMS\",\r\n  \"DateTimeFormat\": \"DD/MM/YYYY\"\r\n}\r\n";
    byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(wd);
    Check(Granite.Toolkit.Core.Json.JsonTextEditor.GetString(bytes, "Business_API_Endpoint") == "https://192.168.68.63:40081/", "reads a string through BOM and comments");
    byte[] edited = Granite.Toolkit.Core.Json.JsonTextEditor.SetString(bytes, "Business_API_Endpoint", "https://ULTRA:40081/");
    string after = enc.GetString(edited);
    Check(after == wd.Replace("https://192.168.68.63:40081/", "https://ULTRA:40081/"), "only the one value's bytes change (BOM, comments, CRLF kept)");
    Check(Granite.Toolkit.Core.Json.JsonTextEditor.GetString(bytes, "Nope") is null, "missing property reads as null");
    bool threwMissing = false;
    try { Granite.Toolkit.Core.Json.JsonTextEditor.SetString(bytes, "Nope", "x"); } catch (InvalidDataException) { threwMissing = true; }
    Check(threwMissing, "setting a missing property throws instead of adding it");
    Check(enc.GetString(Granite.Toolkit.Core.Json.JsonTextEditor.SetString(bytes, "CompanyName", "A \"quoted\" \\ name")).Contains("\"CompanyName\": \"A \\\"quoted\\\" \\\\ name\""), "quotes and backslashes escaped");

    string api = "{\n  \"ConnectionStrings\": { \"CONNECTION\": \"Server=.;Password=x\" },\n  \"AllowedOrigins\": [ \"https://192.168.68.63:40099\", \"https://ULTRA:40099\" ],\n  // keep me\n  \"DateTimeFormat\": \"dd'/'MM'/'yyyy\"\n}\n";
    byte[] apiBytes = enc.GetBytes(api);
    var origins = Granite.Toolkit.Core.Json.JsonTextEditor.GetStringArray(apiBytes, "AllowedOrigins");
    Check(origins is { Count: 2 } && origins[1] == "https://ULTRA:40099", "reads a string array");
    string newApi = enc.GetString(Granite.Toolkit.Core.Json.JsonTextEditor.SetStringArray(apiBytes, "AllowedOrigins", new[] { "https://a:1", "https://b:2" }));
    Check(newApi.Contains("\"AllowedOrigins\": [\n    \"https://a:1\",\n    \"https://b:2\"\n  ],") && newApi.Contains("// keep me") && newApi.Contains("Password=x"), "array rewritten one per line, indented; the rest untouched");
    Check(System.Text.Json.JsonDocument.Parse(newApi, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip }).RootElement.GetProperty("AllowedOrigins").GetArrayLength() == 2, "result is valid JSON");
    Check(Granite.Toolkit.Core.Json.JsonTextEditor.GetString(enc.GetBytes("{\"ConnectionStrings\":{\"AllowedOrigins\":\"nested\"}}"), "AllowedOrigins") is null, "only top-level properties match");
}

// ---- GraniteAddress --------------------------------------------------------------
{
    var machine = new MachineAddresses(new[] { "ULTRA.home.lan", "ULTRA" }, new[] { "10.0.0.5" }, new[] { "192.168.68.70" });
    Check(GraniteAddress.Check("192.168.68.63", machine) == AddressHealth.NotThisMachine, "old DHCP IP: not this machine");
    Check(GraniteAddress.Check("192.168.68.70", machine) == AddressHealth.DhcpAddress, "current DHCP IP flagged");
    Check(GraniteAddress.Check("10.0.0.5", machine) == AddressHealth.Ok, "fixed IP OK");
    Check(GraniteAddress.Check("ultra", machine) == AddressHealth.Ok && GraniteAddress.Check("ULTRA.home.lan", machine) == AddressHealth.Ok, "own names OK, any case");
    Check(GraniteAddress.Check("wms.client.com", machine) == AddressHealth.NameNotChecked, "other DNS name not checked");
    Check(GraniteAddress.Check("localhost", machine) == AddressHealth.Localhost && GraniteAddress.Check("127.0.0.1", machine) == AddressHealth.Localhost, "localhost flagged");
    Check(GraniteAddress.Check(null, machine) == AddressHealth.Missing, "missing");
    Check(GraniteAddress.HostOf("https://192.168.68.63:40081/") == "192.168.68.63" && GraniteAddress.HostOf("https::5001/") is null && GraniteAddress.HostOf("https://:40081/") is null, "HostOf, shipped placeholders aren't URLs");
    Check(GraniteAddress.WithHost("https://192.168.68.63:40081/", "ULTRA") == "https://ULTRA:40081/", "WithHost keeps port and slash");
    Check(GraniteAddress.WithHost("https://192.168.68.63:40099", "ULTRA") == "https://ULTRA:40099", "WithHost keeps no-slash origins");
    Check(GraniteAddress.WithHost("https://x:40081/api/v1", "y") == "https://y:40081/api/v1", "WithHost keeps a path");
    foreach (var (h, ok) in new[] { ("ULTRA", true), ("wms.client.co.za", true), ("10.1.2.3", true), ("localhost", false), ("127.0.0.1", false),
                                     ("", false), ("https://ULTRA", false), ("ULTRA:40081", false), ("ul tra", false), ("-bad", false), ("a\"b", false), ("999.1.1.1", false), ("10.1.2", false) })
        Check(GraniteAddress.IsValidNewHost(h, out _) == ok, $"IsValidNewHost(\"{h}\") == {ok}");
    Check(GraniteAddress.DefaultChoice(machine) == "10.0.0.5", "default: fixed IP when there is one");
    Check(GraniteAddress.DefaultChoice(machine with { FixedIPv4 = Array.Empty<string>() }) == "ULTRA", "default: computer name when the only IP is DHCP");
    Check(GraniteAddress.Suggestions(machine).SequenceEqual(new[] { "ULTRA.home.lan", "ULTRA", "10.0.0.5", "192.168.68.70" }), "suggestions: names, fixed, DHCP");
}

// ---- AddressChange --------------------------------------------------------------
{
    var enc = new System.Text.UTF8Encoding(false);
    var machine = new MachineAddresses(new[] { "ULTRA" }, Array.Empty<string>(), new[] { "192.168.68.70" });
    string wd = "{\r\n  \"Business_API_Endpoint\": \"https://192.168.68.63:40081/\",\r\n  \"URL_LabelPrint\": \"\",\r\n  \"URL_Custodian\": \"https://192.168.68.63:40082/\"\r\n}";
    string pa = "{\r\n  \"ConnectionStrings\": { \"ConnectionString\": \"secret\" },\r\n  \"BusinessApiEndPoint\": \"https://192.168.68.63:40081/\"\r\n}";
    string api = "{\r\n  \"AllowedOrigins\": [ \"https://192.168.68.63:40099\", \"https://ULTRA:40099\", \"https://localhost:40099\", \"https://192.168.68.63:40080\", \"https://192.168.68.70:40080\" ]\r\n}";
    string cus = "{\r\n  \"AllowedOrigins\": [ \"https://192.168.68.63:40099\" ]\r\n}";
    var files = new Dictionary<GraniteAppKind, (string, byte[])>
    {
        [GraniteAppKind.WebDesktop] = ("wd.json", enc.GetBytes(wd)),
        [GraniteAppKind.ProcessApp] = ("pa.json", enc.GetBytes(pa)),
        [GraniteAppKind.BusinessApi] = ("api.json", enc.GetBytes(api)),
        [GraniteAppKind.Custodian] = ("cus.json", enc.GetBytes(cus))
    };
    var plan = AddressChange.Plan(files, "ULTRA", machine);
    string Out(GraniteAppKind k) => enc.GetString(plan.Edits.Single(e => e.App == k).Updated);
    Check(plan.OldHosts.SequenceEqual(new[] { "192.168.68.63" }), "old host found");
    Check(Out(GraniteAppKind.WebDesktop).Contains("\"https://ULTRA:40081/\"") && Out(GraniteAppKind.WebDesktop).Contains("\"https://ULTRA:40082/\"") && Out(GraniteAppKind.WebDesktop).Contains("\"URL_LabelPrint\": \"\""), "Web Desktop endpoints moved, empty one left");
    Check(Out(GraniteAppKind.ProcessApp).Contains("\"https://ULTRA:40081/\"") && Out(GraniteAppKind.ProcessApp).Contains("secret"), "Process App moved, rest untouched");
    var apiOrigins = Granite.Toolkit.Core.Json.JsonTextEditor.GetStringArray(plan.Edits.Single(e => e.App == GraniteAppKind.BusinessApi).Updated, "AllowedOrigins")!;
    Check(!apiOrigins.Any(o => o.Contains("192.168.68.63")), "stale IP origins removed from the API");
    Check(apiOrigins.Contains("https://ultra:40099") && apiOrigins.Contains("https://ultra:40080"), "new host allowed on every origin port");
    Check(apiOrigins.Contains("https://localhost:40099") && apiOrigins.Contains("https://192.168.68.70:40080"), "origins for addresses this machine still has are kept");
    Check(apiOrigins.Count == apiOrigins.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no duplicate origins");
    var cusOrigins = Granite.Toolkit.Core.Json.JsonTextEditor.GetStringArray(plan.Edits.Single(e => e.App == GraniteAppKind.Custodian).Updated, "AllowedOrigins")!;
    Check(cusOrigins.SequenceEqual(new[] { "https://ultra:40099" }), "Custodian origin moved");
    Check(plan.Changes.Count(c => c.Setting != AddressChange.OriginsKey) == 3, "three endpoint changes listed");

    // Moving to an address the machine still has keeps the old one's origins.
    var keep = AddressChange.Plan(files, "ULTRA", machine with { DhcpIPv4 = new[] { "192.168.68.63" } });
    var keptOrigins = Granite.Toolkit.Core.Json.JsonTextEditor.GetStringArray(keep.Edits.Single(e => e.App == GraniteAppKind.BusinessApi).Updated, "AllowedOrigins")!;
    Check(keptOrigins.Contains("https://192.168.68.63:40099"), "origins kept while the old IP is still this machine's (scanners using it keep working)");

    // Already on the new host: nothing to do.
    var moved = plan.Edits.ToDictionary(e => e.App, e => (e.Path, e.Updated));
    var again = AddressChange.Plan(moved, "ULTRA", machine);
    Check(again.NothingToDo && again.Edits.Count == 0, "running it twice changes nothing the second time");

    // Shipped placeholders ("https::5001/") are left alone with a note.
    var shipped = AddressChange.Plan(new Dictionary<GraniteAppKind, (string, byte[])> { [GraniteAppKind.WebDesktop] = ("x", enc.GetBytes("{\"Business_API_Endpoint\": \"https::5001/\"}")) }, "ULTRA", machine);
    Check(shipped.NothingToDo && shipped.Notes.Any(n => n.Contains("isn't a URL")), "unconfigured release values left alone, with a note");
    bool threwLocal = false;
    try { AddressChange.Plan(files, "localhost", machine); } catch (ArgumentException) { threwLocal = true; }
    Check(threwLocal, "localhost refused as a new address");
}

// ---- Origins and site bindings ----------------------------------------------------
{
    Check(GraniteAddress.NormalizeOrigin(" https://Ultra:40099/ ") == "https://ultra:40099", "origin: lower case, no trailing slash");
    Check(GraniteAddress.NormalizeOrigin("https://wms.Client.com:443") == "https://wms.client.com", "origin: default port dropped, as browsers send it");
    Check(GraniteAddress.NormalizeOrigin("not a url") == "not a url", "origin: non-URL left as it is");
    var machine = new MachineAddresses(new[] { "ULTRA" }, Array.Empty<string>(), new[] { "192.168.68.61" });
    var mixed = AddressChange.Plan(new Dictionary<GraniteAppKind, (string, byte[])> { [GraniteAppKind.BusinessApi] = ("x", System.Text.Encoding.UTF8.GetBytes("{\"AllowedOrigins\": [ \"https://Ultra:40099\" ]}")) }, "ultra", machine);
    Check(Granite.Toolkit.Core.Json.JsonTextEditor.GetStringArray(mixed.Edits.Single().Updated, "AllowedOrigins")!.SequenceEqual(new[] { "https://ultra:40099" }), "mixed-case origin lower-cased");
    var bindings = new[] { new IisBinding("https", "192.168.68.63", 40099, ""), new IisBinding("http", "*", 80, "wms") };
    Check(SiteBindings.AllAddressesBindingList(bindings) == "https/*:40099:,http/*:80:wms", "unpinned binding list keeps every binding");
    Check(IisCommands.IpPort("192.168.68.63", 40099) == "192.168.68.63:40099" && IisCommands.IpPort("*", 40099) == "0.0.0.0:40099" && IisCommands.IpPort("fe80::1", 1) == "[fe80::1]:1", "http.sys ip:port keys");
    Check(IisCommands.BindingList(bindings) == "https/192.168.68.63:40099:,http/*:80:wms", "binding list as it is, for undo");
    Check(SiteBindings.IsAllAddresses(new IisBinding("https", "0.0.0.0", 1, "")) && SiteBindings.IsAllAddresses(new IisBinding("https", "", 1, "")), "all-addresses forms");
}

// ---- CertificateCoverage ------------------------------------------------------------
{
    Check(CertificateCoverage.Covers(new[] { "ULTRA", "192.168.68.63" }, "ultra"), "exact name, any case");
    Check(!CertificateCoverage.Covers(new[] { "ULTRA", "192.168.68.63" }, "192.168.68.70"), "new IP not covered");
    Check(CertificateCoverage.Covers(new[] { "*.client.com" }, "wms.client.com") && !CertificateCoverage.Covers(new[] { "*.client.com" }, "a.wms.client.com") && !CertificateCoverage.Covers(new[] { "*.client.com" }, "client.com"), "wildcard one level only");
    string netshEn = "SSL Certificate bindings:\r\n    IP:port                      : 0.0.0.0:40081\r\n    Certificate Hash             : 3f2a9c1b8e7d6c5b4a3928170615f4e3d2c1b0a9\r\n    Application ID               : {5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61}\r\n";
    string netshDe = netshEn.Replace("Certificate Hash", "Zertifikathash").Replace("Application ID", "Anwendungs-ID");
    Check(CertificateCoverage.ThumbprintFromNetsh(netshEn) == "3F2A9C1B8E7D6C5B4A3928170615F4E3D2C1B0A9", "thumbprint from netsh");
    Check(CertificateCoverage.ThumbprintFromNetsh(netshDe) == "3F2A9C1B8E7D6C5B4A3928170615F4E3D2C1B0A9", "thumbprint from translated netsh");
    Check(CertificateCoverage.ThumbprintFromNetsh("The system cannot find the file specified.") is null, "no binding: null");
    var machine = new MachineAddresses(new[] { "ULTRA" }, Array.Empty<string>(), new[] { "192.168.68.70" });
    var (dns, ips) = CertificateCoverage.NamesForReissue(new[] { "ULTRA", "localhost", "192.168.68.63" }, "wms.home.lan", machine);
    Check(dns[0] == "wms.home.lan" && dns.Contains("ULTRA") && dns.Contains("localhost"), "reissue: new name first, old names kept");
    Check(!ips.Contains("192.168.68.63") && ips.Contains("192.168.68.70"), "reissue: stale IP dropped, current IP added");
}

Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
