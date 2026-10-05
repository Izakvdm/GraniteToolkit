using System.IO.Compression;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using GraniteNiFiDeploy.Core;

int passed = 0, failed = 0;
void Check(bool ok, string what)
{
    if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + what); }
}
bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; } catch (T) { return true; }
}

if (args.Length >= 4 && args[0] == "live")
    return await LiveTest.RunAsync(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 8443);

// ---- MediaCatalog: names -------------------------------------------------------
MediaKind? K(string n) => MediaCatalog.Classify(n)?.Kind;
string? V(string n) => MediaCatalog.Classify(n)?.Version;
Check(K("nifi-2.11.0-bin.zip") == MediaKind.NiFi && V("nifi-2.11.0-bin.zip") == "2.11.0", "NiFi zip");
Check(K("nifi-toolkit-2.11.0-bin.zip") is null, "nifi-toolkit isn't NiFi");
Check(K("nifi-2.11.0-source-release.zip") is null, "NiFi source zip isn't NiFi");
Check(K("jdk-26_windows-x64_bin.zip") == MediaKind.Jdk && V("jdk-26_windows-x64_bin.zip") == "26", "Oracle JDK zip");
Check(K("OpenJDK21U-jdk_x64_windows_hotspot_21.0.5_11.zip") == MediaKind.Jdk && V("OpenJDK21U-jdk_x64_windows_hotspot_21.0.5_11.zip") == "21", "Temurin JDK zip");
Check(K("microsoft-jdk-21.0.5-windows-x64.zip") == MediaKind.Jdk && V("microsoft-jdk-21.0.5-windows-x64.zip") == "21", "Microsoft JDK zip");
Check(K("nssm-2.24-101-g897c7ad.zip") == MediaKind.Nssm && V("nssm-2.24-101-g897c7ad.zip") == "2.24", "NSSM prerelease zip");
Check(K("nssm-2.24.zip") == MediaKind.Nssm, "NSSM zip");
Check(K("sqljdbc_13.4.0.0_enu.zip") == MediaKind.JdbcDriver && V("sqljdbc_13.4.0.0_enu.zip") == "13.4.0.0", "JDBC zip");
Check(K("mssql-jdbc-13.4.0.jre11.jar") == MediaKind.JdbcDriver, "bare JDBC jar");
Check(K("MasterDataUpwards.zip") is null && K("readme.txt") is null, "other files ignored");
Check(K(@"C:\media\nifi-2.11.0-bin.zip") == MediaKind.NiFi && K("Nifi/nifi-2.11.0-bin.zip") == MediaKind.NiFi, "paths and zip entry names");

var newest = MediaCatalog.PickNewest(new[] { "nifi-2.6.0-bin.zip", "nifi-2.11.0-bin.zip", "nifi-2.9.0-bin.zip", "jdk-21_windows-x64_bin.zip" });
Check(newest[MediaKind.NiFi].Version == "2.11.0", "newest NiFi picked numerically (2.11 > 2.9)");
Check(MediaCatalog.Missing(newest).SequenceEqual(new[] { MediaKind.Nssm, MediaKind.JdbcDriver }), "missing kinds listed");
Check(MediaCatalog.CompareVersions("13.4.0", "9.2") > 0 && MediaCatalog.CompareVersions(null, "1") < 0, "version compare");

// ---- MediaCatalog: inside the zips (built here) ---------------------------------
string tmp = Path.Combine(Path.GetTempPath(), "nifideploy-harness-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
string Zip(string name, params (string Entry, string Text)[] entries)
{
    string path = Path.Combine(tmp, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entry, text) in entries)
    {
        using var w = new StreamWriter(zip.CreateEntry(entry).Open());
        w.Write(text);
    }
    return path;
}
try
{
    Zip("nifi-2.11.0-bin.zip", ("nifi-2.11.0/bin/nifi.cmd", "x"), ("nifi-2.11.0/conf/nifi.properties", "x"));
    Zip("jdk-21_windows-x64_bin.zip", ("jdk-21.0.5/release", "IMPLEMENTOR=\"Oracle\"\nJAVA_VERSION=\"21.0.5\"\n"), ("jdk-21.0.5/bin/java.exe", "x"));
    Zip("nssm-2.24.zip", ("nssm-2.24/win32/nssm.exe", "x"), ("nssm-2.24/win64/nssm.exe", "x"));
    Zip("sqljdbc_13.4.0.0_enu.zip", ("sqljdbc_13.4/enu/jars/mssql-jdbc-13.4.0.jre8.jar", "x"), ("sqljdbc_13.4/enu/jars/mssql-jdbc-13.4.0.jre11.jar", "x"),
        ("sqljdbc_13.4/enu/jars/mssql-jdbc-13.4.0.jre11-sources.jar", "x"));
    var set = MediaCatalog.Inspect(MediaCatalog.PickNewest(Directory.GetFiles(tmp)));
    Check(set.NiFiFolderName == "nifi-2.11.0" && set.NiFiVersion == "2.11.0", "NiFi top folder read from the zip");
    Check(set.JavaMajor == 21 && set.JavaVersion == "21.0.5" && set.JavaIsLts, "Java version read from the release file");
    Check(set.NssmEntry == "nssm-2.24/win64/nssm.exe", "64-bit NSSM chosen");
    Check(set.JdbcJarName == "mssql-jdbc-13.4.0.jre11.jar", "jre11 driver chosen, not jre8 or sources");

    // Linux JDK (no java.exe) refused
    File.Delete(Path.Combine(tmp, "jdk-21_windows-x64_bin.zip"));
    Zip("jdk-21_linux-x64_bin.zip", ("jdk-21.0.5/release", "JAVA_VERSION=\"21.0.5\""), ("jdk-21.0.5/bin/java", "x"));
    Check(Throws<InvalidDataException>(() => MediaCatalog.Inspect(MediaCatalog.PickNewest(Directory.GetFiles(tmp)))), "JDK without java.exe refused");
    File.Delete(Path.Combine(tmp, "jdk-21_linux-x64_bin.zip"));

    // Java 17 refused
    Zip("jdk-17_windows-x64_bin.zip", ("jdk-17.0.9/release", "JAVA_VERSION=\"17.0.9\""), ("jdk-17.0.9/bin/java.exe", "x"));
    string? why = null;
    try { MediaCatalog.Inspect(MediaCatalog.PickNewest(Directory.GetFiles(tmp))); } catch (InvalidDataException ex) { why = ex.Message; }
    Check(why is not null && why.Contains("too old"), "Java 17 refused as too old");
    File.Delete(Path.Combine(tmp, "jdk-17_windows-x64_bin.zip"));

    // NiFi 1.x refused
    Zip("jdk-25_windows-x64_bin.zip", ("jdk-25/release", "JAVA_VERSION=\"25\""), ("jdk-25/bin/java.exe", "x"));
    File.Delete(Path.Combine(tmp, "nifi-2.11.0-bin.zip"));
    Zip("nifi-1.28.1-bin.zip", ("nifi-1.28.1/bin/nifi.cmd", "x"));
    Check(Throws<InvalidDataException>(() => MediaCatalog.Inspect(MediaCatalog.PickNewest(Directory.GetFiles(tmp)))), "NiFi 1.x refused");

    // Nothing found: the message names what's missing
    why = null;
    try { MediaCatalog.Inspect(MediaCatalog.PickNewest(Array.Empty<string>())); } catch (InvalidDataException ex) { why = ex.Message; }
    Check(why is not null && why.Contains("NSSM") && why.Contains("JDBC"), "missing downloads named");
}
finally
{
    Directory.Delete(tmp, recursive: true);
}

Check(MediaCatalog.NiFiTopFolder(new[] { "nifi-2.11.0/", "nifi-2.11.0/bin/nifi.cmd" }) == "nifi-2.11.0", "top folder");
Check(MediaCatalog.NiFiTopFolder(new[] { "x/nifi-2.11.0/bin/nifi.cmd" }) is null, "nifi.cmd one level too deep isn't accepted");
Check(MediaCatalog.NiFiTopFolder(new[] { "../bin/nifi.cmd" }) is null && MediaCatalog.NiFiTopFolder(new[] { "../../x/bin/nifi.cmd" }) is null, "'..' top folder refused");
Check(MediaCatalog.ParseJavaVersion("JAVA_VERSION=\"26.0.2.1\"") == "26.0.2.1", "release file version");
Check(MediaCatalog.JavaMajor("1.8.0_402") == 8 && MediaCatalog.JavaMajor("21.0.5") == 21 && MediaCatalog.JavaMajor("25") == 25, "Java major");
Check(MediaCatalog.IsLtsJava(21) && MediaCatalog.IsLtsJava(25) && MediaCatalog.IsLtsJava(29) && !MediaCatalog.IsLtsJava(26) && !MediaCatalog.IsLtsJava(22), "LTS releases");
Check(MediaCatalog.PickJdbcJar(new[] { "a/mssql-jdbc-13.4.0.jre8.jar", "a/mssql-jdbc-13.4.0.jre11.jar", "a/mssql-jdbc-13.4.0.jre21.jar" }, 21) == "mssql-jdbc-13.4.0.jre21.jar", "jre21 driver preferred on Java 21");
Check(MediaCatalog.PickJdbcJar(new[] { "mssql-jdbc-13.4.0.jre21.jar" }, 17) is null, "no driver newer than the Java");

// ---- NiFiConfigFiles ---------------------------------------------------------------
const string cmd = "set RUN_COMMAND=\"%~1\"\r\n" +
    "if %RUN_COMMAND% == \"set-single-user-credentials\" (\r\n" +
    "  call \"%JAVA_EXE%\" %JAVA_PARAMS% x\r\n" +
    ") else if %RUN_COMMAND% == \"start\" (\r\n" +
    "  rem Start bootstrap process in new minimized window\r\n" +
    "  call start /MIN \"Apache NiFi\" \"%JAVA_EXE%\" %JAVA_MEMORY% %JAVA_PARAMS% org.apache.nifi.bootstrap.BootstrapProcess %RUN_COMMAND%\r\n" +
    ") else (\r\n" +
    "  call \"%JAVA_EXE%\" %JAVA_MEMORY% %JAVA_PARAMS% org.apache.nifi.bootstrap.BootstrapProcess %RUN_COMMAND%\r\n" +
    ")\r\n";
var (patched, result) = NiFiConfigFiles.PatchNiFiCmd(cmd);
Check(result == NiFiConfigFiles.CmdPatch.Patched && !patched.Contains("/MIN") && patched.Contains("  call \"%JAVA_EXE%\" %JAVA_MEMORY% %JAVA_PARAMS% org.apache.nifi.bootstrap.BootstrapProcess %RUN_COMMAND%\r\n) else ("), "nifi.cmd: start /MIN removed, Java called directly");
Check(NiFiConfigFiles.PatchNiFiCmd(patched).Result == NiFiConfigFiles.CmdPatch.AlreadyPatched, "nifi.cmd: second patch recognised as done");
Check(NiFiConfigFiles.PatchNiFiCmd("echo hello").Result == NiFiConfigFiles.CmdPatch.NotFound, "nifi.cmd: unknown layout not found");
Check(patched.Replace("call \"%JAVA_EXE%\" %JAVA_MEMORY%", "").Length == cmd.Replace("call start /MIN \"Apache NiFi\" \"%JAVA_EXE%\" %JAVA_MEMORY%", "").Replace("call \"%JAVA_EXE%\" %JAVA_MEMORY%", "").Length, "nifi.cmd: nothing else changed");

const string props = "# web\r\nnifi.web.https.host=127.0.0.1\r\nnifi.web.https.port=8443\r\nnifi.web.https.port.forwarding=\r\n";
string props2 = NiFiConfigFiles.SetProperty(props, "nifi.web.https.port", "9443");
Check(props2 == props.Replace("port=8443", "port=9443"), "property set, CRLF kept, the .forwarding line left alone");
Check(NiFiConfigFiles.GetProperty(props2, "nifi.web.https.port") == "9443" && NiFiConfigFiles.GetProperty(props2, "nifi.web.https.host") == "127.0.0.1", "property read");
Check(Throws<InvalidDataException>(() => NiFiConfigFiles.SetProperty(props, "nifi.missing", "x")), "missing property throws");
Check(Throws<ArgumentException>(() => NiFiConfigFiles.SetProperty(props, "nifi.web.https.port", "1\nnifi.evil=1")), "line break in a value refused");

const string boot = "java.arg.1=-Dorg.apache.jasper.compiler.disablejsr199=true\njava.arg.2=-Xms1g\njava.arg.3=-Xmx1g\n";
string boot2 = NiFiConfigFiles.SetHeap(boot, "2g");
Check(boot2.Contains("java.arg.2=-Xms2g\n") && boot2.Contains("java.arg.3=-Xmx2g\n"), "heap set");
Check(Throws<ArgumentException>(() => NiFiConfigFiles.SetHeap(boot, "lots")), "bad heap refused");

var credArgs = NiFiConfigFiles.CredentialsArguments(Path.Combine("C:", "nifi", "nifi-2.11.0"), "graniteadmin", "p@ss \"word\" & more", ';');
Check(credArgs.Contains("org.apache.nifi.authentication.single.user.command.SetSingleUserCredentials") && credArgs[^2] == "graniteadmin" && credArgs[^1] == "p@ss \"word\" & more",
    "credentials: user and password are separate arguments, passed through untouched");
Check(credArgs[1].EndsWith(Path.Combine("conf")) && credArgs[1].Contains(';'), "credentials: classpath is bootstrap lib + conf");

var nssm = NiFiConfigFiles.NssmInstallCommands("NiFi", Path.Combine("C:", "nifi", "nifi-2.11.0"), "2.11.0");
Check(nssm[0].SequenceEqual(new[] { "install", "NiFi", Path.Combine("C:", "nifi", "nifi-2.11.0", "bin", "nifi.cmd"), "start" }), "nssm install: nifi.cmd start");
Check(nssm.Any(a => a.SequenceEqual(new[] { "set", "NiFi", "Start", "SERVICE_AUTO_START" })), "nssm: automatic start");
Check(nssm.Any(a => a[2] == "AppDirectory" && a[3].EndsWith("bin")), "nssm: runs in bin");
Check(NiFiConfigFiles.NiFiEnvCmd.Contains("set JAVA_HOME=%NIFI_HOME%\\jdk\r\n") && !NiFiConfigFiles.NiFiEnvCmd.Contains("\n\n"), "nifi-env.cmd: bundled JDK, CRLF");
{
    // NSSM doesn't create the folder for AppStdout/AppStderr and the NiFi zip has no logs folder:
    // every such folder must be created before the service starts (first Windows run, 2026-10-04).
    string home = Path.Combine("C:", "nifi", "nifi-2.11.0");
    var before = NiFiConfigFiles.FoldersBeforeService(home);
    var outputFolders = NiFiConfigFiles.NssmInstallCommands("NiFi", home, "2.11.0")
        .Where(a => a.Length >= 4 && a[0] == "set" && (a[2] == "AppStdout" || a[2] == "AppStderr"))
        .Select(a => Path.GetDirectoryName(a[3])!).Distinct().ToList();
    Check(outputFolders.Count > 0 && outputFolders.All(f => before.Contains(f)), "nssm: service.log folder is created before the service starts");
}
{
    string sample = "Event[0]:\r\n  Log Name: Application\r\n  Source: nssm\r\n  Event ID: 1040\r\n  Level: Error\r\n  Description: \r\nFailed to open output file\r\n C:\\nifi\\nifi-2.11.0\\logs\\service.log for writing.\r\n\r\nEvent[1]:\r\n  Source: nssm\r\n  Description: Service NiFi ran for less than 1500 milliseconds.\r\n";
    var d = NiFiServiceInfo.ParseEventDescriptions(sample);
    Check(d.Count == 2 && d[0] == "Failed to open output file C:\\nifi\\nifi-2.11.0\\logs\\service.log for writing." && d[1].StartsWith("Service NiFi ran"), "wevtutil text: event descriptions parsed");
    Check(NiFiServiceInfo.ParseEventDescriptions("").Count == 0, "wevtutil text: nothing -> empty");
}

{
    // NiFi shows every set sensitive property as "********"; the verification request must
    // carry the flow's parameter reference instead (first Windows run, 2026-10-04).
    var flowPool = DeployScripts.FlowServiceProperties(DeployScripts.ReadFlow(), ".DBCPConnectionPool");
    Check(flowPool.TryGetValue("Password", out var pw) && pw == "#{granite.db.password}", "flow: pool password is the parameter reference");
    Check(flowPool.TryGetValue("Database User", out var du) && du == "#{granite.db.user}", "flow: pool user is the parameter reference");
    var component = JsonNode.Parse("""
        { "properties": { "Database User": "#{granite.db.user}", "Password": "********", "Other Secret": "********", "Max Wait Time": "500 millis" },
          "descriptors": { "Password": { "sensitive": true }, "Other Secret": { "sensitive": true }, "Database User": { "sensitive": false } } }
        """)!.AsObject();
    var sent = NiFiApiClient.PropertiesForVerification(component, flowPool);
    Check(sent["Password"]!.GetValue<string>() == "#{granite.db.password}", "verification: masked password replaced by the reference");
    Check(sent["Database User"]!.GetValue<string>() == "#{granite.db.user}" && sent["Max Wait Time"]!.GetValue<string>() == "500 millis", "verification: other properties unchanged");
    Check(sent["Other Secret"]!.GetValue<string>() == NiFiApiClient.SensitiveMask, "verification: masked value with nothing in the flow left alone");
    var notSensitive = JsonNode.Parse("""{ "properties": { "Password": "********" }, "descriptors": { "Password": { "sensitive": false } } }""")!.AsObject();
    Check(NiFiApiClient.PropertiesForVerification(notSensitive, flowPool)["Password"]!.GetValue<string>() == "********", "verification: only sensitive properties are swapped");
}

// ---- InputRules ------------------------------------------------------------------
Check(InputRules.IsValidNiFiPassword("abcdefghijkl", "abcdefghijkl", out _), "12-char password OK");
Check(!InputRules.IsValidNiFiPassword("abcdefghijk", "abcdefghijk", out _), "11-char password refused");
Check(!InputRules.IsValidNiFiPassword("abcdefghijkl", "abcdefghijkx", out _), "mismatch refused");
Check(!InputRules.IsValidNiFiPassword(" abcdefghijkl", " abcdefghijkl", out _), "leading space refused");
Check(InputRules.IsValidNiFiPassword("a\"b%c^d&e|f<g>h!", "a\"b%c^d&e|f<g>h!", out _), "special characters allowed (no shell involved)");
Check(InputRules.IsValidNiFiUser("graniteadmin", out _) && !InputRules.IsValidNiFiUser("a b", out _) && !InputRules.IsValidNiFiUser("ab", out _), "user names");
Check(InputRules.IsValidServiceName("NiFi", out _) && InputRules.IsValidServiceName("NiFi-Test_2", out _) && !InputRules.IsValidServiceName("Ni Fi", out _), "service names");
Check(InputRules.IsValidPort(8443, out _) && !InputRules.IsValidPort(80, out _) && !InputRules.IsValidPort(70000, out _), "ports");
Check(InputRules.IsValidHeap("1g", out _) && InputRules.IsValidHeap("1536m", out _) && !InputRules.IsValidHeap("256m", out _) && !InputRules.IsValidHeap("1gb", out _), "heap sizes");
Check(InputRules.IsValidLocalFolder(@"C:\nifi", "x", out _) && InputRules.IsValidLocalFolder(@"D:\Apps\NiFi", "x", out _), "local folders OK");
Check(!InputRules.IsValidLocalFolder(@"\\server\share\nifi", "x", out _), "share refused");
Check(!InputRules.IsValidLocalFolder(@"C:\", "x", out _) && !InputRules.IsValidLocalFolder("nifi", "x", out _), "drive root and relative refused");
Check(!InputRules.IsValidLocalFolder(@"C:\Windows\nifi", "x", out _) && !InputRules.IsValidLocalFolder(@"C:\Program Files\NiFi", "x", out _) && !InputRules.IsValidLocalFolder(@"C:\ProgramData", "x", out _), "system folders refused");
Check(!InputRules.IsValidLocalFolder(@"C:\data\..\Windows", "x", out _) && !InputRules.IsValidLocalFolder(@"C:\data\.", "x", out _), "dot segments refused");
Check(InputRules.Overlap(@"C:\nifi", @"C:\nifi\import") && InputRules.Overlap(@"C:\NiFi\", @"c:\nifi") && !InputRules.Overlap(@"C:\nifi", @"C:\nifi2"), "folder overlap");
Check(InputRules.IsValidSqlLogin("svc_granite_nifi", out _) && !InputRules.IsValidSqlLogin("1abc", out _) && !InputRules.IsValidSqlLogin("a]b", out _), "SQL login names");
var generated = Enumerable.Range(0, 50).Select(_ => InputRules.GeneratePassword()).ToList();
Check(generated.All(p => p.Length == 32 && p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c))), "generated passwords: 32 chars, every class");
Check(generated.Distinct().Count() == 50, "generated passwords differ");
Check(generated.All(p => p.IndexOfAny(new[] { ';', '\'', '"', '{', '}', ' ', '&', '\\' }) < 0), "generated passwords need no escaping");

// ---- JdbcUrl ---------------------------------------------------------------------------
Check(JdbcUrl.Build("SQL01", "GraniteLive", false) == "jdbc:sqlserver://SQL01;databaseName=GraniteLive;encrypt=true;trustServerCertificate=true", "plain server");
Check(JdbcUrl.Build(@"SQL01\SQLEXPRESS", "G", false) == "jdbc:sqlserver://SQL01;instanceName=SQLEXPRESS;databaseName=G;encrypt=true;trustServerCertificate=true", "named instance");
Check(JdbcUrl.Build("SQL01,1500", "G", true) == "jdbc:sqlserver://SQL01:1500;databaseName=G;encrypt=true;trustServerCertificate=false", "port, validated cert");
Check(JdbcUrl.Build(@"tcp:SQL01\INST,1500", "G", false).StartsWith("jdbc:sqlserver://SQL01:1500;databaseName="), "tcp: prefix, port wins over instance");
Check(JdbcUrl.Build(".", "G", false).StartsWith("jdbc:sqlserver://localhost;") && JdbcUrl.Build(@"(local)\SQLEXPRESS", "G", false).StartsWith("jdbc:sqlserver://localhost;instanceName=SQLEXPRESS;"), "local names");
Check(Throws<ArgumentException>(() => JdbcUrl.Build("(localdb)\\MSSQLLocalDB", "G", false)), "LocalDB refused");
Check(Throws<ArgumentException>(() => JdbcUrl.Build("np:SQL01", "G", false)), "named pipes refused");
Check(Throws<ArgumentException>(() => JdbcUrl.Build("SQL01", "G;user=sa", false)), "; in database refused");
Check(Throws<ArgumentException>(() => JdbcUrl.Build("SQL01;x=1", "G", false)), "; in server refused");
Check(Throws<ArgumentException>(() => JdbcUrl.Build("SQL01,99999", "G", false)), "bad port refused");

// ---- DeployScripts ---------------------------------------------------------------------
Check(DeployScripts.ScriptsFor(new[] { "SalesOrder", "MasterItem" }).SequenceEqual(new[] { "01_Framework.sql", "02_Feed_MasterItem.sql", "04_Feed_SalesOrder.sql" }), "framework first, feeds in order");
foreach (var feed in Feeds.All)
{
    string text = DeployScripts.ReadSql(feed.Script);
    Check(text.Contains("CREATE OR ALTER PROCEDURE dbo." + feed.ImportProc) && text.Contains("dbo." + feed.StagingTable), $"{feed.Script}: defines {feed.ImportProc} and {feed.StagingTable}");
    Check(!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?im)^\s*USE\s"), $"{feed.Script}: no USE statement");
}
string framework = DeployScripts.ReadSql(DeployScripts.FrameworkScript);
var batches = DeployScripts.SplitBatches(framework);
Check(batches.Count >= 6 && batches.All(b => !System.Text.RegularExpressions.Regex.IsMatch(b, @"(?im)^\s*GO\s*$")), "framework splits on GO lines only");
Check(batches.Any(b => b.Contains("CREATE OR ALTER PROCEDURE dbo.Custom_NiFi_RunImport")), "framework has RunImport");
Check(DeployScripts.SplitBatches("SELECT 1\nGO\n\nGO\nSELECT 'GO'\n go \n").Count == 2, "empty batches dropped, GO inside a line kept");

string so = DeployScripts.ReadSql("04_Feed_SalesOrder.sql");
string so2 = DeployScripts.ApplyOrderDefaults(so, new OrderDefaults("SO", "OPEN", "JHB'1", "CUSTOMER"));
Check(so2.Contains("= 'SO'") && so2.Contains("= 'OPEN'") && so2.Contains("= 'JHB''1'") && so2.Contains("= 'CUSTOMER'"), "order defaults written, quote escaped");
Check(!so2.Contains("@DocumentType          varchar(30) = 'ORDER'"), "old default replaced");
Check(so2.Length - so.Length == ("'SO'".Length + "'OPEN'".Length + "'JHB''1'".Length + "'CUSTOMER'".Length) - ("'ORDER'".Length + "'ENTERED'".Length + "''".Length + "'ORDER'".Length), "nothing else in the script changed");
Check(Throws<ArgumentException>(() => DeployScripts.ApplyOrderDefaults(so, new OrderDefaults(new string('x', 31), "E", "", "O"))), "over-long default refused");
Check(Throws<InvalidDataException>(() => DeployScripts.ApplyOrderDefaults("no params here", OrderDefaults.ForSalesOrder)), "script without the parameter lines fails loudly");
Check(DeployScripts.FeedScript(Feeds.Get("PurchaseOrder"), new OrderDefaults("REC", "ENTERED", "", "SUPP")).Contains("= 'REC'"), "PO defaults applied");
Check(DeployScripts.FeedScript(Feeds.Get("MasterItem"), null) == DeployScripts.ReadSql("02_Feed_MasterItem.sql"), "non-order feed untouched");
{
    // Temp tables take tempdb's collation (the server default), Granite tables the database's.
    // Ultra: server SQL_Latin1_General_CP1_CI_AS, GraniteDatabase Latin1_General_CI_AS -> error 468
    // in Custom_ImportMasterItem (2026-10-05). Every text column of a #table must say COLLATE DATABASE_DEFAULT.
    var textColumn = new System.Text.RegularExpressions.Regex(@"\b(?:n?varchar|n?char)\s*\(\s*(?:\d+|max)\s*\)(?!\s+COLLATE\s+DATABASE_DEFAULT)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var tempTable = new System.Text.RegularExpressions.Regex(@"CREATE TABLE #\w+\s*\((.*?)\n\s*\);", System.Text.RegularExpressions.RegexOptions.Singleline);
    int tables = 0;
    var missing = new List<string>();
    foreach (var feed in Feeds.All)
    {
        string text = DeployScripts.ReadSql(feed.Script).Replace("\r\n", "\n");
        foreach (System.Text.RegularExpressions.Match m in tempTable.Matches(text))
        {
            tables++;
            foreach (System.Text.RegularExpressions.Match c in textColumn.Matches(m.Groups[1].Value)) missing.Add($"{feed.Name}: {c.Value}");
        }
    }
    Check(tables >= 8 && missing.Count == 0, $"temp tables: every text column is COLLATE DATABASE_DEFAULT ({tables} tables{(missing.Count > 0 ? "; missing: " + string.Join(", ", missing) : "")})");
}

// The embedded flow matches what the module expects of it.
var flow = JsonNode.Parse(DeployScripts.ReadFlow())!;
var contexts = flow["parameterContexts"]!.AsObject();
var paramNames = contexts.SelectMany(c => c.Value!["parameters"]!.AsArray()).Select(p => p!["name"]!.GetValue<string>()).ToHashSet();
Check(contexts.Count == 1 && contexts.Single().Value!["name"]!.GetValue<string>() == NiFiFlowParameters.ContextName, "flow has the Granite CSV Import parameter context");
Check(paramNames.SetEquals(NiFiFlowParameters.AllNames), "flow parameters = the module's parameter names");
Check(contexts.Single().Value!["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == NiFiFlowParameters.DbPassword)!["sensitive"]!.GetValue<bool>(), "password parameter is sensitive");
Check(contexts.Single().Value!["parameters"]!.AsArray().All(p => p!["sensitive"]!.GetValue<bool>() == false || p!["value"] is null), "no sensitive value shipped in the flow");
Check(flow["flowContents"]!["name"]!.GetValue<string>() == NiFiFlowParameters.FlowGroupName, "flow group name");
Check(flow["flowContents"]!["processGroups"]!.AsArray().Any(g => g!["flowFileConcurrency"]!.GetValue<string>() == "SINGLE_FLOWFILE_PER_NODE"), "import group handles one file at a time");
var built = NiFiFlowParameters.Build("jdbc:x", "svc", "secret", "d", "i", "a", "e");
Check(built.Select(p => p.Name).ToHashSet().SetEquals(NiFiFlowParameters.AllNames) && built.Single(p => p.Sensitive).Name == NiFiFlowParameters.DbPassword, "parameter values cover every name; only the password is sensitive");

// ---- NiFiServiceInfo -------------------------------------------------------------------
Check(NiFiServiceInfo.HomeFromApplication(@"C:\nifi\nifi-2.11.0\bin\nifi.cmd") == @"C:\nifi\nifi-2.11.0", "NSSM application -> home");
Check(NiFiServiceInfo.HomeFromApplication("\"D:\\Apps\\NiFi\\bin\\nifi.cmd\"") == @"D:\Apps\NiFi", "quoted application");
Check(NiFiServiceInfo.HomeFromApplication(@"C:\Program Files\Something\app.exe") is null && NiFiServiceInfo.HomeFromApplication(null) is null, "other services ignored");
Check(NiFiServiceInfo.VersionFromHome(@"C:\nifi\nifi-2.11.0") == "2.11.0" && NiFiServiceInfo.VersionFromHome(@"D:\Apps\NiFi") is null, "version from folder name");
const string sc = "\r\nSERVICE_NAME: NiFi\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\r\n";
Check(NiFiServiceInfo.ParseScState(sc) == "RUNNING" && NiFiServiceInfo.ParseScState("nothing") is null, "sc query state");

// ---- Certificate pinning -----------------------------------------------------------------
using var key = RSA.Create(2048);
using var mine = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
using var key2 = RSA.Create(2048);
using var other = new CertificateRequest("CN=localhost", key2, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
string pin = mine.GetCertHashString(HashAlgorithmName.SHA256);
using var handler = NiFiApiClient.PinnedHandler(pin);
var callback = handler.ServerCertificateCustomValidationCallback!;
Check(callback(new HttpRequestMessage(), mine, null, SslPolicyErrors.RemoteCertificateChainErrors), "pinned self-signed certificate accepted");
Check(!callback(new HttpRequestMessage(), other, null, SslPolicyErrors.RemoteCertificateChainErrors), "any other certificate refused, even with the same name");
Check(!callback(new HttpRequestMessage(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable), "no certificate refused");
Check(!handler.UseProxy, "NiFi calls never go through a proxy");
Check(!handler.UseCookies, "no cookies: bearer token only (a cookie triggers NiFi's CSRF check and a 403)");

Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

/// <summary>
/// Optional round trip against a running NiFi 2.x: sign in (pinned to its
/// own keystore certificate), upload the embedded flow under a test name,
/// set parameters (the SQL connection will fail: nothing listens on the
/// test port), have NiFi verify the pool, enable, start, check, then stop,
/// disable and delete the test group.
/// </summary>
static class LiveTest
{
    public static async Task<int> RunAsync(string nifiHome, string user, string password, int port)
    {
        void Log(LogEntry e) => Console.WriteLine($"{e.Prefix} {e.Message}");
        try
        {
            string thumb = NiFiApiClient.KeystoreThumbprint(nifiHome);
            Console.WriteLine("Keystore certificate SHA-256: " + thumb);
            using var api = new NiFiApiClient(new Uri($"https://localhost:{port}/"), NiFiApiClient.PinnedHandler(thumb), Log);
            await api.LoginAsync(user, password, TimeSpan.FromMinutes(3), CancellationToken.None);
            Console.WriteLine("Signed in");
            string root = await api.RootGroupIdAsync(CancellationToken.None);
            string name = "Harness " + DateTime.Now.ToString("HHmmss");
            var before = await api.ParameterContextIdsAsync(CancellationToken.None);
            var (group, ctx) = await api.UploadFlowAsync(root, name, DeployScripts.ReadFlow(), CancellationToken.None);
            Console.WriteLine($"Uploaded group {group}, parameter context {ctx}");
            if (ctx is null || before.Contains(ctx))
            {
                // This NiFi already has a "Granite CSV Import" context (a real one, perhaps):
                // never overwrite its settings from a test. Remove the test group and stop.
                await api.DeleteGroupAsync(group, CancellationToken.None);
                Console.WriteLine("This NiFi already has the Granite CSV Import parameter context, so the live test stops here rather than change it. Run it against a NiFi without the Granite flow.");
                return 3;
            }
            Console.WriteLine("Found by name: " + (await api.FindChildGroupAsync(root, name, CancellationToken.None) == group));
            string temp = Path.Combine(Path.GetTempPath(), "nifideploy-live");
            foreach (string d in new[] { "Inbound", "Archive", "Error" }) Directory.CreateDirectory(Path.Combine(temp, d));
            await api.UpdateParametersAsync(ctx!, NiFiFlowParameters.Build(
                "jdbc:sqlserver://127.0.0.1:1;databaseName=GraniteTest;encrypt=true;trustServerCertificate=true;loginTimeout=2",
                "svc_granite_nifi", InputRules.GeneratePassword(), Path.Combine(nifiHome, "..", "drivers"),
                Path.Combine(temp, "Inbound"), Path.Combine(temp, "Archive"), Path.Combine(temp, "Error"), "2 sec"), CancellationToken.None);
            Console.WriteLine("Parameters set");
            var services = await api.ControllerServicesAsync(group, CancellationToken.None);
            foreach (var s in services) Console.WriteLine($"  service {s.Name} {s.State} {string.Join(" ", s.Errors)}");
            var pool = services.First(s => s.Type.EndsWith(".DBCPConnectionPool"));
            foreach (var r in await api.VerifyControllerServiceAsync(pool.Id, DeployScripts.FlowServiceProperties(DeployScripts.ReadFlow(), ".DBCPConnectionPool"), CancellationToken.None))
                Console.WriteLine($"  verify: {r.Step}: {r.Outcome} {r.Explanation}");
            await api.EnableControllerServicesAsync(group, CancellationToken.None);
            Console.WriteLine("Services enabled");
            await api.StartGroupAsync(group, CancellationToken.None);
            await Task.Delay(3000);
            var problems = await api.ProblemsAsync(group, CancellationToken.None);
            Console.WriteLine($"Problems after start: {problems.Count}");
            foreach (var p in problems) Console.WriteLine($"  {p.Group}/{p.Name}: {p.State} {p.Detail}");
            await api.StopGroupAsync(group, CancellationToken.None);
            await api.DisableControllerServicesAsync(group, CancellationToken.None);
            await Task.Delay(2000);
            await api.DeleteGroupAsync(group, CancellationToken.None);
            Console.WriteLine("Stopped, disabled and deleted. LIVE OK");
            return problems.Count == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("LIVE FAILED: " + ex);
            return 1;
        }
    }
}
