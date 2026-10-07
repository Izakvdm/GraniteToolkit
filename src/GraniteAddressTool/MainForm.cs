using System.Reflection;
using GraniteAddressTool.Services;

namespace GraniteAddressTool;

/// <summary>
/// One screen: pick the install, see where its apps look for the Business
/// API now, pick the new address, preview exactly what will change, apply.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color GunMetal = ColorTranslator.FromHtml("#1D252C");
    private static readonly Color PacificBlue = ColorTranslator.FromHtml("#00A6CE");
    private static readonly Color Page = ColorTranslator.FromHtml("#F4F4F3");
    private static readonly Color Good = ColorTranslator.FromHtml("#2E7D32");
    private static readonly Color Warn = ColorTranslator.FromHtml("#B26A00");
    private static readonly Color Bad = ColorTranslator.FromHtml("#C62828");
    private static readonly Color Muted = ColorTranslator.FromHtml("#5A6672");

    private readonly ComboBox _cmbInstall = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly Button _btnReload = new() { Text = "Reload", AutoSize = true };
    private readonly ListView _current = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Size = new Size(880, 140) };
    private readonly Label _lblCert = new() { AutoSize = true, MaximumSize = new Size(860, 0), Margin = new Padding(0, 6, 0, 0) };
    private readonly ComboBox _cmbNew = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 320 };
    private readonly Label _lblNewHint = new() { AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Muted, Margin = new Padding(0, 4, 0, 0) };
    private readonly Button _btnPreview = new() { Text = "Preview changes", AutoSize = true };
    private readonly Button _btnApply = new() { Text = "Apply", AutoSize = true, Enabled = false };
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), BackColor = Color.White };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };

    private readonly ToolLog _log = new();
    private IReadOnlyList<GraniteInstall> _installs = Array.Empty<GraniteInstall>();
    private InstallState? _state;
    private AddressPlan? _plan;
    private CertificatePlan? _certPlan;
    private bool _busy;

    public MainForm()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Text = $"GraniteWMS Change Address v{version}";
        Font = new Font("Segoe UI", 9.5F);
        BackColor = Page;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(940, 780);
        MinimumSize = new Size(820, 640);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = GunMetal };
        header.Controls.Add(new Label
        {
            Text = "Change server address",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 14)
        });

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 0) };
        top.Controls.Add(Field("GraniteWMS install"));
        top.Controls.Add(Row(_cmbInstall, _btnReload));
        top.Controls.Add(Field("Where the apps look for the Business API now"));
        _current.Columns.Add("App", 120);
        _current.Columns.Add("Setting", 170);
        _current.Columns.Add("Address", 300);
        _current.Columns.Add("Status", 260);
        top.Controls.Add(_current);
        top.Controls.Add(Field("HTTPS certificate on the Granite sites"));
        top.Controls.Add(_lblCert);
        top.Controls.Add(Field("New address (what users and scanners will type)"));
        top.Controls.Add(Row(_cmbNew, _btnPreview, _btnApply));
        top.Controls.Add(_lblNewHint);
        top.Controls.Add(Field("Preview and log"));

        // The list and the wrapped labels follow the window's width.
        top.Resize += (_, _) =>
        {
            int w = Math.Max(600, top.ClientSize.Width - top.Padding.Horizontal - 4);
            _current.Width = w;
            _current.Columns[3].Width = Math.Max(160, w - _current.Columns[0].Width - _current.Columns[1].Width - _current.Columns[2].Width - 24);
            _lblCert.MaximumSize = _lblNewHint.MaximumSize = new Size(w, 0);
        };

        var outputHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 8) };
        outputHost.Controls.Add(_output);

        Controls.Add(outputHost);
        Controls.Add(_status);
        Controls.Add(top);
        Controls.Add(header);

        _status.Text = _log.Problem is null ? $"Log: {_log.FilePath}" : $"No log file: {_log.Problem}";

        _btnReload.Click += async (_, _) => await LoadInstallsAsync();
        _cmbInstall.SelectedIndexChanged += async (_, _) => await LoadStateAsync();
        _cmbNew.TextChanged += (_, _) => { _plan = null; _btnApply.Enabled = false; DescribeNewHost(); };
        _btnPreview.Click += (_, _) => Preview();
        _btnApply.Click += async (_, _) => await ApplyAsync();
        Shown += async (_, _) => await LoadInstallsAsync();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private static Label Field(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        ForeColor = GunMetal,
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 4)
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in controls) { c.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(c); }
        return row;
    }

    // ---- loading ---------------------------------------------------------------

    private async Task LoadInstallsAsync()
    {
        SetBusy(true, "Reading IIS...");
        try
        {
            var scan = await Task.Run(() => GraniteInstallScanner.ScanAsync(CancellationToken.None));
            _installs = scan.Installs;
            _cmbInstall.Items.Clear();
            foreach (var i in _installs) _cmbInstall.Items.Add(i);
            if (_installs.Count == 0)
            {
                _output.Text = "No GraniteWMS install found in IIS on this server.";
                return;
            }
            _cmbInstall.SelectedIndex = 0; // triggers LoadStateAsync
        }
        catch (Exception ex)
        {
            _output.Text = "Couldn't read IIS: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task LoadStateAsync(bool keepOutput = false)
    {
        if (_cmbInstall.SelectedItem is not GraniteInstall install) return;
        SetBusy(true, "Reading settings and certificates...");
        try
        {
            _state = await Task.Run(() => InstallState.LoadAsync(install, CancellationToken.None));
            ShowCurrent(_state);
            FillSuggestions(_state);
            if (!keepOutput) _output.Text = _state.Problems.Count == 0 ? "" : string.Join(Environment.NewLine, _state.Problems);
        }
        catch (Exception ex)
        {
            _output.Text = "Couldn't read this install: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ShowCurrent(InstallState state)
    {
        _current.Items.Clear();
        foreach (var e in state.Endpoints)
        {
            var health = GraniteAddress.Check(e.Host, state.Machine);
            var item = new ListViewItem(new[] { AddressChange.Title(e.App), e.Key, e.Url, Describe(health) })
            {
                ForeColor = health switch
                {
                    AddressHealth.Ok => Good,
                    AddressHealth.NotThisMachine or AddressHealth.Localhost or AddressHealth.Missing => Bad,
                    AddressHealth.DhcpAddress => Warn,
                    _ => Color.Black
                }
            };
            _current.Items.Add(item);
        }

        var bound = state.Certificates.Where(c => c.Certificate is not null).DistinctBy(c => c.Thumbprint).ToList();
        _lblCert.Text = bound.Count == 0
            ? "No certificate found on the Granite sites' HTTPS ports."
            : string.Join(Environment.NewLine, bound.Select(c =>
                $"{(c.SelfSigned ? "Self-signed" : "Issued by " + c.Certificate!.Issuer)}, valid to {c.Certificate!.NotAfter:yyyy-MM-dd}, for: {string.Join(", ", c.Names)}"));
    }

    private void FillSuggestions(InstallState state)
    {
        _cmbNew.Items.Clear();
        foreach (string h in GraniteAddress.Suggestions(state.Machine)) _cmbNew.Items.Add(h);
        _cmbNew.Text = GraniteAddress.DefaultChoice(state.Machine) ?? "";
        DescribeNewHost();
    }

    private void DescribeNewHost()
    {
        if (_state is null) return;
        string host = _cmbNew.Text.Trim();
        if (!GraniteAddress.IsValidNewHost(host, out string error)) { _lblNewHint.ForeColor = Bad; _lblNewHint.Text = error; return; }
        _lblNewHint.ForeColor = Muted;
        _lblNewHint.Text = GraniteAddress.Check(host, _state.Machine) switch
        {
            AddressHealth.Ok when GraniteAddress.IsIPv4(host) => "A fixed IP address of this server. Works for scanners with no DNS.",
            AddressHealth.Ok => "This server's own name: keeps working when its IP changes. Scanners need a DNS entry for it (Android often can't resolve Windows computer names).",
            AddressHealth.DhcpAddress => "This IP was handed out by DHCP and can change. Fine if it's reserved on the DHCP server; otherwise choose the server's name.",
            AddressHealth.NameNotChecked => "A DNS name that isn't this computer's own name. Make sure DNS points it at this server.",
            AddressHealth.NotThisMachine => "This server doesn't have that IP address right now. Only use it if you're about to give the server that address.",
            _ => ""
        };
    }

    private static string Describe(AddressHealth health) => health switch
    {
        AddressHealth.Ok => "OK: this server",
        AddressHealth.NameNotChecked => "A DNS name (not checked)",
        AddressHealth.DhcpAddress => "This server, but a DHCP address",
        AddressHealth.Localhost => "localhost: only works on the server",
        AddressHealth.NotThisMachine => "Not this server's address any more",
        _ => "Missing"
    };

    // ---- preview and apply -----------------------------------------------------------

    private void Preview()
    {
        if (_state is null) return;
        string host = _cmbNew.Text.Trim();
        if (!GraniteAddress.IsValidNewHost(host, out string error)) { _output.Text = error; return; }

        _plan = AddressChange.Plan(_state.Files, host, _state.Machine);
        _certPlan = AddressApplier.PlanCertificate(_state, host);

        var lines = new List<string> { $"Moving {_state.Install.RootFolder} to {host}", "" };
        if (_plan.NothingToDo) lines.Add("The settings already use this address. Nothing to change in the config files.");
        foreach (var c in _plan.Changes)
            lines.Add($"{AddressChange.Title(c.App),-13} {c.Setting,-22} {c.Old}  ->  {c.New}");
        lines.Add("");
        lines.Add("Certificate: " + _certPlan.Summary);
        foreach (string note in _plan.Notes) lines.Add("Note: " + note);
        lines.Add("");
        lines.Add(_certPlan.Blocked
            ? "Can't apply until the certificate question above is sorted."
            : $"Apply backs up each file it changes to {Path.Combine(ToolkitPaths.DataRoot, "Backups")} (administrators only), makes the changes, recycles the Granite app pools and checks the new address answers.");
        _output.Text = string.Join(Environment.NewLine, lines);
        _btnApply.Enabled = !_certPlan.Blocked && (!_plan.NothingToDo || _certPlan.Reissue);
    }

    private async Task ApplyAsync()
    {
        if (_state is null || _plan is null || _certPlan is null) return;
        var answer = MessageBox.Show(this,
            $"Move {_state.Install.RootFolder} to {_plan.NewHost}?\n\nThe Granite app pools are recycled, so anyone logged in will need to log in again." +
            (_certPlan.Reissue ? "\n\nA new HTTPS certificate is created. Scanners and PCs that trusted the old one need the new .cer." : ""),
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _output.Clear();
        SetBusy(true, "Applying...");
        void Log(LogEntry e)
        {
            _log.Write(e);
            BeginInvoke(() => _output.AppendText($"{e.Prefix} {e.Message}{Environment.NewLine}"));
        }
        try
        {
            var applier = new AddressApplier(Log);
            var state = _state; var plan = _plan; var cert = _certPlan;
            bool ok = await Task.Run(() => applier.ApplyAsync(state, plan, cert, CancellationToken.None));
            _output.AppendText(Environment.NewLine + (ok ? "Address changed. The list above now shows the new settings." : "Not changed. See the log above.") + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log(new LogEntry(LogLevel.Error, ex.Message));
        }
        finally
        {
            SetBusy(false, null);
            _plan = null;
            _btnApply.Enabled = false;
            await LoadStateAsync(keepOutput: true);
        }
    }

    private void SetBusy(bool busy, string? text)
    {
        _busy = busy;
        _cmbInstall.Enabled = _btnReload.Enabled = _btnPreview.Enabled = _cmbNew.Enabled = !busy;
        if (busy) _btnApply.Enabled = false;
        UseWaitCursor = busy;
        if (text is not null) _status.Text = text;
        else _status.Text = _log.Problem is null ? $"Log: {_log.FilePath}" : $"No log file: {_log.Problem}";
    }
}
