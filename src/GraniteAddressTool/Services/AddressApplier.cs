using System.Security.Cryptography.X509Certificates;

namespace GraniteAddressTool.Services;

/// <summary>One Granite site whose bindings move from one IP address to all addresses.</summary>
public sealed record SiteUnpin(string Site, string OldBindings, string NewBindings, IReadOnlyList<string> OldIpPorts);

/// <summary>
/// What has to happen in IIS and http.sys for the new address: sites tied
/// to one IP are bound to all addresses, and every Granite HTTPS port ends
/// up on one certificate that covers the new address and this server trusts.
/// </summary>
public sealed record ServerPlan(
    bool Blocked,
    IReadOnlyList<string> Summary,
    bool Reissue,
    IReadOnlyList<string> Dns,
    IReadOnlyList<string> Ips,
    SiteCertificate? Target,
    bool TrustTarget,
    IReadOnlyList<int> Ports,
    IReadOnlyList<SiteUnpin> Unpins)
{
    public bool NothingToDo => !Reissue && !TrustTarget && Ports.Count == 0 && Unpins.Count == 0;
}

/// <summary>
/// Carries out an AddressPlan and a ServerPlan: backs up and rewrites the
/// config files, binds pinned sites to all addresses, puts every HTTPS port
/// on one trusted certificate, recycles the app pools and checks the new
/// address answers. Anything that fails before the app pools are recycled
/// is undone, newest change first.
/// </summary>
public sealed class AddressApplier
{
    /// <summary>Same application ID the Install Wizard registers its http.sys bindings with.</summary>
    private static readonly Guid SslAppId = new("5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61");

    private readonly Action<LogEntry> _log;
    public AddressApplier(Action<LogEntry> log) => _log = log;
    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public static ServerPlan PlanServer(InstallState state, string newHost)
    {
        var summary = new List<string>();
        var unpins = state.Install.Apps
            .Where(app => state.Pinned.Any(p => p.App == app))
            .Select(app => new SiteUnpin(app.SiteName, IisCommands.BindingList(app.Bindings), SiteBindings.AllAddressesBindingList(app.Bindings),
                state.Pinned.Where(p => p.App == app && p.Binding.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                    .Select(p => IisCommands.IpPort(p.Binding.Address, p.Binding.Port)).Distinct().ToList()))
            .ToList();
        foreach (var p in state.Pinned)
            summary.Add($"{p.App.Title} is bound to {p.Binding.Address}:{p.Binding.Port} only; it will be bound to all addresses, so it keeps answering when the IP changes and over IPv6.");

        var httpsPorts = state.Install.Apps.SelectMany(a => a.Bindings)
            .Where(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)).Select(b => b.Port).Distinct().ToList();
        if (httpsPorts.Count == 0)
            return new(false, summary.Append("No HTTPS bindings, so no certificate to check.").ToList(), false, Array.Empty<string>(), Array.Empty<string>(), null, false, Array.Empty<int>(), unpins);

        var found = state.Certificates.Where(c => c.Certificate is not null).ToList();
        var covering = found.Where(c => CertificateCoverage.Covers(c.Names, newHost)).ToList();
        SiteCertificate? target = covering.Where(c => c.Trusted)
            .OrderBy(c => c.SelfSigned).ThenByDescending(c => c.Certificate!.NotAfter).FirstOrDefault();
        bool trust = false, reissue = false;
        IReadOnlyList<string> dns = Array.Empty<string>(), ips = Array.Empty<string>();

        if (target is null && covering.FirstOrDefault(c => c.SelfSigned && DateTime.Now < c.Certificate!.NotAfter) is { } untrusted)
        {
            target = untrusted;
            trust = true;
            summary.Add($"Certificate {untrusted.Thumbprint} covers {newHost} but this server doesn't trust it yet; it will be added to Trusted Root.");
        }
        else if (target is null)
        {
            var foreign = found.FirstOrDefault(c => !c.SelfSigned);
            if (foreign is not null)
                return new(true, summary.Append(
                    $"The certificate on {foreign.App.Title} (port {foreign.Port}) was issued by {foreign.Certificate!.Issuer} and doesn't include {newHost}. " +
                    $"Get one that does and bind it first, or choose an address it covers: {string.Join(", ", foreign.Names)}.").ToList(),
                    false, dns, ips, null, false, Array.Empty<int>(), unpins);
            (dns, ips) = CertificateCoverage.NamesForReissue(found.SelectMany(c => c.Names), newHost, state.Machine);
            reissue = true;
            summary.Add($"No trusted certificate covers {newHost}. A new self-signed one will be created for {string.Join(", ", dns.Concat(ips))} " +
                        "and trusted on this server. Scanners and other PCs that trusted the old one need the new .cer.");
        }

        // Ports already served on all addresses by the target need nothing; everything else is (re)bound.
        var pinnedPorts = state.Pinned.Select(p => p.Binding.Port).ToHashSet();
        var ports = httpsPorts.Where(port => reissue || pinnedPorts.Contains(port) ||
                !state.Certificates.Any(c => c.Port == port && !c.Pinned && c.Thumbprint == target!.Thumbprint)).ToList();
        if (!reissue && target is not null)
            summary.Add(ports.Count == 0
                ? $"Every HTTPS port already uses certificate {target.Thumbprint}, which covers {newHost}. No change needed."
                : $"Port(s) {string.Join(", ", ports)} will use certificate {target.Thumbprint} ({string.Join(", ", target.Names)}), so every Granite site serves the same trusted certificate.");
        return new(false, summary, reissue, dns, ips, target, trust, ports, unpins);
    }

    public async Task<bool> ApplyAsync(InstallState state, AddressPlan plan, ServerPlan server, CancellationToken token)
    {
        if (server.Blocked) { Log(LogLevel.Error, string.Join(" ", server.Summary)); return false; }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var undo = new Stack<(string What, Func<Task> Action)>();
        string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        string appcmd = IisCommands.AppCmdPath;

        Log(LogLevel.Stage, $"Moving {state.Install.RootFolder} to {plan.NewHost}");
        try
        {
            // 1. Nothing changed on disk since the preview.
            foreach (var edit in plan.Edits)
            {
                byte[] now = await File.ReadAllBytesAsync(edit.Path, token);
                if (!now.AsSpan().SequenceEqual(edit.Original))
                    throw new InvalidOperationException($"{edit.Path} changed since the preview. Press Preview again.");
            }

            // 2. Back up, then write.
            // Backups go in the toolkit's admin-only data folder, never next to
            // the files or under the install root: Web Desktop's folder is
            // served by IIS as static files, an install root can sit inside a
            // web root, and the API's settings hold its database password.
            if (plan.Edits.Count > 0)
            {
                string backupsRoot = Path.Combine(ToolkitPaths.DataRoot, "Backups");
                SecureFolders.EnsureAdminOnly(ToolkitPaths.DataRoot);
                SecureFolders.EnsureAdminOnly(backupsRoot);
                string backupDir = Path.Combine(backupsRoot, $"address-{stamp}");
                foreach (var edit in plan.Edits)
                {
                    string appDir = Path.Combine(backupDir, Path.GetFileName(Path.GetDirectoryName(edit.Path)!));
                    Directory.CreateDirectory(appDir);
                    string backup = Path.Combine(appDir, Path.GetFileName(edit.Path));
                    File.Copy(edit.Path, backup, overwrite: false);
                    undo.Push(($"restore {edit.Path}", () => File.WriteAllBytesAsync(edit.Path, edit.Original)));
                    await File.WriteAllBytesAsync(edit.Path, edit.Updated, token);
                    Log(LogLevel.Success, $"{AddressChange.Title(edit.App)}: updated {edit.Path} (backup in {backupDir})");
                }
                foreach (var change in plan.Changes)
                    Log(LogLevel.Detail, $"{AddressChange.Title(change.App)} {change.Setting}: {change.Old} -> {change.New}");
            }

            // 3. Sites tied to one IP go to all addresses.
            foreach (var unpin in server.Unpins)
            {
                await RunAsync(appcmd, IisCommands.SetSiteBindings(unpin.Site, unpin.NewBindings), token);
                undo.Push(($"bindings of {unpin.Site}", () => RunAsync(appcmd, IisCommands.SetSiteBindings(unpin.Site, unpin.OldBindings), CancellationToken.None)));
                Log(LogLevel.Success, $"{unpin.Site}: bound to all addresses ({unpin.NewBindings}).");
            }

            // 4. The certificate every port will use.
            string? target = server.Target?.Thumbprint;
            if (server.Reissue)
            {
                var old = state.Certificates.FirstOrDefault(c => c.Certificate is not null)?.Certificate;
                string friendly = string.IsNullOrWhiteSpace(old?.FriendlyName) ? "Granite WMS" : old.FriendlyName;
                using X509Certificate2 cert = CertificateService.CreateSelfSigned(friendly, server.Dns, server.Ips);
                Log(LogLevel.Success, $"New certificate {cert.Thumbprint} for {string.Join(", ", server.Dns.Concat(server.Ips))}, valid to {cert.NotAfter:yyyy-MM-dd}.");
                CertificateService.TrustOnThisMachine(cert);
                string cer = CertificateService.ExportPublic(cert, Path.Combine(state.Install.RootFolder, "Certificates"), $"{friendly} {stamp}");
                Log(LogLevel.Info, $"Public certificate for scanners and PCs: {cer}");
                if (old is not null) Log(LogLevel.Info, $"The old certificate {old.Thumbprint} is still in the store; remove it in certlm.msc once nothing uses it.");
                target = cert.Thumbprint;
            }
            else if (server.TrustTarget && server.Target?.Certificate is { } toTrust)
            {
                CertificateService.TrustOnThisMachine(toTrust);
                Log(LogLevel.Success, $"Certificate {toTrust.Thumbprint} is now trusted on this server.");
            }

            // 5. Every port on that certificate, read fresh: changing a site's bindings can move its http.sys entry.
            if (target is not null)
            {
                foreach (int port in server.Ports)
                {
                    string ipPort = IisCommands.AllAddresses(port);
                    string? was = await ThumbprintAsync(netsh, ipPort, token);
                    if (string.Equals(was, target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (was is not null) await RunAsync(netsh, IisCommands.DeleteSslCert(ipPort), token);
                    undo.Push(($"certificate on port {port}", async () =>
                    {
                        await ProcessRunner.RunAsync(netsh, IisCommands.DeleteSslCert(ipPort), TimeSpan.FromMinutes(1), CancellationToken.None);
                        if (was is not null) await RunAsync(netsh, IisCommands.AddSslCert(ipPort, was, SslAppId), CancellationToken.None);
                    }));
                    await RunAsync(netsh, IisCommands.AddSslCert(ipPort, target, SslAppId), token);
                    Log(LogLevel.Success, $"Port {port} now uses certificate {target}.");
                }
            }

            // 6. The pinned sites' own http.sys entries are no longer used.
            foreach (string ipPort in server.Unpins.SelectMany(u => u.OldIpPorts).Distinct())
            {
                string? was = await ThumbprintAsync(netsh, ipPort, token);
                if (was is null) continue;
                await RunAsync(netsh, IisCommands.DeleteSslCert(ipPort), token);
                undo.Push(($"certificate entry {ipPort}", () => RunAsync(netsh, IisCommands.AddSslCert(ipPort, was, SslAppId), CancellationToken.None)));
                Log(LogLevel.Success, $"Removed the unused certificate entry for {ipPort}.");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex.Message);
            await RollBackAsync(undo);
            return false;
        }

        // 7. Recycle so the apps read the new settings (past the point of rollback: the change is made).
        foreach (string pool in state.Install.Apps.Select(a => a.AppPool).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = await ProcessRunner.RunAsync(appcmd, IisCommands.RecycleAppPool(pool), TimeSpan.FromMinutes(1), token);
            if (r.ExitCode != 0) r = await ProcessRunner.RunAsync(appcmd, IisCommands.StartAppPool(pool), TimeSpan.FromMinutes(1), token);
            Log(r.ExitCode == 0 ? LogLevel.Success : LogLevel.Warning, r.ExitCode == 0 ? $"App pool {pool} recycled." : $"App pool {pool}: {r.Output.Trim()}");
        }

        // 8. Check the new addresses answer.
        await VerifyAsync(plan, state, token);
        Log(LogLevel.Stage, "Done. Open Web Desktop at the new address and press Ctrl+F5 once, so the browser reloads its settings.");
        return true;
    }

    private static async Task<string?> ThumbprintAsync(string netsh, string ipPort, CancellationToken token)
    {
        var r = await ProcessRunner.RunAsync(netsh, IisCommands.ShowSslCert(ipPort), TimeSpan.FromSeconds(20), token);
        return r.ExitCode == 0 ? CertificateCoverage.ThumbprintFromNetsh(r.Output) : null;
    }

    private async Task RunAsync(string exe, string[] args, CancellationToken token)
    {
        Log(LogLevel.Detail, ProcessRunner.Describe(exe, args));
        var r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromMinutes(1), token);
        if (r.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
    }

    private async Task RollBackAsync(Stack<(string What, Func<Task> Action)> undo)
    {
        if (undo.Count == 0) return;
        Log(LogLevel.Warning, "Undoing what was changed...");
        while (undo.TryPop(out var step))
        {
            try { await step.Action(); Log(LogLevel.Info, $"Undone: {step.What}"); }
            catch (Exception ex) { Log(LogLevel.Error, $"Couldn't undo {step.What}: {ex.Message}. File backups are in {Path.Combine(ToolkitPaths.DataRoot, "Backups")}."); }
        }
    }

    private async Task VerifyAsync(AddressPlan plan, InstallState state, CancellationToken token)
    {
        var urls = plan.Changes.Where(c => c.Setting != AddressChange.OriginsKey).Select(c => c.New)
            .Concat(state.Install.Apps.Where(a => a.Kind == GraniteAppKind.WebDesktop)
                .SelectMany(a => a.Bindings.Where(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)))
                .Select(b => $"https://{plan.NewHost}:{b.Port}/"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (string url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, token);
                Log(LogLevel.Success, $"{url} answers (HTTP {(int)response.StatusCode}).");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                string why = ex.InnerException?.Message ?? ex.Message;
                Log(LogLevel.Warning, $"{url} didn't answer from this server: {why}. If {plan.NewHost} is a name, check it resolves here (ping {plan.NewHost}).");
            }
        }
    }
}
