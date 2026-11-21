using System.Diagnostics;

namespace LocalSendDrop;

/// <summary>
/// Where this PC's mouse and keyboard come from for sharing: the hooks in this
/// process (<see cref="InputEngine"/>), or the same engine in the elevated admin
/// helper (<see cref="HelperClient"/>), which also sees input meant for admin windows.
/// Events may arrive on any thread.
/// </summary>
interface IInputSource : IDisposable
{
    /// <summary>The pointer pushed through a live zone; the source is already holding input back.</summary>
    event Action<Zone, Point>? Crossed;
    /// <summary>The pointer pushed into the zone of an offline PC (rate limited): wake it.</summary>
    event Action<Zone>? Wake;
    /// <summary>A Move, Mouse or Key message for the other PC.</summary>
    event Action<byte[]>? Forward;
    /// <summary>Ctrl+Alt+Home; the source already let go.</summary>
    event Action? Emergency;
    /// <summary>Input got past the hook (now, expected, slowest callback ms); the source let go and put in a fresh hook.</summary>
    event Action<Point, Point, double>? Stray;

    /// <summary>The zones that lead to a connected paired PC, and those that lead to an offline one.</summary>
    void SetZones(Zone[] live, Zone[] offline);
    /// <summary>Stop holding input back: control is on this PC.</summary>
    void Free();
    /// <summary>Cross over now (a file drag reached the edge: the button is held, so resting doesn't count).</summary>
    void Cross(Zone zone, Point p);
    /// <summary>Keep the next left-button release from this PC and raise <see cref="LeftUpHeld"/> instead.</summary>
    void HoldLeftUp(bool on);
    /// <summary>The held release happened; play it later with Inject.Button(LeftUp).</summary>
    event Action? LeftUpHeld;
    /// <summary>The switch shortcut (double-tap right Ctrl) was pressed, here or while crossed over.</summary>
    event Action? Shortcut;
}

/// <summary>
/// The hooks and what they decide: free, input stays here and the pointer is watched
/// for resting against a live edge; crossed over, every event is kept from this PC
/// and forwarded. Keys already held when crossing finish here, so nothing sticks.
/// </summary>
sealed class InputEngine : IInputSource
{
    public event Action<Zone, Point>? Crossed;
    public event Action<Zone>? Wake;
    public event Action<byte[]>? Forward;
    public event Action? Emergency;
    public event Action<Point, Point, double>? Stray;
    public event Action? LeftUpHeld;
    public event Action? Shortcut;

    InputHook _hook;
    volatile bool _holdUp;
    readonly System.Threading.Timer _watch;
    volatile bool _captured;
    volatile ZoneSet _zones = new([], []);
    // The options the admin helper was given (it can't read the app's settings); null here: read them live,
    // so a change made in Settings applies at once.
    volatile CrossingSettings? _givenRules;
    CrossingSettings _rules => _givenRules ?? Settings.Current.Crossing;
    DisplayInfo[] _displays = [];
    long _capturedTicks;
    int _strayChecks;
    Point _last;                      // where the (hidden) pointer really is while crossed over
    // Hook thread only:
    readonly HashSet<ushort> _localDown = [];
    readonly HashSet<ushort> _remoteDown = [];
    CrossState _cross = new();
    readonly DoubleTap _tap = new(300);
    readonly PointerScaler _scaler = new();   // mouse hook thread
    Point _lastPoint;                 // the previous pointer position while free

    public InputEngine()
    {
        _hook = NewHook();
        _watch = new System.Threading.Timer(_ => Watch(), null, 250, 250);
    }

    /// <summary>Whether input is being held back and forwarded right now.</summary>
    public bool Captured => _captured;
    public double SlowestCallbackMs => _hook.MaxCallbackMs;

    InputHook NewHook() => new() { OnMouse = OnMouse, OnKey = OnKey };

    public void SetZones(Zone[] live, Zone[] offline) => SetZones(live, offline, null);

    /// <summary>Also sets the crossing options (the admin helper gets them from the app); null reads this process's settings.</summary>
    public void SetZones(Zone[] live, Zone[] offline, CrossingSettings? rules)
    {
        _givenRules = rules;
        try { _displays = KvmZones.Current(); } catch { }
        _zones = new ZoneSet(live, offline);
    }

    public void Free()
    {
        _captured = false;
        lock (_remoteDown) _remoteDown.Clear();
        try { _lastPoint = Inject.CursorPos; } catch { }   // the next real move starts from where the pointer is now
    }

    public void Cross(Zone zone, Point p)
    {
        if (_captured) return;
        Point cursor;
        try { cursor = Inject.CursorPos; } catch { cursor = p; }
        Capture(zone, p, CrossingRule.Baseline(p, fromHook: false, cursor));
    }

    public void HoldLeftUp(bool on) => _holdUp = on;

    void Capture(Zone zone, Point p) => Capture(zone, p, CrossingRule.Baseline(p, fromHook: true, p));

    void Capture(Zone zone, Point p, Point baseline)
    {
        _cross = _cross with { Pinned = null, Overshoot = 0 };
        lock (_remoteDown) _remoteDown.Clear();
        _last = baseline;
        _lastPoint = p;
        _capturedTicks = Stopwatch.GetTimestamp();
        _strayChecks = 0;
        _captured = true;
        Crossed?.Invoke(zone, p);
    }

    void CountButtons(MouseKind k)
    {
        var n = _cross.Buttons;
        if (k is MouseKind.LeftDown or MouseKind.RightDown or MouseKind.MiddleDown or MouseKind.XDown) n++;
        else if (k is MouseKind.LeftUp or MouseKind.RightUp or MouseKind.MiddleUp or MouseKind.XUp) n = Math.Max(0, n - 1);
        _cross = _cross with { Buttons = n };
    }

    // ---- hook thread ----

    bool OnMouse(MouseEvent e)
    {
        if (_captured)
        {
            var p = new Point(e.X, e.Y);
            if (e.Injected) { if (e.Kind == MouseKind.Move) _last = p; return false; }
            CountButtons(e.Kind);   // so a button held while crossing (a file drag) is let go of here too
            if (e.Kind == MouseKind.Move)
            {
                int dx = p.X - _last.X, dy = p.Y - _last.Y;
                if (dx != 0 || dy != 0)
                {
                    (dx, dy) = _scaler.Scale(dx, dy, _rules.PointerSpeed.Value);
                    if (dx != 0 || dy != 0) Forward?.Invoke(KvmWire.Move(dx, dy));
                }
            }
            else if (e.Kind is MouseKind.Wheel or MouseKind.HWheel)
            {
                var wheel = _scaler.Wheel(e.Data, _rules.ScrollSpeed.Value, _rules.ReverseScroll.Value, e.Kind == MouseKind.HWheel);
                if (wheel != 0) Forward?.Invoke(KvmWire.Mouse(e.Kind, wheel));
            }
            else Forward?.Invoke(KvmWire.Mouse(e.Kind, e.Data));
            return true;
        }

        if (e.Injected)
        {
            // Our own moves (coming back from the other PC, a drag) put the pointer somewhere new: that is
            // where the next real move starts from, not where it was before.
            if (e.Kind == MouseKind.Move) _lastPoint = new Point(e.X, e.Y);
            return false;
        }
        if (e.Kind == MouseKind.LeftUp && _holdUp)
        {
            // A drop of files still on their way: it's played once they've arrived.
            _holdUp = false;
            CountButtons(e.Kind);
            LeftUpHeld?.Invoke();
            return true;
        }
        CountButtons(e.Kind);
        if (e.Kind == MouseKind.Move) CheckEdge(new Point(e.X, e.Y));
        return false;
    }

    void CheckEdge(Point p)
    {
        var zones = _zones;
        var delta = StepDelta(_lastPoint, p, _cross.Pinned?.Side);
        _lastPoint = p;
        if (zones.Live.Count == 0 && zones.Offline.Count == 0) { _cross = _cross with { Pinned = null, Overshoot = 0 }; return; }
        var now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        var (state, cross, wake) = CrossingRule.Step(_cross, p, delta, zones, _displays, _rules, now);
        _cross = state;
        if (wake != null) Wake?.Invoke(wake);
        if (cross != null) Capture(cross, p);
    }

    /// <summary>The most one pointer event can count as movement: a jump (a warp, a stale previous point) isn't a push.</summary>
    const int MaxStep = 50;

    /// <summary>
    /// How far the pointer moved since <paramref name="last"/>, capped per event to real movement. Windows clamps
    /// the pointer at the screen edge, so a push shows as moves that go nowhere: while pinned on an edge
    /// each such move counts as a pixel outward, so the resistance still adds up.
    /// </summary>
    internal static Point StepDelta(Point last, Point p, Edge? pinned)
    {
        var delta = new Point(Math.Clamp(p.X - last.X, -MaxStep, MaxStep), Math.Clamp(p.Y - last.Y, -MaxStep, MaxStep));
        if (delta == Point.Empty && pinned is { } side) delta = Step1(side);
        return delta;
    }

    static Point Step1(Edge side) => side switch
    {
        Edge.Left => new Point(-1, 0), Edge.Right => new Point(1, 0), Edge.Top => new Point(0, -1), _ => new Point(0, 1),
    };

    bool OnKey(KeyEvent e)
    {
        if (e.Injected) return false;
        var tapped = false;
        if (_rules.Shortcut.Value == "RightCtrl")
        {
            _tap.WindowMs = _rules.DoubleTapMs.Value;
            tapped = _tap.Feed(e.Vk, e.Up, Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        }
        if (!_captured)
        {
            if (e.Up) _localDown.Remove(e.Vk); else _localDown.Add(e.Vk);
            if (tapped) { _tap.Reset(); Shortcut?.Invoke(); }
            return false;
        }

        // Keys held on this PC before crossing finish here, so nothing sticks.
        if (_localDown.Contains(e.Vk))
        {
            if (e.Up) { _localDown.Remove(e.Vk); return false; }
            return true;
        }

        if (!e.Up && e.Vk == 0x24 && Held(0xA2, 0xA3, 0x11) && Held(0xA4, 0xA5, 0x12))   // Ctrl+Alt+Home
        {
            Free();
            Emergency?.Invoke();
            return true;
        }
        lock (_remoteDown) { if (e.Up) _remoteDown.Remove(e.Vk); else _remoteDown.Add(e.Vk); }
        Forward?.Invoke(KvmWire.Key(e));   // media and volume keys go through like any other
        if (tapped) { _tap.Reset(); Shortcut?.Invoke(); }   // the key is already let go over there, so nothing sticks
        return true;
    }

    bool Held(params ushort[] vks)
    {
        lock (_remoteDown) return vks.Any(_remoteDown.Contains);
    }

    /// <summary>
    /// While crossed over, this PC's pointer only moves when it's parked. If it moves
    /// anyway, input is getting past the hook (a window it can't see, or Windows dropped
    /// the hook): let go and put in a fresh hook.
    /// </summary>
    void Watch()
    {
        if (!_captured || Stopwatch.GetElapsedTime(_capturedTicks).TotalMilliseconds < 600) return;
        var now = Inject.CursorPos;
        var expected = _last;
        if (Math.Abs(now.X - expected.X) + Math.Abs(now.Y - expected.Y) <= 3) { _strayChecks = 0; return; }
        if (++_strayChecks < 2) return;
        Free();
        var slowest = _hook.MaxCallbackMs;
        var old = _hook;
        _hook = NewHook();
        old.Dispose();
        Stray?.Invoke(now, expected, slowest);
    }

    public void Dispose()
    {
        _watch.Dispose();
        Free();
        _hook.Dispose();
    }
}
