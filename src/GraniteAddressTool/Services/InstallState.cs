using System.Security.Cryptography.X509Certificates;

namespace GraniteAddressTool.Services;

/// <summary>
/// The HTTPS certificate http.sys serves for one Granite site binding.
/// <paramref name="IpPort"/> is the http.sys key it was read from:
/// 0.0.0.0:port for a site on all addresses, ip:port for a pinned one.
/// </summary>
public sealed record SiteCertificate(GraniteApp App, int Port, string IpPort, string? Thumbprint, X509Certificate2? Certificate, IReadOnlyList<string> Names, bool Trusted)
{
    public bool SelfSigned => Certificate is not null && Certificate.Subject == Certificate.Issuer;
    public bool Pinned => IpPort != IisCommands.AllAddresses(Port);
}

/// <summary>
/// Everything the Change address screen shows for one install, read fresh
/// from disk, IIS and http.sys. Read-only: nothing here changes anything.
/// </summary>
public sealed class InstallState
{
    public required GraniteInstall Install { get; init; }
    public required MachineAddresses Machine { get; init; }
    public required IReadOnlyDictionary<GraniteAppKind, (string Path, byte[] Bytes)> Files { get; init; }
    public required IReadOnlyList<EndpointSetting> Endpoints { get; init; }
    public required IReadOnlyList<SiteCertificate> Certificates { get; init; }
    public required IReadOnlyList<PinnedBinding> Pinned { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }

    public static async Task<InstallState> LoadAsync(GraniteInstall install, CancellationToken token)
    {
        var problems = new List<string>();
        var files = new Dictionary<GraniteAppKind, (string, byte[])>();
        foreach (var app in install.Apps)
        {
            try { files[app.Kind] = (app.AppSettingsPath, await File.ReadAllBytesAsync(app.AppSettingsPath, token)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{app.Title}: couldn't read {app.AppSettingsPath} ({ex.Message})");
            }
        }

        var endpoints = files.SelectMany(f => AddressChange.ReadEndpoints(f.Key, f.Value.Item2)).ToList();

        var certs = new List<SiteCertificate>();
        string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        foreach (var app in install.Apps)
        {
            foreach (var binding in app.Bindings.Where(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                // A pinned binding is served from its own ip:port entry when there is one.
                string ipPort = IisCommands.IpPort(binding.Address, binding.Port);
                var r = await ProcessRunner.RunAsync(netsh, IisCommands.ShowSslCert(ipPort), TimeSpan.FromSeconds(20), token);
                string? thumb = r.ExitCode == 0 ? CertificateCoverage.ThumbprintFromNetsh(r.Output) : null;
                if (thumb is null && ipPort != IisCommands.AllAddresses(binding.Port))
                {
                    ipPort = IisCommands.AllAddresses(binding.Port);
                    r = await ProcessRunner.RunAsync(netsh, IisCommands.ShowSslCert(ipPort), TimeSpan.FromSeconds(20), token);
                    thumb = r.ExitCode == 0 ? CertificateCoverage.ThumbprintFromNetsh(r.Output) : null;
                }
                var cert = thumb is null ? null : CertificateService.Find(thumb);
                var names = cert is null ? (IReadOnlyList<string>)Array.Empty<string>() : CertificateService.SubjectAlternativeNames(cert);
                certs.Add(new SiteCertificate(app, binding.Port, ipPort, thumb, cert, names, cert is not null && IsTrusted(cert)));
            }
        }

        var machine = LocalAddressDiscovery.Current();
        return new InstallState
        {
            Install = install,
            Machine = machine,
            Pinned = SiteBindings.Pinned(install, machine),
            Files = files,
            Endpoints = endpoints,
            Certificates = certs,
            Problems = problems
        };
    }

    /// <summary>
    /// Whether browsers on this server will accept the certificate: a
    /// self-signed one must be in Trusted Root; any other must chain to a
    /// trusted root. Revocation isn't checked (no network calls on a server
    /// that may be offline); the chain and dates are.
    /// </summary>
    public static bool IsTrusted(X509Certificate2 cert)
    {
        if (DateTime.Now > cert.NotAfter || DateTime.Now < cert.NotBefore) return false;
        if (cert.Subject == cert.Issuer)
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return root.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert);
    }
}
