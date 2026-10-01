using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Creates the app pools and HTTPS sites through appcmd.exe and binds the
/// certificate through netsh (see IisCommands for why not
/// Microsoft.Web.Administration).
/// </summary>
public sealed class IisService
{
    /// <summary>Identifies this wizard's http.sys SSL bindings in "netsh http show sslcert".</summary>
    private static readonly Guid AppId = new("5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61");

    private readonly Action<LogEntry> _log;

    public IisService(Action<LogEntry> log) => _log = log;

    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public static bool IisInstalled => File.Exists(IisCommands.AppCmdPath);

    public static async Task<IReadOnlyList<IisSite>> ListSitesAsync(CancellationToken token)
    {
        if (!IisInstalled) return Array.Empty<IisSite>();
        var r = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, IisCommands.ListSites(), TimeSpan.FromMinutes(1), token);
        return r.ExitCode == 0 ? IisCommands.ParseSites(r.Output) : Array.Empty<IisSite>();
    }

    public static async Task<IReadOnlyList<string>> ListAppPoolsAsync(CancellationToken token)
    {
        if (!IisInstalled) return Array.Empty<string>();
        var r = await ProcessRunner.RunAsync(IisCommands.AppCmdPath, IisCommands.ListAppPools(), TimeSpan.FromMinutes(1), token);
        return r.ExitCode == 0 ? IisCommands.ParseAppPools(r.Output) : Array.Empty<string>();
    }

    private async Task<ProcessResult> RunAsync(string exe, string[] args, CancellationToken token, bool mustSucceed = true)
    {
        Log(LogLevel.Detail, ProcessRunner.Describe(exe, args));
        var r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromMinutes(2), token);
        if (mustSucceed && r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
        return r;
    }

    public async Task CreateSitesAsync(InstallContext c, string certThumbprint, CancellationToken token)
    {
        if (c.DryRun)
        {
            foreach (var comp in c.EnabledComponents)
            {
                var s = c.Sites[comp.Key];
                Log(LogLevel.DryRun, $"Would create app pool \"{s.SiteName}\" (No Managed Code, AlwaysRunning) and HTTPS site \"{s.SiteName}\" on port {s.Port} -> {c.InstallPathFor(comp)}");
            }
            return;
        }

        if (!IisInstalled)
            throw new InvalidOperationException($"appcmd.exe not found at {IisCommands.AppCmdPath}. IIS should have been installed by the prerequisites stage.");

        string appcmd = IisCommands.AppCmdPath;
        var pools = await ListAppPoolsAsync(token);
        var sites = await ListSitesAsync(token);
        int nextId = sites.Count == 0 ? 1 : sites.Max(s => s.Id) + 1;

        foreach (var comp in c.EnabledComponents)
        {
            token.ThrowIfCancellationRequested();
            var s = c.Sites[comp.Key];
            string pool = s.SiteName;
            string path = c.InstallPathFor(comp);

            if (pools.Contains(pool, StringComparer.OrdinalIgnoreCase))
            {
                Log(LogLevel.Warning, $"App pool {pool} already exists; reconfiguring it.");
                await RunAsync(appcmd, IisCommands.ConfigureAppPool(pool), token);
            }
            else
            {
                await RunAsync(appcmd, IisCommands.AddAppPool(pool), token);
            }

            await RunAsync(appcmd, IisCommands.AddSite(s.SiteName, nextId++, path, s.Port), token);
            await RunAsync(appcmd, IisCommands.SetAppPoolForRootApp(s.SiteName, pool), token);
            await RunAsync(appcmd, IisCommands.EnablePreload(s.SiteName), token);

            // Replace any stale http.sys binding on this port (left by an
            // earlier, removed site) rather than failing on "already exists".
            var existing = await RunAsync("netsh.exe", IisCommands.ShowSslCert(s.Port), token, mustSucceed: false);
            if (existing.ExitCode == 0 && existing.Output.Contains("Certificate Hash", StringComparison.OrdinalIgnoreCase))
            {
                Log(LogLevel.Warning, $"Port {s.Port} already had an SSL certificate binding in http.sys; replacing it.");
                await RunAsync("netsh.exe", IisCommands.DeleteSslCert(s.Port), token);
            }
            await RunAsync("netsh.exe", IisCommands.AddSslCert(s.Port, certThumbprint, AppId), token);

            var acl = await RunAsync("icacls.exe", IisCommands.GrantFolderModify(path, pool), token, mustSucceed: false);
            if (acl.ExitCode != 0) Log(LogLevel.Warning, $"Could not grant the app pool write access to {path}: {acl.Output.Trim()}");
            Log(LogLevel.Info, $"{s.SiteName} configured.");
        }

        // Start everything only once every site is fully configured.
        // v0.2.0 started each site straight after configuring it; the first
        // real run showed the Business API's log going "Application started"
        // then "Application is shutting down" in the same millisecond while
        // later sites were still being written to applicationHost.config,
        // and the verify stage caught it mid-restart (HTTP 503). An
        // AlwaysRunning pool starts the moment it's created, so the pool is
        // recycled here once to give each app a clean start with its final
        // settings.
        foreach (var comp in c.EnabledComponents)
        {
            token.ThrowIfCancellationRequested();
            var s = c.Sites[comp.Key];
            await RunAsync(appcmd, IisCommands.StartAppPool(s.SiteName), token, mustSucceed: false);
            await RunAsync(appcmd, IisCommands.RecycleAppPool(s.SiteName), token, mustSucceed: false);
            await RunAsync(appcmd, IisCommands.StartSite(s.SiteName), token, mustSucceed: false);
            Log(LogLevel.Success, $"{s.SiteName}: {c.UrlFor(comp.Key)}");
        }
    }

    /// <summary>
    /// Removes the existing IIS sites that have Step 4's names ("Replace
    /// existing IIS sites"), with their same-named app pools, the http.sys
    /// certificate bindings on their https ports, and this wizard's
    /// firewall rules for their old ports. Runs before the copy stage.
    /// </summary>
    public async Task RemoveSitesBeingReplacedAsync(InstallContext c, CancellationToken token)
    {
        var sites = await ListSitesAsync(token);
        var pools = await ListAppPoolsAsync(token);
        bool removedAny = false;

        foreach (var comp in c.EnabledComponents)
        {
            token.ThrowIfCancellationRequested();
            string wanted = c.Sites[comp.Key].SiteName;
            foreach (var site in sites.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                string name = site.Name;

                if (c.DryRun)
                {
                    Log(LogLevel.DryRun, $"Would stop and remove the existing IIS site \"{site.Name}\" and its app pool, SSL bindings and firewall rules (ports {string.Join(", ", site.Bindings.Select(b => b.Port))}).");
                    continue;
                }

                Log(LogLevel.Warning, $"Removing the existing IIS site \"{site.Name}\" (being replaced)...");
                string appcmd = IisCommands.AppCmdPath;
                await RunAsync(appcmd, IisCommands.StopSite(site.Name), token, mustSucceed: false);
                if (pools.Contains(name, StringComparer.OrdinalIgnoreCase))
                    await RunAsync(appcmd, IisCommands.StopAppPool(name), token, mustSucceed: false);
                await RunAsync(appcmd, IisCommands.DeleteSite(site.Name), token);
                if (pools.Contains(name, StringComparer.OrdinalIgnoreCase))
                    await RunAsync(appcmd, IisCommands.DeleteAppPool(name), token, mustSucceed: false);

                foreach (var binding in site.Bindings.Where(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)))
                {
                    var show = await RunAsync("netsh.exe", IisCommands.ShowSslCert(binding.Port), token, mustSucceed: false);
                    if (show.ExitCode == 0 && show.Output.Contains("Certificate Hash", StringComparison.OrdinalIgnoreCase))
                        await RunAsync("netsh.exe", IisCommands.DeleteSslCert(binding.Port), token, mustSucceed: false);
                }
                foreach (var binding in site.Bindings)
                {
                    string rule = IisCommands.FirewallRuleName(comp.Title, binding.Port);
                    var show = await ProcessRunner.RunAsync("netsh.exe", IisCommands.ShowFirewallRule(rule), TimeSpan.FromMinutes(1), token);
                    if (show.ExitCode == 0) await RunAsync("netsh.exe", IisCommands.DeleteFirewallRule(rule), token, mustSucceed: false);
                }
                Log(LogLevel.Success, $"Removed \"{site.Name}\".");
                removedAny = true;
            }
        }

        // The worker processes take a moment to exit and release their files.
        if (removedAny) await Task.Delay(TimeSpan.FromSeconds(3), token);
    }

    public async Task OpenFirewallAsync(InstallContext c, CancellationToken token)
    {
        foreach (var comp in c.EnabledComponents)
        {
            var s = c.Sites[comp.Key];
            string rule = IisCommands.FirewallRuleName(comp.Title, s.Port);
            if (c.DryRun) { Log(LogLevel.DryRun, $"Would open inbound TCP {s.Port} ({rule})"); continue; }

            var show = await ProcessRunner.RunAsync("netsh.exe", IisCommands.ShowFirewallRule(rule), TimeSpan.FromMinutes(1), token);
            if (show.ExitCode == 0) { Log(LogLevel.Info, $"Firewall rule already exists: {rule}"); continue; }
            await RunAsync("netsh.exe", IisCommands.AddFirewallRule(rule, s.Port), token);
            Log(LogLevel.Success, $"Opened inbound TCP {s.Port} ({rule}).");
        }
    }
}
