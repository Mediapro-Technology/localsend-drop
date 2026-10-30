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
    /// <param name="Hide">0 = at home, 1 = ducked out of sight behind the edge (negative pops out a bit).</param>
    /// <param name="Tilt">Extra head tilt in degrees.</param>
    public record struct Pose(double Open, double Squash = 0, double Food = -1, Image? FoodImage = null,
        bool Happy = false, bool Blink = false, double Hide = 0, double Tilt = 0);

    const int Canvas = 112;
    static readonly TimeSpan GulpTime = TimeSpan.FromMilliseconds(1300), BlinkTime = TimeSpan.FromMilliseconds(160),
        DashTime = TimeSpan.FromMilliseconds(1400), ArriveTime = TimeSpan.FromMilliseconds(1600), WaveTime = TimeSpan.FromMilliseconds(1600);

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer _blink = new() { Interval = 5000 };
    // A file held here this long opens the panel; dropped sooner, it just goes on the shelf.
    readonly System.Windows.Forms.Timer _want = new() { Interval = 600 };
    double _open, _openTarget;
    // Pull it: press and drag, and it stretches into a snake that follows the cursor.
    readonly List<PointF> _chain = [];  // screen points from the head back towards home
    PointF _home, _headVel;
    bool _pulling, _retracting;
    Point _pressAt;
    double _retractSpeed;
    long _snakeStart;
    DateTime _animStart = DateTime.MinValue, _blinkStart = DateTime.MinValue;
    TimeSpan _animTime;
    Func<double, Pose>? _anim;   // what it's acting out, from 0 to 1 over _animTime

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
    /// <param name="travel">Then dash off behind the edge to deliver it, and pop back.</param>
    public void Gulp(Image? food, bool travel = false)
    {
        if (travel) Play(t => AtTravel(t, food), GulpTime + DashTime);
        else Play(t => AtGulp(t, food), GulpTime);
    }

    /// <summary>Something's arriving: pop out with it in its mouth and spit it out.</summary>
    public void Arrive(Image? food) => Play(t => AtArrive(t, food), ArriveTime);

    /// <summary>Someone waved: wiggle hello.</summary>
    public void Wave() => Play(AtWave, WaveTime);

    void Play(Func<double, Pose> anim, TimeSpan time, double from = 0)
    {
        _anim = anim;
        _animTime = time;
        _animStart = DateTime.UtcNow - time * from;
        _open = _openTarget = 0;
        _tick.Start();
    }

    bool Animating => _anim != null && DateTime.UtcNow - _animStart < _animTime;

    void Step()
    {
        if (_pulling || _retracting) { SnakeStep(); return; }
        _open += (_openTarget - _open) * 0.3;
        if (Math.Abs(_openTarget - _open) < 0.01) _open = _openTarget;
        var busy = _open != _openTarget
            || Animating
            || DateTime.UtcNow - _blinkStart < BlinkTime;
        Render();
        if (!busy) _tick.Stop();
    }

    Pose Current()
    {
        var blink = DateTime.UtcNow - _blinkStart < BlinkTime;
        return Animating ? _anim!((DateTime.UtcNow - _animStart) / _animTime) : new Pose(_open, Blink: blink);
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

    /// <summary>Gulp, then off it goes to deliver it: a little wind-up, a dive behind the edge, and a bouncy return.</summary>
    public static Pose AtTravel(double t, Image? food)
    {
        var g = GulpTime / (GulpTime + DashTime);
        if (t < g) return AtGulp(t / g, food);
        var u = (t - g) / (1 - g);
        if (u < 0.15) return new Pose(0, Hide: -0.08 * Math.Sin(u / 0.15 * Math.PI));
        if (u < 0.35) { var k = (u - 0.15) / 0.2; return new Pose(0, Hide: k * k); }
        if (u < 0.65) return new Pose(0, Hide: 1);
        return new Pose(0, Hide: 1 - EaseOutBack((u - 0.65) / 0.35), Happy: true);
    }

    /// <summary>Back from the other PC with something: pop out, open up, spit it out towards you.</summary>
    public static Pose AtArrive(double t, Image? food)
    {
        if (t < 0.35) return new Pose(0.3, Hide: 1 - EaseOutBack(t / 0.35));
        if (t < 0.5) return new Pose(0.3 + (t - 0.35) / 0.15 * 0.7);
        if (t < 0.8) return new Pose(1, Food: 1 - (t - 0.5) / 0.3, FoodImage: food);
        return new Pose(1 - (t - 0.8) / 0.2, Happy: true);
    }

    /// <summary>A happy side-to-side wiggle that settles.</summary>
    public static Pose AtWave(double t) =>
        new(0.35 * (1 - t), Squash: 0.04 * Math.Abs(Math.Sin(t * 6 * Math.PI)), Happy: true, Tilt: 16 * Math.Sin(t * 6 * Math.PI) * (1 - t));

    static double EaseOutBack(double x)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }

    // ---- drawing ----

    /// <summary>Draws the creature with the corner at (0,0) and the screen towards +x, +y.</summary>
    /// <param name="center">Middle of the body; the corner default is just inside (0,0).</param>
    /// <param name="angle">Which way it faces, in degrees: 45 out of a corner, 90 down from the top edge.</param>
    /// <param name="face">Size of the eyes and mouth relative to the body (bigger on the snake's small head).</param>
    /// <param name="color">Body colour; your accent colour unless it's a colleague's creature.</param>
    public static void Paint(Graphics g, float s, Pose p, PointF? center = null, float angle = 45, float face = 1, Color? color = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = color ?? Ui.Solid(Ui.Accent);
        var dark = Ui.Blend(body, Color.Black, 0.55);
        var k = 8 * s;
        var r = (float)((34 + 12 * p.Open) * s * (1 + p.Squash));
        var home = center ?? new PointF(k, k);
        float hx = MathF.Cos(angle * MathF.PI / 180), hy = MathF.Sin(angle * MathF.PI / 180);
        var c = new PointF(home.X - hx * r * 1.4f * (float)p.Hide, home.Y - hy * r * 1.4f * (float)p.Hide);
        angle += (float)p.Tilt;
        using (var b = new SolidBrush(body)) g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var edge = new Pen(Ui.Blend(body, Color.Black, 0.25), 1.2f * s)) g.DrawEllipse(edge, c.X - r, c.Y - r, 2 * r, 2 * r);

        // Facing the middle of the screen: "along" points out of the edge, "across" sideways.
        float dx = MathF.Cos(angle * MathF.PI / 180), dy = MathF.Sin(angle * MathF.PI / 180);
        PointF At(float along, float across) => new(c.X + dx * along - dy * across, c.Y + dy * along + dx * across);

        // Eyes
        var e = (float)(5.5 * s * face * (1 + 0.15 * p.Open));
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
        var w = (float)((10 + 14 * p.Open) * s * face);
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
    void Present(Bitmap bmp, Point at) => Layered.Present(Handle, bmp, at);

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
        if (e.Button != MouseButtons.Left || _retracting || _pulling) return;
        var p = Cursor.Position;
        if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < 6 * Scale_) return;
        _pulling = true;
        _home = Home();
        _chain.Clear();
        _chain.Add(_home);
        _headVel = PointF.Empty;
        _snakeStart = Environment.TickCount64;
        Grabbed?.Invoke();
        _tick.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (!_pulling) { Wanted?.Invoke(); return; }   // a plain click opens the panel
        _pulling = false;
        _retracting = true;
        _retractSpeed = 2 * Scale_;
        _tick.Start();
    }

    /// <summary>Pressed and pulled: the panel steps aside.</summary>
    public event Action? Grabbed;

    float Link => 9 * Scale_;   // spacing between body points
    float HeadR(double open) => (float)((34 + 12 * open) * Scale_ * 0.6);

    void SnakeStep()
    {
        if (_pulling) Pull(Cursor.Position);
        else if (!Retract()) return;
        Relax();
        RenderSnake();
    }

    /// <summary>The head chases the cursor on a spring that gets stiffer to pull the longer the body.</summary>
    void Pull(Point target)
    {
        var head = _chain[0];
        var length = _chain.Count * Link;
        var stiff = 0.14f / (1 + length / (900 * Scale_));
        _headVel = new PointF(_headVel.X * 0.74f + (target.X - head.X) * stiff, _headVel.Y * 0.74f + (target.Y - head.Y) * stiff);
        _chain[0] = new PointF(head.X + _headVel.X, head.Y + _headVel.Y);

        // Each point follows the one in front, so the body traces the path the head took.
        for (var i = 1; i < _chain.Count; i++)
        {
            var a = _chain[i - 1];
            var b = _chain[i];
            var d = Dist(a, b);
            if (d > Link) _chain[i] = new PointF(a.X + (b.X - a.X) / d * Link, a.Y + (b.Y - a.Y) / d * Link);
        }
        // Still joined to home: grow towards it, or drop links that have curled back onto it.
        while (_chain.Count > 1 && Dist(_chain[^2], _home) <= Link) _chain.RemoveAt(_chain.Count - 1);
        while (Dist(_chain[^1], _home) > Link && _chain.Count < 600)
        {
            var t = _chain[^1];
            var d = Dist(t, _home);
            _chain.Add(new PointF(t.X + (_home.X - t.X) / d * Link, t.Y + (_home.Y - t.Y) / d * Link));
        }
    }

    /// <summary>Let go: the head eases back along its own body, slow at first, then quicker.</summary>
    bool Retract()
    {
        var budget = _retractSpeed;
        _retractSpeed = Math.Min(_retractSpeed * 1.09 + 0.4 * Scale_, 70 * Scale_);
        while (budget > 0 && _chain.Count > 1)
        {
            var a = _chain[0];
            var b = _chain[1];
            var d = Dist(a, b);
            if (d <= budget) { _chain.RemoveAt(0); budget -= d; continue; }
            var f = (float)(budget / d);
            _chain[0] = new PointF(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
            budget = 0;
        }
        if (_chain.Count > 1 && Dist(_chain[0], _home) > 1) return true;
        _retracting = false;
        _chain.Clear();
        Place();
        Play(t => AtGulp(t, null), GulpTime, from: 0.65);   // back home: a little squash and a happy face
        return false;
    }

    /// <summary>Body tension: every point drifts a little towards its neighbours, smoothing kinks into curves.</summary>
    void Relax()
    {
        for (var i = 1; i < _chain.Count - 1; i++)
        {
            var a = _chain[i - 1];
            var c = _chain[i + 1];
            var b = _chain[i];
            _chain[i] = new PointF(b.X + ((a.X + c.X) / 2 - b.X) * 0.18f, b.Y + ((a.Y + c.Y) / 2 - b.Y) * 0.18f);
        }
    }

    static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    void RenderSnake()
    {
        if (SnakeFrame() is not { } frame) return;
        using (frame.Image) Present(frame.Image, frame.At);
    }

    /// <summary>Diagnostics (--snapshot): pull along these cursor points, then picture the snake.</summary>
    public Bitmap? SimulatePull(PointF home, IEnumerable<Point> cursor)
    {
        _home = home;
        _chain.Clear();
        _chain.Add(home);
        _headVel = PointF.Empty;
        foreach (var p in cursor) { Pull(p); Relax(); }
        return SnakeFrame()?.Image;
    }

    (Bitmap Image, Point At)? SnakeFrame()
    {
        var s = Scale_;
        var open = _retracting ? 0 : 0.5;
        var headR = HeadR(open);
        var spine = Smooth([.. _chain, _home]);
        var pad = (int)(headR * 2.4f);
        var box = Rectangle.FromLTRB((int)spine.Min(p => p.X) - pad, (int)spine.Min(p => p.Y) - pad,
            (int)spine.Max(p => p.X) + pad, (int)spine.Max(p => p.Y) + pad);
        box.Intersect(Settings.Current.TargetScreen.Bounds);
        if (box.Width <= 0 || box.Height <= 0) return null;

        var bmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(-box.Left, -box.Top);
            var body = Ui.Solid(Ui.Accent);
            if (spine.Length > 1)
            {
                // As wide as the head where it joins, a little slimmer where it comes out; a slow slither runs along it.
                var time = (Environment.TickCount64 - _snakeStart) / 1000f;
                var left = new PointF[spine.Length];
                var right = new PointF[spine.Length];
                var centre = new PointF[spine.Length];
                for (var i = 0; i < spine.Length; i++)
                {
                    var t = i / (float)(spine.Length - 1);
                    var prev = spine[Math.Max(0, i - 1)];
                    var next = spine[Math.Min(spine.Length - 1, i + 1)];
                    var tx = next.X - prev.X; var ty = next.Y - prev.Y;
                    var tl = MathF.Max(0.001f, MathF.Sqrt(tx * tx + ty * ty));
                    var nx = -ty / tl; var ny = tx / tl;
                    var wiggle = MathF.Sin(time * 7 - i * 0.35f) * 2.2f * s * MathF.Sin(t * MathF.PI);
                    var c = new PointF(spine[i].X + nx * wiggle, spine[i].Y + ny * wiggle);
                    var half = headR * (0.92f - 0.22f * t);
                    centre[i] = c;
                    left[i] = new PointF(c.X + nx * half, c.Y + ny * half);
                    right[i] = new PointF(c.X - nx * half, c.Y - ny * half);
                }
                using var outline = new GraphicsPath();
                outline.AddLines(left);
                outline.AddLines(right.Reverse().ToArray());
                outline.CloseFigure();
                using (var fill = new SolidBrush(body)) g.FillPath(fill, outline);
                var tailR = headR * 0.70f;
                var tail = centre[^1];
                using (var fill = new SolidBrush(body)) g.FillEllipse(fill, tail.X - tailR, tail.Y - tailR, 2 * tailR, 2 * tailR);
                using (var edge = new Pen(Ui.Blend(body, Color.Black, 0.25), 1.4f * s) { LineJoin = LineJoin.Round })
                {
                    g.DrawLines(edge, left);
                    g.DrawLines(edge, right);
                }
                // Belly scales down the middle.
                using var belly = new Pen(Ui.Blend(body, Color.White, 0.3), headR * 0.45f)
                    { DashCap = DashCap.Round, DashPattern = [0.6f, 1.1f], LineJoin = LineJoin.Round };
                g.DrawLines(belly, centre);
            }
            // The head looks where it's going.
            var head = spine[0];
            var from = spine[Math.Min(spine.Length - 1, 6)];
            var angle = head == from ? 90 : MathF.Atan2(head.Y - from.Y, head.X - from.X) * 180 / MathF.PI;
            var pts = new[] { head };
            g.Transform.TransformPoints(pts);
            g.ResetTransform();
            Paint(g, s * 0.6f, new Pose(open), pts[0], angle, face: 1.45f);
        }
        return (bmp, box.Location);
    }

    /// <summary>A Catmull-Rom curve through the points: no corners anywhere along the body.</summary>
    static PointF[] Smooth(List<PointF> p)
    {
        if (p.Count < 3) return [.. p];
        var result = new List<PointF>(p.Count * 3);
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(p.Count - 1, i + 2)];
            for (var k = 0; k < 3; k++)
            {
                var t = k / 3f; var t2 = t * t; var t3 = t2 * t;
                result.Add(new PointF(
                    0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        result.Add(p[^1]);
        return [.. result];
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // The top edge is where browser tabs live: open only if the mouse stays a moment.
        if (Settings.Current.Center) { _want.Interval = 350; _want.Start(); }
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
        if (Settings.Current.OpenDelay <= 0) { Wanted?.Invoke(); return; }
        _want.Interval = Settings.Current.OpenDelay;
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
}

/// <summary>Puts a per-pixel-alpha frame on a layered window (WS_EX_LAYERED), moving and sizing it to match.</summary>
static class Layered
{
    public static void Present(IntPtr hwnd, Bitmap bmp, Point at)
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
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hbmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
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
