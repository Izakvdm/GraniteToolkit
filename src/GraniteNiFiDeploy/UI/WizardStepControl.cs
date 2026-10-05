using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.UI;

/// <summary>
/// This module's step base class. Everything lives in the shared
/// Granite.Toolkit.UI.WizardStepControl; this fixes the context type and
/// the hint width (same as the Attach installer).
/// </summary>
public abstract class WizardStepControl : Granite.Toolkit.UI.WizardStepControl<DeployContext>
{
    protected override int HintMaxWidth => 680;
}
