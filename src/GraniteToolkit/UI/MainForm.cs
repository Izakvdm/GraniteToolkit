using System.Diagnostics;
using System.Reflection;
using System.Text;
using Granite.Toolkit.Core.Logging;
using Granite.Toolkit.Core.Security;
using GraniteToolkit.Logic;
using GraniteToolkit.Services;

namespace GraniteToolkit.UI;

/// <summary>
/// The toolkit's dashboard: what's on this server (read-only), and the
/// modules installed next to the launcher. One module runs at a time; the
/// status refreshes when it closes.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly SignatureInfo _self;
    private readonly LauncherLog _log;
    private readonly ModuleLauncher _launcher;

    private readonly FlowLayoutPanel _banners = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Page, Padding = new Padding(16, 8, 16, 0) };
    private readonly FlowLayoutPanel _statusList = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = new Padding(12, 8, 12, 8) };
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Page, Padding = new Padding(0, 0, 0, 8) };
    private readonly Label _statusFooter = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.Small };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true, FlatStyle = FlatStyle.System };
    private readonly Button _copy = new() { Text = "Copy summary", AutoSize = true, FlatStyle = FlatStyle.System };

    private readonly Dictionary<ModuleId, (Button Open, Label Line, Panel Card, Label Badge)> _tileControls = new();
    private ServerSnapshot? _snapshot;
    private Process? _running;
    private ModuleInfo? _runningModule;
    private CancellationTokenSource? _refreshCts;

    public MainForm(SignatureInfo self, LauncherLog log, IReadOnlyList<(HealthState State, string Text)> startupBanners)
    {
        _self = self;
        _log = log;
        _launcher = new ModuleLauncher(self, Log);

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Text = $"GraniteWMS Toolkit v{version}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 560);
        Size = new Size(1120, 760);
        BackColor = Theme.Page;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;

        var body = BuildBody();
        var footer = BuildFooter();
        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(_banners);
        Controls.Add(BuildHeader(version));

        foreach (var (state, text) in startupBanners) AddBanner(state, text);

        _refresh.Click += async (_, _) => await RefreshAsync();
        _copy.Click += (_, _) => CopySummary();
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += OnClosing;
    }

    // ---- layout -------------------------------------------------------------

    private Control BuildHeader(string version)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Theme.GunMetal, Padding = new Padding(20, 14, 20, 14) };

        var logo = new PictureBox { Dock = DockStyle.Left, Width = 170, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GraniteToolkit.Assets.granite_reversed.png"))
            if (stream is not null) logo.Image = Image.FromStream(stream);

        var title = new Label
        {
            Text = "Toolkit",
            ForeColor = Color.White,
            Font = new Font(Theme.FontName, 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(196, 18)
        };

        bool signed = _self.State == SignatureState.Valid;
        var badge = new Label
        {
            Text = signed ? $"v{version}  •  Signed by {ShortPublisher(_self.SignerSubject)}" : $"v{version}  •  Development build (not signed)",
            ForeColor = signed ? Theme.PacificBlue : Color.Orange,
            Font = Theme.Small,
            AutoSize = true,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 14, 0, 0)
        };

        header.Controls.Add(title);
        header.Controls.Add(badge);
        header.Controls.Add(logo);
        return header;
    }

    private Control BuildBody()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(16, 12, 16, 8), BackColor = Theme.Page };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // Left: this server
        var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), BackColor = Color.White, Padding = new Padding(1) };
        left.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, left.ClientRectangle, Theme.Rule, ButtonBorderStyle.Solid);
        var leftHead = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White, Padding = new Padding(12, 10, 10, 6) };
        var leftTitle = new Label { Text = "This server", Font = Theme.Section, ForeColor = Theme.GunMetal, AutoSize = true, Dock = DockStyle.Left };
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        leftButtons.Controls.Add(_copy);
        leftButtons.Controls.Add(_refresh);
        leftHead.Controls.Add(leftTitle);
        leftHead.Controls.Add(leftButtons);
        var note = new Label
        {
            Text = "Read-only: nothing here changes the server or connects to SQL Server.",
            Dock = DockStyle.Bottom, Height = 26, ForeColor = Theme.Muted, Font = Theme.Small,
            Padding = new Padding(12, 4, 0, 0), BackColor = Color.White
        };
        left.Controls.Add(_statusList);
        left.Controls.Add(note);
        left.Controls.Add(leftHead);

        // Right: tools
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Page };
        var rightTitle = new Label { Text = "Tools", Font = Theme.Section, ForeColor = Theme.GunMetal, Dock = DockStyle.Top, Height = 34, Padding = new Padding(2, 8, 0, 0) };
        right.Controls.Add(_tiles);
        right.Controls.Add(rightTitle);
        BuildTiles();
        right.Resize += (_, _) => SizeTiles();

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        return grid;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 32, ColumnCount = 2, BackColor = Color.White, Padding = new Padding(16, 0, 16, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var openLogs = new LinkLabel { Text = "Open log folder", AutoSize = true, Anchor = AnchorStyles.Right, LinkColor = Theme.PacificBlue, Font = Theme.Small };
        openLogs.Enabled = _log.FilePath is not null;
        openLogs.LinkClicked += (_, _) => OpenFolder(ToolkitPaths.Logs);
        footer.Controls.Add(_statusFooter, 0, 0);
        footer.Controls.Add(openLogs, 1, 0);
        return footer;
    }

    private void BuildTiles()
    {
        foreach (var module in ModuleCatalog.All)
        {
            if (!ModuleLauncher.IsPresent(module)) continue; // developer tools aren't installed on client servers

            var card = new Panel { Height = 136, Margin = new Padding(0, 0, 0, 10), BackColor = Color.White };
            var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = module.DeveloperOnly ? Theme.Muted : Theme.PacificBlue };
            var title = new Label { Text = module.Title, Font = Theme.TileTitle, ForeColor = Theme.GunMetal, AutoSize = true, Location = new Point(20, 10) };
            var badge = new Label
            {
                Text = module.DeveloperOnly ? "DEVELOPER" : "SUGGESTED",
                Font = new Font(Theme.FontName, 7.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = module.DeveloperOnly ? Theme.Muted : Theme.PacificBlue,
                AutoSize = true,
                Padding = new Padding(6, 2, 6, 2),
                Visible = module.DeveloperOnly
            };
            var desc = new Label { Text = module.Description, ForeColor = Theme.Ink, Font = Theme.Small, Location = new Point(20, 38), AutoSize = false, Height = 50 };
            var line = new Label { Text = "Checking...", ForeColor = Theme.Muted, Font = Theme.Small, Location = new Point(20, 92), AutoSize = false, Height = 34 };
            var open = new Button { Text = "Open", AutoSize = true, MinimumSize = new Size(96, 32), FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Top | AnchorStyles.Right };

            open.Click += (_, _) => Launch(module);
            card.Controls.Add(open);
            card.Controls.Add(badge);
            card.Controls.Add(title);
            card.Controls.Add(desc);
            card.Controls.Add(line);
            card.Controls.Add(accent);
            card.Resize += (_, _) =>
            {
                open.Location = new Point(card.ClientSize.Width - open.Width - 16, 12);
                badge.Location = new Point(title.Right + 10, 15);
                desc.Width = card.ClientSize.Width - 40 - open.Width - 16;
                line.Width = card.ClientSize.Width - 40;
            };
            _tileControls[module.Id] = (open, line, card, badge);
            _tiles.Controls.Add(card);
        }

        if (_tileControls.Count == 0)
            _tiles.Controls.Add(new Label { Text = "No modules found next to the toolkit. Reinstall it.", AutoSize = true, ForeColor = Theme.Bad });
    }

    private void SizeTiles()
    {
        int width = _tiles.ClientSize.Width - (_tiles.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
        foreach (var (_, line, card, _) in _tileControls.Values) card.Width = Math.Max(300, width);
    }

    private void AddBanner(HealthState state, string text)
    {
        var (fore, back) = state switch
        {
            HealthState.Missing => (Theme.Bad, Theme.BadBack),
            HealthState.Attention => (Theme.Warn, Theme.WarnBack),
            _ => (Theme.Good, Theme.GoodBack)
        };
        var banner = new Label
        {
            Text = text,
            ForeColor = fore,
            BackColor = back,
            AutoSize = true,
            MaximumSize = new Size(1400, 0),
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 0, 0, 6),
            Font = Theme.Body
        };
        _banners.Controls.Add(banner);
    }

    // ---- refresh ------------------------------------------------------------

    private async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        _refresh.Enabled = false;
        _statusFooter.Text = "Reading this server...";
        try
        {
            var snapshot = await Task.Run(() => ServerProbe.GatherAsync(token), token);
            if (token.IsCancellationRequested) return;
            _snapshot = snapshot;
            ShowStatus(snapshot);
            ShowTileStatus(snapshot);
            FitToContent();
            _statusFooter.Text = $"Checked at {DateTime.Now:HH:mm:ss}.  Log: {_log.FilePath ?? "not written (see the warning above)"}";
            Log(new LogEntry(LogLevel.Info, $"Server checked: {snapshot.Installs.Count} GraniteWMS install(s), IIS {snapshot.IisVersion ?? "not installed"}."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _statusFooter.Text = "Couldn't read this server: " + ex.Message;
            Log(new LogEntry(LogLevel.Error, "Server check failed: " + ex));
        }
        finally
        {
            _refresh.Enabled = _running is null;
        }
    }

    private bool _fitted;

    /// <summary>
    /// Sizes the window so the server list and every tile show without
    /// scrolling: exactly on the first check, and only ever taller after
    /// that (so a size the operator chose isn't undone). Never maximised,
    /// and never taller than the screen's working area; on a screen too
    /// small for everything, the lists scroll as before.
    /// </summary>
    private void FitToContent()
    {
        if (WindowState != FormWindowState.Normal) return;

        int statusNeeded = _statusList.Padding.Vertical + _statusList.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        int tilesNeeded = _tiles.Padding.Vertical + _tiles.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        int growth = Math.Max(statusNeeded - _statusList.ClientSize.Height, tilesNeeded - _tiles.ClientSize.Height);
        if (_fitted && growth <= 0) return;
        _fitted = true;

        var area = Screen.FromControl(this).WorkingArea;
        int height = Math.Clamp(Height + growth, MinimumSize.Height, area.Height);
        int width = Math.Min(Width, area.Width);
        if (height == Height && width == Width) return;

        Bounds = new Rectangle(
            area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2,
            width, height);
        SizeTiles();
    }

    private void ShowStatus(ServerSnapshot snapshot)
    {
        _statusList.SuspendLayout();
        _statusList.Controls.Clear();
        string? area = null;
        int width = Math.Max(300, _statusList.ClientSize.Width - 30);
        foreach (var row in Dashboard.StatusRows(snapshot))
        {
            if (row.Area != area)
            {
                area = row.Area;
                _statusList.Controls.Add(new Label
                {
                    Text = area.ToUpperInvariant(),
                    Font = new Font(Theme.FontName, 7.75F, FontStyle.Bold),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new Padding(0, _statusList.Controls.Count == 0 ? 2 : 12, 0, 4)
                });
            }

            var line = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = width, Margin = new Padding(0, 0, 0, 4) };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24F));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            line.Controls.Add(new StatusDot(row.State) { Margin = new Padding(2, 4, 0, 0) }, 0, 0);
            var text = new Label
            {
                Text = row.Title + (string.IsNullOrWhiteSpace(row.Detail) ? "" : Environment.NewLine + row.Detail),
                AutoSize = true,
                MaximumSize = new Size(width - 30, 0),
                ForeColor = Theme.Ink
            };
            line.Controls.Add(text, 1, 0);
            _statusList.Controls.Add(line);
        }
        _statusList.ResumeLayout();
    }

    private void ShowTileStatus(ServerSnapshot snapshot)
    {
        foreach (var (id, (open, line, _, badge)) in _tileControls)
        {
            var status = Dashboard.ForModule(id, snapshot);
            line.Text = status.Line;
            var module = ModuleCatalog.All.First(m => m.Id == id);
            badge.Visible = module.DeveloperOnly || status.Suggested;
        }
        SizeTiles();
    }

    // ---- launching ----------------------------------------------------------

    private void Launch(ModuleInfo module)
    {
        if (_running is not null) return;

        if (module.DeveloperOnly)
        {
            var answer = MessageBox.Show(this,
                $"{module.Title} is a developer tool. It changes which database the GraniteWMS apps on this machine use.\n\nOnly continue on a development or test machine. Continue?",
                module.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        try
        {
            var (process, verdict) = _launcher.Start(module);
            if (process is null)
            {
                MessageBox.Show(this, verdict.Reason, $"{module.Title} was not started", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _running = process;
            _runningModule = module;
            SetRunning(true);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => BeginInvoke(async () =>
            {
                Log(new LogEntry(LogLevel.Info, $"{module.Title} closed (exit code {SafeExitCode(process)})."));
                process.Dispose();
                _running = null;
                _runningModule = null;
                SetRunning(false);
                await RefreshAsync();
            });
        }
        catch (Exception ex)
        {
            Log(new LogEntry(LogLevel.Error, $"{module.Title} couldn't be started: {ex}"));
            MessageBox.Show(this, ex.Message, $"{module.Title} couldn't be started", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>One module at a time: two installers changing IIS together could trip over each other.</summary>
    private void SetRunning(bool running)
    {
        foreach (var (open, _, _, _) in _tileControls.Values) open.Enabled = !running;
        _refresh.Enabled = !running;
        if (running) _statusFooter.Text = $"{_runningModule?.Title} is open. The toolkit refreshes when it closes.";
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_running is { HasExited: false })
        {
            var answer = MessageBox.Show(this,
                $"{_runningModule?.Title} is still open. Closing the toolkit leaves it running. Close the toolkit anyway?",
                "GraniteWMS Toolkit", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { e.Cancel = true; return; }
        }
        _refreshCts?.Cancel();
        Log(new LogEntry(LogLevel.Info, "Toolkit closed."));
    }

    // ---- helpers ------------------------------------------------------------

    private void CopySummary()
    {
        if (_snapshot is null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"GraniteWMS Toolkit server summary ({DateTime.Now:yyyy-MM-dd HH:mm})");
        foreach (var row in Dashboard.StatusRows(_snapshot))
            sb.AppendLine($"[{row.State}] {row.Area}: {row.Title}{(string.IsNullOrWhiteSpace(row.Detail) ? "" : " - " + row.Detail)}");
        Clipboard.SetText(sb.ToString());
        _statusFooter.Text = "Summary copied to the clipboard.";
    }

    private void Log(LogEntry entry) => _log.Write(entry);

    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { folder }, UseShellExecute = false });
    }

    private static string ShortPublisher(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "unknown publisher";
        foreach (string part in subject.Split(','))
        {
            string p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..].Trim('"');
        }
        return subject;
    }
}
