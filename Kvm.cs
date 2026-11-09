using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// Sharing this PC's mouse and keyboard with paired PCs. Push the pointer off
/// an edge with a paired PC behind it and, from then on, input goes there
/// instead: the hooks keep it from this PC and send it over the link. The
/// other PC sends Leave when the pointer comes back over its edge.
/// Safety: no word from the other PC for over a second, this PC locking,
/// Ctrl+Alt+Home, or the link dropping all give control straight back.
/// </summary>
sealed class Kvm : IDisposable
{
    const int TimeoutMs = 1200;       // no word from them this long: take control back
    const int ReturnInset = 6;        // px inside our edge when the pointer comes back

    readonly Identity _id;
    readonly Discovery _discovery;
    readonly Action<Action> _ui;
    readonly Action<string, bool> _notify;
    // The local hooks, or the admin helper once it's connected. Switched only while not crossed over.
    volatile IInputSource _source;
    readonly object _switch = new();
    bool _wantHelper;
    readonly CaptureWindow _capture;
    readonly System.Threading.Timer _beat;
    // Changed on the UI thread, read on the hook thread.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, KvmLink> _links = new(StringComparer.OrdinalIgnoreCase);

    volatile KvmLink? _active;
    // Hiding and showing the pointer run in order on one worker, never in parallel.
    readonly System.Threading.Channels.Channel<Action> _pointer = System.Threading.Channels.Channel.CreateUnbounded<Action>(
        new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    long _lastHeard;
    Point _crossedAt;
    Edge _crossedEdge;

    public Kvm(Identity id, Discovery discovery, Action<Action> ui, Action<string, bool> notify)
    {
        _id = id;
        _discovery = discovery;
        _ui = ui;
        _notify = notify;
        HiddenCursor.RecoverAfterCrash();
        _capture = new CaptureWindow();
        _source = Wire(new InputEngine());
        Sync();
        Pairing.Changed += Sync;
        KvmRemote.ActiveChanged = on => (_source as HelperClient)?.SetRemoteActive(on);
        KvmLog.Write($"started; paired: {string.Join(", ", Pairing.All.Select(p => $"{p.Alias} {p.Edge}"))}");
        _beat = new System.Threading.Timer(_ => Beat(), null, 250, 250);
        _ = Task.Run(async () =>
        {
            await foreach (var work in _pointer.Reader.ReadAllAsync())
                try { work(); } catch { }
        });
        if (Settings.Current.AdminHelper) UseHelper(true);
    }

    /// <summary>Whether input comes through the elevated admin helper right now.</summary>
    public bool HelperConnected => _source is HelperClient;

    IInputSource Wire(IInputSource s)
    {
        s.Crossed += OnCrossed;
        s.Forward += m => { if (_active is { } l) l.Send(m); else s.Free(); };
        s.Emergency += () => Release(BackPoint(), null, "Ctrl+Alt+Home");
        s.Stray += OnStray;
        return s;
    }

    void Use(IInputSource next)
    {
        IInputSource old;
        lock (_switch)
        {
            if (_active != null) TakeBack();
            old = _source;
            _source = next;
            Inject.Via = next as IInjector;
        }
        old.Dispose();
        UpdateEdges();
    }

    /// <summary>Turns the admin helper on (starting it through its task) or off (back to this process's hooks).</summary>
    public void UseHelper(bool on)
    {
        _wantHelper = on;
        if (!on)
        {
            if (_source is HelperClient) { Use(Wire(new InputEngine())); KvmLog.Write("using this process's hooks"); }
            return;
        }
        _ = Task.Run(async () =>
        {
            if (_source is HelperClient || !_wantHelper) return;
            var helper = await AdminHelper.StartAsync();
            if (helper == null)
            {
                _ui(() => _notify("Couldn't start the admin helper, so admin windows stay out of reach. Turn it off and on in Settings to set it up again.", true));
                return;
            }
            if (!_wantHelper) { helper.Dispose(); return; }
            helper.Lost += () => OnHelperLost(helper);
            Use(Wire(helper));
            KvmLog.Write("using the admin helper");
        });
    }

    void OnHelperLost(HelperClient helper)
    {
        if (_source != helper) return;
        KvmLog.Write("the admin helper went away");
        if (_active != null) Release(BackPoint(), "The admin helper stopped, so control is back on this PC.", "helper lost");
        Use(Wire(new InputEngine()));
        // Bring it back after a pause, unless it was turned off.
        _ = Task.Delay(10_000).ContinueWith(_ => { if (_wantHelper && _source is not HelperClient) UseHelper(true); });
    }

    /// <summary>Tells the input source which edges have a connected paired PC.</summary>
    void UpdateEdges()
    {
        var mask = 0;
        foreach (var p in Pairing.All)
            if (_links.TryGetValue(p.Fingerprint, out var l) && l.IsConnected) mask |= 1 << (int)p.Edge;
        _source.SetEdges(mask);
    }

    /// <summary>Whether input is going to another PC right now.</summary>
    public bool CrossedOver => _active != null;

    /// <summary>Gives control back to this PC (it's locking, the app is closing…).</summary>
    public void TakeBack(string? why = null) => Release(BackPoint(), why, "taken back");

    /// <summary>One link per paired PC. UI thread.</summary>
    void Sync()
    {
        var paired = Pairing.All.Select(p => p.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _links.Keys.Where(k => !paired.Contains(k)).ToList())
        {
            if (_active == _links[gone]) TakeBack();
            if (_links.TryRemove(gone, out var old)) old.Dispose();
        }
        foreach (var fp in paired.Where(fp => !_links.ContainsKey(fp)))
        {
            var link = new KvmLink(_id, fp, () => _discovery.Devices.FirstOrDefault(d =>
                string.Equals(d.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)));
            link.Message += OnLinkMessage;
            link.Dropped += l =>
            {
                KvmLog.Write($"link to {l.Name} dropped");
                if (_active == l) Release(BackPoint(), $"Lost the connection to {l.Name}", "link dropped");
                UpdateEdges();
            };
            link.Connected += l => { KvmLog.Write($"link to {l.Name} connected"); UpdateEdges(); };
            _links[fp] = link;
        }
        UpdateEdges();
    }

    KvmLink? LinkOn(Edge e)
    {
        var p = Pairing.All.FirstOrDefault(x => x.Edge == e);
        return p != null && _links.TryGetValue(p.Fingerprint, out var l) && l.IsConnected ? l : null;
    }

    // ---- the input source (any thread) ----

    void OnCrossed(Edge edge, Point p)
    {
        var source = _source;
        var link = LinkOn(edge);
        var desk = Inject.Desktop;
        if (link == null || !link.Send(KvmWire.Fraction(KvmMsg.Enter, KvmWire.Along(edge, p, desk)))) { source.Free(); return; }
        _crossedAt = p;
        _crossedEdge = edge;
        Volatile.Write(ref _lastHeard, Environment.TickCount64);
        _active = link;
        KvmLog.Write($"crossed to {link.Name} over {edge} at {p}{(source is HelperClient ? " (admin helper)" : "")}");
        // Park the hidden pointer mid-screen so moves in every direction register.
        var park = Screen.FromPoint(p).Bounds;
        var centre = new Point(park.Left + park.Width / 2, park.Top + park.Height / 2);
        _pointer.Writer.TryWrite(() =>
        {
            if (_active != link) return;   // already back: don't hide
            HiddenCursor.Hide();
            Inject.MoveTo(centre.X, centre.Y);
        });
        _ui(() =>
        {
            if (_active != link) return;
            // Without the admin helper: our own window under the parked pointer and holding the
            // keyboard, so an admin window on this PC can't take the mouse or keys from the hook.
            if (source is InputEngine && !_capture.Engage(park) && AdminWindow(GetForegroundWindow()))
            {
                KvmLog.Write("an admin window kept the keyboard focus");
                _notify("An administrator window here has the keyboard, so keys may stay on this PC. Click away from it first, or turn on Control administrator windows in Settings.", true);
            }
            if (Clip.ForSync() is { } clip) link.Send(clip);
        });
    }

    void OnStray(Point now, Point expected, double slowest)
    {
        var admin = AdminWindowAt(now);
        KvmLog.Write($"input got past the hook (pointer at {now}, expected {expected}, admin window: {admin}, slowest hook {slowest:0.0} ms); hook reinstalled");
        Release(now, admin
            ? "An administrator window on this PC took the mouse, so control is back here. Move the pointer off it and cross again, or turn on Control administrator windows in Settings."
            : "Windows stopped passing the mouse to LocalSend Drop, so control is back here. Cross again to carry on.", "input got past the hook");
    }

    // ---- links (reading threads) and the beat ----

    void OnLinkMessage(KvmLink link, byte[] m)
    {
        if (m.Length == 0) return;
        if (_active == link) Volatile.Write(ref _lastHeard, Environment.TickCount64);
        switch ((KvmMsg)m[0])
        {
            case KvmMsg.Leave when _active == link:
                Release(KvmWire.OnEdge(link.Edge, KvmWire.ReadFraction(m), Inject.Desktop, ReturnInset), null, "came back over the edge");
                break;
            case KvmMsg.Locked when _active == link:
                Release(BackPoint(), $"{link.Name} is locked. Unlock it with its own keyboard.", "they're locked");
                break;
            case KvmMsg.ClipText or KvmMsg.ClipImage:
                _ui(() => Clip.FromSync(m));
                break;
        }
    }

    void Beat()
    {
        if (_active is not { } link)
        {
            if (HiddenCursor.IsHidden) _pointer.Writer.TryWrite(() => { if (_active == null) HiddenCursor.Show(); });
            return;
        }
        link.Send(KvmWire.Simple(KvmMsg.Beat));
        if (Environment.TickCount64 - Volatile.Read(ref _lastHeard) > TimeoutMs)
            Release(BackPoint(), $"{link.Name} stopped answering. Control is back on this PC.", "timed out");
    }

    Point BackPoint()
    {
        var desk = Inject.Desktop;
        return KvmWire.OnEdge(_crossedEdge, KvmWire.Along(_crossedEdge, _crossedAt, desk), desk, ReturnInset);
    }

    void Release(Point at, string? why, string cause)
    {
        var link = Interlocked.Exchange(ref _active, null);
        if (link == null) return;
        KvmLog.Write($"back from {link.Name}: {cause}");
        _source.Free();
        link.Send(KvmWire.Simple(KvmMsg.Release));
        _pointer.Writer.TryWrite(() =>
        {
            Inject.MoveTo(at.X, at.Y);
            HiddenCursor.Show();
        });
        _ui(() =>
        {
            if (_active == null) _capture.Disengage();
            if (why != null) _notify(why, true);
        });
    }

    /// <summary>Whether the window under a point belongs to an elevated app (we can't read its token).</summary>
    static bool AdminWindowAt(Point p) => AdminWindow(WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y }));

    static bool AdminWindow(nint hwnd)
    {
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0) return false;
        var process = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return true;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out var token)) return true;   // TOKEN_QUERY: denied for elevated apps
            try { return GetTokenInformation(token, 20, out var elevated, 4, out _) && elevated != 0; }   // TokenElevation
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")] static extern nint WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(nint token, int cls, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    public void Dispose()
    {
        Pairing.Changed -= Sync;
        _beat.Dispose();
        TakeBack();
        _pointer.Writer.TryComplete();
        _capture.Dispose();
        _wantHelper = false;
        Inject.Via = null;
        _source.Dispose();
        HiddenCursor.Show();
        foreach (var l in _links.Values) l.Dispose();
    }
}

/// <summary>A standing connection to one paired PC, for sending it input. Reconnects on its own.</summary>
sealed class KvmLink : IDisposable
{
    public readonly string Fingerprint;
    readonly Identity _id;
    readonly Func<Device?> _find;
    readonly CancellationTokenSource _cts = new();
    volatile WsPipe? _pipe;

    public event Action<KvmLink, byte[]>? Message;
    public event Action<KvmLink>? Dropped;
    public event Action<KvmLink>? Connected;

    public KvmLink(Identity id, string fingerprint, Func<Device?> find)
    {
        _id = id;
        Fingerprint = fingerprint;
        _find = find;
        _ = Task.Run(Loop);
    }

    public bool IsConnected => _pipe != null;
    public string Name => Settings.Current.Nicknames.GetValueOrDefault(Fingerprint) ?? Pairing.Find(Fingerprint)?.Alias ?? "The other PC";
    public Edge Edge => Pairing.Find(Fingerprint)?.Edge ?? Edge.Right;

    public bool Send(byte[] m) => _pipe?.Send(m) == true;

    async Task Loop()
    {
        var failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            if (_find() is { } d)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    using var invoker = new HttpMessageInvoker(LsHttp.Handler(_id, LsHttp.PinFor(d)));
                    var uri = new Uri(d.Root.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/localsend-drop/v1/kvm");
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(8));
                        await ws.ConnectAsync(uri, invoker, connect.Token);
                    }
                    failures = 0;
                    var pipe = new WsPipe(ws);
                    _pipe = pipe;
                    Connected?.Invoke(this);
                    await pipe.RunAsync(m => Message?.Invoke(this, m), _cts.Token);
                }
                catch (Exception) when (!_cts.IsCancellationRequested) { failures++; }
                catch (OperationCanceledException) { }
                finally
                {
                    if (_pipe != null) { _pipe = null; Dropped?.Invoke(this); }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures, 4))), _cts.Token); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>
/// Hides the pointer on this PC while input goes elsewhere, by swapping every
/// system cursor for a blank one. A flag file brings them back after a crash.
/// </summary>
static class HiddenCursor
{
    static readonly uint[] Ids = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651];
    static readonly object Lock = new();
    static volatile bool _hidden;
    public static bool IsHidden => _hidden;
    static string Flag => Path.Combine(Identity.DataDir, "cursor-hidden");

    public static void Hide()
    {
        lock (Lock)
        {
            if (_hidden) return;
            try { File.WriteAllText(Flag, ""); } catch { }
            var and = Enumerable.Repeat((byte)0xFF, 128).ToArray();
            var xor = new byte[128];
            var module = Native.GetModuleHandle(null);
            foreach (var id in Ids)
            {
                var blank = CreateCursor(module, 0, 0, 32, 32, and, xor);
                if (blank != 0 && !SetSystemCursor(blank, id)) DestroyCursor(blank);
            }
            _hidden = true;
        }
    }

    public static void Show()
    {
        lock (Lock)
        {
            if (!_hidden) return;
            Restore();
            _hidden = false;
        }
    }

    public static void RecoverAfterCrash()
    {
        if (File.Exists(Flag)) Restore();
    }

    static void Restore()
    {
        SystemParametersInfo(0x0057, 0, 0, 0);   // SPI_SETCURSORS: reload the user's cursors
        try { File.Delete(Flag); } catch { }
    }

    [DllImport("user32.dll")] static extern nint CreateCursor(nint hInst, int x, int y, int w, int h, byte[] and, byte[] xor);
    [DllImport("user32.dll")] static extern bool SetSystemCursor(nint cursor, uint id);
    [DllImport("user32.dll")] static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, nint pv, uint winIni);
}

/// <summary>What the mouse-and-keyboard sharing did, for finding out why: %APPDATA%\\LocalSendDrop\\kvm.log.</summary>
static class KvmLog
{
    static readonly object Lock = new();
    static string LogPath => Path.Combine(Identity.DataDir, "kvm.log");

    public static void Write(string line)
    {
        lock (Lock)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) File.Move(LogPath, LogPath + ".old", true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

/// <summary>
/// An invisible window over the monitor where the pointer is parked while input
/// goes to another PC. Windows sends mouse input to the window under the pointer
/// and keys to the focused one; when that's an administrator window, a normal
/// app's hook never sees them. With this one on top and focused, it's ours.
/// </summary>
sealed class CaptureWindow : Form
{
    nint _before;

    public CaptureWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 1 / 255.0;   // invisible, but still under the mouse (fully transparent windows aren't)
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }   // toolwindow: not in Alt+Tab
    }

    /// <summary>Covers the monitor and takes the keyboard. False if Windows wouldn't give it focus.</summary>
    public bool Engage(Rectangle monitor)
    {
        _before = GetForegroundWindow();
        Bounds = monitor;
        if (!Visible) Show();
        Ui.KeepOnTop(Handle);
        if (SetForegroundWindow(Handle)) return true;
        // Windows only lets the app that sent the last input take focus: press and release Alt, then try again.
        // (Alt is released after the switch, so it can't open the old window's menu.)
        Inject.Key(0x12, 0, false, false);
        var ok = SetForegroundWindow(Handle);
        Inject.Key(0x12, 0, true, false);
        return ok && GetForegroundWindow() == Handle;
    }

    public void Disengage()
    {
        if (!Visible) return;
        Hide();
        if (_before != 0 && IsWindow(_before)) SetForegroundWindow(_before);
        _before = 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
}
