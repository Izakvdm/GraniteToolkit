using GraniteAttachInstaller.Core;
using GraniteAttachInstaller.Models;

namespace GraniteAttachInstaller.UI;

public sealed class Step2SiteControl : WizardStepControl
{
    public override string StepTitle => "Step 2 of 3: App, IIS site, and process";

    private readonly TextBox _txtProjectPath = MakeTextBox(460);
    private readonly Button _btnBrowseProject = MakeButton("Browse...");
    private readonly TextBox _txtSiteName = MakeTextBox(260);
    private readonly TextBox _txtAppPool = MakeTextBox(260);
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535, Width = 100, Value = 5080 };
    private readonly TextBox _txtPhysicalPath = MakeTextBox(400);
    private readonly Button _btnBrowsePath = MakeButton("Browse...");
    private readonly TextBox _txtProcessName = MakeTextBox(260);
    private readonly TextBox _txtPublicBaseUrl = MakeTextBox(340);
    private readonly Button _btnDetectIp = MakeButton("Detect LAN IP", 120);

    public Step2SiteControl()
    {
        var page = MakePage();

        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("GraniteAttach.csproj"));
        page.Controls.Add(MakeRow(_txtProjectPath, _btnBrowseProject));
        page.Controls.Add(MakeHint("The Granite Attach app project to publish. Auto-detected relative to this installer if it's still inside the Granite Attach folder."));

        page.Controls.Add(MakeFieldLabel("IIS site name"));
        page.Controls.Add(_txtSiteName);
        page.Controls.Add(MakeFieldLabel("App pool name"));
        page.Controls.Add(_txtAppPool);
        page.Controls.Add(MakeFieldLabel("Port"));
        page.Controls.Add(_numPort);
        page.Controls.Add(MakeHint("If a site with this name already exists, it's removed and recreated with these settings (its app pool and files are not deleted)."));

        page.Controls.Add(MakeFieldLabel("Physical path (where the published app files go)"));
        page.Controls.Add(MakeRow(_txtPhysicalPath, _btnBrowsePath));

        page.Controls.Add(MakeFieldLabel("Process name (optional)"));
        page.Controls.Add(_txtProcessName);
        page.Controls.Add(MakeHint("If you already know what you'll call the Granite process that uses the attach step, enter it here - the installer will also set up its three stored procedures and a ready-to-paste WebTemplate file. Leave blank to install just the app and tables, and run this again later once you've decided."));

        page.Controls.Add(MakeFieldLabel("Public base URL (what phones/scanners on this network use to reach the app)"));
        page.Controls.Add(MakeRow(_txtPublicBaseUrl, _btnDetectIp));
        page.Controls.Add(MakeHint("Auto-filled from this machine's LAN IP and the port above. Re-detect if the port changes, or edit directly if this will sit behind a different host name."));

        Controls.Add(page);

        _btnBrowseProject.Click += (_, _) => BrowseProject();
        _btnBrowsePath.Click += (_, _) => BrowsePath();
        _btnDetectIp.Click += (_, _) => DetectIp();
        _numPort.ValueChanged += (_, _) => DetectIp();

        _txtProjectPath.Text = FindDefaultProjectPath();
        DetectIp();
    }

    /// <summary>
    /// Walks up from the running exe's own folder looking for
    /// app/GraniteAttach/GraniteAttach.csproj. Deliberately a search rather
    /// than a fixed "../../../.." depth: that fixed-depth version shipped
    /// broken (it undercounted how many folders deep bin/Debual|Release/
    /// net10.0-windows/[RID/publish] puts the exe), silently fell through to
    /// an empty path, and let a rushed Browse pick land on this installer's
    /// OWN .csproj instead - which is exactly what published the installer
    /// itself into IIS instead of the GraniteAttach app on Sept 29, 2026.
    /// A directory walk is correct regardless of Debug/Release, RID, or
    /// publish-vs-build layout.
    /// </summary>
    private static string FindDefaultProjectPath()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "app", "GraniteAttach", "GraniteAttach.csproj");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* fall through to empty - the field just starts blank */ }
        return string.Empty;
    }

    private void BrowseProject()
    {
        using var dlg = new OpenFileDialog { Filter = "Project file (*.csproj)|*.csproj", Title = "Locate GraniteAttach.csproj" };
        if (!string.IsNullOrWhiteSpace(_txtProjectPath.Text) && File.Exists(_txtProjectPath.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(_txtProjectPath.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtProjectPath.Text = dlg.FileName;
    }

    private void BrowsePath()
    {
        using var dlg = new FolderBrowserDialog { Description = "Where should the published app files go?" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtPhysicalPath.Text = dlg.SelectedPath;
    }

    private void DetectIp()
    {
        var ip = LocalAddressDiscovery.IPv4Addresses().FirstOrDefault();
        _txtPublicBaseUrl.Text = ip is null ? string.Empty : $"http://{ip}:{_numPort.Value}";
    }

    public override void OnEnter(InstallContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ProjectPath)) _txtProjectPath.Text = context.ProjectPath;
        _txtSiteName.Text = context.SiteName;
        _txtAppPool.Text = context.AppPoolName;
        _numPort.Value = context.Port;
        _txtPhysicalPath.Text = context.PhysicalPath;
        _txtProcessName.Text = context.ProcessName;
        if (!string.IsNullOrWhiteSpace(context.PublicBaseUrl)) _txtPublicBaseUrl.Text = context.PublicBaseUrl;
    }

    public override void OnLeave(InstallContext context)
    {
        context.ProjectPath = _txtProjectPath.Text.Trim();
        context.SiteName = _txtSiteName.Text.Trim();
        context.AppPoolName = _txtAppPool.Text.Trim();
        context.Port = (int)_numPort.Value;
        context.PhysicalPath = _txtPhysicalPath.Text.Trim();
        context.ProcessName = _txtProcessName.Text.Trim();
        context.PublicBaseUrl = _txtPublicBaseUrl.Text.Trim();
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_txtProjectPath.Text) || !File.Exists(_txtProjectPath.Text))
        { error = "Locate GraniteAttach.csproj."; return false; }
        if (string.IsNullOrWhiteSpace(_txtSiteName.Text)) { error = "Enter an IIS site name."; return false; }
        if (string.IsNullOrWhiteSpace(_txtAppPool.Text)) { error = "Enter an app pool name."; return false; }
        if (string.IsNullOrWhiteSpace(_txtPhysicalPath.Text)) { error = "Enter a physical path for the published app."; return false; }
        if (string.IsNullOrWhiteSpace(_txtPublicBaseUrl.Text)) { error = "Enter (or detect) the public base URL."; return false; }
        error = string.Empty;
        return true;
    }
}
