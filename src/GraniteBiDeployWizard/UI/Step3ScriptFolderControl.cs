using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

public sealed class Step3ScriptFolderControl : WizardStepControl
{
    private readonly TextBox _txtFolder = new() { Width = 420, ReadOnly = true };
    private readonly Button _btnBrowse = new() { Text = "Browse...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(100, 0), Margin = new Padding(8, 0, 0, 0) };
    private readonly CheckedListBox _list = new()
    {
        Width = 528,
        Height = 220,
        CheckOnClick = true,
        IntegralHeight = false,
        Margin = new Padding(0, 8, 0, 0)
    };
    private readonly Label _lblNote = new()
    {
        AutoSize = true,
        MaximumSize = new Size(528, 0),
        ForeColor = Color.DimGray,
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        Margin = new Padding(0, 6, 0, 0)
    };

    private List<ScriptFileItem> _items = new();

    public override string StepTitle => "Step 4 of 6: Deployment Script Folder";

    public Step3ScriptFolderControl()
    {
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true
        };

        layout.Controls.Add(MakeHeading(StepTitle));
        layout.Controls.Add(MakeFieldLabel("Folder holding the .sql deployment scripts:"));

        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        folderRow.Controls.Add(_txtFolder);
        folderRow.Controls.Add(_btnBrowse);
        layout.Controls.Add(folderRow);
        layout.Controls.Add(MakeHint(
            "Copy the current GraniteWMS BI deployment kit (the numbered .sql files) onto " +
            "this server first, then point this step at that copy -- fresh, for this " +
            "engagement. Do not point this at a folder that lives with the wizard itself, " +
            "or a personal sync/Dropbox folder you also use for other work: either one can " +
            "silently drift out of date after a fix is made elsewhere, and this wizard has " +
            "no way to tell that the scripts it's about to run are stale."));

        layout.Controls.Add(MakeFieldLabel("Scripts that will run, in order (untick to skip):"));
        layout.Controls.Add(_list);
        layout.Controls.Add(_lblNote);
        layout.Controls.Add(MakeHint(
            "Files that use SQLCMD-only directives (:setvar, :r) can't be parsed by this " +
            "wizard's native GO-batch engine and are shown disabled. Files outside the " +
            "standard deploy order (a master orchestrator, a one-off remediation script, " +
            "a standalone SQL-Agent scheduler script for a manual, non-wizard deployment) " +
            "start unticked; tick them back on if you need them. Choosing \"SQL Server " +
            "Agent\" on Step 5 registers the Agent job itself, at whatever interval you " +
            "pick there -- it doesn't need this file ticked on."));

        Controls.Add(layout);

        _btnBrowse.Click += (_, _) => BrowseForFolder();
        _list.ItemCheck += List_ItemCheck;
        _list.SelectedIndexChanged += (_, _) => UpdateNoteFor(_list.SelectedIndex);
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder containing the GraniteWMS BI deployment scripts",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(_txtFolder.Text) ? string.Empty : _txtFolder.Text
        };

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _txtFolder.Text = PathHelper.NormalizeFolder(dialog.SelectedPath);
            RescanFolder();
        }
    }

    private void RescanFolder()
    {
        _items = ScriptFileScanner.Scan(_txtFolder.Text);
        _list.Items.Clear();

        foreach (var item in _items)
        {
            int index = _list.Items.Add(FormatLabel(item));
            _list.SetItemChecked(index, item.Included);
            if (item.UnsupportedDirectives)
                _list.SetItemCheckState(index, CheckState.Indeterminate);
        }

        _lblNote.Text = _items.Count == 0
            ? "No .sql files were found in this folder."
            : $"{_items.Count} script file(s) found.";
    }

    private static string FormatLabel(ScriptFileItem item) =>
        item.UnsupportedDirectives ? $"{item.FileName}  (not supported)" : item.FileName;

    /// <summary>
    /// CheckedListBox has no real per-item "disabled" state, so unsupported
    /// files are locked to Indeterminate here: any attempt to check or
    /// uncheck one is reverted, and the caption keeps calling it out.
    /// </summary>
    private void List_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _items.Count) return;

        if (_items[e.Index].UnsupportedDirectives)
            e.NewValue = CheckState.Indeterminate;

        BeginInvoke(new MethodInvoker(() => UpdateNoteFor(e.Index)));
    }

    private void UpdateNoteFor(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        _lblNote.Text = item.Note ?? $"{item.FileName} will run.";
    }

    public override void OnEnter(DeploymentContext context)
    {
        _txtFolder.Text = context.ScriptFolder;
        if (!string.IsNullOrWhiteSpace(_txtFolder.Text) && Directory.Exists(_txtFolder.Text))
            RescanFolder();
    }

    public override void OnLeave(DeploymentContext context)
    {
        context.ScriptFolder = PathHelper.NormalizeFolder(_txtFolder.Text);

        // Reflect the checklist's current state (including any unsupported
        // files the user can never check) back into the shared items, then
        // sync check state from the list control before saving.
        for (int i = 0; i < _items.Count; i++)
            _items[i].Included = !_items[i].UnsupportedDirectives && _list.GetItemChecked(i);

        context.ScriptFiles.Clear();
        context.ScriptFiles.AddRange(_items);
    }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_txtFolder.Text) || !Directory.Exists(_txtFolder.Text))
        {
            error = "Select a valid script folder.";
            return false;
        }

        bool anyChecked = Enumerable.Range(0, _items.Count)
            .Any(i => !_items[i].UnsupportedDirectives && _list.GetItemChecked(i));
        if (!anyChecked)
        {
            error = "Tick at least one script to run.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
