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
    /// <summary>The pointer rested against a live edge; the source is already holding input back.</summary>
    event Action<Edge, Point>? Crossed;
    /// <summary>A Move, Mouse or Key message for the other PC.</summary>
    event Action<byte[]>? Forward;
    /// <summary>Ctrl+Alt+Home; the source already let go.</summary>
    event Action? Emergency;
    /// <summary>Input got past the hook (now, expected, slowest callback ms); the source let go and put in a fresh hook.</summary>
    event Action<Point, Point, double>? Stray;

    /// <summary>Which edges have a paired PC that's connected (bit per <see cref="Edge"/>).</summary>
    void SetEdges(int mask);
    /// <summary>Stop holding input back: control is on this PC.</summary>
    void Free();
    /// <summary>Cross over now (a file drag reached the edge: the button is held, so resting doesn't count).</summary>
    void Cross(Edge edge, Point p);
    /// <summary>Keep the next left-button release from this PC and raise <see cref="LeftUpHeld"/> instead.</summary>
    void HoldLeftUp(bool on);
    /// <summary>The held release happened; play it later with Inject.Button(LeftUp).</summary>
    event Action? LeftUpHeld;
}

/// <summary>
/// The hooks and what they decide: free, input stays here and the pointer is watched
/// for resting against a live edge; crossed over, every event is kept from this PC
/// and forwarded. Keys already held when crossing finish here, so nothing sticks.
/// </summary>
sealed class InputEngine : IInputSource
{
    const int ResistanceMs = 150;     // rest against the edge this long before crossing

    public event Action<Edge, Point>? Crossed;
    public event Action<byte[]>? Forward;
    public event Action? Emergency;
    public event Action<Point, Point, double>? Stray;
    public event Action? LeftUpHeld;

    InputHook _hook;
    volatile bool _holdUp;
    readonly System.Threading.Timer _watch;
    volatile bool _captured;
    volatile int _edges;
    long _capturedTicks;
    int _strayChecks;
    Point _last;                      // where the (hidden) pointer really is while crossed over
    // Hook thread only:
    readonly HashSet<ushort> _localDown = [];
    readonly HashSet<ushort> _remoteDown = [];
    int _buttonsDown;
    Edge? _restingOn;
    long _restingSince;

    public InputEngine()
    {
        _hook = NewHook();
        _watch = new System.Threading.Timer(_ => Watch(), null, 250, 250);
    }

    /// <summary>Whether input is being held back and forwarded right now.</summary>
    public bool Captured => _captured;
    public double SlowestCallbackMs => _hook.MaxCallbackMs;

    InputHook NewHook() => new() { OnMouse = OnMouse, OnKey = OnKey };

    public void SetEdges(int mask) => _edges = mask;

    public void Free()
    {
        _captured = false;
        lock (_remoteDown) _remoteDown.Clear();
    }

    public void Cross(Edge edge, Point p)
    {
        if (_captured) return;
        Capture(edge, p);
    }

    public void HoldLeftUp(bool on) => _holdUp = on;

    void Capture(Edge edge, Point p)
    {
        _restingOn = null;
        lock (_remoteDown) _remoteDown.Clear();
        _last = p;
        _capturedTicks = Stopwatch.GetTimestamp();
        _strayChecks = 0;
        _captured = true;
        Crossed?.Invoke(edge, p);
    }

    void CountButtons(MouseKind k)
    {
        if (k is MouseKind.LeftDown or MouseKind.RightDown or MouseKind.MiddleDown or MouseKind.XDown) _buttonsDown++;
        else if (k is MouseKind.LeftUp or MouseKind.RightUp or MouseKind.MiddleUp or MouseKind.XUp) _buttonsDown = Math.Max(0, _buttonsDown - 1);
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
                if (dx != 0 || dy != 0) Forward?.Invoke(KvmWire.Move(dx, dy));
            }
            else Forward?.Invoke(KvmWire.Mouse(e.Kind, e.Data));
            return true;
        }

        if (e.Injected) return false;
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
        var edges = _edges;
        if (_buttonsDown > 0 || edges == 0) { _restingOn = null; return; }
        var desk = Inject.Desktop;
        Edge? at = null;
        foreach (var e in (Edge[])[Edge.Left, Edge.Right, Edge.Top, Edge.Bottom])
            if ((edges & (1 << (int)e)) != 0 && KvmWire.AtEdge(e, p, desk) && !NearCorner(e, p, desk)) { at = e; break; }
        if (at == null) { _restingOn = null; return; }
        var now = Stopwatch.GetTimestamp();
        if (_restingOn != at) { _restingOn = at; _restingSince = now; return; }
        if (Stopwatch.GetElapsedTime(_restingSince, now).TotalMilliseconds < ResistanceMs) return;
        Capture(at.Value, p);
    }

    static bool NearCorner(Edge e, Point p, Rectangle d) => KvmWire.NearCorner(e, p, d);

    bool OnKey(KeyEvent e)
    {
        if (e.Injected) return false;
        if (!_captured)
        {
            if (e.Up) _localDown.Remove(e.Vk); else _localDown.Add(e.Vk);
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
        Forward?.Invoke(KvmWire.Key(e));
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
