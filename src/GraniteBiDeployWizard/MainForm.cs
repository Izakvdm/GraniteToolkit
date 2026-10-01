using GraniteBiDeployWizard.Models;
using GraniteBiDeployWizard.UI;

namespace GraniteBiDeployWizard;

public sealed class MainForm : Form
{
    private readonly DeploymentContext _context = new();
    private readonly WizardStepControl[] _steps;
    private int _currentIndex;
    private bool _deploymentRunning;

    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill };
    private readonly Label _lblStep = new()
    {
        Dock = DockStyle.Top,
        Height = 40,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        Padding = new Padding(16, 0, 0, 0),
        BackColor = Color.FromArgb(31, 41, 55),
        ForeColor = Color.White
    };
    private readonly FlowLayoutPanel _stepDots = new()
    {
        Dock = DockStyle.Top,
        Height = 8,
        BackColor = Color.FromArgb(31, 41, 55),
        Padding = new Padding(16, 0, 0, 8)
    };

    private readonly Button _btnBack = new() { Text = "< Back", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(100, 32) };
    private readonly Button _btnNext = new() { Text = "Next >", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(100, 32) };

    // Read from the assembly's own AssemblyVersion (set from <Version> in
    // the .csproj), not hardcoded, so the title bar can never drift out of
    // sync with what actually got built -- important once multiple .exe
    // copies of this wizard are circulating among implementation resources
    // and someone needs to say "which build is this" during a support call.
    private static string VersionLabel
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version is null ? "dev build" : $"v{version.ToString(3)}";
        }
    }

    public MainForm()
    {
        Text = $"GraniteWMS BI Deployment Wizard ({VersionLabel})";
        StartPosition = FormStartPosition.CenterScreen;

        // A fixed 900x780 (the original size here, before this fix) looks
        // reasonable on a large monitor but can be most of a small-resolution
        // laptop screen's entire working area -- reported on a real machine
        // as Step 1's own content needing an internal scrollbar and the
        // window needing to be dragged bigger by hand on every launch, even
        // though 900x780 is comfortably below common desktop resolutions.
        // A separate attempt to fix this via AutoScaleMode.Dpi did not help
        // (the window really was filling most of the available screen
        // already, just not tall enough for one panel's content plus its
        // own padding) and was later found to clip button text on this
        // machine's DPI setting, so it was removed again -- see the v1.5.5
        // README entry. Sizing off the actual monitor's working area instead
        // of a hardcoded guess fixes the original problem on any screen:
        // generous on a large display, still as large as the screen
        // reasonably allows on a small one, and never below the floor set
        // by MinimumSize.
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        int width = Math.Clamp((int)(workingArea.Width * 0.85), 850, 1200);
        int height = Math.Clamp((int)(workingArea.Height * 0.85), 700, 950);
        Size = new Size(width, height);
        MinimumSize = new Size(850, 700);
        Font = new Font("Segoe UI", 9F);

        _steps = new WizardStepControl[]
        {
            new Step1CredentialsControl(),
            new Step2ReportingViewsControl(),
            new Step2DatabasesControl(),
            new Step3ScriptFolderControl(),
            new Step4ScheduleControl(),
            new Step5DeployLogControl()
        };
        ((Step5DeployLogControl)_steps[5]).RunningChanged += (_, running) =>
        {
            _deploymentRunning = running;
            UpdateButtons();
        };

        BuildLayout();
        ShowStep(0);

        FormClosing += MainForm_FormClosing;
    }

    private void BuildLayout()
    {
        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 12, 16, 12)
        };
        buttonBar.Controls.Add(_btnNext);
        buttonBar.Controls.Add(_btnBack);

        Controls.Add(_contentHost);
        Controls.Add(buttonBar);
        Controls.Add(_stepDots);
        Controls.Add(_lblStep);

        _btnBack.Click += (_, _) => GoBack();
        _btnNext.Click += (_, _) => GoNext();

        BuildStepDots();
    }

    private void BuildStepDots()
    {
        _stepDots.Controls.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            var dot = new Panel
            {
                Width = 28,
                Height = 6,
                Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.FromArgb(75, 85, 99),
                Tag = i
            };
            _stepDots.Controls.Add(dot);
        }
    }

    private void RefreshStepDots()
    {
        foreach (Panel dot in _stepDots.Controls)
        {
            int i = (int)dot.Tag!;
            dot.BackColor = i <= _currentIndex ? Color.FromArgb(56, 189, 248) : Color.FromArgb(75, 85, 99);
        }
    }

    private void ShowStep(int index)
    {
        _contentHost.Controls.Clear();
        var step = _steps[index];
        step.OnEnter(_context);
        _contentHost.Controls.Add(step);

        _lblStep.Text = step.StepTitle;
        _currentIndex = index;
        RefreshStepDots();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool isLast = _currentIndex == _steps.Length - 1;
        _btnBack.Enabled = _currentIndex > 0 && !_deploymentRunning;
        _btnNext.Text = isLast ? "Close" : "Next >";
        _btnNext.Enabled = !_deploymentRunning;
    }

    private void GoBack()
    {
        if (_currentIndex == 0) return;
        ShowStep(_currentIndex - 1);
    }

    private void GoNext()
    {
        var current = _steps[_currentIndex];

        if (!current.ValidateStep(_context, out string error))
        {
            MessageBox.Show(this, error, "Check this step", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        current.OnLeave(_context);

        if (_currentIndex == _steps.Length - 1)
        {
            Close();
            return;
        }

        ShowStep(_currentIndex + 1);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_deploymentRunning) return;

        var result = MessageBox.Show(this,
            "A deployment is still running. Close the wizard anyway?",
            "Deployment in progress",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (result == DialogResult.No)
            e.Cancel = true;
    }
}
