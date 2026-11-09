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
    const int ResistanceMs = 150;     // rest against the edge this long before crossing
    const int TimeoutMs = 1200;       // no word from them this long: take control back
    const int ReturnInset = 6;        // px inside our edge when the pointer comes back

    readonly Identity _id;
    readonly Discovery _discovery;
    readonly Action<Action> _ui;
    readonly Action<string, bool> _notify;
    readonly InputHook _hook;
    readonly System.Threading.Timer _beat;
    // Changed on the UI thread, read on the hook thread.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, KvmLink> _links = new(StringComparer.OrdinalIgnoreCase);

    volatile KvmLink? _active;
    // Hiding and showing the pointer run in order on one worker, never in parallel.
    readonly System.Threading.Channels.Channel<Action> _pointer = System.Threading.Channels.Channel.CreateUnbounded<Action>(
        new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    long _lastHeard;
    Point _last;                      // where the (hidden) pointer really is while crossed over
    Point _crossedAt;
    Edge _crossedEdge;
    // Hook thread only:
    readonly HashSet<ushort> _localDown = [];
    readonly HashSet<ushort> _remoteDown = [];
    int _buttonsDown;
    Edge? _restingOn;
    long _restingSince;

    public Kvm(Identity id, Discovery discovery, Action<Action> ui, Action<string, bool> notify)
    {
        _id = id;
        _discovery = discovery;
        _ui = ui;
        _notify = notify;
        HiddenCursor.RecoverAfterCrash();
        Sync();
        Pairing.Changed += Sync;
        _hook = new InputHook { OnMouse = OnMouse, OnKey = OnKey };
        _beat = new System.Threading.Timer(_ => Beat(), null, 250, 250);
        _ = Task.Run(async () =>
        {
            await foreach (var work in _pointer.Reader.ReadAllAsync())
                try { work(); } catch { }
        });
    }

    /// <summary>Whether input is going to another PC right now.</summary>
    public bool CrossedOver => _active != null;

    /// <summary>Gives control back to this PC (it's locking, the app is closing…).</summary>
    public void TakeBack(string? why = null) => Release(BackPoint(), why);

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
            link.Dropped += l => { if (_active == l) Release(BackPoint(), $"Lost the connection to {l.Name}"); };
            _links[fp] = link;
        }
    }

    KvmLink? LinkOn(Edge e)
    {
        var p = Pairing.All.FirstOrDefault(x => x.Edge == e);
        return p != null && _links.TryGetValue(p.Fingerprint, out var l) && l.Connected ? l : null;
    }

    // ---- hook thread ----

    bool OnMouse(MouseEvent e)
    {
        var active = _active;
        if (active != null)
        {
            var p = new Point(e.X, e.Y);
            if (e.Injected) { if (e.Kind == MouseKind.Move) _last = p; return false; }
            if (e.Kind == MouseKind.Move)
            {
                int dx = p.X - _last.X, dy = p.Y - _last.Y;
                if (dx != 0 || dy != 0) active.Send(KvmWire.Move(dx, dy));
            }
            else active.Send(KvmWire.Mouse(e.Kind, e.Data));
            return true;
        }

        if (e.Injected) return false;
        switch (e.Kind)
        {
            case MouseKind.LeftDown or MouseKind.RightDown or MouseKind.MiddleDown or MouseKind.XDown: _buttonsDown++; break;
            case MouseKind.LeftUp or MouseKind.RightUp or MouseKind.MiddleUp or MouseKind.XUp: _buttonsDown = Math.Max(0, _buttonsDown - 1); break;
            case MouseKind.Move: CheckEdge(new Point(e.X, e.Y)); break;
        }
        return false;
    }

    void CheckEdge(Point p)
    {
        if (_buttonsDown > 0 || _links.IsEmpty) { _restingOn = null; return; }
        var desk = Inject.Desktop;
        Edge? at = null;
        foreach (var e in (Edge[])[Edge.Left, Edge.Right, Edge.Top, Edge.Bottom])
            if (KvmWire.AtEdge(e, p, desk) && !NearCorner(e, p, desk) && LinkOn(e) != null) { at = e; break; }
        if (at == null) { _restingOn = null; return; }
        var now = Stopwatch.GetTimestamp();
        if (_restingOn != at) { _restingOn = at; _restingSince = now; return; }
        if (Stopwatch.GetElapsedTime(_restingSince, now).TotalMilliseconds < ResistanceMs) return;
        _restingOn = null;
        if (LinkOn(at.Value) is { } link) Cross(link, at.Value, p, desk);
    }

    /// <summary>Corners stay here, so Start, close buttons and hot corners keep working.</summary>
    static bool NearCorner(Edge e, Point p, Rectangle d)
    {
        var (pos, start, length) = e is Edge.Left or Edge.Right ? (p.Y, d.Top, d.Height) : (p.X, d.Left, d.Width);
        var margin = Math.Max(30, length * 3 / 100);
        return pos - start < margin || start + length - 1 - pos < margin;
    }

    void Cross(KvmLink link, Edge edge, Point p, Rectangle desk)
    {
        if (!link.Send(KvmWire.Fraction(KvmMsg.Enter, KvmWire.Along(edge, p, desk)))) return;
        lock (_remoteDown) _remoteDown.Clear();
        _crossedAt = p;
        _crossedEdge = edge;
        _last = p;
        Volatile.Write(ref _lastHeard, Environment.TickCount64);
        _active = link;
        // Park the hidden pointer mid-screen so moves in every direction register.
        var park = Screen.FromPoint(p).Bounds;
        var centre = new Point(park.Left + park.Width / 2, park.Top + park.Height / 2);
        _pointer.Writer.TryWrite(() =>
        {
            if (_active != link) return;   // already back: don't hide
            HiddenCursor.Hide();
            Inject.MoveTo(centre.X, centre.Y);
        });
        _ui(() => { if (_active == link && Clip.ForSync() is { } clip) link.Send(clip); });
    }

    bool OnKey(KeyEvent e)
    {
        if (e.Injected) return false;
        if (_active is not { } active)
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
            Release(BackPoint(), null);
            return true;
        }
        lock (_remoteDown) { if (e.Up) _remoteDown.Remove(e.Vk); else _remoteDown.Add(e.Vk); }
        active.Send(KvmWire.Key(e));
        return true;
    }

    bool Held(params ushort[] vks)
    {
        lock (_remoteDown) return vks.Any(_remoteDown.Contains);
    }

    // ---- links (reading threads) and the beat ----

    void OnLinkMessage(KvmLink link, byte[] m)
    {
        if (m.Length == 0) return;
        if (_active == link) Volatile.Write(ref _lastHeard, Environment.TickCount64);
        switch ((KvmMsg)m[0])
        {
            case KvmMsg.Leave when _active == link:
                Release(KvmWire.OnEdge(link.Edge, KvmWire.ReadFraction(m), Inject.Desktop, ReturnInset), null);
                break;
            case KvmMsg.Locked when _active == link:
                Release(BackPoint(), $"{link.Name} is locked. Unlock it with its own keyboard.");
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
            Release(BackPoint(), $"{link.Name} stopped answering. Control is back on this PC.");
    }

    Point BackPoint()
    {
        var desk = Inject.Desktop;
        return KvmWire.OnEdge(_crossedEdge, KvmWire.Along(_crossedEdge, _crossedAt, desk), desk, ReturnInset);
    }

    void Release(Point at, string? why)
    {
        var link = Interlocked.Exchange(ref _active, null);
        if (link == null) return;
        link.Send(KvmWire.Simple(KvmMsg.Release));
        lock (_remoteDown) _remoteDown.Clear();
        _pointer.Writer.TryWrite(() =>
        {
            Inject.MoveTo(at.X, at.Y);
            HiddenCursor.Show();
        });
        if (why != null) _ui(() => _notify(why, true));
    }

    public void Dispose()
    {
        Pairing.Changed -= Sync;
        _beat.Dispose();
        TakeBack();
        _pointer.Writer.TryComplete();
        _hook.Dispose();
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

    public KvmLink(Identity id, string fingerprint, Func<Device?> find)
    {
        _id = id;
        Fingerprint = fingerprint;
        _find = find;
        _ = Task.Run(Loop);
    }

    public bool Connected => _pipe != null;
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
