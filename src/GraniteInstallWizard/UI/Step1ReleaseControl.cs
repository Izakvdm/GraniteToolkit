using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step1ReleaseControl : WizardStepControl
{
    private readonly TextBox _txtRelease = new() { Width = 480 };
    private readonly Button _btnBrowseFolder = MakeButton("Folder...", 90);
    private readonly Button _btnBrowseZip = MakeButton("Zip file...", 90);
    private readonly Label _lblReleaseStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 4, 0, 8) };

    private readonly TextBox _txtInstallRoot = new() { Width = 480 };
    private readonly Button _btnBrowseInstall = MakeButton("Browse...", 90);
    private readonly Button _btnCreateInstall = MakeButton("Create folder", 110);
    private readonly Label _lblInstallStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 4, 0, 4) };

    private readonly TextBox _txtCompany = new() { Width = 300 };
    private readonly ComboBox _cmbDateFormat = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblDateHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 6, 0, 0) };

    private readonly Button _btnLoadProfile = MakeButton("Load saved profile...", 160, new Padding(0, 16, 0, 0));

    private InstallContext? _context;

    /// <summary>The source path (folder or zip) that <see cref="_resolvedRelease"/> belongs to.</summary>
    private string? _resolvedFor;
    private string? _resolvedRelease;
    private bool _extracting;
    private CancellationTokenSource? _extractCts;

    public override string StepTitle => "Step 1 of 6: Release and Install Folder";

    public Step1ReleaseControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(MakeHint(
            "Installs the GraniteWMS core stack on this server: Web Desktop, Business API, Custodian API " +
            "and Process App, with the Granite database, IIS sites and an HTTPS certificate: on a new server, or over an earlier install. " +
            "Nothing is changed until you press Start Install on Step 6, and a dry run there checks everything first."));

        page.Controls.Add(MakeFieldLabel("Granite release (a folder, or the release .zip):"));
        page.Controls.Add(MakeRow(_txtRelease, _btnBrowseFolder, _btnBrowseZip));
        page.Controls.Add(_lblReleaseStatus);
        page.Controls.Add(MakeHint($"A zip is extracted to {ReleaseSource.ExtractionRoot} and reused if you pick the same zip again. The release can sit inside a top-level folder in the zip. V7 releases, where each app is its own zip inside the release, are unpacked there too."));

        page.Controls.Add(MakeFieldLabel("Install folder:"));
        page.Controls.Add(MakeRow(_txtInstallRoot, _btnBrowseInstall, _btnCreateInstall));
        page.Controls.Add(_lblInstallStatus);
        page.Controls.Add(MakeHint("Each component gets its own subfolder here (GraniteBusinessAPI, GraniteWebdesktop, ...). IIS serves the sites from these folders, so pick somewhere permanent."));

        page.Controls.Add(MakeFieldLabel("Company name (shown in Web Desktop and Process App):"));
        page.Controls.Add(_txtCompany);

        page.Controls.Add(MakeFieldLabel("Date format:"));
        _cmbDateFormat.Items.AddRange(DateFormatConverter.Choices.ToArray<object>());
        page.Controls.Add(MakeRow(_cmbDateFormat, _lblDateHint));
        page.Controls.Add(MakeHint(
            "Web Desktop and the Business API spell date formats differently and must agree (the release's own " +
            "Business API appsettings says so). Pick it once here and the wizard writes both spellings."));

        page.Controls.Add(_btnLoadProfile);
        page.Controls.Add(MakeHint("A profile holds every answer except passwords. The wizard saves one to the install folder after each install, so the next server can start from the same answers."));

        Controls.Add(page);

        _btnBrowseFolder.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the Granite release folder", UseDescriptionForTitle = true };
            if (Directory.Exists(_txtRelease.Text)) dialog.SelectedPath = _txtRelease.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) { _txtRelease.Text = dialog.SelectedPath; await ResolveReleaseAsync(); }
        };
        _btnBrowseZip.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Granite release (*.zip)|*.zip", Title = "Select the Granite release zip" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _txtRelease.Text = dialog.FileName; await ResolveReleaseAsync(); }
        };
        // Typed or pasted paths are resolved when the box is left, not per keystroke.
        _txtRelease.Leave += async (_, _) => await ResolveReleaseAsync();
        _txtRelease.TextChanged += (_, _) =>
        {
            if (!_extracting && !string.Equals(_txtRelease.Text.Trim(), _resolvedFor, StringComparison.OrdinalIgnoreCase))
                ShowResult(_lblReleaseStatus, "Checked when you leave this box.", Color.DimGray);
        };

        _btnBrowseInstall.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select or create the install folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(_txtInstallRoot.Text)) dialog.SelectedPath = _txtInstallRoot.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) _txtInstallRoot.Text = dialog.SelectedPath;
        };
        _btnCreateInstall.Click += (_, _) => CreateInstallFolder(askFirst: false);
        _txtInstallRoot.TextChanged += (_, _) => RefreshInstallStatus();

        _cmbDateFormat.SelectedIndexChanged += (_, _) =>
            _lblDateHint.Text = "Business API: " + DateFormatConverter.ToDotNet(_cmbDateFormat.SelectedItem as string ?? "DD/MM/YYYY");
        _btnLoadProfile.Click += async (_, _) => await LoadProfileAsync();
    }

    /// <summary>
    /// Resolves the release box to a release folder: checks a folder (or
    /// finds the release one or two levels inside it), or extracts a zip.
    /// </summary>
    private async Task ResolveReleaseAsync()
    {
        string source = _txtRelease.Text.Trim().Trim('"');
        if (_extracting || source.Length == 0) return;
        if (string.Equals(source, _resolvedFor, StringComparison.OrdinalIgnoreCase) && _resolvedRelease is not null) return;
        _resolvedFor = null;
        _resolvedRelease = null;

        if (ReleaseSource.IsZip(source))
        {
            await UnpackAsync(source, "Extracting the zip",
                (progress, token) => ReleaseSource.ExtractZip(source, WizardDataFolder.PrepareExtractionRoot(), progress, token),
                root => $"Release extracted to {root}.", "Couldn't use that zip");
            return;
        }

        string? found = ReleaseSource.FindReleaseRoot(source);
        if (found is not null)
        {
            Resolved(source, found, string.Equals(found, source, StringComparison.OrdinalIgnoreCase)
                ? "Found a complete Granite release."
                : $"Found a complete Granite release in {found}.");
            return;
        }

        // A V7.0 release unzipped by hand: the apps are still inner zips.
        string? packed = ReleaseSource.FindPackedReleaseRoot(source);
        if (packed is not null)
        {
            await UnpackAsync(source, "Unpacking the app zips in this release",
                (progress, token) => ReleaseSource.PreparePackedFolder(packed, WizardDataFolder.PrepareExtractionRoot(), progress, token),
                root => $"The apps in this release are zipped; unpacked them to {root}.", "Couldn't unpack that release");
            return;
        }

        var problems = ReleaseFolderCheck.Problems(source);
        ShowResult(_lblReleaseStatus, problems.Count > 0 ? string.Join(" ", problems) : "No Granite release found in that folder.", Color.Firebrick);
    }

    /// <summary>Runs an extraction off the UI thread with progress in the status line.</summary>
    private async Task UnpackAsync(string source, string what, Func<IProgress<int>, CancellationToken, string> work,
        Func<string, string> success, string failurePrefix)
    {
        _extracting = true;
        SetBrowseEnabled(false);
        _extractCts = new CancellationTokenSource();
        var progress = new Progress<int>(pct => ShowResult(_lblReleaseStatus, $"{what}... {pct}%", Color.DimGray));
        ShowResult(_lblReleaseStatus, $"{what}...", Color.DimGray);
        try
        {
            var token = _extractCts.Token;
            string root = await Task.Run(() => work(progress, token));
            Resolved(source, root, success(root));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowResult(_lblReleaseStatus, $"{failurePrefix}: {ex.Message}", Color.Firebrick);
        }
        finally
        {
            _extracting = false;
            SetBrowseEnabled(true);
            _extractCts.Dispose();
            _extractCts = null;
        }
    }

    private void Resolved(string source, string root, string message)
    {
        _resolvedFor = source;
        _resolvedRelease = root;
        ShowResult(_lblReleaseStatus, message, Color.SeaGreen);
    }

    private void SetBrowseEnabled(bool enabled)
    {
        _btnBrowseFolder.Enabled = enabled;
        _btnBrowseZip.Enabled = enabled;
        _btnLoadProfile.Enabled = enabled;
        _txtRelease.ReadOnly = !enabled;
    }

    private void RefreshInstallStatus()
    {
        string path = _txtInstallRoot.Text.Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            ShowResult(_lblInstallStatus, @"Enter a full path, e.g. C:\Program Files\GraniteWMS.", Color.Firebrick);
            _btnCreateInstall.Visible = false;
        }
        else if (Directory.Exists(path))
        {
            ShowResult(_lblInstallStatus, "Folder exists.", Color.SeaGreen);
            _btnCreateInstall.Visible = false;
        }
        else
        {
            ShowResult(_lblInstallStatus, "This folder doesn't exist yet. Click Create folder, or it will be offered when you press Next.", Color.DarkOrange);
            _btnCreateInstall.Visible = true;
        }
    }

    /// <returns>True if the folder exists afterwards.</returns>
    private bool CreateInstallFolder(bool askFirst)
    {
        string path = _txtInstallRoot.Text.Trim();
        if (!Path.IsPathFullyQualified(path)) return false;
        if (Directory.Exists(path)) return true;
        if (askFirst)
        {
            var answer = MessageBox.Show(this, $"{path} doesn't exist. Create it now?", "Install folder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes) return false;
        }
        try
        {
            Directory.CreateDirectory(path);
            RefreshInstallStatus();
            return true;
        }
        catch (Exception ex)
        {
            ShowResult(_lblInstallStatus, $"Couldn't create the folder: {ex.Message}", Color.Firebrick);
            return false;
        }
    }

    private async Task LoadProfileAsync()
    {
        if (_context is null) return;
        using var dialog = new OpenFileDialog { Filter = "Install profile (*.json)|*.json", Title = "Load a saved install profile" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            InstallProfile.FromJson(File.ReadAllText(dialog.FileName)).ApplyTo(_context);
            OnEnter(_context);
            await ResolveReleaseAsync();
            MessageBox.Show(this,
                "Profile loaded. Passwords are never saved in profiles, so enter them again on Step 3.",
                "Profile loaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read that profile: {ex.Message}", "Load profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public override void OnEnter(InstallContext context)
    {
        _context = context;
        _txtRelease.Text = string.IsNullOrWhiteSpace(context.ReleaseSourcePath) ? context.ReleaseFolder : context.ReleaseSourcePath;
        if (!string.IsNullOrWhiteSpace(context.ReleaseFolder) && ReleaseFolderCheck.Problems(context.ReleaseFolder).Count == 0)
            Resolved(_txtRelease.Text.Trim(), context.ReleaseFolder, ReleaseSource.IsZip(_txtRelease.Text.Trim())
                ? $"Release extracted to {context.ReleaseFolder}."
                : "Found a complete Granite release.");
        else
            _ = ResolveReleaseAsync();
        _txtInstallRoot.Text = context.InstallRoot;
        _txtCompany.Text = context.CompanyName;
        int idx = _cmbDateFormat.Items.IndexOf(context.DateFormat);
        _cmbDateFormat.SelectedIndex = idx >= 0 ? idx : 0;
        RefreshInstallStatus();
    }

    public override void OnLeave(InstallContext context)
    {
        context.ReleaseSourcePath = _txtRelease.Text.Trim().Trim('"');
        if (_resolvedRelease is not null && string.Equals(_resolvedFor, context.ReleaseSourcePath, StringComparison.OrdinalIgnoreCase))
            context.ReleaseFolder = _resolvedRelease;
        context.InstallRoot = _txtInstallRoot.Text.Trim();
        context.CompanyName = _txtCompany.Text.Trim();
        context.DateFormat = _cmbDateFormat.SelectedItem as string ?? "DD/MM/YYYY";
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        string source = _txtRelease.Text.Trim().Trim('"');
        if (_extracting) { error = "The release is still being unpacked. Next will work once it's done."; return false; }
        if (_resolvedRelease is null || !string.Equals(_resolvedFor, source, StringComparison.OrdinalIgnoreCase))
        {
            _ = ResolveReleaseAsync();
            error = _extracting
                ? "Unpacking the release now. Press Next again when the status line turns green."
                : "Pick a Granite release folder or zip. The status line under the box says what's wrong with the current one.";
            return false;
        }

        string install = _txtInstallRoot.Text.Trim();
        if (!Path.IsPathFullyQualified(install)) { error = @"Enter a full install folder path, e.g. C:\Program Files\GraniteWMS."; return false; }
        string releaseFull = Path.GetFullPath(_resolvedRelease).TrimEnd('\\') + "\\";
        string installFull = Path.GetFullPath(install).TrimEnd('\\') + "\\";
        if (installFull.StartsWith(releaseFull, StringComparison.OrdinalIgnoreCase))
        {
            error = "The install folder can't be inside the release folder.";
            return false;
        }
        if (!Directory.Exists(install) && !CreateInstallFolder(askFirst: true))
        {
            error = "The install folder doesn't exist. Create it, or choose an existing folder.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_txtCompany.Text)) { error = "Enter a company name."; return false; }
        error = string.Empty;
        return true;
    }
}
