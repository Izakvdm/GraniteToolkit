using GraniteNiFiDeploy.Core;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.UI;

public sealed class Step2ServiceControl : WizardStepControl
{
    public override string StepTitle => "Step 2 of 4: NiFi service";

    private readonly TextBox _txtInstallRoot = MakeTextBox(340);
    private readonly TextBox _txtService = MakeTextBox(200);
    private readonly NumericUpDown _numPort = new() { Minimum = 1024, Maximum = 65535, Value = 8443, Width = 90 };
    private readonly ComboBox _cboHeap = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _txtUser = MakeTextBox(200);
    private readonly TextBox _txtPassword = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _txtConfirm = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _txtImportRoot = MakeTextBox(340);
    private readonly TextBox _txtDropAccount = MakeTextBox(260);
    private readonly Label _lblHome = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 2, 0, 0) };

    public Step2ServiceControl()
    {
        _cboHeap.Items.AddRange(new object[] { "1g", "2g", "4g", "8g" });

        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("Install folder"));
        page.Controls.Add(_txtInstallRoot);
        page.Controls.Add(_lblHome);
        page.Controls.Add(MakeHint("NiFi goes in a version folder under this, with the JDBC driver in its drivers folder. Locked down to Administrators and SYSTEM: NiFi's conf folder holds its keys."));

        page.Controls.Add(MakeRow(
            Field("Service name", _txtService),
            Field("HTTPS port", _numPort),
            Field("Memory (heap)", _cboHeap)));
        page.Controls.Add(MakeHint("The service starts automatically and runs as LocalSystem. NiFi listens on this server only: https://localhost:<port>/nifi."));

        page.Controls.Add(MakeFieldLabel("NiFi sign-in"));
        page.Controls.Add(MakeRow(
            Field("User", _txtUser),
            Field("Password", _txtPassword),
            Field("Confirm", _txtConfirm)));
        page.Controls.Add(MakeHint($"At least {InputRules.MinNiFiPassword} characters. Stored only as a hash in NiFi. Keep it in the client's password vault: the toolkit doesn't keep it."));

        page.Controls.Add(MakeFieldLabel("Import folder"));
        page.Controls.Add(_txtImportRoot);
        page.Controls.Add(MakeHint("Gets Inbound\\<Feed>, Archive and Error. Administrators and SYSTEM only by default."));

        page.Controls.Add(MakeFieldLabel("Account that drops CSV files (optional)"));
        page.Controls.Add(_txtDropAccount);
        page.Controls.Add(MakeHint("DOMAIN\\user of the ERP export or integration job. It gets Modify on Inbound only. Leave blank to grant it later."));

        Controls.Add(page);

        _txtInstallRoot.TextChanged += (_, _) => UpdateHome();
    }

    private static FlowLayoutPanel Field(string label, Control control)
    {
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 18, 0) };
        col.Controls.Add(MakeFieldLabel(label));
        col.Controls.Add(control);
        return col;
    }

    private DeployContext? _context;

    private void UpdateHome()
    {
        string folder = _context?.Media?.NiFiFolderName ?? "nifi-x.y.z";
        _lblHome.Text = InputRules.IsValidLocalFolder(_txtInstallRoot.Text, "install folder", out _)
            ? "NiFi home: " + Path.Combine(_txtInstallRoot.Text.Trim().TrimEnd('\\'), folder)
            : string.Empty;
    }

    public override void OnEnter(DeployContext context)
    {
        _context = context;
        _txtInstallRoot.Text = context.InstallRoot;
        _txtService.Text = context.ServiceName;
        _numPort.Value = context.Port;
        _cboHeap.Text = context.HeapSize;
        _txtUser.Text = context.AdminUser;
        _txtPassword.Text = context.AdminPassword;
        _txtConfirm.Text = context.AdminPassword;
        _txtImportRoot.Text = context.ImportRoot;
        _txtDropAccount.Text = context.DropAccount;
        UpdateHome();
    }

    public override void OnLeave(DeployContext context)
    {
        context.InstallRoot = _txtInstallRoot.Text.Trim().TrimEnd('\\');
        context.ServiceName = _txtService.Text.Trim();
        context.Port = (int)_numPort.Value;
        context.HeapSize = _cboHeap.Text.Trim().ToLowerInvariant();
        context.AdminUser = _txtUser.Text.Trim();
        context.AdminPassword = _txtPassword.Text;
        context.ImportRoot = _txtImportRoot.Text.Trim().TrimEnd('\\');
        context.DropAccount = _txtDropAccount.Text.Trim();
    }

    public override bool ValidateStep(DeployContext context, out string error)
    {
        string root = _txtInstallRoot.Text.Trim().TrimEnd('\\');
        string import = _txtImportRoot.Text.Trim().TrimEnd('\\');
        if (!InputRules.IsValidLocalFolder(root, "install folder", out error)) return false;
        if (!InputRules.IsValidLocalFolder(import, "import folder", out error)) return false;
        if (InputRules.Overlap(root, import)) { error = "The install folder and the import folder must be separate (neither inside the other)."; return false; }
        if (!InputRules.IsValidServiceName(_txtService.Text.Trim(), out error)) return false;
        if (!InputRules.IsValidPort((int)_numPort.Value, out error)) return false;
        if (!InputRules.IsValidHeap(_cboHeap.Text.Trim(), out error)) return false;
        if (!InputRules.IsValidNiFiUser(_txtUser.Text.Trim(), out error)) return false;
        if (!InputRules.IsValidNiFiPassword(_txtPassword.Text, _txtConfirm.Text, out error)) return false;

        if (_txtDropAccount.Text.Trim().Length > 0)
        {
            try { NiFiInstaller.ResolveAccount(_txtDropAccount.Text); }
            catch (ArgumentException ex) { error = ex.Message; return false; }
        }

        // The server-side checks, now rather than halfway through the install.
        var probe = new DeployContext { Media = context.Media, InstallRoot = root, ServiceName = _txtService.Text.Trim(), Port = (int)_numPort.Value };
        var problems = NiFiInstaller.PreflightProblems(probe);
        if (problems.Count > 0) { error = string.Join("\n\n", problems); return false; }

        error = string.Empty;
        return true;
    }
}
