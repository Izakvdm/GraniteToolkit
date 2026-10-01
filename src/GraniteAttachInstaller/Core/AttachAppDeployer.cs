using System.Security.Cryptography;
using System.Text.Json;
using GraniteAttachInstaller.Models;

namespace GraniteAttachInstaller.Core;

/// <summary>
/// The app/IIS stage: publish, write appsettings.Production.json (keeping
/// existing secrets across a reinstall), create or update the IIS app pool
/// and site, grant the pool identity write access to the app folder, open
/// the firewall, and start it.
/// </summary>
public sealed class AttachAppDeployer
{
    private readonly Action<LogEntry> _log;
    public AttachAppDeployer(Action<LogEntry> log) => _log = log;
    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    private static string NewRandomBase64Key(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>
    /// Stops the existing site and app pool (if this install has run before) before
    /// publishing, so the IIS worker process releases its lock on GraniteAttach.dll
    /// in the app's physical path. Without this, a reinstall's `dotnet publish` fails
    /// with "The process cannot access the file ... it is being used by another
    /// process" (IIS Worker Process) once MSBuild's copy retries run out - hit on a
    /// real reinstall Sept 29, 2026. SetUpIisAsync recreates and restarts the site
    /// unconditionally afterward, so leaving things stopped here is safe; a first
    /// install (no site/pool yet, or no IIS at all) is a no-op.
    /// </summary>
    public async Task StopExistingIisAsync(InstallContext c, CancellationToken token)
    {
        if (!IisCommands.AppCmdExists())
        {
            return;
        }

        (bool siteListOk, string siteListOut) = await RunAppCmdAsync(IisCommands.ListSites(), token);
        if (siteListOk && ParseSitesOrEmpty(siteListOut).Any(s => string.Equals(s.Name, c.SiteName, StringComparison.OrdinalIgnoreCase)))
        {
            Log(LogLevel.Detail, $"Stopping existing site '{c.SiteName}' before publishing, so it releases its file lock on the app folder...");
            await RunAppCmdAsync(IisCommands.StopSite(c.SiteName), token);
        }

        (bool poolListOk, string poolListOut) = await RunAppCmdAsync(IisCommands.ListAppPools(), token);
        if (poolListOk && ParseAppPoolNamesOrEmpty(poolListOut).Contains(c.AppPoolName, StringComparer.OrdinalIgnoreCase))
        {
            await RunAppCmdAsync(IisCommands.StopAppPool(c.AppPoolName), token);
        }

        // Stopping the pool/site isn't synchronous with the worker process actually
        // exiting - give it a moment to actually let go of the DLL.
        await Task.Delay(TimeSpan.FromSeconds(2), token);
    }

    public async Task PublishAsync(InstallContext c, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(c.ProjectPath) || !File.Exists(c.ProjectPath))
            throw new InvalidOperationException($"Can't find the project file at '{c.ProjectPath}'.");

        Directory.CreateDirectory(c.PhysicalPath);

        Log(LogLevel.Stage, $"Publishing (Release) to {c.PhysicalPath} ...");
        var args = new[] { "publish", c.ProjectPath, "-c", "Release", "-o", c.PhysicalPath };
        Log(LogLevel.Detail, ProcessRunner.Describe("dotnet", args));
        var result = await ProcessRunner.RunAsync("dotnet", args, TimeSpan.FromMinutes(5), token);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Log(LogLevel.Detail, line);
        if (!result.Succeeded())
            throw new InvalidOperationException($"dotnet publish failed (exit {result.ExitCode}) - see the log above.");

        // dotnet publish exits 0 regardless of WHICH project it published, so a
        // wrong ProjectPath (e.g. pointing at this installer's own .csproj instead
        // of app/GraniteAttach/GraniteAttach.csproj) looks like a success right up
        // until the site serves a directory listing instead of the app - as
        // happened on Sept 29, 2026. Fail loudly here instead.
        string expectedDll = Path.Combine(c.PhysicalPath, "GraniteAttach.dll");
        if (!File.Exists(expectedDll))
        {
            throw new InvalidOperationException(
                $"Publish reported success, but {expectedDll} was not produced. This almost " +
                $"always means the wrong project was published - check that the project path " +
                $"('{c.ProjectPath}') really points at app/GraniteAttach/GraniteAttach.csproj, " +
                $"not this installer's own project.");
        }
        Log(LogLevel.Success, "Published.");
    }

    /// <summary>Writes appsettings.Production.json, generating secrets on first install only (kept across a reinstall so existing encrypted attachments stay readable).</summary>
    public void WriteAppSettings(InstallContext c)
    {
        string configPath = Path.Combine(c.PhysicalPath, "appsettings.Production.json");
        string? existingKey = null, existingSecret = null;
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("GraniteAttach", out var ga))
                {
                    if (ga.TryGetProperty("EncryptionKeyBase64", out var k)) existingKey = k.GetString();
                    if (ga.TryGetProperty("CaptureLinkSecret", out var s)) existingSecret = s.GetString();
                }
            }
            catch { /* unreadable/old file - treat as first install */ }
        }

        c.EncryptionKeyBase64 = !string.IsNullOrWhiteSpace(existingKey) ? existingKey! : NewRandomBase64Key();
        c.CaptureLinkSecret = !string.IsNullOrWhiteSpace(existingSecret) ? existingSecret! : NewRandomBase64Key();
        Log(LogLevel.Info, existingKey is null ? "Generated a new encryption key (first install)." : "Kept the existing encryption key (reinstall) - existing encrypted attachments stay readable.");

        var config = new
        {
            Logging = new { LogLevel = new { Default = "Information", MicrosoftAspNetCore = "Warning" } },
            AllowedHosts = "*",
            ConnectionStrings = new { GraniteLive = c.ConnectionString },
            GraniteAttach = new
            {
                EncryptionKeyBase64 = c.EncryptionKeyBase64,
                CaptureLinkSecret = c.CaptureLinkSecret,
                CaptureLinkExpiryMinutes = 15,
                MaxImageDimension = 1600,
                JpegQuality = 75,
                PublicBaseUrl = c.PublicBaseUrl
            }
        };
        // System.Text.Json can't rename "MicrosoftAspNetCore" to "Microsoft.AspNetCore" via
        // an anonymous type property name, so patch that one key after serializing.
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        json = json.Replace("\"MicrosoftAspNetCore\"", "\"Microsoft.AspNetCore\"");
        File.WriteAllText(configPath, json);
        Log(LogLevel.Success, $"Wrote {configPath}");
    }

    private async Task<(bool ok, string output)> RunAppCmdAsync(string[] args, CancellationToken token)
    {
        if (!IisCommands.AppCmdExists())
            return (false, $"appcmd.exe not found at {IisCommands.AppCmdPath} - is IIS installed on this machine? (The Granite Install Wizard sets that up.)");

        Log(LogLevel.Detail, ProcessRunner.Describe(IisCommands.AppCmdPath, args));
        var result = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, args, TimeSpan.FromSeconds(60), token);
        return (result.Succeeded(), result.Output);
    }

    public async Task SetUpIisAsync(InstallContext c, CancellationToken token)
    {
        if (!IisCommands.AppCmdExists())
        {
            Log(LogLevel.Warning, $"appcmd.exe not found at {IisCommands.AppCmdPath} - IIS doesn't look installed on this machine. Skipping the IIS site/app pool step; the app has still been published to {c.PhysicalPath}, so you can wire it into IIS by hand, or run this installer again once IIS is set up (the Granite Install Wizard does that).");
            return;
        }

        Log(LogLevel.Stage, "Setting up the IIS app pool and site ...");

        (bool poolListOk, string poolListOut) = await RunAppCmdAsync(IisCommands.ListAppPools(), token);
        IReadOnlyList<string> existingPools = poolListOk ? ParseAppPoolNamesOrEmpty(poolListOut) : Array.Empty<string>();
        if (existingPools.Contains(c.AppPoolName, StringComparer.OrdinalIgnoreCase))
        {
            Log(LogLevel.Detail, $"App pool '{c.AppPoolName}' already exists - reconfiguring it.");
            await RunAppCmdAsync(IisCommands.ConfigureAppPool(c.AppPoolName), token);
        }
        else
        {
            (bool ok, string msg) = await RunAppCmdAsync(IisCommands.AddAppPool(c.AppPoolName), token);
            if (!ok) throw new InvalidOperationException($"Could not create app pool '{c.AppPoolName}': {msg}");
            Log(LogLevel.Success, $"App pool '{c.AppPoolName}' created.");
        }

        (bool siteListOk, string siteListOut) = await RunAppCmdAsync(IisCommands.ListSites(), token);
        List<IisSite> existingSites = siteListOk ? ParseSitesOrEmpty(siteListOut).ToList() : new List<IisSite>();
        IisSite? matchingSite = existingSites.FirstOrDefault(s => string.Equals(s.Name, c.SiteName, StringComparison.OrdinalIgnoreCase));

        if (matchingSite is not null)
        {
            Log(LogLevel.Detail, $"Site '{c.SiteName}' already exists - removing it so it can be recreated with these settings (its app pool or physical path may have changed).");
            await RunAppCmdAsync(IisCommands.StopSite(c.SiteName), token);
            await RunAppCmdAsync(IisCommands.DeleteSite(c.SiteName), token);
        }

        int newId = existingSites.Count == 0 ? 100 : existingSites.Max(s => s.Id) + 1;
        (bool addOk, string addMsg) = await RunAppCmdAsync(IisCommands.AddSite(c.SiteName, newId, c.PhysicalPath, c.Port, protocol: "http"), token);
        if (!addOk) throw new InvalidOperationException($"Could not create site '{c.SiteName}': {addMsg}");
        Log(LogLevel.Success, $"Site '{c.SiteName}' created on port {c.Port} (id {newId}).");

        await RunAppCmdAsync(IisCommands.SetAppPoolForRootApp(c.SiteName, c.AppPoolName), token);

        var grantResult = await ProcessRunner.RunAsync("icacls.exe", IisCommands.GrantFolderModify(c.PhysicalPath, c.AppPoolName), TimeSpan.FromSeconds(30), token);
        if (!grantResult.Succeeded())
            Log(LogLevel.Warning, $"Could not grant the app pool write access to {c.PhysicalPath}: {grantResult.Output}. The app may fail to write logs - grant IIS AppPool\\{c.AppPoolName} Modify access to that folder by hand if it does.");

        string ruleName = FirewallRuleName(c.Port);
        var firewallCheck = await ProcessRunner.RunAsync("netsh.exe", IisCommands.ShowFirewallRule(ruleName), TimeSpan.FromSeconds(20), token);
        if (!firewallCheck.Succeeded())
        {
            await ProcessRunner.RunAsync("netsh.exe", IisCommands.AddFirewallRule(ruleName, c.Port), TimeSpan.FromSeconds(20), token);
            Log(LogLevel.Success, $"Opened firewall port {c.Port}.");
        }
        else
        {
            Log(LogLevel.Detail, "Firewall rule already present.");
        }

        await RunAppCmdAsync(IisCommands.StartAppPool(c.AppPoolName), token);
        await RunAppCmdAsync(IisCommands.StartSite(c.SiteName), token);
        Log(LogLevel.Success, $"Site '{c.SiteName}' started.");
    }

    /// <summary>
    /// Attach's firewall rule name. Kept exactly as before the toolkit so an
    /// existing install's rule is still found (the core Granite sites use
    /// IisCommands.FirewallRuleName, "Granite WMS - title (port)").
    /// </summary>
    internal static string FirewallRuleName(int port) => $"Granite Attach ({port})";

    // The shared IisCommands parsers throw on malformed XML (the Install
    // module relies on that). This installer has always treated an
    // unreadable listing as "nothing there", so it keeps doing so.
    private static IReadOnlyList<IisSite> ParseSitesOrEmpty(string xml)
    {
        try { return IisCommands.ParseSites(xml); }
        catch (System.Xml.XmlException) { return Array.Empty<IisSite>(); }
    }

    private static IReadOnlyList<string> ParseAppPoolNamesOrEmpty(string xml)
    {
        try { return IisCommands.ParseAppPoolNames(xml); }
        catch (System.Xml.XmlException) { return Array.Empty<string>(); }
    }
}
