using System.Security.Cryptography.X509Certificates;

namespace GraniteAddressTool.Services;

/// <summary>The HTTPS certificate bound to one Granite site's port.</summary>
public sealed record SiteCertificate(GraniteApp App, int Port, string? Thumbprint, X509Certificate2? Certificate, IReadOnlyList<string> Names)
{
    public bool SelfSigned => Certificate is not null && Certificate.Subject == Certificate.Issuer;
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
                var r = await ProcessRunner.RunAsync(netsh, IisCommands.ShowSslCert(binding.Port), TimeSpan.FromSeconds(20), token);
                string? thumb = r.ExitCode == 0 ? CertificateCoverage.ThumbprintFromNetsh(r.Output) : null;
                var cert = thumb is null ? null : CertificateService.Find(thumb);
                var names = cert is null ? (IReadOnlyList<string>)Array.Empty<string>() : CertificateService.SubjectAlternativeNames(cert);
                certs.Add(new SiteCertificate(app, binding.Port, thumb, cert, names));
            }
        }

        return new InstallState
        {
            Install = install,
            Machine = LocalAddressDiscovery.Current(),
            Files = files,
            Endpoints = endpoints,
            Certificates = certs,
            Problems = problems
        };
    }
}
