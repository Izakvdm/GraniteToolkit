using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step4WebsitesControl : WizardStepControl
{
    private readonly ComboBox _cmbHost = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _chkFirewall = new() { Text = "Open these ports in Windows Firewall (inbound TCP)", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    private readonly CheckBox _chkReplace = new()
    {
        Text = "Replace existing IIS sites with these names (for reinstalling: the old site, app pool, SSL binding and firewall rule are removed; its folder is kept as .bak)",
        AutoSize = true,
        MaximumSize = new Size(660, 0),
        ForeColor = Color.DarkOrange,
        Margin = new Padding(0, 8, 0, 0)
    };
    private readonly Dictionary<string, (CheckBox Enabled, TextBox Name, NumericUpDown Port, Label Url)> _rows = new();

    public override string StepTitle => "Step 4 of 6: Websites";

    public Step4WebsitesControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("Address users and scanners will type to reach this server:"));
        page.Controls.Add(_cmbHost);
        page.Controls.Add(MakeHint(
            "An IP address is the safest choice for scanners on Wi-Fi without a DNS entry for this server. " +
            "It must be on the HTTPS certificate; Step 5 adds it automatically. Web Desktop and Process App are " +
            "configured to call the Business API at this address."));

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 8, 0, 0) };
        grid.Controls.Add(MakeFieldLabel("Component"), 0, 0);
        grid.Controls.Add(MakeFieldLabel("IIS site and app pool name"), 1, 0);
        grid.Controls.Add(MakeFieldLabel("HTTPS port"), 2, 0);
        grid.Controls.Add(MakeFieldLabel("URL"), 3, 0);
        int row = 1;
        foreach (var comp in GraniteComponent.CoreStack)
        {
            var chk = new CheckBox { Text = comp.Title, AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
            if (comp.Key == GraniteComponent.BusinessApi) chk.Enabled = false; // always installed
            var name = new TextBox { Width = 220, Margin = new Padding(0, 3, 12, 3) };
            var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 90, Margin = new Padding(0, 3, 12, 3) };
            var url = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) };
            grid.Controls.Add(chk, 0, row);
            grid.Controls.Add(name, 1, row);
            grid.Controls.Add(port, 2, row);
            grid.Controls.Add(url, 3, row);
            _rows[comp.Key] = (chk, name, port, url);
            chk.CheckedChanged += (_, _) => { name.Enabled = chk.Checked; port.Enabled = chk.Checked; RefreshUrls(); };
            port.ValueChanged += (_, _) => RefreshUrls();
            row++;
        }
        page.Controls.Add(grid);
        page.Controls.Add(MakeHint("The Business API is always installed: Web Desktop, Process App and Custodian all depend on it."));
        page.Controls.Add(_chkFirewall);
        page.Controls.Add(_chkReplace);
        Controls.Add(page);

        _cmbHost.TextChanged += (_, _) => RefreshUrls();
    }

    private void RefreshUrls()
    {
        foreach (var (key, r) in _rows)
            r.Url.Text = r.Enabled.Checked ? $"https://{_cmbHost.Text.Trim()}:{r.Port.Value}/" : string.Empty;
    }

    public override void OnEnter(InstallContext context)
    {
        _cmbHost.Items.Clear();
        foreach (string a in LocalAddressDiscovery.IPv4Addresses().Concat(LocalAddressDiscovery.HostNames()))
            _cmbHost.Items.Add(a);
        if (string.IsNullOrWhiteSpace(context.PublicHost) && _cmbHost.Items.Count > 0)
            context.PublicHost = (string)_cmbHost.Items[0]!;
        _cmbHost.Text = context.PublicHost;

        foreach (var (key, r) in _rows)
        {
            var s = context.Sites[key];
            r.Enabled.Checked = s.Enabled || key == GraniteComponent.BusinessApi;
            r.Name.Text = s.SiteName;
            r.Port.Value = Math.Clamp(s.Port, 1, 65535);
        }
        _chkFirewall.Checked = context.OpenFirewall;
        _chkReplace.Checked = context.ReplaceExistingSites;
        RefreshUrls();
    }

    public override void OnLeave(InstallContext context)
    {
        context.PublicHost = _cmbHost.Text.Trim();
        foreach (var (key, r) in _rows)
        {
            var s = context.Sites[key];
            s.Enabled = r.Enabled.Checked || key == GraniteComponent.BusinessApi;
            s.SiteName = r.Name.Text.Trim();
            s.Port = (int)r.Port.Value;
        }
        context.OpenFirewall = _chkFirewall.Checked;
        context.ReplaceExistingSites = _chkReplace.Checked;

        // Make sure a new certificate will cover the chosen address.
        string host = context.PublicHost;
        if (host.Length > 0)
        {
            bool isIp = System.Net.IPAddress.TryParse(host, out _);
            var list = isIp ? context.CertIpAddresses : context.CertDnsNames;
            if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Insert(0, host);
        }
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_cmbHost.Text)) { error = "Enter the address users will use to reach this server."; return false; }
        var enabled = _rows.Where(r => r.Value.Enabled.Checked || r.Key == GraniteComponent.BusinessApi).ToList();
        if (enabled.Any(r => string.IsNullOrWhiteSpace(r.Value.Name.Text))) { error = "Every installed component needs an IIS site name."; return false; }
        if (enabled.Select(r => r.Value.Name.Text.Trim().ToLowerInvariant()).Distinct().Count() != enabled.Count) { error = "Each site needs its own name."; return false; }
        if (enabled.Select(r => r.Value.Port.Value).Distinct().Count() != enabled.Count) { error = "Each site needs its own port."; return false; }
        if (enabled.Any(r => r.Value.Name.Text.IndexOfAny(new[] { '/', '\\', '"', ':' }) >= 0)) { error = "Site names can't contain / \\ \" or :."; return false; }
        if (_chkReplace.Checked && !context.ReplaceExistingSites)
        {
            var answer = MessageBox.Show(this,
                "Any existing IIS site with one of these names will be removed during the install, together with its app pool, SSL binding and firewall rule, and recreated. Its files are kept (the folder is renamed to .bak).\n\nContinue with this setting?",
                "Replace existing IIS sites", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { error = "Untick \"Replace existing IIS sites\" or choose other site names."; return false; }
        }
        error = string.Empty;
        return true;
    }
}
