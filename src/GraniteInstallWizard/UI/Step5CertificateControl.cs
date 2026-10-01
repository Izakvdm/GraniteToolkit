using System.Net;
using System.Security.Cryptography.X509Certificates;
using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step5CertificateControl : WizardStepControl
{
    private readonly RadioButton _rbNew = new() { Text = "Create a new self-signed certificate", AutoSize = true, Checked = true };
    private readonly TextBox _txtFriendly = new() { Width = 300 };
    private readonly TextBox _txtDns = new() { Width = 560 };
    private readonly TextBox _txtIps = new() { Width = 560 };
    private readonly CheckBox _chkTrust = new() { Text = "Trust it on this server (adds it to Trusted Root, so this server's own browser doesn't warn)", AutoSize = true };

    private readonly RadioButton _rbExisting = new() { Text = "Use an existing certificate from Local Computer\\Personal", AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
    private readonly ComboBox _cmbExisting = new() { Width = 620, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _btnRefresh = MakeButton("Refresh", 90, new Padding(0, 6, 0, 0));
    private readonly Label _lblExisting = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) };

    private List<X509Certificate2> _certs = new();
    private bool _defaultsApplied;
    private string _publicHost = string.Empty;

    public override string StepTitle => "Step 5 of 6: HTTPS Certificate";

    public Step5CertificateControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(MakeHint("All four sites are HTTPS. Browsers and scanners only trust the certificate if it lists the exact name or IP they use to reach the server."));

        page.Controls.Add(_rbNew);
        page.Controls.Add(MakeFieldLabel("Friendly name:"));
        page.Controls.Add(_txtFriendly);
        page.Controls.Add(MakeFieldLabel("DNS names (comma-separated):"));
        page.Controls.Add(_txtDns);
        page.Controls.Add(MakeFieldLabel("IP addresses (comma-separated):"));
        page.Controls.Add(_txtIps);
        page.Controls.Add(MakeHint("Valid for 5 years. A public copy (.cer) is saved to the install folder's Certificates subfolder: install it on scanners and client PCs so they trust the sites."));
        page.Controls.Add(_chkTrust);

        page.Controls.Add(_rbExisting);
        page.Controls.Add(_cmbExisting);
        page.Controls.Add(_btnRefresh);
        page.Controls.Add(_lblExisting);
        Controls.Add(page);

        _rbNew.CheckedChanged += (_, _) => UpdateEnabled();
        _rbExisting.CheckedChanged += (_, _) => UpdateEnabled();
        _btnRefresh.Click += (_, _) => LoadCertificates(null);
        _cmbExisting.SelectedIndexChanged += (_, _) => ShowExistingNames();
    }

    private void UpdateEnabled()
    {
        bool isNew = _rbNew.Checked;
        _txtFriendly.Enabled = _txtDns.Enabled = _txtIps.Enabled = _chkTrust.Enabled = isNew;
        _cmbExisting.Enabled = _btnRefresh.Enabled = !isNew;
    }

    private void LoadCertificates(string? select)
    {
        _cmbExisting.Items.Clear();
        _certs = CertificateService.ListUsable();
        foreach (var c in _certs)
        {
            string friendly = string.IsNullOrWhiteSpace(c.FriendlyName) ? "(no friendly name)" : c.FriendlyName;
            _cmbExisting.Items.Add($"{friendly}  |  {c.GetNameInfo(X509NameType.SimpleName, false)}  |  expires {c.NotAfter:yyyy-MM-dd}  |  {c.Thumbprint}");
        }
        int idx = select is null ? -1 : _certs.FindIndex(c => string.Equals(c.Thumbprint, select, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _cmbExisting.SelectedIndex = idx;
        else if (_certs.Count == 0) _lblExisting.Text = "No usable certificates (with a private key, not expired) in Local Computer\\Personal.";
    }

    private void ShowExistingNames()
    {
        int i = _cmbExisting.SelectedIndex;
        if (i < 0) return;
        var names = CertificateService.SubjectAlternativeNames(_certs[i]);
        bool covers = names.Contains(_publicHost, StringComparer.OrdinalIgnoreCase);
        _lblExisting.ForeColor = covers ? Color.SeaGreen : Color.DarkOrange;
        _lblExisting.Text = $"Names on this certificate: {string.Join(", ", names)}." +
                            (covers ? string.Empty : $" It doesn't cover {_publicHost}, so browsers will warn when using that address.");
    }

    private static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public override void OnEnter(InstallContext context)
    {
        _publicHost = context.PublicHost;
        if (!_defaultsApplied)
        {
            // First visit: offer every name and IPv4 address this server has,
            // plus localhost, on top of whatever Step 4 already added.
            foreach (string n in LocalAddressDiscovery.HostNames().Append("localhost"))
                if (!context.CertDnsNames.Contains(n, StringComparer.OrdinalIgnoreCase)) context.CertDnsNames.Add(n);
            foreach (string ip in LocalAddressDiscovery.IPv4Addresses())
                if (!context.CertIpAddresses.Contains(ip)) context.CertIpAddresses.Add(ip);
            _defaultsApplied = true;
        }

        _txtFriendly.Text = context.CertFriendlyName;
        _txtDns.Text = string.Join(", ", context.CertDnsNames);
        _txtIps.Text = string.Join(", ", context.CertIpAddresses);
        _chkTrust.Checked = context.TrustCertificate;
        _rbExisting.Checked = context.CertMode == CertificateMode.UseExisting;
        _rbNew.Checked = context.CertMode == CertificateMode.CreateSelfSigned;
        LoadCertificates(context.ExistingCertThumbprint);
        UpdateEnabled();
    }

    public override void OnLeave(InstallContext context)
    {
        context.CertMode = _rbExisting.Checked ? CertificateMode.UseExisting : CertificateMode.CreateSelfSigned;
        context.CertFriendlyName = _txtFriendly.Text.Trim();
        context.CertDnsNames = SplitList(_txtDns.Text);
        context.CertIpAddresses = SplitList(_txtIps.Text);
        context.TrustCertificate = _chkTrust.Checked;
        int i = _cmbExisting.SelectedIndex;
        context.ExistingCertThumbprint = i >= 0 ? _certs[i].Thumbprint : null;
        context.ExistingCertNames = i >= 0 ? CertificateService.SubjectAlternativeNames(_certs[i]) : new List<string>();
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        error = string.Empty;
        if (_rbNew.Checked)
        {
            if (string.IsNullOrWhiteSpace(_txtFriendly.Text)) { error = "Enter a friendly name for the certificate."; return false; }
            var dns = SplitList(_txtDns.Text);
            var ips = SplitList(_txtIps.Text);
            if (dns.Count == 0) { error = "Enter at least one DNS name."; return false; }
            var badIp = ips.FirstOrDefault(ip => !IPAddress.TryParse(ip, out _));
            if (badIp is not null) { error = $"Not an IP address: {badIp}"; return false; }
            var badDns = dns.FirstOrDefault(d => IPAddress.TryParse(d, out _));
            if (badDns is not null) { error = $"{badDns} is an IP address; put it in the IP addresses box."; return false; }
            if (!dns.Concat(ips).Contains(context.PublicHost, StringComparer.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(this, $"{context.PublicHost} (the address from Step 4) isn't on the certificate, so browsers will warn when using it. Continue anyway?",
                    "Certificate names", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { error = $"Add {context.PublicHost} to the certificate."; return false; }
            }
        }
        else
        {
            if (_cmbExisting.SelectedIndex < 0) { error = "Pick a certificate, or create a new one."; return false; }
        }
        return true;
    }
}
