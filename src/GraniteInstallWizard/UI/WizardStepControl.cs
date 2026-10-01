using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

/// <summary>
/// Base class for each of the six wizard panels -- the same contract as
/// the BI Deployment Wizard's: MainForm calls OnEnter when a panel becomes
/// visible, ValidateStep before moving forward, and OnLeave to persist the
/// panel's fields into the shared InstallContext.
/// </summary>
public abstract class WizardStepControl : UserControl
{
    protected WizardStepControl()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24);
        BackColor = Color.White;
    }

    /// <summary>Shown in the header, e.g. "Step 1 of 6: Release and Install Folder".</summary>
    public abstract string StepTitle { get; }

    public virtual void OnEnter(InstallContext context) { }

    public virtual void OnLeave(InstallContext context) { }

    public abstract bool ValidateStep(InstallContext context, out string error);

    protected static Label MakeHeading(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 14F, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 16)
    };

    protected static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 9F),
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 8, 0, 2)
    };

    protected static Label MakeHint(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        Margin = new Padding(0, 4, 0, 12)
    };

    /// <summary>
    /// Buttons size to their text (AutoSize, GrowOnly) with the design
    /// width only as a floor -- the BI wizard's v1.5.6 fix for labels
    /// clipped at higher Windows display scaling. Never set a fixed Width.
    /// </summary>
    protected static Button MakeButton(string text, int minWidth = 100, Padding? margin = null) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(minWidth, 0),
        Margin = margin ?? new Padding(0, 0, 6, 0)
    };

    protected static FlowLayoutPanel MakeRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    protected static FlowLayoutPanel MakePage() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        Dock = DockStyle.Fill,
        WrapContents = false,
        AutoScroll = true
    };

    protected static void ShowResult(Label label, string text, Color color)
    {
        label.ForeColor = color;
        label.Text = text;
    }
}
