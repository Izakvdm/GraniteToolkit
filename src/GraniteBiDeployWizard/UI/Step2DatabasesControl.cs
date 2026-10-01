using System.Text.RegularExpressions;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

public sealed partial class Step2DatabasesControl : WizardStepControl
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#]*$")]
    private static partial Regex ValidIdentifier();

    private readonly TextBox _txtSourceDb = new() { Width = 360, Text = "GraniteLive" };
    private readonly TextBox _txtBiDb = new() { Width = 360, Text = "GraniteLive_BI" };

    public override string StepTitle => "Step 3 of 6: Database Names";

    public Step2DatabasesControl()
    {
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true
        };

        layout.Controls.Add(MakeHeading(StepTitle));

        layout.Controls.Add(MakeFieldLabel("Source database (live GraniteWMS):"));
        layout.Controls.Add(_txtSourceDb);

        layout.Controls.Add(MakeFieldLabel("Target BI database (created if missing):"));
        layout.Controls.Add(_txtBiDb);

        layout.Controls.Add(MakeHint(
            "Every occurrence of $(SourceDb) and $(BiDb) in the deployment scripts is " +
            "replaced with these two names before the scripts run -- the same SQLCMD " +
            "variables the .sql files already use."));

        Controls.Add(layout);
    }

    public override void OnEnter(DeploymentContext context)
    {
        _txtSourceDb.Text = context.SourceDb;
        _txtBiDb.Text = context.BiDb;
    }

    public override void OnLeave(DeploymentContext context)
    {
        context.SourceDb = _txtSourceDb.Text.Trim();
        context.BiDb = _txtBiDb.Text.Trim();
    }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        string src = _txtSourceDb.Text.Trim();
        string bi = _txtBiDb.Text.Trim();

        if (!ValidIdentifier().IsMatch(src)) { error = "Source database name is not a valid SQL Server identifier."; return false; }
        if (!ValidIdentifier().IsMatch(bi)) { error = "Target BI database name is not a valid SQL Server identifier."; return false; }
        if (string.Equals(src, bi, StringComparison.OrdinalIgnoreCase)) { error = "Source and BI database names must be different."; return false; }

        // Step 2 already confirmed a specific source database has its
        // reporting views. If the name is changed here to something else,
        // that confirmation no longer applies to this database -- send the
        // user back rather than silently letting an unchecked database
        // through.
        if (!string.IsNullOrEmpty(context.SourceViewsCheckedForDb)
            && !string.Equals(context.SourceViewsCheckedForDb, src, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Source database was changed to [{src}] since Step 2 checked [{context.SourceViewsCheckedForDb}]'s " +
                    "reporting views. Go back to Step 2 and check this database before continuing.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
