using GraniteNiFiDeploy.Models;
using GraniteNiFiDeploy.UI;

namespace GraniteNiFiDeploy;

/// <summary>
/// Wizard shell: header, step dots, one step panel at a time, Back/Next.
/// Same structure as the Attach installer's and Install Wizard's MainForm.
/// </summary>
public sealed class MainForm : Form
{
    private readonly DeployContext _context = new();
    private readonly WizardStepControl[] _steps;
    private int _currentIndex;
    private bool _installRunning;

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

    public static string VersionLabel
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version is null ? "dev build" : $"v{version.ToString(3)}";
        }
    }

    public MainForm()
    {
        Text = $"Granite NiFi Deploy ({VersionLabel})";
        StartPosition = FormStartPosition.CenterScreen;

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        int width = Math.Clamp((int)(workingArea.Width * 0.75), 800, 1100);
        int height = Math.Clamp((int)(workingArea.Height * 0.85), 650, 900);
        Size = new Size(width, height);
        MinimumSize = new Size(800, 650);
        Font = new Font("Segoe UI", 9F);

        var installStep = new Step4InstallControl();
        _steps = new WizardStepControl[]
        {
            new Step1MediaControl(),
            new Step2ServiceControl(),
            new Step3DatabaseControl(),
            installStep
        };
        installStep.RunningChanged += (_, running) =>
        {
            _installRunning = running;
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

        for (int i = 0; i < _steps.Length; i++)
        {
            _stepDots.Controls.Add(new Panel
            {
                Width = 28,
                Height = 6,
                Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.FromArgb(75, 85, 99),
                Tag = i
            });
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
        _btnBack.Enabled = _currentIndex > 0 && !_installRunning;
        _btnNext.Text = isLast ? "Close" : "Next >";
        _btnNext.Enabled = !_installRunning;
    }

    private void GoBack()
    {
        if (_currentIndex == 0) return;
        _steps[_currentIndex].OnLeave(_context);
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
        if (!_installRunning)
        {
            _context.ForgetSecrets();
            return;
        }

        var result = MessageBox.Show(this,
            "An install is still running. Closing now can leave it half done. Close the wizard anyway?",
            "Install in progress",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (result == DialogResult.No)
            e.Cancel = true;
    }
}
