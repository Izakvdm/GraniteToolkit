using GraniteNiFiDeploy.Core;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.UI;

public sealed class Step1MediaControl : WizardStepControl
{
    public override string StepTitle => "Step 1 of 4: Install media";

    private readonly TextBox _txtSource = MakeTextBox(480);
    private readonly Button _btnFolder = MakeButton("Folder...");
    private readonly Button _btnZip = MakeButton("Bundle zip...");
    private readonly Button _btnCheck = MakeButton("Check media", 120);
    private readonly Label _lblResult = new() { AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 8, 0, 0), Font = new Font("Consolas", 9F) };

    private MediaSet? _checked;
    private string _checkedSource = string.Empty;
    private bool _busy;

    public Step1MediaControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(new Label
        {
            Text = "NiFi Deploy installs from four downloads you provide, so the server never needs internet access and you control the versions. " +
                   "Point it at the folder that holds them, or at a bundle zip that has them inside.",
            AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 0, 0, 12)
        });

        page.Controls.Add(MakeFieldLabel("Folder or bundle zip"));
        page.Controls.Add(MakeRow(_txtSource, _btnFolder, _btnZip));
        page.Controls.Add(MakeHint(
            "Needed:\n" +
            "  - Apache NiFi 2.x: nifi-2.x.x-bin.zip (nifi.apache.org)\n" +
            "  - Java JDK 21 or newer for Windows x64, as a zip. 21 or 25 (long-term support) is best.\n" +
            "  - NSSM: nssm-2.24.zip (nssm.cc)\n" +
            "  - Microsoft JDBC Driver for SQL Server: sqljdbc_*.zip, or just the mssql-jdbc-*.jre11.jar"));
        page.Controls.Add(MakeRow(_btnCheck));
        page.Controls.Add(_lblResult);
        Controls.Add(page);

        _btnFolder.Click += (_, _) => PickFolder();
        _btnZip.Click += (_, _) => PickZip();
        _btnCheck.Click += async (_, _) => await CheckAsync();
        _txtSource.TextChanged += (_, _) => { if (_txtSource.Text.Trim() != _checkedSource) _checked = null; };
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder with the NiFi, JDK, NSSM and JDBC driver downloads", UseDescriptionForTitle = true };
        if (Directory.Exists(_txtSource.Text)) dlg.SelectedPath = _txtSource.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) { _txtSource.Text = dlg.SelectedPath; _ = CheckAsync(); }
    }

    private void PickZip()
    {
        using var dlg = new OpenFileDialog { Filter = "Zip files (*.zip)|*.zip", Title = "Bundle zip with the NiFi, JDK, NSSM and JDBC driver downloads" };
        if (dlg.ShowDialog(this) == DialogResult.OK) { _txtSource.Text = dlg.FileName; _ = CheckAsync(); }
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        string source = _txtSource.Text.Trim().Trim('"');
        if (source.Length == 0) { ShowResult(_lblResult, "Choose a folder or a bundle zip first.", Color.DarkOrange); return; }

        _busy = true;
        _btnCheck.Enabled = _btnFolder.Enabled = _btnZip.Enabled = false;
        _checked = null;
        ShowResult(_lblResult, "Checking...", Color.DimGray);
        try
        {
            void Progress(string text) => BeginInvoke(new MethodInvoker(() => ShowResult(_lblResult, text, Color.DimGray)));
            var (set, note) = await Task.Run(() => Inspect(source, Progress));
            _checked = set;
            _checkedSource = source;
            string java = set.JavaIsLts ? $"Java {set.JavaVersion} (LTS)" : $"Java {set.JavaVersion}  - not a long-term-support release; 21 or 25 is recommended";
            ShowResult(_lblResult,
                $"OK  NiFi {set.NiFiVersion,-10} {set.NiFi.FileName}\n" +
                $"OK  {java}\n" +
                $"OK  NSSM {set.Nssm.Version ?? "",-10} {set.Nssm.FileName}\n" +
                $"OK  JDBC driver  {set.JdbcJarName}" + (note is null ? "" : "\n\n" + note),
                set.JavaIsLts ? Color.SeaGreen : Color.DarkGoldenrod);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowResult(_lblResult, ex.Message, Color.Firebrick);
        }
        finally
        {
            _busy = false;
            _btnCheck.Enabled = _btnFolder.Enabled = _btnZip.Enabled = true;
        }
    }

    private static (MediaSet Set, string? Note) Inspect(string source, Action<string> progress)
    {
        if (Directory.Exists(source))
            return (MediaCatalog.Inspect(MediaStaging.ScanFolder(source)), null);

        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // A zip that is itself one of the downloads isn't a bundle.
            if (MediaCatalog.Classify(source) is not null)
                throw new InvalidDataException($"{Path.GetFileName(source)} is one of the four downloads, not a bundle. Choose the folder it's in instead.");
            var found = MediaStaging.UnpackBundle(source, progress);
            if (found.Count == 0) throw new InvalidDataException($"{Path.GetFileName(source)} doesn't contain any of the downloads.");
            return (MediaCatalog.Inspect(found), $"Unpacked to {Path.GetDirectoryName(found.Values.First().Path)} (administrators only).");
        }

        throw new InvalidDataException("That isn't a folder or a .zip file.");
    }

    public override void OnEnter(DeployContext context)
    {
        if (_txtSource.Text.Length == 0) _txtSource.Text = context.MediaSource;
        _checked ??= context.Media;
        _checkedSource = context.Media is null ? _checkedSource : context.MediaSource;
    }

    public override void OnLeave(DeployContext context)
    {
        context.MediaSource = _checkedSource;
        context.Media = _checked;
    }

    public override bool ValidateStep(DeployContext context, out string error)
    {
        if (_busy) { error = "Still checking the media."; return false; }
        if (_checked is null || _checkedSource != _txtSource.Text.Trim().Trim('"'))
        {
            error = "Click Check media first. All four downloads have to be found and checked.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
