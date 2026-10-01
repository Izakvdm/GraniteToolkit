using GraniteAttachInstaller.Models;

namespace GraniteAttachInstaller.UI;

/// <summary>
/// This module's step base class. Everything lives in the shared
/// Granite.Toolkit.UI.WizardStepControl; this only fixes the context type
/// (and the hint width) so the step classes didn't need to change.
/// </summary>
public abstract class WizardStepControl : Granite.Toolkit.UI.WizardStepControl<InstallContext>
{
    protected override int HintMaxWidth => 680;
}
