using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;
using GraniteInstallWizard.UI;

namespace GraniteInstallWizard;

/// <summary>
/// Wizard shell: header, step dots, one step panel at a time, Back/Next.
/// Same structure as the BI Deployment Wizard's MainForm, including its
/// hard-won sizing rules (sized from the screen's working area, no
/// AutoScaleMode.Dpi, auto-sizing buttons -- see that README's v1.5.4 to
/// v1.5.6 entries).
/// </summary>
public sealed class MainForm : Form
{
    private readonly InstallContext _context = new();
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

    /// <summary>From the assembly version (the csproj's &lt;Version&gt;), never hardcoded.</summary>
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
        Text = $"GraniteWMS Install Wizard ({VersionLabel})";
        StartPosition = FormStartPosition.CenterScreen;

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        int width = Math.Clamp((int)(workingArea.Width * 0.85), 900, 1250);
        int height = Math.Clamp((int)(workingArea.Height * 0.85), 700, 980);
        Size = new Size(width, height);
        MinimumSize = new Size(900, 700);
        Font = new Font("Segoe UI", 9F);

        _context.ReleaseFolder = FindReleaseFolder() ?? string.Empty;
        _context.ReleaseSourcePath = _context.ReleaseFolder;

        var installStep = new Step6InstallControl(VersionLabel);
        _steps = new WizardStepControl[]
        {
            new Step1ReleaseControl(),
            new Step2PrerequisitesControl(),
            new Step3SqlServerControl(),
            new Step4WebsitesControl(),
            new Step5CertificateControl(),
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

    /// <summary>
    /// Looks for a V6.0 release next to the wizard, one level up, and in
    /// the usual place under Documents. Only a folder that passes
    /// ReleaseFolderCheck counts; otherwise Step 1 starts empty.
    /// </summary>
    private static string? FindReleaseFolder()
    {
        string exeDir = AppContext.BaseDirectory;
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new[]
        {
            Path.Combine(exeDir, "Granite V6.0"),
            Path.Combine(exeDir, "..", "Granite V6.0"),
            exeDir,
            Path.Combine(docs, "Granite WMS", "Granite V6.0"),
            @"C:\Granite V6.0"
        };
        foreach (string candidate in candidates)
        {
            try
            {
                string full = Path.GetFullPath(candidate);
                if (ReleaseFolderCheck.Problems(full).Count == 0) return full;
            }
            catch { /* invalid path: skip */ }
        }
        return null;
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
        // Keep what was typed on this step even when going back.
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
        if (!_installRunning) return;

        var result = MessageBox.Show(this,
            "An install is still running. Closing now can leave it half done. Close the wizard anyway?",
            "Install in progress",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (result == DialogResult.No)
            e.Cancel = true;
    }
}
