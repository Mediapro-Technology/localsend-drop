using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;

namespace LocalSendDrop;

/// <summary>
/// The controlled side: a paired PC connects (GET /api/localsend-drop/v1/kvm,
/// WebSocket over the usual mutual TLS) and, after Enter, its input is played
/// here. When the pointer goes back over the edge facing that PC, we send Leave.
/// Every held key and button is let go when it leaves, goes quiet or disconnects.
/// </summary>
static class KvmRemote
{
    const int TimeoutMs = 1000;
    const int GraceMs = 300;     // just arrived: a wobble back doesn't count
    const int LeavePush = 40;    // px pushed against the facing edge before going back
    const int DragPush = 240;    // carrying a file drag: more, so the strip has caught it first

    /// <summary>Runs work on the UI thread (clipboard).</summary>
    public static Action<Action> Ui = a => a();

    static Session? _active;
    static readonly object Lock = new();
    static DropStrip? _strip;   // UI thread

    static DropStrip.Held? StripHolding() => _strip?.Holding;
    static void StripShow(Edge e, Rectangle desk) => Ui(() => { _strip ??= new DropStrip(); _strip.ShowOn(e, desk); });
    static void StripAway() => Ui(() => _strip?.Away());

    static void Escape()
    {
        Inject.Key(0x1B, 0x01, false, false);
        Inject.Key(0x1B, 0x01, true, false);
    }

    /// <summary>A paired PC started (true) or stopped (false) using this PC's input. The admin helper only plays input in between.</summary>
    public static Action<bool>? ActiveChanged;

    public static async Task Accept(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var fingerprint = ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc) : null;
        if (fingerprint == null || Pairing.Find(fingerprint) == null) { ctx.Response.StatusCode = 403; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new Session(fingerprint, new WsPipe(ws));
        await session.RunAsync(ctx.RequestAborted);
    }

    sealed class Session(string fingerprint, WsPipe pipe)
    {
        bool _on;
        Point _pos;
        long _enteredAt;
        int _push;
        KvmDrag.Staging? _incoming;   // their files, being dragged here
        bool _dropWaiting;            // let go over a drop; played once the files are here
        bool _stripShown;
        long _lastHeard;
        readonly HashSet<(ushort vk, ushort scan, bool ext)> _keys = [];
        readonly HashSet<(MouseKind down, int data)> _buttons = [];

        string Name => Pairing.Find(fingerprint)?.Alias ?? "The other PC";

        /// <summary>Where the controlling PC is, seen from here.</summary>
        Edge Facing => Pairing.Find(fingerprint)?.Edge ?? Edge.Left;

        public async Task RunAsync(CancellationToken ct)
        {
            using var watchdog = new System.Threading.Timer(_ => Watch(), null, 250, 250);
            try { await pipe.RunAsync(OnMessage, ct); }
            finally { lock (this) Off(); }
        }

        void OnMessage(byte[] m)
        {
            if (m.Length == 0) return;
            lock (this)
            {
                _lastHeard = Environment.TickCount64;
                switch ((KvmMsg)m[0])
                {
                    case KvmMsg.Enter: Enter(KvmWire.ReadFraction(m)); break;
                    case KvmMsg.Move when _on && m.Length >= 5:
                        Move(BitConverter.ToInt16(m, 1), BitConverter.ToInt16(m, 3));
                        break;
                    case KvmMsg.Mouse when _on && m.Length >= 4:
                        Button((MouseKind)m[1], BitConverter.ToInt16(m, 2));
                        break;
                    case KvmMsg.Key when _on && m.Length >= 6:
                        Key(BitConverter.ToUInt16(m, 1), BitConverter.ToUInt16(m, 3), (m[5] & 1) != 0, (m[5] & 2) != 0);
                        break;
                    case KvmMsg.Release: Off(); break;
                    case KvmMsg.DragMeta when _on && KvmDrag.ReadMeta(m) is { } carried: StartIncoming(carried.meta, carried.image); break;
                    case KvmMsg.DragWant: KvmDrag.OnWant(KvmDrag.ReadToken(m), fingerprint); break;
                    case KvmMsg.DragCancel: KvmDrag.OnCancel(KvmDrag.ReadToken(m)); break;
                    case KvmMsg.ClipText or KvmMsg.ClipImage: Ui(() => Clip.FromSync(m)); break;
                }
            }
        }

        void Enter(double f)
        {
            if (InputLocked()) { KvmLog.Write($"{Name} tried to cross over, but this PC is locked"); pipe.Send(KvmWire.Simple(KvmMsg.Locked)); return; }
            KvmLog.Write($"{Name} crossed over");
            lock (Lock)
            {
                if (_active != null && _active != this) lock (_active) _active.Off();
                _active = this;
            }
            ActiveChanged?.Invoke(true);
            _on = true;
            _enteredAt = Environment.TickCount64;
            _push = 0;
            _pos = KvmWire.OnEdge(Facing, f, Inject.Desktop, 1);
            Inject.MoveTo(_pos.X, _pos.Y);
        }

        void Move(int dx, int dy)
        {
            var desk = Inject.Desktop;
            var next = new Point(_pos.X + dx, _pos.Y + dy);
            var leftHeld = _buttons.Contains((MouseKind.LeftDown, 0));
            if (KvmWire.Past(Facing, next, desk))
            {
                // Push against the edge to go back, like crossing over: a wobble isn't enough.
                if (Environment.TickCount64 - _enteredAt > GraceMs) _push += Overshoot(Facing, next, desk);
                var holding = leftHeld && _incoming == null ? StripHolding() : null;
                var needed = leftHeld && _incoming == null && holding == null ? DragPush : LeavePush;
                if (_push >= needed)
                {
                    var fraction = KvmWire.Along(Facing, _pos, desk);
                    if (_incoming is { } back)
                    {
                        // Their files' drag going back: it carries on there.
                        KvmLog.Write($"{Name}'s drag went back over the edge");
                        pipe.Send(KvmDrag.TokenMessage(KvmMsg.DragCancel, back.Token));
                        _incoming = null;
                        Escape();
                        KvmDrag.Discard(back.Token);
                        pipe.Send(KvmWire.Fraction(KvmMsg.Leave, fraction));
                        Off();
                        return;
                    }
                    if (holding != null)
                    {
                        // A file drag from here: carry it over, and cancel it here (Esc).
                        KvmLog.Write($"carrying a drag back to {Name}");
                        pipe.Send(KvmWire.Fraction(KvmMsg.Leave, fraction));
                        pipe.Send(KvmDrag.Offer(holding.Paths, fingerprint, holding.Image, holding.Offset, out _));
                        Escape();
                        StripAway();
                        _stripShown = false;
                        Off();
                        return;
                    }
                    KvmLog.Write($"{Name} went back over the edge");
                    pipe.Send(KvmWire.Fraction(KvmMsg.Leave, fraction));
                    Off();
                    Ui(() => { if (Clip.ForSync() is { } clip) pipe.Send(clip); });
                    return;
                }
                next = new Point(Math.Clamp(next.X, desk.Left, desk.Right - 1), Math.Clamp(next.Y, desk.Top, desk.Bottom - 1));
            }
            else _push = 0;
            // Stay on a monitor: the desktop's bounding box can have gaps.
            if (!Screen.AllScreens.Any(s => s.Bounds.Contains(next)))
            {
                var b = Screen.FromPoint(next).Bounds;
                next = new Point(Math.Clamp(next.X, b.Left, b.Right - 1), Math.Clamp(next.Y, b.Top, b.Bottom - 1));
            }
            _pos = next;
            Inject.MoveTo(_pos.X, _pos.Y);
            // A drag (button held) at the edge facing them: put the strip there to catch files.
            var strip = leftHeld && _incoming == null && KvmWire.AtEdge(Facing, _pos, desk);
            if (strip != _stripShown)
            {
                _stripShown = strip;
                if (strip) StripShow(Facing, desk); else StripAway();
            }
        }

        /// <summary>Their files came over with the pointer: drag them here (the button is pressed for them).</summary>
        void StartIncoming(KvmDrag.Meta meta, Bitmap? image)
        {
            var st = KvmDrag.Stage(meta, fingerprint);
            pipe.Send(KvmDrag.TokenMessage(KvmMsg.DragWant, meta.Token));
            _incoming = st;
            Ui(() => KvmDrag.RunDrag(st.Top, image, new Point(meta.OffsetX, meta.OffsetY),
                () => { lock (this) _buttons.Add((MouseKind.LeftDown, 0)); Inject.Button(MouseKind.LeftDown); },
                dropped =>
                {
                    lock (this) if (_incoming == st) _incoming = null;
                    if (!dropped)
                    {
                        pipe.Send(KvmDrag.TokenMessage(KvmMsg.DragCancel, st.Token));
                        KvmDrag.Discard(st.Token);
                    }
                }));
        }

        static int Overshoot(Edge e, Point p, Rectangle d) => e switch
        {
            Edge.Left => d.Left - p.X,
            Edge.Right => p.X - (d.Right - 1),
            Edge.Top => d.Top - p.Y,
            _ => p.Y - (d.Bottom - 1),
        };

        void Button(MouseKind kind, int data)
        {
            if (kind == MouseKind.LeftUp && _incoming is { } st)
            {
                // Let go over a drop: play it once their files are here, so nothing lands half-copied.
                _buttons.Remove((MouseKind.LeftDown, 0));
                _dropWaiting = true;
                _ = Task.Run(async () =>
                {
                    string? error;
                    try { error = await st.Done.Task.WaitAsync(TimeSpan.FromMinutes(30)); }
                    catch (TimeoutException) { error = "it took too long"; }
                    lock (this)
                    {
                        if (!_dropWaiting) return;
                        _dropWaiting = false;
                        if (error != null) { KvmLog.Write($"drag: dropping cancelled ({error})"); Escape(); }
                        Inject.Button(MouseKind.LeftUp);
                    }
                });
                return;
            }
            // Only X buttons and wheels carry data.
            var d = kind is MouseKind.XDown or MouseKind.XUp or MouseKind.Wheel or MouseKind.HWheel ? data : 0;
            switch (kind)
            {
                case MouseKind.LeftDown or MouseKind.RightDown or MouseKind.MiddleDown or MouseKind.XDown: _buttons.Add((kind, d)); break;
                case MouseKind.LeftUp: _buttons.Remove((MouseKind.LeftDown, 0)); break;
                case MouseKind.RightUp: _buttons.Remove((MouseKind.RightDown, 0)); break;
                case MouseKind.MiddleUp: _buttons.Remove((MouseKind.MiddleDown, 0)); break;
                case MouseKind.XUp: _buttons.Remove((MouseKind.XDown, d)); break;
            }
            Inject.Button(kind, d);
        }

        void Key(ushort vk, ushort scan, bool up, bool ext)
        {
            if (up) _keys.Remove((vk, scan, ext)); else _keys.Add((vk, scan, ext));
            Inject.Key(vk, scan, up, ext);
        }

        /// <summary>Lets go of everything still held and stops taking input. Caller holds the lock.</summary>
        public void Off()
        {
            if (_incoming is { } st)
            {
                // Stopped mid-drag: cancel it here rather than drop half-copied files.
                Escape();
                if (_dropWaiting) { Inject.Button(MouseKind.LeftUp); _dropWaiting = false; }
                pipe.Send(KvmDrag.TokenMessage(KvmMsg.DragCancel, st.Token));
                KvmDrag.Discard(st.Token);
                _incoming = null;
            }
            if (_stripShown) { _stripShown = false; StripAway(); }
            foreach (var (vk, scan, ext) in _keys) Inject.Key(vk, scan, true, ext);
            _keys.Clear();
            foreach (var (down, data) in _buttons)
                Inject.Button(down switch
                {
                    MouseKind.LeftDown => MouseKind.LeftUp, MouseKind.RightDown => MouseKind.RightUp,
                    MouseKind.MiddleDown => MouseKind.MiddleUp, _ => MouseKind.XUp,
                }, data);
            _buttons.Clear();
            var was = _on;
            _on = false;
            var last = false;
            lock (Lock) if (_active == this) { _active = null; last = true; }
            if (was && last) ActiveChanged?.Invoke(false);
        }

        void Watch()
        {
            lock (this)
            {
                if (!_on) return;
                pipe.Send(KvmWire.Simple(KvmMsg.Beat));
                if (InputLocked()) { KvmLog.Write("locked while in use"); pipe.Send(KvmWire.Simple(KvmMsg.Locked)); Off(); }
                else if (Environment.TickCount64 - _lastHeard > TimeoutMs) { KvmLog.Write($"{Name} went quiet; let go"); Off(); }
            }
        }
    }

    /// <summary>The lock screen or a UAC prompt has the input; injected input can't reach it.</summary>
    static bool InputLocked()
    {
        var desk = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
        if (desk == 0) return true;
        try
        {
            var name = new char[64];
            return !GetUserObjectInformation(desk, 2, name, name.Length * 2, out _)   // UOI_NAME
                || !new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desk); }
    }

    [DllImport("user32.dll")] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(nint obj, int index, char[] info, int length, out int needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desk);
}

/// <summary>
/// Diagnostics: --kvm-loopback. This PC connects to its own server over the
/// real TLS WebSocket, pretending to be a paired PC on the left, and checks
/// what the controlled side does. Moves the pointer briefly, then puts it back.
/// </summary>
static class KvmTest
{
    public static int Loopback()
    {
        ApplicationConfiguration.Initialize();
        var id = Identity.LoadOrCreate();
        var (discovery, server) = Program.StartNetwork(id);
        var self = new Device { Fingerprint = id.Fingerprint, Alias = "self", Ip = "127.0.0.1", Port = server.Port, Protocol = "https" };
        var start = Inject.CursorPos;
        var desk = Inject.Desktop;
        var failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

        try
        {
            Pairing.UseForTest([]);
            Check(Connect(id, self) == null, "an unpaired PC is refused");

            Pairing.UseForTest([new PairedPc { Fingerprint = id.Fingerprint, Alias = "self", Edge = Edge.Left }]);
            using var ws = Connect(id, self);
            Check(ws != null, "a paired PC connects (mutual TLS, pinned fingerprint)");
            if (ws == null) return 1;
            var got = new System.Collections.Concurrent.BlockingCollection<byte[]>();
            var pipe = new WsPipe(ws);
            var run = pipe.RunAsync(got.Add, CancellationToken.None);
            byte[]? Next(KvmMsg type, int ms)
            {
                var until = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < until)
                    if (got.TryTake(out var m, 50) && m[0] == (byte)type) return m;
                return null;
            }

            pipe.Send(KvmWire.Fraction(KvmMsg.Enter, 0.5));
            Thread.Sleep(150);
            var entered = KvmWire.OnEdge(Edge.Left, 0.5, desk, 1);
            Check(Near(Inject.CursorPos, entered), $"Enter puts the pointer on the facing edge ({entered}, got {Inject.CursorPos})");

            pipe.Send(KvmWire.Move(100, 0));
            Thread.Sleep(100);
            Check(Near(Inject.CursorPos, new Point(entered.X + 100, entered.Y)), $"Move goes 100 px right (got {Inject.CursorPos})");

            pipe.Send(KvmWire.Move(-102, 0));   // a 1 px wobble past the edge
            Check(Next(KvmMsg.Leave, 200) == null, "a small wobble past the edge doesn't send it back");
            Thread.Sleep(300);
            pipe.Send(KvmWire.Move(-60, 0));
            var leave = Next(KvmMsg.Leave, 1000);
            Check(leave != null && Math.Abs(KvmWire.ReadFraction(leave) - 0.5) < 0.01, "pushing past the facing edge sends Leave at the same height");

            pipe.Send(KvmWire.Move(50, 0));
            Thread.Sleep(100);
            var afterLeave = Inject.CursorPos;
            Check(Near(afterLeave, new Point(desk.Left, entered.Y)), $"input after Leave is ignored (got {afterLeave})");

            pipe.Send(KvmWire.Fraction(KvmMsg.Enter, 0.25));
            Check(Next(KvmMsg.Beat, 600) != null, "the controlled side sends heartbeats while in use");
            Thread.Sleep(1500);   // say nothing: the watchdog should let go
            var quiet = Inject.CursorPos;
            pipe.Send(KvmWire.Move(80, 0));
            Thread.Sleep(100);
            Check(Near(Inject.CursorPos, quiet), "after 1 s of silence it stops taking input");

            ws.Abort();
            run.Wait(2000);
        }
        finally
        {
            Inject.MoveTo(start.X, start.Y);
            discovery.Dispose();
            server.DisposeAsync().AsTask().Wait(2000);
        }
        Console.WriteLine(failures == 0 ? "RESULT: all passed" : $"RESULT: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

    static System.Net.WebSockets.ClientWebSocket? Connect(Identity id, Device d)
    {
        var ws = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            using var invoker = new HttpMessageInvoker(LsHttp.Handler(id, LsHttp.PinFor(d)));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            ws.ConnectAsync(new Uri(d.Root.Replace("https://", "wss://") + "/api/localsend-drop/v1/kvm"), invoker, cts.Token).GetAwaiter().GetResult();
            return ws;
        }
        catch (Exception) { ws.Dispose(); return null; }
    }
}
