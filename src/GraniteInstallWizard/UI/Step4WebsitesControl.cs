using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step4WebsitesControl : WizardStepControl
{
    private readonly ComboBox _cmbHost = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Label _lblHostHint = new() { AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 4, 0, 0) };
    private MachineAddresses _machine = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
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
    private readonly CheckBox _chkAlongside = new()
    {
        Text = "Install alongside an existing Granite install (e.g. V7 next to V6): new site names and free ports, nothing existing is touched",
        AutoSize = true,
        MaximumSize = new Size(660, 0),
        Margin = new Padding(0, 8, 0, 0)
    };
    private readonly Button _btnFindPorts = MakeButton("Find free ports", 120, new Padding(0, 8, 6, 0));
    private readonly Label _lblPorts = new() { AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 6, 0, 0) };
    private InstallContext? _context;
    private bool _loading;
    private int _scanGeneration;

    public override string StepTitle => "Step 4 of 6: Websites";

    public Step4WebsitesControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("Address users and scanners will type to reach this server:"));
        page.Controls.Add(_cmbHost);
        page.Controls.Add(_lblHostHint);
        page.Controls.Add(MakeHint(
            "Web Desktop and Process App are configured to call the Business API at this address, so it must not change. " +
            "A fixed IP works for scanners on Wi-Fi without a DNS entry. If this server's IP comes from DHCP, use its name " +
            "(or reserve the IP on the DHCP server). Step 5 puts the address on the HTTPS certificate. If it changes later, " +
            "use Change server address in the toolkit."));

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
        page.Controls.Add(_chkAlongside);
        page.Controls.Add(MakeRow(_btnFindPorts));
        page.Controls.Add(_lblPorts);
        page.Controls.Add(MakeHint(
            "Ports are checked against the IIS sites on this server, anything else listening, and the ranges Windows " +
            "reserves for Hyper-V, WSL and Docker. Suggested ports stay between 1024 and 49151, shifted as a block " +
            "where possible (40080-40099 becomes 40180-40199)."));
        page.Controls.Add(_chkFirewall);
        page.Controls.Add(_chkReplace);
        Controls.Add(page);

        _cmbHost.TextChanged += (_, _) => RefreshUrls();
        _chkAlongside.CheckedChanged += async (_, _) => { if (!_loading) await AlongsideChangedAsync(); };
        _chkReplace.CheckedChanged += async (_, _) => { if (!_loading) await CheckPortsAsync(suggest: false); };
        _btnFindPorts.Click += async (_, _) => await CheckPortsAsync(suggest: true);
    }

    /// <summary>
    /// Ticking "alongside" renames the sites (" V7" from the release name,
    /// or " 2", " 3"...) and picks free ports; replacing is switched off,
    /// because the point is to leave the existing install alone. Unticking
    /// puts the default names and ports back.
    /// </summary>
    private async Task AlongsideChangedAsync()
    {
        bool on = _chkAlongside.Checked;
        _chkReplace.Enabled = !on;
        if (on) _chkReplace.Checked = false;

        if (!on)
        {
            foreach (var comp in GraniteComponent.CoreStack)
            {
                _rows[comp.Key].Name.Text = comp.DefaultSiteName;
                _rows[comp.Key].Port.Value = comp.DefaultPort;
            }
            await CheckPortsAsync(suggest: false);
            return;
        }

        var scan = await ScanAsync();
        if (scan is null) return;
        string suffix = PortPlanner.SuggestNameSuffix(
            GraniteComponent.CoreStack.Select(c => c.DefaultSiteName),
            scan.Sites.Select(s => s.Name),
            _context is null ? null : (_context.ReleaseSourcePath.Length > 0 ? _context.ReleaseSourcePath : _context.ReleaseFolder));
        foreach (var comp in GraniteComponent.CoreStack)
            _rows[comp.Key].Name.Text = comp.DefaultSiteName + suffix;
        ApplySuggestedPorts(scan.Use);
        ShowStatus(scan.Use, suffix.Length == 0
            ? "No existing Granite sites found, so the default names are kept. "
            : $"Site names end in \"{suffix.Trim()}\". ");
    }

    /// <summary>Checks the current ports and, when asked, replaces any that are taken with free ones.</summary>
    private async Task CheckPortsAsync(bool suggest)
    {
        var scan = await ScanAsync();
        if (scan is null) return;
        if (suggest) ApplySuggestedPorts(scan.Use);
        ShowStatus(scan.Use, "");
    }

    private async Task<PortScanner.Scan?> ScanAsync()
    {
        int generation = ++_scanGeneration;
        _btnFindPorts.Enabled = false;
        _lblPorts.ForeColor = Color.DimGray;
        _lblPorts.Text = "Checking ports...";
        try
        {
            // Sites being replaced give up their ports, so they don't count as taken.
            var replaced = _chkReplace.Checked ? EnabledRows().Select(r => r.Name.Text.Trim()).ToList() : new List<string>();
            var scan = await PortScanner.ScanAsync(replaced, CancellationToken.None);
            return generation == _scanGeneration ? scan : null; // a newer check has started
        }
        catch (Exception ex)
        {
            _lblPorts.ForeColor = Color.DarkOrange;
            _lblPorts.Text = $"Couldn't check the ports: {ex.Message}. Pre-flight checks them again before installing.";
            return null;
        }
        finally
        {
            if (generation == _scanGeneration) _btnFindPorts.Enabled = true;
        }
    }

    private void ApplySuggestedPorts(PortUse use)
    {
        var wanted = GraniteComponent.CoreStack.Where(c => IsRowEnabled(c.Key)).Select(c => (c.Key, c.DefaultPort)).ToList();
        try
        {
            foreach (var (key, port) in PortPlanner.Suggest(wanted, use.IsFree))
                _rows[key].Port.Value = port;
        }
        catch (InvalidOperationException ex)
        {
            _lblPorts.ForeColor = Color.Firebrick;
            _lblPorts.Text = ex.Message;
        }
    }

    private void ShowStatus(PortUse use, string prefix)
    {
        var problems = GraniteComponent.CoreStack.Where(c => IsRowEnabled(c.Key))
            .Select(c => (c.Title, Port: (int)_rows[c.Key].Port.Value))
            .Select(x => (x.Title, x.Port, Why: use.WhyTaken(x.Port)))
            .Where(x => x.Why is not null)
            .ToList();
        if (problems.Count == 0)
        {
            _lblPorts.ForeColor = Color.ForestGreen;
            _lblPorts.Text = prefix + "All ports are free.";
        }
        else
        {
            _lblPorts.ForeColor = Color.DarkOrange;
            _lblPorts.Text = prefix + string.Join(" ", problems.Select(p => $"{p.Port} ({p.Title}) is {p.Why}.")) +
                (_chkAlongside.Checked ? " Click \"Find free ports\"." : " Tick \"Install alongside\" or click \"Find free ports\", or tick \"Replace\" to reinstall over the existing sites.");
        }
    }

    private bool IsRowEnabled(string key) => key == GraniteComponent.BusinessApi || _rows[key].Enabled.Checked;

    private IEnumerable<(CheckBox Enabled, TextBox Name, NumericUpDown Port, Label Url)> EnabledRows() =>
        _rows.Where(r => IsRowEnabled(r.Key)).Select(r => r.Value);

    private void RefreshUrls()
    {
        foreach (var (key, r) in _rows)
            r.Url.Text = r.Enabled.Checked ? $"https://{_cmbHost.Text.Trim()}:{r.Port.Value}/" : string.Empty;
        DescribeHost();
    }

    /// <summary>Says what kind of address is chosen, and warns about ones that can change.</summary>
    private void DescribeHost()
    {
        string host = _cmbHost.Text.Trim();
        if (host.Length == 0) { _lblHostHint.Text = ""; return; }
        if (!GraniteAddress.IsValidNewHost(host, out string error)) { _lblHostHint.ForeColor = Color.Firebrick; _lblHostHint.Text = error; return; }
        (_lblHostHint.ForeColor, _lblHostHint.Text) = GraniteAddress.Check(host, _machine) switch
        {
            AddressHealth.Ok when GraniteAddress.IsIPv4(host) => (Color.SeaGreen, "A fixed IP address of this server."),
            AddressHealth.Ok => (Color.SeaGreen, "This server's name: keeps working if its IP changes. Scanners need a DNS entry for it."),
            AddressHealth.DhcpAddress => (Color.DarkOrange, "This IP came from DHCP and can change, which would break Web Desktop and Process App. Reserve it on the DHCP server, or choose the server's name."),
            AddressHealth.NotThisMachine => (Color.DarkOrange, "This server doesn't have that IP address right now."),
            _ => (Color.DimGray, "A DNS name: make sure it points at this server.")
        };
    }

    public override void OnEnter(InstallContext context)
    {
        _context = context;
        _loading = true;
        _cmbHost.Items.Clear();
        _machine = LocalAddressDiscovery.Current();
        foreach (string a in GraniteAddress.Suggestions(_machine))
            _cmbHost.Items.Add(a);
        // Default: a fixed IP if the server has one, otherwise its name (a DHCP IP can change under the install).
        if (string.IsNullOrWhiteSpace(context.PublicHost))
            context.PublicHost = GraniteAddress.DefaultChoice(_machine) ?? (_cmbHost.Items.Count > 0 ? (string)_cmbHost.Items[0]! : "");
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
        _chkAlongside.Checked = context.InstallAlongside;
        _chkReplace.Enabled = !context.InstallAlongside;
        _loading = false;
        RefreshUrls();
        // Say straight away whether the ports are free; nothing is changed until asked.
        _ = CheckPortsAsync(suggest: false);
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
        context.ReplaceExistingSites = _chkReplace.Checked && !_chkAlongside.Checked;
        context.InstallAlongside = _chkAlongside.Checked;

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
        if (!GraniteAddress.IsValidNewHost(_cmbHost.Text, out string hostError)) { error = hostError; return false; }
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
