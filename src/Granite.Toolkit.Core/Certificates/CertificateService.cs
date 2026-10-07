using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Granite.Toolkit.Core.Certificates;

/// <summary>
/// The HTTPS certificate for the Granite sites: create a self-signed one,
/// or list the existing ones IIS could use (Install Wizard Step 5, Change address).
/// </summary>
public static class CertificateService
{
    /// <summary>Certificates in LocalMachine\My that IIS could use: private key present, not expired.</summary>
    public static List<X509Certificate2> ListUsable()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        DateTime now = DateTime.Now;
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotAfter > now)
            .OrderByDescending(c => c.NotAfter)
            .ToList();
    }

    public static X509Certificate2? Find(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>DNS names and IP addresses in a certificate's Subject Alternative Name.</summary>
    public static List<string> SubjectAlternativeNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        foreach (X509Extension ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            // Decoded from the raw bytes rather than relying on the
            // collection having already produced the typed extension.
            var san = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
            names.AddRange(san.EnumerateDnsNames());
            names.AddRange(san.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }
        if (names.Count == 0)
        {
            string cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!string.IsNullOrWhiteSpace(cn)) names.Add(cn);
        }
        return names;
    }

    /// <summary>
    /// Creates a self-signed server certificate with every DNS name and IP
    /// as a Subject Alternative Name (browsers ignore the CN), stores it in
    /// LocalMachine\My, and returns it.
    /// </summary>
    /// <remarks>
    /// The release's GraniteScaffold\Install\Scripts\create-certificate.ps1
    /// takes exactly one server name and one IP. This takes any number of
    /// each (hostname, FQDN, localhost, every NIC's IP), so the same
    /// certificate works however a PC or scanner addresses the server.
    /// <para/>
    /// The key is exported and re-imported with MachineKeySet |
    /// PersistKeySet: CreateSelfSigned produces an ephemeral key, and a
    /// certificate stored with one looks fine in certlm.msc but fails in
    /// IIS/http.sys with "A specified logon session does not exist".
    /// </remarks>
    public static X509Certificate2 CreateSelfSigned(string friendlyName, IReadOnlyList<string> dnsNames, IReadOnlyList<string> ipAddresses, int years = 5)
    {
        if (dnsNames.Count == 0) throw new ArgumentException("At least one DNS name is needed.", nameof(dnsNames));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsNames[0]}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (string dns in dnsNames) san.AddDnsName(dns);
        foreach (string ip in ipAddresses) san.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // server auth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        DateTimeOffset now = DateTimeOffset.Now;
        using X509Certificate2 ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(years));

        string transferPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx, transferPassword);
        var persisted = new X509Certificate2(pfx, transferPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        persisted.FriendlyName = friendlyName;

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    /// <summary>Adds the certificate (public part only) to LocalMachine\Root so this server's own browser trusts it.</summary>
    public static void TrustOnThisMachine(X509Certificate2 cert)
    {
        using var publicOnly = new X509Certificate2(cert.Export(X509ContentType.Cert));
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(publicOnly);
    }

    /// <summary>Writes the public certificate as a .cer file, for scanners and client PCs.</summary>
    public static string ExportPublic(X509Certificate2 cert, string folder, string friendlyName)
    {
        Directory.CreateDirectory(folder);
        string safe = string.Concat(friendlyName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string path = Path.Combine(folder, safe + ".cer");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }
}
