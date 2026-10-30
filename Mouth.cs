using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// The "Little mouth" drop zone: a small creature peeking out of the corner. It opens
/// wide when a file comes near, and gobbles what you sent once the panel closes.
/// A layered window with per-pixel alpha, so only the creature itself catches the mouse.
/// </summary>
sealed class Mouth : Form
{
    /// <summary>One frame: how wide the mouth is, the food on its way in, the mood.</summary>
    public record struct Pose(double Open, double Squash = 0, double Food = -1, Image? FoodImage = null,
        bool Happy = false, bool Blink = false);

    const int Canvas = 112;
    static readonly TimeSpan GulpTime = TimeSpan.FromMilliseconds(1300), BlinkTime = TimeSpan.FromMilliseconds(160);

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer _blink = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer _want = new() { Interval = 220 };
    double _open, _openTarget;
    DateTime _gulpStart = DateTime.MinValue, _blinkStart = DateTime.MinValue;
    Image? _food;

    /// <summary>A file or the mouse arrived: open the panel.</summary>
    public event Action? Wanted;
    /// <summary>Files dropped straight into the mouth (they go on the shelf).</summary>
    public event Action<string[]>? Ate;

    public Mouth()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        _tick.Tick += (_, _) => Step();
        _blink.Tick += (_, _) => { _blinkStart = DateTime.UtcNow; _tick.Start(); };
        _want.Tick += (_, _) => { _want.Stop(); Wanted?.Invoke(); };
        Ui.Changed += OnThemeChanged;
    }

    void OnThemeChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Render);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 /*WS_EX_LAYERED*/ | 0x80 /*WS_EX_TOOLWINDOW*/ | 0x8 /*WS_EX_TOPMOST*/ | 0x8000000 /*WS_EX_NOACTIVATE*/;
            return cp;
        }
    }

    float Scale_ => DeviceDpi / 96f;

    /// <summary>Moves to the chosen corner and shows, mouth closed (or gulping).</summary>
    public void Place()
    {
        var screen = Settings.Current.TargetScreen.Bounds;
        var st = Settings.Current;
        var size = (int)(Canvas * Scale_);
        Bounds = new Rectangle(st.Right ? screen.Right - size : screen.Left, st.Bottom ? screen.Bottom - size : screen.Top, size, size);
        if (!Visible) Show();
        Ui.KeepOnTop(Handle);
        _blink.Start();
        Render();
    }

    /// <summary>Half open while the panel is up; closed when it's gone.</summary>
    public void Waiting(bool on)
    {
        _openTarget = on ? 0.55 : 0;
        _tick.Start();
    }

    /// <summary>Gobble what was just sent. <paramref name="food"/> is its icon, if there is one.</summary>
    public void Gulp(Image? food)
    {
        _food = food;
        _gulpStart = DateTime.UtcNow;
        _open = _openTarget = 0;
        _tick.Start();
    }

    void Step()
    {
        _open += (_openTarget - _open) * 0.3;
        if (Math.Abs(_openTarget - _open) < 0.01) _open = _openTarget;
        var busy = _open != _openTarget
            || DateTime.UtcNow - _gulpStart < GulpTime
            || DateTime.UtcNow - _blinkStart < BlinkTime;
        Render();
        if (!busy) _tick.Stop();
    }

    Pose Current()
    {
        var gulp = (DateTime.UtcNow - _gulpStart) / GulpTime;
        var blink = DateTime.UtcNow - _blinkStart < BlinkTime;
        return gulp < 1 ? AtGulp(gulp, _food) : new Pose(_open, Blink: blink);
    }

    /// <summary>The gulp, from 0 to 1: the food flies in, three chomps, then a happy swallow.</summary>
    public static Pose AtGulp(double t, Image? food)
    {
        if (t < 0.25) return new Pose(1, Food: t / 0.25, FoodImage: food);
        if (t < 0.65)
        {
            var u = (t - 0.25) / 0.4;
            return new Pose((1 + Math.Cos(u * 6 * Math.PI)) / 2 * (1 - u), Squash: 0.05 * Math.Abs(Math.Sin(u * 6 * Math.PI)));
        }
        return new Pose(0, Squash: 0.08 * Math.Sin((t - 0.65) / 0.35 * Math.PI), Happy: true);
    }

    // ---- drawing ----

    /// <summary>Draws the creature with the corner at (0,0) and the screen towards +x, +y.</summary>
    public static void Paint(Graphics g, float s, Pose p)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = Ui.Solid(Ui.Accent);
        var dark = Ui.Blend(body, Color.Black, 0.55);
        var k = 8 * s;
        var r = (float)((34 + 12 * p.Open) * s * (1 + p.Squash));
        var c = new PointF(k, k);
        using (var b = new SolidBrush(body)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var edge = new Pen(Ui.Blend(body, Color.Black, 0.25), 1.2f * s)) g.DrawEllipse(edge, c.X - r, c.Y - r, 2 * r, 2 * r);

        // Facing the middle of the screen: d points out of the corner, n across it.
        const float h = 0.70710677f;
        PointF At(float along, float across) => new(c.X + h * along - h * across, c.Y + h * along + h * across);

        // Eyes
        var e = (float)(5.5 * s * (1 + 0.15 * p.Open));
        foreach (var side in new[] { -1f, 1f })
        {
            var eye = At(r * 0.40f, side * r * 0.30f);
            if (p.Happy || p.Blink)
            {
                using var pen = new Pen(dark, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var state = g.Save();
                g.TranslateTransform(eye.X, eye.Y);
                g.RotateTransform(45);
                if (p.Happy) g.DrawArc(pen, -e * 0.8f, -e, e * 1.6f, 2 * e, 90, 180);    // ^ ^
                else g.DrawLine(pen, 0, -e, 0, e);                                        // - -
                g.Restore(state);
                continue;
            }
            g.FillEllipse(Brushes.White, eye.X - e, eye.Y - e, 2 * e, 2 * e);
            var pupil = e * 0.52f;
            var look = At(e * 0.40f, 0);
            using var pb = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.FillEllipse(pb, eye.X + (look.X - c.X) - pupil, eye.Y + (look.Y - c.Y) - pupil, 2 * pupil, 2 * pupil);
        }

        // Mouth, across the diagonal.
        var m = At(r * 0.74f, 0);
        var w = (float)((10 + 14 * p.Open) * s);
        var mouthState = g.Save();
        g.TranslateTransform(m.X, m.Y);
        g.RotateTransform(45);   // local x points out of the corner, local y across
        if (p.Open < 0.08)
        {
            using var pen = new Pen(dark, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, -3 * s, -w / 2, 6 * s, w, -90, 180);   // a small smile
        }
        else
        {
            var depth = (float)((2 + 14 * p.Open) * s);
            var hole = new RectangleF(-depth / 2, -w / 2, depth, w);
            using (var b = new SolidBrush(dark)) g.FillEllipse(b, hole);
            using var clip = new GraphicsPath();
            clip.AddEllipse(hole);
            g.SetClip(clip, CombineMode.Intersect);
            using (var tongue = new SolidBrush(Color.FromArgb(240, 120, 140)))
                g.FillEllipse(tongue, depth * 0.05f, -w * 0.3f, depth * 0.7f, w * 0.6f);
            g.ResetClip();
        }
        g.Restore(mouthState);

        // The file on its way in. Drawn upright whichever corner we're in.
        if (p.Food is >= 0 and < 1)
        {
            var f = (float)p.Food;
            var at = At(r * 0.74f + (1 - f) * 64 * s, 0);
            var size = 28 * s * (1 - 0.75f * f);
            var pts = new[] { at };
            g.Transform.TransformPoints(pts);
            var saved = g.Save();
            g.ResetTransform();
            var rect = new RectangleF(pts[0].X - size / 2, pts[0].Y - size / 2, size, size);
            if (p.FoodImage != null) g.DrawImage(p.FoodImage, rect);
            else
            {
                using var paper = Ui.Round(rect, 3 * s);
                g.FillPath(Brushes.White, paper);
                using var pen = new Pen(Color.FromArgb(120, 120, 120), 1f * s);
                g.DrawPath(pen, paper);
            }
            g.Restore(saved);
        }
    }

    /// <summary>Flips the drawing so the corner sits in the chosen corner of a size×size canvas.</summary>
    public static void ToCorner(Graphics g, int size)
    {
        var st = Settings.Current;
        g.TranslateTransform(st.Right ? size : 0, st.Bottom ? size : 0);
        g.ScaleTransform(st.Right ? -1 : 1, st.Bottom ? -1 : 1);
    }

    void Render()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var size = Width;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            ToCorner(g, size);
            Paint(g, Scale_, Current());
        }
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hbmp);
        try
        {
            var dst = new POINT { X = Left, Y = Top };
            var sz = new SIZE { cx = size, cy = size };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hbmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---- mouse and drops ----

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Wanted?.Invoke();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        var files = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
        _openTarget = 1;
        _tick.Start();
        _want.Start();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _want.Stop();
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var food = Thumbs.Preview(files[0], () => { })?.Image;
            BeginInvoke(() => { Gulp(food); Ate?.Invoke(files); });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Ui.Changed -= OnThemeChanged;
            _tick.Dispose();
            _blink.Dispose();
            _want.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
