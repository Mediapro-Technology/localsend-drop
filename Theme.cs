using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// "Mail drop" look: blue-black ink surfaces, postal yellow only where something
/// can be acted on (drop target, primary button, progress, the corner line),
/// Bahnschrift (Windows' DIN signage face) for names and headings.
/// </summary>
static class Ui
{
    public static readonly Color Ink = Color.FromArgb(0x1A, 0x22, 0x38);
    public static readonly Color Ink2 = Color.FromArgb(0x23, 0x2D, 0x47);
    public static readonly Color InkHover = Color.FromArgb(0x2C, 0x38, 0x57);
    public static readonly Color Line = Color.FromArgb(0x33, 0x40, 0x5F);
    public static readonly Color Text = Color.FromArgb(0xEE, 0xF1, 0xF7);
    public static readonly Color Muted = Color.FromArgb(0x93, 0xA0, 0xBD);
    public static readonly Color Postal = Color.FromArgb(0xFF, 0xC5, 0x30);
    public static readonly Color PostalDim = Color.FromArgb(0x5C, 0x4D, 0x24);
    public static readonly Color Error = Color.FromArgb(0xF2, 0x8B, 0x82);

    static readonly Color[] MonogramColors =
    [
        Color.FromArgb(0x6C, 0x8C, 0xFF), Color.FromArgb(0x4F, 0xC3, 0xA1), Color.FromArgb(0xF2, 0x8B, 0x82),
        Color.FromArgb(0xB3, 0x9D, 0xDB), Color.FromArgb(0x7F, 0xC8, 0xF8), Color.FromArgb(0xE6, 0xA0, 0x6F),
    ];

    static readonly bool HasBahnschrift = FontFamily.Families.Any(f => f.Name == "Bahnschrift");

    static readonly Dictionary<(string, float, FontStyle), Font> Fonts = [];
    static Font Get(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var key = (family, size, style);
        if (!Fonts.TryGetValue(key, out var f)) Fonts[key] = f = new Font(family, size, style);
        return f;
    }

    /// <summary>Names and headings.</summary>
    public static Font Display(float size) => HasBahnschrift ? Get("Bahnschrift SemiBold", size) : Get("Segoe UI Semibold", size);
    public static Font Body(float size = 9.5f) => Get("Segoe UI", size);
    public static Font BodyStrong(float size = 9.5f) => Get("Segoe UI Semibold", size);
    public static Font Glyph(float size) => Get("Segoe MDL2 Assets", size);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        using var p = Round(r, radius);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, width);
        using var p = Round(r, radius);
        g.DrawPath(pen, p);
    }

    public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c,
        TextFormatFlags extra = TextFormatFlags.Left | TextFormatFlags.VerticalCenter) =>
        TextRenderer.DrawText(g, text, font, r, c, extra | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    public static string Initials(string alias)
    {
        var words = alias.Split([' ', '-', '_', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) return "?";
        var s = words.Count == 1 ? words[0][..Math.Min(2, words[0].Length)] : $"{words[0][0]}{words[1][0]}";
        return s.ToUpperInvariant();
    }

    /// <summary>A coloured circle with initials; colour is stable per device.</summary>
    public static void Monogram(Graphics g, Rectangle r, string alias, string key, bool done = false)
    {
        var hash = key.Aggregate(17, (h, ch) => unchecked(h * 31 + ch));
        var color = done ? Postal : MonogramColors[(int)((uint)hash % MonogramColors.Length)];
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(color)) g.FillEllipse(b, r);
        // Font sizes are in points; derive them from the circle's pixel size.
        var pt = (float)Math.Round(r.Height * 72f / g.DpiY * 0.36f * 2) / 2;
        if (done)
            DrawText(g, "", Glyph(pt), r, Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
            DrawText(g, Initials(alias), Display(pt), r, Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    public static string DeviceLabel(Device d)
    {
        var kind = d.DeviceType switch { "mobile" => "phone", "web" => "browser", "headless" or "server" => "server", _ => "PC" };
        return string.IsNullOrWhiteSpace(d.DeviceModel) ? char.ToUpper(kind[0]) + kind[1..] : $"{d.DeviceModel} {kind}";
    }

    // ---- Windows 11 window chrome ----

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Smooth, anti-aliased system corners (Windows 11; ignored elsewhere).</summary>
    public static void SetCorners(IntPtr hwnd, bool round)
    {
        var pref = round ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
        try { DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { }
    }

    public static void SetBorderColor(IntPtr hwnd, Color c)
    {
        var v = c.R | (c.G << 8) | (c.B << 16); // COLORREF
        try { DwmSetWindowAttribute(hwnd, 34, ref v, sizeof(int)); } catch { }
    }
}

/// <summary>Base for owner-drawn controls: double-buffered, DPI helper.</summary>
abstract class Drawn : Control
{
    protected bool Hot;

    protected Drawn()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Ui.Ink;
        ForeColor = Ui.Text;
    }

    protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hot = false; Invalidate(); base.OnMouseLeave(e); }
}

/// <summary>Primary = postal yellow fill; ghost = quiet outline.</summary>
sealed class PillButton : Drawn
{
    public bool Primary { get; set; }

    public PillButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = Height / 2f;
        if (Primary)
            Ui.FillRound(g, Hot ? Color.FromArgb(0xFF, 0xD3, 0x5F) : Ui.Postal, r, radius);
        else
        {
            if (Hot) Ui.FillRound(g, Ui.InkHover, r, radius);
            Ui.StrokeRound(g, Ui.Line, r, radius);
        }
        if (Focused && ShowFocusCues) Ui.StrokeRound(g, Ui.Text, RectangleF.Inflate(r, -2, -2), radius - 2);
        Ui.DrawText(g, Text, Ui.BodyStrong(9.5f), ClientRectangle, Primary ? Ui.Ink : Ui.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }
}

/// <summary>Clickable text (e.g. "Change", "Look again").</summary>
sealed class LinkText : Drawn
{
    public LinkText(string text) { Text = text; Cursor = Cursors.Hand; }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Ui.Ink);
        Ui.DrawText(e.Graphics, Text, Ui.BodyStrong(9.5f), ClientRectangle, Hot ? Ui.Text : Ui.Postal);
    }

    public Size Measure() => TextRenderer.MeasureText(Text, Ui.BodyStrong(9.5f));
}
