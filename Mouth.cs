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
    // A file held here this long opens the panel; dropped sooner, it just goes on the shelf.
    readonly System.Windows.Forms.Timer _want = new() { Interval = 600 };
    double _open, _openTarget;
    // Pull it: press and drag, and it stretches into a snake that follows the cursor.
    readonly List<PointF> _path = [];   // screen points from home to the head
    bool _pulling, _retracting;
    Point _pressAt;
    double _retractSpeed;
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

    /// <summary>Moves to the chosen corner (or the top centre) and shows, mouth closed (or gulping).</summary>
    public void Place()
    {
        if (_pulling || _retracting) return;
        var screen = Settings.Current.TargetScreen.Bounds;
        var st = Settings.Current;
        var size = (int)(Canvas * Scale_);
        Bounds = new Rectangle(st.ZoneX(screen, size, 0), st.Bottom ? screen.Bottom - size : screen.Top, size, size);
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
        if (_pulling || _retracting) { SnakeStep(); return; }
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
    /// <param name="center">Middle of the body; the corner default is just inside (0,0).</param>
    /// <param name="angle">Which way it faces, in degrees: 45 out of a corner, 90 down from the top edge.</param>
    public static void Paint(Graphics g, float s, Pose p, PointF? center = null, float angle = 45)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = Ui.Solid(Ui.Accent);
        var dark = Ui.Blend(body, Color.Black, 0.55);
        var k = 8 * s;
        var r = (float)((34 + 12 * p.Open) * s * (1 + p.Squash));
        var c = center ?? new PointF(k, k);
        using (var b = new SolidBrush(body)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var edge = new Pen(Ui.Blend(body, Color.Black, 0.25), 1.2f * s)) g.DrawEllipse(edge, c.X - r, c.Y - r, 2 * r, 2 * r);

        // Facing the middle of the screen: "along" points out of the edge, "across" sideways.
        float dx = MathF.Cos(angle * MathF.PI / 180), dy = MathF.Sin(angle * MathF.PI / 180);
        PointF At(float along, float across) => new(c.X + dx * along - dy * across, c.Y + dy * along + dx * across);

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
                g.RotateTransform(angle);
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
        g.RotateTransform(angle);   // local x points out of the edge, local y across
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
            if (Settings.Current.Center) Paint(g, Scale_, Current(), new PointF(size / 2f, -4 * Scale_), 90);
            else
            {
                ToCorner(g, size);
                Paint(g, Scale_, Current());
            }
        }
        Present(bmp, Location);
    }

    /// <summary>Puts a frame on screen at <paramref name="at"/> (the window takes the bitmap's size).</summary>
    void Present(Bitmap bmp, Point at)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hbmp);
        try
        {
            var dst = new POINT { X = at.X, Y = at.Y };
            var sz = new SIZE { cx = bmp.Width, cy = bmp.Height };
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

    // ---- the snake ----

    /// <summary>Middle of the creature at rest, in screen pixels.</summary>
    PointF Home()
    {
        var st = Settings.Current;
        var s = Scale_;
        var b = Bounds;
        if (st.Center) return new PointF(b.Left + b.Width / 2f, b.Top - 4 * s);
        return new PointF(st.Right ? b.Right - 8 * s : b.Left + 8 * s, st.Bottom ? b.Bottom - 8 * s : b.Top + 8 * s);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _retracting) return;
        _pressAt = Cursor.Position;
        _want.Stop();
        Capture = true;   // keep the mouse while it's pulled across the screen
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // Lost the mouse mid-pull (another window grabbed it): let go.
        if (_pulling && !Capture) OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || _retracting) return;
        var p = Cursor.Position;
        if (!_pulling)
        {
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < 6 * Scale_) return;
            _pulling = true;
            _path.Clear();
            _path.Add(Home());
            Grabbed?.Invoke();
        }
        var last = _path[^1];
        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 3 * Scale_) _path.Add(p);
        _tick.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (!_pulling) { Wanted?.Invoke(); return; }   // a plain click opens the panel
        _pulling = false;
        _retracting = true;
        _retractSpeed = 4 * Scale_;
        _tick.Start();
    }

    /// <summary>Pressed and pulled: the panel steps aside.</summary>
    public event Action? Grabbed;

    void SnakeStep()
    {
        if (_retracting)
        {
            // Slither back along the same path, speeding up.
            var budget = _retractSpeed;
            _retractSpeed = Math.Min(_retractSpeed * 1.12, 60 * Scale_);
            while (budget > 0 && _path.Count > 1)
            {
                var a = _path[^1];
                var b = _path[^2];
                var d = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                if (d <= budget) { _path.RemoveAt(_path.Count - 1); budget -= d; continue; }
                var f = (float)(budget / d);
                _path[^1] = new PointF(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
                budget = 0;
            }
            if (_path.Count <= 1)
            {
                _retracting = false;
                _path.Clear();
                Place();
                _gulpStart = DateTime.UtcNow - GulpTime * 0.65;   // ends with the happy face
                _tick.Start();
                return;
            }
        }
        RenderSnake();
    }

    void RenderSnake()
    {
        var s = Scale_;
        var headR = 34 * s * 0.6f;
        var pad = (int)(headR * 2 + 8 * s);
        var minX = _path.Min(p => p.X); var maxX = _path.Max(p => p.X);
        var minY = _path.Min(p => p.Y); var maxY = _path.Max(p => p.Y);
        var box = Rectangle.FromLTRB((int)minX - pad, (int)minY - pad, (int)maxX + pad, (int)maxY + pad);
        box.Intersect(Settings.Current.TargetScreen.Bounds);
        if (box.Width <= 0 || box.Height <= 0) return;

        using var bmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var pts = _path.Select(p => new PointF(p.X - box.Left, p.Y - box.Top)).ToArray();
            var body = Ui.Solid(Ui.Accent);
            if (pts.Length > 1)
            {
                using var edge = new Pen(Ui.Blend(body, Color.Black, 0.25), 28 * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var fill = new Pen(body, 25 * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var belly = new Pen(Ui.Blend(body, Color.White, 0.35), 6 * s)
                    { LineJoin = LineJoin.Round, DashCap = DashCap.Round, DashPattern = [1.2f, 1.6f] };
                g.DrawLines(edge, pts);
                g.DrawLines(fill, pts);
                g.DrawLines(belly, pts);
            }
            // The head faces where it's going.
            var head = pts[^1];
            var from = pts.Length > 3 ? pts[^4] : pts[0];
            var angle = (float)(Math.Atan2(head.Y - from.Y, head.X - from.X) * 180 / Math.PI);
            if (pts.Length < 2 || (head.X == from.X && head.Y == from.Y)) angle = 90;
            Paint(g, s * 0.6f, new Pose(_retracting ? 0 : 0.5), head, angle);
        }
        Present(bmp, box.Location);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // The top edge is where browser tabs live: open only if the mouse stays a moment.
        if (Settings.Current.Center) _want.Start();
        else Wanted?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (Settings.Current.Center && _openTarget == 0) _want.Stop();
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

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        if (!_want.Enabled) return;   // the panel is open: it handles the mouth from here
        _want.Stop();
        _openTarget = 0;
        _tick.Start();
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
