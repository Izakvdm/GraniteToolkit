using System.Security.Cryptography.X509Certificates;

namespace GraniteAddressTool.Services;

/// <summary>What has to happen to the HTTPS certificate for the new address.</summary>
public sealed record CertificatePlan(bool Reissue, bool Blocked, string Summary, IReadOnlyList<string> Dns, IReadOnlyList<string> Ips);

/// <summary>
/// Carries out an AddressPlan: backs up and rewrites the config files,
/// replaces the certificate if it has to, recycles the app pools and checks
/// the new address answers. Anything that fails before the app pools are
/// recycled is undone (files restored, old certificate rebound).
/// </summary>
public sealed class AddressApplier
{
    /// <summary>Same application ID the Install Wizard registers its http.sys bindings with.</summary>
    private static readonly Guid SslAppId = new("5b9e0c3a-6a47-4a9e-9d3c-7e1f2a8b4c61");

    private readonly Action<LogEntry> _log;
    public AddressApplier(Action<LogEntry> log) => _log = log;
    private void Log(LogLevel level, string message) => _log(new LogEntry(level, message));

    public static CertificatePlan PlanCertificate(InstallState state, string newHost)
    {
        var bound = state.Certificates.Where(c => c.Thumbprint is not null).ToList();
        if (bound.Count == 0)
            return new(false, false, "No HTTPS certificate is bound to these sites, so there's nothing to check.", Array.Empty<string>(), Array.Empty<string>());

        var missing = bound.Where(c => c.Certificate is null).Select(c => $"{c.App.Title} (port {c.Port})").ToList();
        if (missing.Count > 0)
            return new(false, true, $"The certificate bound to {string.Join(", ", missing)} isn't in the Local Computer store. Fix the binding before changing the address.", Array.Empty<string>(), Array.Empty<string>());

        var notCovering = bound.Where(c => !CertificateCoverage.Covers(c.Names, newHost)).ToList();
        if (notCovering.Count == 0)
            return new(false, false, $"The HTTPS certificate already covers {newHost}. No change needed.", Array.Empty<string>(), Array.Empty<string>());

        var foreign = notCovering.Where(c => !c.SelfSigned).ToList();
        if (foreign.Count > 0)
        {
            var c = foreign[0];
            return new(false, true,
                $"The certificate on {c.App.Title} (port {c.Port}) was issued by {c.Certificate!.Issuer} and doesn't include {newHost}. " +
                $"Get one that does and bind it first, or choose an address it covers: {string.Join(", ", c.Names)}.",
                Array.Empty<string>(), Array.Empty<string>());
        }

        var oldNames = bound.SelectMany(b => b.Names);
        var (dns, ips) = CertificateCoverage.NamesForReissue(oldNames, newHost, state.Machine);
        return new(true, false,
            $"The self-signed certificate doesn't include {newHost}. A new one will be created for {string.Join(", ", dns.Concat(ips))}, " +
            $"bound to all {bound.Count} Granite site(s), and trusted on this server. Scanners and other PCs that trusted the old one need the new .cer.",
            dns, ips);
    }

    public async Task<bool> ApplyAsync(InstallState state, AddressPlan plan, CertificatePlan certPlan, CancellationToken token)
    {
        if (certPlan.Blocked) { Log(LogLevel.Error, certPlan.Summary); return false; }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var written = new List<(FileEdit Edit, string Backup)>();
        var rebound = new List<(int Port, string OldThumb)>();
        string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

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
                await File.WriteAllBytesAsync(edit.Path, edit.Updated, token);
                written.Add((edit, backup));
                Log(LogLevel.Success, $"{AddressChange.Title(edit.App)}: updated {edit.Path} (backup in {backupDir})");
            }
            foreach (var change in plan.Changes)
                Log(LogLevel.Detail, $"{AddressChange.Title(change.App)} {change.Setting}: {change.Old} -> {change.New}");

            // 3. Certificate.
            if (certPlan.Reissue)
            {
                var old = state.Certificates.First(c => c.Certificate is not null).Certificate!;
                string friendly = string.IsNullOrWhiteSpace(old.FriendlyName) ? "Granite WMS" : old.FriendlyName;
                using X509Certificate2 cert = CertificateService.CreateSelfSigned(friendly, certPlan.Dns, certPlan.Ips);
                Log(LogLevel.Success, $"New certificate {cert.Thumbprint} for {string.Join(", ", certPlan.Dns.Concat(certPlan.Ips))}, valid to {cert.NotAfter:yyyy-MM-dd}.");
                CertificateService.TrustOnThisMachine(cert);
                string cer = CertificateService.ExportPublic(cert, Path.Combine(state.Install.RootFolder, "Certificates"), $"{friendly} {stamp}");
                Log(LogLevel.Info, $"Public certificate for scanners and PCs: {cer}");

                foreach (var site in state.Certificates.Where(c => c.Thumbprint is not null).DistinctBy(c => c.Port))
                {
                    await RunAsync(netsh, IisCommands.DeleteSslCert(site.Port), token);
                    rebound.Add((site.Port, site.Thumbprint!));
                    await RunAsync(netsh, IisCommands.AddSslCert(site.Port, cert.Thumbprint, SslAppId), token);
                    Log(LogLevel.Success, $"Port {site.Port} ({site.App.Title}) now uses the new certificate.");
                }
                Log(LogLevel.Info, $"The old certificate {old.Thumbprint} is still in the store; remove it in certlm.msc once nothing uses it.");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex.Message);
            await RollBackAsync(written, rebound, netsh);
            return false;
        }

        // 4. Recycle so the apps read the new settings (past the point of rollback: the change is made).
        string appcmd = IisCommands.AppCmdPath;
        foreach (string pool in state.Install.Apps.Select(a => a.AppPool).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = await ProcessRunner.RunAsync(appcmd, IisCommands.RecycleAppPool(pool), TimeSpan.FromMinutes(1), token);
            if (r.ExitCode != 0) r = await ProcessRunner.RunAsync(appcmd, IisCommands.StartAppPool(pool), TimeSpan.FromMinutes(1), token);
            Log(r.ExitCode == 0 ? LogLevel.Success : LogLevel.Warning, r.ExitCode == 0 ? $"App pool {pool} recycled." : $"App pool {pool}: {r.Output.Trim()}");
        }

        // 5. Check the new addresses answer.
        await VerifyAsync(plan, state, token);
        Log(LogLevel.Stage, "Done. Open Web Desktop at the new address and press Ctrl+F5 once, so the browser reloads its settings.");
        return true;
    }

    private async Task RunAsync(string exe, string[] args, CancellationToken token)
    {
        Log(LogLevel.Detail, ProcessRunner.Describe(exe, args));
        var r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromMinutes(1), token);
        if (r.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
    }

    private async Task RollBackAsync(List<(FileEdit Edit, string Backup)> written, List<(int Port, string OldThumb)> rebound, string netsh)
    {
        if (written.Count == 0 && rebound.Count == 0) return;
        Log(LogLevel.Warning, "Undoing what was changed...");
        foreach (var (edit, _) in written)
        {
            try { await File.WriteAllBytesAsync(edit.Path, edit.Original); Log(LogLevel.Info, $"Restored {edit.Path}"); }
            catch (Exception ex) { Log(LogLevel.Error, $"Couldn't restore {edit.Path}: {ex.Message}. The backup next to it has the original."); }
        }
        foreach (var (port, thumb) in rebound)
        {
            await ProcessRunner.RunAsync(netsh, IisCommands.DeleteSslCert(port), TimeSpan.FromMinutes(1), CancellationToken.None);
            var r = await ProcessRunner.RunAsync(netsh, IisCommands.AddSslCert(port, thumb, SslAppId), TimeSpan.FromMinutes(1), CancellationToken.None);
            Log(r.ExitCode == 0 ? LogLevel.Info : LogLevel.Error, r.ExitCode == 0 ? $"Port {port} back on certificate {thumb}." : $"Couldn't rebind port {port} to {thumb}: {r.Output.Trim()}");
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
