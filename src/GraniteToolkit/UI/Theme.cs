using GraniteToolkit.Logic;

namespace GraniteToolkit.UI;

/// <summary>GraniteWMS palette (Gun Metal, Pacific Blue, Isabelline) and the launcher's fonts.</summary>
internal static class Theme
{
    public static readonly Color GunMetal = ColorTranslator.FromHtml("#1D252C");
    public static readonly Color PacificBlue = ColorTranslator.FromHtml("#00A6CE");
    public static readonly Color Isabelline = ColorTranslator.FromHtml("#D9D8D6");
    public static readonly Color Page = ColorTranslator.FromHtml("#F4F4F3");
    public static readonly Color Ink = ColorTranslator.FromHtml("#1A1A1A");
    public static readonly Color Muted = ColorTranslator.FromHtml("#5A6672");
    public static readonly Color Rule = ColorTranslator.FromHtml("#C7CDD2");

    public static readonly Color Good = ColorTranslator.FromHtml("#2E7D32");
    public static readonly Color Warn = ColorTranslator.FromHtml("#B26A00");
    public static readonly Color Bad = ColorTranslator.FromHtml("#C62828");

    public static readonly Color WarnBack = ColorTranslator.FromHtml("#FFF4E0");
    public static readonly Color BadBack = ColorTranslator.FromHtml("#FDECEA");
    public static readonly Color GoodBack = ColorTranslator.FromHtml("#E8F5E9");

    public const string FontName = "Segoe UI";

    public static Font Body => new(FontName, 9.5F);
    public static Font Small => new(FontName, 8.75F);
    public static Font Strong => new(FontName, 9.5F, FontStyle.Bold);
    public static Font Section => new(FontName, 11F, FontStyle.Bold);
    public static Font TileTitle => new(FontName, 12F, FontStyle.Bold);

    public static Color For(HealthState state) => state switch
    {
        HealthState.Ok => Good,
        HealthState.Attention => Warn,
        HealthState.Missing => Bad,
        _ => Muted
    };
}

/// <summary>
/// A painted status dot (grey for information), so
/// it looks the same on every server whatever symbol fonts are installed.
/// The row's text always says the same thing in words.
/// </summary>
internal sealed class StatusDot : Control
{
    private readonly HealthState _state;

    public StatusDot(HealthState state)
    {
        _state = state;
        Size = new Size(13, 13);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleName = state.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var brush = new SolidBrush(_state == HealthState.Info ? Theme.Rule : Theme.For(_state));
        e.Graphics.FillEllipse(brush, rect);
    }
}
