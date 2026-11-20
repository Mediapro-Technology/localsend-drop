using System.Runtime.InteropServices;

namespace LocalSendDrop;

enum IdleAction { None, Hide, Show }

/// <summary>Pure idle-pointer logic: hide after a still stretch, show on the first move. Never decides for "crossing".</summary>
sealed class IdleClock
{
    public const int StillMs = 3000;
    Point _last;
    long _movedAt;
    bool _started, _hidden;

    public bool Hidden => _hidden;

    /// <summary>Forget everything (hiding was switched off): counting still starts again from the next tick.</summary>
    public void Reset()
    {
        _started = false;
        _hidden = false;
        _movedAt = 0;
    }

    /// <summary>Feed the pointer position every tick. <paramref name="live"/> is whether a paired link is up.</summary>
    public IdleAction Tick(Point pos, bool crossed, long nowMs, bool live = true)
    {
        var moved = !_started || pos != _last;
        _started = true;
        _last = pos;
        if (moved) _movedAt = nowMs;

        // Input is on the other PC (or there is nobody to share with): the idle reason is not ours to act on,
        // except to let go of a hide we made when the link is gone.
        if (crossed) { _movedAt = nowMs; return IdleAction.None; }
        if (!live)
        {
            if (!_hidden) return IdleAction.None;
            _hidden = false;
            return IdleAction.Show;
        }
        if (_hidden)
        {
            if (!moved) return IdleAction.None;
            _hidden = false;
            return IdleAction.Show;
        }
        if (nowMs - _movedAt < StillMs) return IdleAction.None;
        _hidden = true;
        return IdleAction.Hide;
    }
}

/// <summary>Edge glow, arrival effect and idle-pointer hiding.</summary>
static class KvmLook
{
    const int Reach = 40;       // px from a live edge where the glow starts
    const int Thick = 10;       // glow strip thickness
    const int RingMs = 400;

    // Kvm owns the link and crossed state. Until it hands these over: every paired PC counts as live, never crossed.
    public static Func<bool> IsCrossed = () => false;
    public static Func<string, bool> IsLive = fp => Pairing.All.Any(p => string.Equals(p.Fingerprint, fp, StringComparison.OrdinalIgnoreCase));

    static bool _started;
    static System.Windows.Forms.Timer? _timer;
    static GlowStrip? _strip;
    static readonly IdleClock Idle = new();

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) => { try { Tick(); } catch { } };
        _timer.Start();
        KvmZones.Arrived += OnArrived;
    }

    // ---- pure geometry (tested) ----

    /// <summary>The screen rectangle of a zone's span along its edge, <paramref name="thick"/> px deep inside the display.</summary>
    internal static Rectangle SpanRect(Zone z, Rectangle d, int thick)
    {
        double a = Math.Min(z.From, z.To), b = Math.Max(z.From, z.To);
        if (z.Side is Edge.Left or Edge.Right)
        {
            var top = d.Top + (int)Math.Round(a * (d.Height - 1));
            var h = Math.Max(1, (int)Math.Round((b - a) * (d.Height - 1)));
            return new Rectangle(z.Side == Edge.Left ? d.Left : d.Right - thick, top, thick, h);
        }
        var left = d.Left + (int)Math.Round(a * (d.Width - 1));
        var w = Math.Max(1, (int)Math.Round((b - a) * (d.Width - 1)));
        return new Rectangle(left, z.Side == Edge.Top ? d.Top : d.Bottom - thick, w, thick);
    }

    /// <summary>1 on the edge inside the zone's span, fading to 0 at <paramref name="reach"/> px away; 0 outside the span.</summary>
    internal static double Proximity(Zone z, Rectangle d, Point p, int reach)
    {
        if (!d.Contains(p)) return 0;
        var along = KvmWire.Along(z.Side, p, d);
        if (along < Math.Min(z.From, z.To) || along > Math.Max(z.From, z.To)) return 0;
        var dist = z.Side switch
        {
            Edge.Left => p.X - d.Left,
            Edge.Right => d.Right - 1 - p.X,
            Edge.Top => p.Y - d.Top,
            _ => d.Bottom - 1 - p.Y,
        };
        return dist >= reach ? 0 : 1 - dist / (double)reach;
    }

    // ---- the timer ----

    static void Tick()
    {
        var s = Settings.Current;
        var pos = Cursor.Position;
        var crossed = IsCrossed();
        // The same zones the crossing uses (a pairing without a saved layout has its old edge).
        var displays = KvmZones.Current();
        var zones = Pairing.All.SelectMany(p => Kvm.ZonesFor(p, s.Zones, displays)).ToList();
        var live = zones.Any(z => IsLive(z.Peer));

        if (s.Crossing.HideIdle.Value)
        {
            switch (Idle.Tick(pos, crossed, Environment.TickCount64, live))
            {
                case IdleAction.Hide: HiddenCursor.Hide(HiddenCursor.Idle); break;
                case IdleAction.Show: HiddenCursor.Show(HiddenCursor.Idle); break;
            }
        }
        else
        {
            if (Idle.Hidden) HiddenCursor.Show(HiddenCursor.Idle);
            Idle.Reset();   // so switching it back on starts counting from then
        }

        if (!s.Crossing.Glow.Value || crossed) { _strip?.Fade(null, 0); return; }
        Zone? best = null; double bestP = 0; Rectangle bestD = default;
        foreach (var z in zones)
        {
            if (!IsLive(z.Peer)) continue;
            var d = displays.FirstOrDefault(x => x.Index == z.Display);
            if (d == null) continue;
            var pr = Proximity(z, d.Bounds, pos, Reach);
            if (pr > bestP) { best = z; bestP = pr; bestD = d.Bounds; }
        }
        if (best == null) { _strip?.Fade(null, 0); return; }
        _strip ??= new GlowStrip();
        _strip.Fade(SpanRect(best, bestD, Thick), bestP);
    }

    static void OnArrived(Point p)
    {
        if (!Settings.Current.Crossing.Arrival.Value) return;
        try { new RingForm(p, RingMs).Show(); } catch { }
    }

    // ---- windows ----

    /// <summary>A click-through top-most window that never takes focus.</summary>
    class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000;   // layered, transparent, toolwindow, noactivate
                return cp;
            }
        }
    }

    sealed class GlowStrip : OverlayForm
    {
        double _level;
        Rectangle _rect;
        public GlowStrip() { Opacity = 0; }

        public void Fade(Rectangle? rect, double target)
        {
            if (rect is { } r && r != _rect)
            {
                _rect = r;
                Bounds = r;
            }
            _level += (target - _level) * 0.35;
            if (target == 0 && _level < 0.02) _level = 0;
            BackColor = Ui.Accent;
            if (_level > 0)
            {
                Opacity = Math.Min(0.75, _level * 0.75);
                if (!Visible) Show();
            }
            else if (Visible) Hide();
        }
    }

    sealed class RingForm : OverlayForm
    {
        const int Size = 140;
        readonly long _start = Environment.TickCount64;
        readonly int _ms;
        readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };

        public RingForm(Point at, int ms)
        {
            _ms = ms;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Bounds = new Rectangle(at.X - Size / 2, at.Y - Size / 2, Size, Size);
            DoubleBuffered = true;
            _t.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - _start >= _ms) { _t.Stop(); Close(); return; }
                Invalidate();
            };
            _t.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var k = Math.Clamp((Environment.TickCount64 - _start) / (double)_ms, 0, 1);
            var r = (int)(12 + k * (Size / 2 - 16));
            var a = (int)(255 * (1 - k));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(a, Ui.Accent), 4);
            e.Graphics.DrawEllipse(pen, Size / 2 - r, Size / 2 - r, r * 2, r * 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _t.Dispose();
            base.Dispose(disposing);
        }
    }
}
