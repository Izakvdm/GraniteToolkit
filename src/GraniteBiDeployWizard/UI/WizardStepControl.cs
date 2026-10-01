using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

/// <summary>
/// Base class for each of the six wizard panels. MainForm hosts one of
/// these at a time, calling OnEnter when it becomes visible, ValidateStep
/// before letting the user move to the next one, and OnLeave to persist the
/// panel's fields back into the shared DeploymentContext.
/// </summary>
public abstract class WizardStepControl : UserControl
{
    protected WizardStepControl()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24);
        BackColor = Color.White;
    }

    /// <summary>Shown in the header, e.g. "Step 1 of 6: SQL Server Connection".</summary>
    public abstract string StepTitle { get; }

    /// <summary>Called every time this step is shown, after Back or Next navigation.</summary>
    public virtual void OnEnter(DeploymentContext context) { }

    /// <summary>Called when leaving this step forward; persist field values into context here.</summary>
    public virtual void OnLeave(DeploymentContext context) { }

    /// <summary>Return false with an error message to block moving to the next step.</summary>
    public abstract bool ValidateStep(DeploymentContext context, out string error);

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
        MaximumSize = new Size(560, 0),
        Margin = new Padding(0, 4, 0, 12)
    };
}
