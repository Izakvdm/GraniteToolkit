namespace Granite.Toolkit.UI;

/// <summary>
/// Base class for every toolkit wizard panel. The host form shows one at a
/// time, calling OnEnter when it becomes visible, ValidateStep before moving
/// forward, and OnLeave to persist the panel's fields into the module's
/// shared context (InstallContext, DeploymentContext and so on).
/// </summary>
/// <remarks>
/// Each module keeps a thin, non-generic WizardStepControl of its own that
/// derives from this one, so its step classes didn't need to change. A
/// module whose hint text should wrap at a different width overrides
/// <see cref="HintMaxWidth"/> there.
/// </remarks>
public abstract class WizardStepControl<TContext> : UserControl
{
    protected WizardStepControl()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24);
        BackColor = Color.White;
    }

    /// <summary>Shown in the header, e.g. "Step 1 of 6: Release and Install Folder".</summary>
    public abstract string StepTitle { get; }

    /// <summary>Called every time this step is shown, after Back or Next navigation.</summary>
    public virtual void OnEnter(TContext context) { }

    /// <summary>Called when leaving this step forward; persist field values into context here.</summary>
    public virtual void OnLeave(TContext context) { }

    /// <summary>Return false with an error message to block moving to the next step.</summary>
    public abstract bool ValidateStep(TContext context, out string error);

    /// <summary>Width hint labels wrap at. The modules used 560 (BI), 620 (Install) and 680 (Attach).</summary>
    protected virtual int HintMaxWidth => 620;

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

    protected Label MakeHint(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        AutoSize = true,
        MaximumSize = new Size(HintMaxWidth, 0),
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

    protected static TextBox MakeTextBox(int width = 340) => new() { Width = width };

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
