using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace LocalSendDrop;

/// <summary>
/// Windows 11 look: the system's own surface colours, type ramp and corner radii,
/// following light/dark mode and the user's accent colour, switching live.
/// Values are WinUI's (Fluent) tokens flattened onto their usual backgrounds.
/// </summary>
static class Ui
{
    public static bool Dark { get; private set; }

    // Surfaces
    public static Color Flyout;        // panel, popup (flyouts)
    public static Color Base;          // Settings window, panel footer
    public static Color Card;          // setting cards
    public static Color CardStroke;
    public static Color Divider;
    public static Color SubtleHover;   // list item hover
    public static Color SubtlePressed;
    public static Color ControlFill;   // text box, standard button
    public static Color ControlHover;
    public static Color ControlStroke;
    public static Color StrongStroke;  // toggle/radio outlines
    // Text
    public static Color Text, TextSecondary, TextTertiary, TextOnAccent;
    // Accent and status
    public static Color Accent;        // fills (buttons, toggles, progress)
    public static Color AccentHover;
    public static Color AccentText;    // accent-coloured text (links)
    public static Color Error, Success;

    public static event Action? Changed;

    /// <summary>
    /// Runs <paramref name="apply"/> on the control's UI thread whenever the theme
    /// changes, and stops once the control is disposed (closed windows never repaint).
    /// </summary>
    public static void OnChange(Control c, Action apply)
    {
        void Handler()
        {
            if (!c.IsHandleCreated || c.IsDisposed) return;
            try { c.BeginInvoke(() => { if (!c.IsDisposed) apply(); }); }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
        }
        Changed += Handler;
        c.Disposed += (_, _) => Changed -= Handler;
    }

    /// <summary>The three looks people can pick in Settings.</summary>
    public enum Look { Windows11, Windows10, Glass }

    public static Look Style { get; private set; } =
        Enum.TryParse<Look>(Settings.Current.Theme, out var look) ? look : Look.Windows11;

    public static void SetStyle(Look style)
    {
        Style = style;
        Load();
        Changed?.Invoke();
    }

    static Ui()
    {
        Load();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                Load();
                Changed?.Invoke();
            }
        };
    }

    static Color Hex(uint rgb) => Color.FromArgb((int)(0xFF000000 | rgb));

    static bool? _forced;

    /// <summary>Pins light or dark regardless of Windows (used by --snapshot).</summary>
    public static void Force(bool dark) { _forced = dark; Load(); }

    static void Load()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            Dark = _forced ?? (k?.GetValue("AppsUseLightTheme") is int v && v == 0);

        Color accent = Hex(0x0078D4), accentLight1 = Hex(0x4CC2FF), accentDark1 = Hex(0x005FB8), accentLight2 = Hex(0x99EBFF), accentLight3 = Hex(0xC0F0FF), accentDark2 = Hex(0x004C87);
        try
        {
            var ui = new UISettings();
            static Color C(Windows.UI.Color c) => Color.FromArgb(c.R, c.G, c.B);
            accent = C(ui.GetColorValue(UIColorType.Accent));
            accentLight1 = C(ui.GetColorValue(UIColorType.AccentLight1));
            accentDark1 = C(ui.GetColorValue(UIColorType.AccentDark1));
            accentDark2 = C(ui.GetColorValue(UIColorType.AccentDark2));
            accentLight2 = C(ui.GetColorValue(UIColorType.AccentLight2));
            accentLight3 = C(ui.GetColorValue(UIColorType.AccentLight3));
        }
        catch { /* older Windows: Windows' default blue */ }

        if (Dark)
        {
            Flyout = Hex(0x2C2C2C); Base = Hex(0x202020); Card = Hex(0x2B2B2B); CardStroke = Hex(0x1D1D1D);
            Divider = Hex(0x3D3D3D); SubtleHover = Hex(0x383838); SubtlePressed = Hex(0x333333);
            ControlFill = Hex(0x383838); ControlHover = Hex(0x3D3D3D); ControlStroke = Hex(0x454545); StrongStroke = Hex(0x9A9A9A);
            Text = Hex(0xFFFFFF); TextSecondary = Hex(0xC8C8C8); TextTertiary = Hex(0x9D9D9D); TextOnAccent = Hex(0x000000);
            Accent = accentLight2; AccentHover = accentLight3; AccentText = accentLight3;
            Error = Hex(0xFF99A4); Success = Hex(0x6CCB5F);
        }
        else
        {
            Flyout = Hex(0xF9F9F9); Base = Hex(0xF3F3F3); Card = Hex(0xFBFBFB); CardStroke = Hex(0xE5E5E5);
            Divider = Hex(0xE5E5E5); SubtleHover = Hex(0xEDEDED); SubtlePressed = Hex(0xF0F0F0);
            ControlFill = Hex(0xFEFEFE); ControlHover = Hex(0xF6F6F6); ControlStroke = Hex(0xE0E0E0); StrongStroke = Hex(0x8A8A8A);
            Text = Hex(0x1B1B1B); TextSecondary = Hex(0x5D5D5D); TextTertiary = Hex(0x8A8A8A); TextOnAccent = Hex(0xFFFFFF);
            Accent = accentDark1; AccentHover = Color.FromArgb(Math.Min(255, accentDark1.R + 18), Math.Min(255, accentDark1.G + 18), Math.Min(255, accentDark1.B + 18));
            AccentText = accentDark2;
            Error = Hex(0xC42B1C); Success = Hex(0x0F7B0F);
        }

        if (Style == Look.Windows10)
        {
            // Windows 10: flat, square, the accent colour used as is with white text.
            if (Dark)
            {
                Flyout = Hex(0x1F1F1F); Base = Hex(0x171717); Card = Hex(0x262626); CardStroke = Hex(0x262626);
                Divider = Hex(0x333333); SubtleHover = Hex(0x333333); ControlFill = Hex(0x333333); ControlHover = Hex(0x404040);
                ControlStroke = Hex(0x555555); AccentText = accentLight1;
            }
            else
            {
                Flyout = Hex(0xF2F2F2); Base = Hex(0xE6E6E6); Card = Hex(0xFFFFFF); CardStroke = Hex(0xFFFFFF);
                Divider = Hex(0xD9D9D9); SubtleHover = Hex(0xDADADA); ControlFill = Hex(0xFFFFFF); ControlHover = Hex(0xF0F0F0);
                ControlStroke = Hex(0xBFBFBF); AccentText = accentDark1;
            }
            Accent = accent;
            AccentHover = Blend(accent, Color.White, 0.15);
            TextOnAccent = Color.White;
        }
        else if (Style == Look.Glass)
        {
            // Glass: frosted surfaces over a blurred view of what's behind, translucent layers on top.
            static Color A(int a, Color c) => Color.FromArgb(a, c);
            if (Dark)
            {
                Flyout = Hex(0x1C1D27); Base = Hex(0x15161F); Card = A(22, Color.White); CardStroke = A(40, Color.White);
                Divider = A(34, Color.White); SubtleHover = A(30, Color.White); SubtlePressed = A(20, Color.White);
                ControlFill = A(24, Color.White); ControlHover = A(40, Color.White); ControlStroke = A(52, Color.White); StrongStroke = A(170, Color.White);
                Text = Hex(0xFFFFFF); TextSecondary = Hex(0xCDD0DC); TextTertiary = Hex(0x9A9EB0);
                Accent = accentLight1; AccentHover = accentLight2; AccentText = accentLight2; TextOnAccent = Hex(0x0B0C10);
            }
            else
            {
                Flyout = Hex(0xF3F4F9); Base = Hex(0xEBEDF4); Card = A(150, Color.White); CardStroke = A(200, Color.White);
                Divider = A(28, Color.Black); SubtleHover = A(16, Color.Black); SubtlePressed = A(10, Color.Black);
                ControlFill = A(160, Color.White); ControlHover = A(210, Color.White); ControlStroke = A(36, Color.Black); StrongStroke = A(130, Color.Black);
                Text = Hex(0x10121A); TextSecondary = Hex(0x484C5A); TextTertiary = Hex(0x787D8E);
                Accent = accentDark1; AccentHover = Blend(accentDark1, Color.White, 0.15); AccentText = accentDark2; TextOnAccent = Color.White;
            }
        }
    }

    /// <summary>The panel's footer bar: a darker band, or a frosted one on Glass.</summary>
    public static Color Footer => Style == Look.Glass
        ? (Dark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255))
        : Base;

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>A translucent colour flattened onto the page, for controls that can't be see-through (text boxes).</summary>
    public static Color Solid(Color c) => c.A == 255 ? c : Blend(Base, Color.FromArgb(c.R, c.G, c.B), c.A / 255.0);

    // ---- type ramp (Windows 11: Caption 12, Body 14, Subtitle 20, Title 28 px) ----

    static readonly HashSet<string> Families = new(FontFamily.Families.Select(f => f.Name));
    static string Pick(params string[] names) => names.FirstOrDefault(Families.Contains) ?? "Segoe UI";
    static bool Classic => Style == Look.Windows10;
    static string TextFace => Classic ? "Segoe UI" : Pick("Segoe UI Variable Text", "Segoe UI");
    static string TextStrongFace => Classic ? "Segoe UI Semibold" : Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    static string DisplayFace => Classic ? "Segoe UI Light" : Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
    static string SmallFace => Classic ? "Segoe UI" : Pick("Segoe UI Variable Small", "Segoe UI");
    static string IconFace => Classic ? Pick("Segoe MDL2 Assets", "Segoe Fluent Icons") : Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    static readonly Dictionary<(string, float), Font> Fonts = [];
    static Font Get(string face, float pt)
    {
        if (!Fonts.TryGetValue((face, pt), out var f)) Fonts[(face, pt)] = f = new Font(face, pt);
        return f;
    }

    public static Font Caption => Get(SmallFace, 9f);          // 12 px
    public static Font Body => Get(TextFace, 10.5f);           // 14 px
    public static Font BodyStrong => Get(TextStrongFace, 10.5f);
    public static Font Subtitle => Get(DisplayFace, 15f);      // 20 px
    public static Font Title => Get(DisplayFace, 21f);         // 28 px
    public static Font Icon(float pt = 12f) => Get(IconFace, pt);

    // Segoe Fluent Icons code points
    public const string GlyphPc = "", GlyphPhone = "", GlyphGlobe = "", GlyphServer = "";
    public const string GlyphSettings = "", GlyphRefresh = "", GlyphClose = "";
    public const string GlyphDown = "", GlyphUp = "", GlyphCheck = "", GlyphMessage = "";
    public const string GlyphFolder = "", GlyphSend = "";
    public const string GlyphQuiet = "", GlyphPeople = "", GlyphPin = "", GlyphQr = "";
    public const string GlyphShelf = "", GlyphLink = "";

    /// <summary>A right-click menu drawn like Windows 11's (rounded, themed).</summary>
    public static ContextMenuStrip Menu()
    {
        var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Body, Padding = new Padding(4) };
        menu.Opening += (_, _) =>
        {
            menu.BackColor = Flyout;
            foreach (ToolStripItem i in menu.Items) { i.ForeColor = Text; i.Padding = new Padding(4, 6, 4, 6); }
        };
        menu.Opened += (_, _) => Chrome(menu.Handle, small: false);
        return menu;
    }

    sealed class MenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Flyout);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            FillRound(e.Graphics, SubtleHover, r, 4 * e.Item.Owner!.DeviceDpi / 96f);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var r = e.TextRectangle;
            if (e.Item is ToolStripMenuItem { Checked: true })
                DrawIcon(e.Graphics, GlyphCheck, 9f, new Rectangle(e.Item.Width - r.Height - 8, r.Top, r.Height, r.Height), Text);
            TextRenderer.DrawText(e.Graphics, e.Text, Body, r, e.Item.Enabled ? Text : TextTertiary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Divider);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { } // drawn with the text
    }

    public static string DeviceGlyph(Device d) => d.DeviceType switch
    {
        "mobile" => GlyphPhone, "web" => GlyphGlobe, "headless" or "server" => GlyphServer, _ => GlyphPc,
    };

    public static string DeviceLabel(Device d)
    {
        var model = d.DeviceModel?.Trim();
        if (d.DeviceType is null or "desktop" or "laptop")
            return model switch
            {
                null or "" => "PC",
                "Mac" or "macOS" => "Mac",
                _ => $"{model} PC",   // "Windows PC", "Linux PC"
            };
        var kind = d.DeviceType switch { "mobile" => "phone", "web" => "browser", "headless" or "server" => "server", _ => "device" };
        return string.IsNullOrWhiteSpace(model) ? char.ToUpper(kind[0]) + kind[1..] : $"{model} {kind}";
    }

    // ---- drawing helpers ----

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

    /// <summary>
    /// Corner radius for the current look: square on Windows 10, extra round on Glass.
    /// Pills (radius = half the height) stay pills everywhere.
    /// </summary>
    public static float Radius(RectangleF r, float radius)
    {
        var pill = radius >= Math.Min(r.Width, r.Height) / 2f - 0.5f;
        if (pill || radius <= 0) return radius;
        return Style switch
        {
            Look.Windows10 => 0,
            Look.Glass => Math.Min(radius * 2.5f, Math.Min(r.Width, r.Height) / 2f),
            _ => radius,
        };
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        radius = Radius(r, radius);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        using var p = Round(r, radius);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        radius = Radius(r, radius);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, width);
        using var p = Round(r, radius);
        g.DrawPath(pen, p);
    }

    public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c,
        TextFormatFlags extra = TextFormatFlags.Left | TextFormatFlags.VerticalCenter) =>
        TextRenderer.DrawText(g, text, font, r, c, extra | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    public static void DrawIcon(Graphics g, string glyph, float pt, Rectangle r, Color c) =>
        TextRenderer.DrawText(g, glyph, Icon(pt), r, c,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    /// <summary>Windows 11 progress bar: thin track, 3 px rounded fill.</summary>
    public static void Progress(Graphics g, Rectangle r, double fraction, float scale)
    {
        var mid = r.Top + r.Height / 2f;
        using (var track = new Pen(StrongStroke, Math.Max(1f, scale))) g.DrawLine(track, r.Left, mid, r.Right, mid);
        if (fraction <= 0) return;
        var h = 3 * scale;
        FillRound(g, Accent, new RectangleF(r.Left, mid - h / 2, Math.Max(h, (float)(r.Width * fraction)), h), h / 2);
    }

    // ---- window chrome ----

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Windows 11 rounded corners (small = menus/tooltips radius) and border colour.</summary>
    public static void Chrome(IntPtr hwnd, bool round = true, bool small = false)
    {
        var pref = !round || Style == Look.Windows10 ? 1 : small ? 3 : 2; // DONOTROUND / ROUNDSMALL / ROUND
        try { DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { }
        var dark = Dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { }   // immersive dark mode
        var border = CardStroke.R | (CardStroke.G << 8) | (CardStroke.B << 16);
        if (Dark) border = 0x404040;
        try { DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Raises an always-on-top window above the other always-on-top windows without
    /// taking focus. Windows only keeps the most recently raised one in front, so apps
    /// like Chrome (fullscreen video, picture-in-picture) would otherwise cover ours.
    /// </summary>
    public static void KeepOnTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE

    /// <summary>Dark scrollbars for scrollable panels.</summary>
    public static void ThemeScrollbars(Control c) => SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
}

/// <summary>Base for owner-drawn controls: double-buffered, DPI helper, re-themes live.</summary>
abstract class Drawn : Control
{
    protected bool Hot, Pressed;

    protected Drawn()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Ui.OnChange(this, Invalidate);
    }

    protected float Scale_ => DeviceDpi / 96f;
    protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected Color Surface => Parent is Drawn d ? d.BackFill : Parent?.BackColor ?? Ui.Base;

    /// <summary>
    /// Paints what's behind this control. Translucent looks (Glass) ask the parent to
    /// draw its own background here; opaque ones just fill the parent's colour.
    /// </summary>
    protected void PaintBack(Graphics g)
    {
        if (Surface.A < 255 || Ui.Style == Ui.Look.Glass) ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        else g.Clear(Surface);
    }
    /// <summary>Colour children paint behind themselves.</summary>
    public virtual Color BackFill => Surface;

    protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hot = Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    /// <summary>Windows 11 keyboard focus: a 2 px rectangle just outside the control.</summary>
    protected void FocusRect(Graphics g, Rectangle r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        Ui.StrokeRound(g, Ui.Text, RectangleF.Inflate(r, -1, -1), radius, 2 * Scale_);
    }
}

/// <summary>Windows 11 button: Accent (filled) or standard.</summary>
sealed class PillButton : Drawn
{
    public bool Primary { get; set; }

    public PillButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        TabStop = true;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBack(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = 4 * Scale_;
        if (Primary)
            Ui.FillRound(g, !Enabled ? Ui.ControlStroke : Hot ? Ui.AccentHover : Ui.Accent, r, radius);
        else
        {
            Ui.FillRound(g, Hot ? Ui.ControlHover : Ui.ControlFill, r, radius);
            Ui.StrokeRound(g, Ui.ControlStroke, r, radius);
        }
        FocusRect(g, ClientRectangle, radius);
        var fg = Primary ? Ui.TextOnAccent : Pressed ? Ui.TextSecondary : Ui.Text;
        Ui.DrawText(g, Text, Ui.Body, ClientRectangle, Enabled ? fg : Ui.TextTertiary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }
}

/// <summary>Windows 11 hyperlink button: accent-coloured text.</summary>
sealed class LinkText : Drawn
{
    public LinkText(string text) { Text = text; Cursor = Cursors.Hand; TabStop = true; }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintBack(e.Graphics);
        Ui.DrawText(e.Graphics, Text, Ui.Body, ClientRectangle, !Enabled ? Ui.TextTertiary : Pressed ? Ui.TextSecondary : Ui.AccentText);
        if (Hot && Enabled)
        {
            var w = Math.Min(Width, Measure().Width);
            using var pen = new Pen(Ui.AccentText, Scale_);
            e.Graphics.DrawLine(pen, 0, Height - S(4), w, Height - S(4));
        }
    }

    public Size Measure() => TextRenderer.MeasureText(Text, Ui.Body, Size.Empty, TextFormatFlags.NoPadding);
}
