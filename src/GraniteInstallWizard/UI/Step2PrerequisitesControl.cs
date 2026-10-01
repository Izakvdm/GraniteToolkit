using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step2PrerequisitesControl : WizardStepControl
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Width = 660,
        Height = 150
    };
    private readonly Button _btnCheck = MakeButton("Check again", 110, new Padding(0, 8, 8, 0));
    private readonly Label _lblStatus = new() { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };

    private readonly CheckBox _chkIis = new() { Text = "Install missing IIS features", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    private readonly CheckBox _chkRewrite = new() { Text = "Install the IIS URL Rewrite module (Web Desktop needs it)", AutoSize = true };
    private readonly CheckBox _chkNet8 = new() { Text = "Install the ASP.NET Core 8 Hosting Bundle (all four apps run on .NET 8)", AutoSize = true };
    private readonly CheckBox _chkNet6 = new() { Text = "Also install the ASP.NET Core 6 Hosting Bundle (optional; not used by the core stack)", AutoSize = true };

    private IReadOnlyList<PrereqStatus>? _status;
    private bool _checking;

    public override string StepTitle => "Step 2 of 6: Prerequisites";

    public Step2PrerequisitesControl()
    {
        _list.Columns.Add("Component", 210);
        _list.Columns.Add("Status", 80);
        _list.Columns.Add("Detail", 360);

        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(MakeHint(
            "What this server already has. Anything missing is installed during Step 6 from the release's own " +
            "GraniteScaffold\\Prerequisites folder, so the server doesn't need internet access."));
        page.Controls.Add(_list);
        page.Controls.Add(MakeRow(_btnCheck, _lblStatus));
        page.Controls.Add(_chkIis);
        page.Controls.Add(_chkRewrite);
        page.Controls.Add(_chkNet8);
        page.Controls.Add(_chkNet6);
        page.Controls.Add(MakeHint(
            "Installing the Hosting Bundle restarts IIS so it picks up the new module and PATH (Process App starts " +
            "through \"dotnet\" and fails until IIS sees it). Windows may also ask for a server restart; Step 6 says so if it does."));
        Controls.Add(page);

        _btnCheck.Click += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_checking) return;
        _checking = true;
        _btnCheck.Enabled = false;
        ShowResult(_lblStatus, "Checking (DISM can take up to a minute)...", Color.DimGray);
        try
        {
            _status = await new PrerequisiteService(_ => { }).CheckAsync(CancellationToken.None);
            _list.Items.Clear();
            foreach (var s in _status)
            {
                string state = s.Installed ? "OK" : s.Required ? "Missing" : "Optional";
                var item = new ListViewItem(new[] { s.Name, state, s.Detail })
                {
                    ForeColor = s.Installed ? Color.SeaGreen : s.Required ? Color.Firebrick : Color.DimGray
                };
                _list.Items.Add(item);
            }
            int missing = _status.Count(s => s.Required && !s.Installed);
            ShowResult(_lblStatus, missing == 0 ? "Everything the core stack needs is already installed." : $"{missing} missing; installed during Step 6.",
                missing == 0 ? Color.SeaGreen : Color.DarkOrange);
        }
        catch (Exception ex)
        {
            ShowResult(_lblStatus, $"Check failed: {ex.Message}", Color.Firebrick);
        }
        finally
        {
            _btnCheck.Enabled = true;
            _checking = false;
        }
    }

    public override void OnEnter(InstallContext context)
    {
        _chkIis.Checked = context.InstallIisFeatures;
        _chkRewrite.Checked = context.InstallUrlRewrite;
        _chkNet8.Checked = context.InstallDotNet8Hosting;
        _chkNet6.Checked = context.InstallDotNet6Hosting;
        if (_status is null) _ = RefreshAsync();
    }

    public override void OnLeave(InstallContext context)
    {
        context.InstallIisFeatures = _chkIis.Checked;
        context.InstallUrlRewrite = _chkRewrite.Checked;
        context.InstallDotNet8Hosting = _chkNet8.Checked;
        context.InstallDotNet6Hosting = _chkNet6.Checked;
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        if (_status is not null)
        {
            var blocked = new List<string>();
            foreach (var s in _status.Where(s => s.Required && !s.Installed))
            {
                bool willInstall = s.Id switch
                {
                    PrereqId.IisFeatures => _chkIis.Checked,
                    PrereqId.UrlRewrite => _chkRewrite.Checked,
                    PrereqId.DotNet8Hosting => _chkNet8.Checked,
                    _ => true
                };
                if (!willInstall) blocked.Add(s.Name);
            }
            if (blocked.Count > 0)
            {
                error = "These are missing and the core stack needs them, so leave their install options ticked: " + string.Join(", ", blocked) + ".";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }
}
