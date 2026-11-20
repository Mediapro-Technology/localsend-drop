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
    // Dragging files across:
    readonly DropStrip _strip;
    readonly System.Windows.Forms.Timer _dragWatch;
    volatile DropStrip.Held? _carry;          // a file drag caught at the edge, crossing with it now
    volatile Edge _carryEdge;
    string? _outgoing;                        // our files, being dragged on the other PC
    (List<string> paths, Bitmap? image, Point offset)? _cameBack;   // that drag came back: carry on here
    volatile KvmDrag.Staging? _incoming;      // their files, being dragged here
    volatile bool _upHeld;
    // Changed on the UI thread, read on the hook thread.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, KvmLink> _links = new(StringComparer.OrdinalIgnoreCase);

    volatile KvmLink? _active;
    // What each link registered with KvmSync while it is up, so it can be taken out when the link drops.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> _hellos = new(StringComparer.OrdinalIgnoreCase);
    // Hiding and showing the pointer run in order on one worker, never in parallel.
    readonly System.Threading.Channels.Channel<Action> _pointer = System.Threading.Channels.Channel.CreateUnbounded<Action>(
        new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    long _lastHeard;
    Point _crossedAt;
    string _lastPeer = "";            // the PC last crossed to, for the shortcut
    int _lastTargetDisplay;
    Zone? _crossedZone;              // the zone we crossed through: the pointer comes back through it
    public Kvm(Identity id, Discovery discovery, Action<Action> ui, Action<string, bool> notify)
    {
        _id = id;
        _discovery = discovery;
        _ui = ui;
        _notify = notify;
        HiddenCursor.RecoverAfterCrash();
        KvmLook.Start();
        _capture = new CaptureWindow();
        _strip = new DropStrip();
        _dragWatch = new System.Windows.Forms.Timer { Interval = 40 };
        _dragWatch.Tick += (_, _) => DragWatch();
        _dragWatch.Start();
        _source = Wire(new InputEngine());
        Sync();
        Pairing.Changed += Sync;
        KvmSync.SettingsChanged += UpdateEdges;   // a new layout or option from the other PC
        KvmSync.LocalChanged += UpdateEdges;      // ... or one made here: apply it now
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        KvmLook.IsCrossed = () => CrossedOver;
        KvmLook.IsLive = fp => _links.TryGetValue(fp, out var l) && l.IsConnected;
        KvmRemote.ActiveChanged = on => (_source as HelperClient)?.SetRemoteActive(on);
        KvmRemote.Away = () => _active != null || Volatile.Read(ref _crossing);   // from before our Enter goes out
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

    /// <summary>Whether the link to that paired PC is up.</summary>
    public bool IsLinked(string fingerprint) => _links.TryGetValue(fingerprint, out var l) && l.IsConnected;

    IInputSource Wire(IInputSource s)
    {
        s.Crossed += OnCrossed;
        s.Wake += OnWake;
        s.Forward += m => { if (_active is { } l) l.Send(m); else s.Free(); };
        s.Emergency += () => Release(BackPoint(), null, "Ctrl+Alt+Home");
        s.Stray += OnStray;
        s.Shortcut += OnShortcut;
        s.LeftUpHeld += OnLeftUpHeld;
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

    /// <summary>
    /// The zones that lead to a paired PC: the saved layout, or for a pairing without one (made before
    /// layouts existed) a full-span zone on the outer edge it was paired on.
    /// </summary>
    internal static Zone[] ZonesFor(PairedPc pc) => ZonesFor(pc, Settings.Current.Zones, KvmZones.Current());

    internal static Zone[] ZonesFor(PairedPc pc, IEnumerable<Zone> saved, IReadOnlyList<DisplayInfo> displays)
    {
        var own = saved.Where(z => string.Equals(z.Peer, pc.Fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
        return own.Length > 0 ? KvmZones.Widen(own) : [.. KvmZones.Migrate(pc, displays)];
    }

    /// <summary>
    /// What to send on crossing through <paramref name="zone"/>: the monitor and side it leads to, if
    /// the zone knows them and the peer understands that; else a plain fraction (the peer uses its own outer edge).
    /// </summary>
    internal static byte[] EnterMessage(Zone zone, double at, bool peerKnown) =>
        peerKnown && zone.TargetDisplay >= 0
            ? KvmWire.EnterAt(zone.TargetDisplay, zone.TargetSide, at)
            : KvmWire.Fraction(KvmMsg.Enter, at);

    /// <summary>
    /// The zone the pointer comes back through: of the PC's zones, the one that leads to the monitor and side
    /// it left by (the one whose span holds <paramref name="fraction"/>, if several do); else the zone it crossed through.
    /// </summary>
    internal static Zone ZoneForLeave(IEnumerable<Zone> zones, Zone crossed, int? display, Edge? side, double fraction)
    {
        if (display is not { } di || side is not { } sd) return crossed;
        var fits = zones.Where(z => z.Peer == crossed.Peer && z.TargetDisplay == di && z.TargetSide == sd).ToList();
        if (fits.Count == 0) return crossed;
        return fits.FirstOrDefault(z => fraction >= Math.Min(z.TargetFrom, z.TargetTo) && fraction <= Math.Max(z.TargetFrom, z.TargetTo))
            ?? fits.OrderBy(z => Math.Min(Math.Abs(fraction - z.TargetFrom), Math.Abs(fraction - z.TargetTo))).First();
    }

    /// <summary>Tells the input source which zones lead to a connected paired PC and which to an offline one.</summary>
    void UpdateEdges()
    {
        var live = new List<Zone>();
        var offline = new List<Zone>();
        foreach (var p in Pairing.All)
            (_links.TryGetValue(p.Fingerprint, out var l) && l.IsConnected ? live : offline).AddRange(ZonesFor(p));
        _source.SetZones([.. live], [.. offline]);
    }

    /// <summary>Pushed into an offline PC's zone (at most once per 30 s): send it a Wake-on-LAN packet.</summary>
    void OnWake(Zone zone) => Task.Run(() => Wake(zone));   // hook thread: no file or socket work here

    /// <summary>A monitor was added, removed or moved: new zone geometry here, and tell the linked PCs.</summary>
    void OnDisplaysChanged(object? sender, EventArgs e)
    {
        UpdateEdges();
        KvmSync.ResendDisplays();
    }

    void Wake(Zone zone)
    {
        var pc = Pairing.Find(zone.Peer);
        if (pc == null) return;
        var name = Settings.Current.Nicknames.GetValueOrDefault(pc.Fingerprint) ?? pc.Alias;
        KvmLog.Write(WakeOnLan.Send(pc.Mac)
            ? $"wake packet sent to {name} ({pc.Mac})"
            : $"wanted to wake {name}, but its hardware address isn't known yet (it is learnt while linked)");
    }

    /// <summary>A link came up: remember the PC's hardware address, so it can be woken when it's off.</summary>
    void LearnMac(KvmLink link)
    {
        var pc = Pairing.Find(link.Fingerprint);
        var device = _discovery.Devices.FirstOrDefault(d => string.Equals(d.Fingerprint, link.Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (pc == null || device == null || !System.Net.IPAddress.TryParse(device.Ip, out var ip)) return;
        var mac = WakeOnLan.LearnMac(ip);
        if (mac == "" || mac == pc.Mac) return;
        _ui(() => { pc.Mac = mac; Settings.Current.Save(); KvmLog.Write($"learnt the hardware address of {pc.Alias}: {mac}"); });
    }

    /// <summary>This PC's monitor <paramref name="index"/>, or the whole desktop if it's gone.</summary>
    static Rectangle DisplayBounds(int index)
    {
        var all = KvmZones.Current();
        return all.FirstOrDefault(d => d.Index == index)?.Bounds ?? Inject.Desktop;
    }

    KvmLink? LinkFor(string fingerprint) =>
        _links.TryGetValue(fingerprint, out var l) && l.IsConnected ? l : null;

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
            if (_hellos.TryRemove(gone, out var hello)) hello.Dispose();
        }
        foreach (var fp in paired.Where(fp => !_links.ContainsKey(fp)))
        {
            var link = new KvmLink(_id, fp, () => _discovery.Devices.FirstOrDefault(d =>
                string.Equals(d.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)));
            link.Message += OnLinkMessage;
            link.Dropped += l =>
            {
                KvmLog.Write($"link to {l.Name} dropped");
                if (_hellos.TryRemove(l.Fingerprint, out var hello)) hello.Dispose();
                if (_active == l) Release(BackPoint(), $"Lost the connection to {l.Name}", "link dropped");
                UpdateEdges();
            };
            link.Connected += l =>
            {
                KvmLog.Write($"link to {l.Name} connected");
                if (_hellos.TryRemove(l.Fingerprint, out var previous)) previous.Dispose();
                _hellos[l.Fingerprint] = KvmSync.Hello(l.Send);
                UpdateEdges();
                _ = Task.Run(() => LearnMac(l));
            };
            _links[fp] = link;
        }
        UpdateEdges();
    }

    // ---- the input source (any thread) ----

    bool _crossing;   // an Enter of ours is on its way

    void OnCrossed(Zone zone, Point p)
    {
        var source = _source;
        if (KvmRemote.Controlled)
        {
            // Another PC is driving this one: one owner at a time, so no crossing of our own.
            _carry = null;
            source.Free();
            return;
        }
        var link = LinkFor(zone.Peer);
        var fraction = KvmWire.Along(zone.Side, p, DisplayBounds(zone.Display));
        var at = KvmZones.Target(zone, fraction);
        // A peer that has told us its monitors understands EnterAt; an older one only gets Enter.
        var enter = EnterMessage(zone, at, KvmSync.PeerDisplays.ContainsKey(zone.Peer));
        // Claimed before the Enter goes out, so a crossing coming the other way meanwhile is refused (busy).
        Volatile.Write(ref _crossing, true);
        try
        {
            if (link == null || !link.Send(enter)) { source.Free(); return; }
            _crossedAt = p;
            _crossedZone = zone;
            _lastPeer = zone.Peer;
            _lastTargetDisplay = zone.TargetDisplay;
            Volatile.Write(ref _lastHeard, Environment.TickCount64);   // before _active, so the watchdog never sees a stale time
            _active = link;
        }
        finally { Volatile.Write(ref _crossing, false); }
        KvmLog.Write($"crossed to {link.Name} over {zone.Side} of display {zone.Display} at {p}{(source is HelperClient ? " (admin helper)" : "")}");
        if (_carry is { } carry && _carryEdge == zone.Side)
        {
            // Carrying a file drag over: they start their own drag of it; ours is cancelled with Esc
            // (the button stays held, and now drives the drag there).
            _carry = null;
            link.Send(KvmDrag.Offer(carry.Paths, link.Fingerprint, carry.Image, carry.Offset, out var token));
            _outgoing = token;
            _pointer.Writer.TryWrite(() => { Inject.Key(0x1B, 0x01, false, false); Inject.Key(0x1B, 0x01, true, false); });
            _ui(() => _strip.Away());
        }
        // Park the hidden pointer mid-screen so moves in every direction register.
        var park = Screen.FromPoint(p).Bounds;
        var centre = new Point(park.Left + park.Width / 2, park.Top + park.Height / 2);
        _pointer.Writer.TryWrite(() =>
        {
            if (_active != link) return;   // already back: don't hide
            HiddenCursor.Hide();
            Inject.MoveTo(centre.X, centre.Y);
        });
        // Left at the edge, the hidden pointer would count moves away from it but not into it: say so if it didn't park.
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            if (_active != link) return;
            var at = Inject.CursorPos;
            if (Math.Abs(at.X - centre.X) + Math.Abs(at.Y - centre.Y) > 40)
                KvmLog.Write($"the hidden pointer didn't park mid-screen (at {at}, wanted {centre})");
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

    /// <summary>
    /// Double-tap of right Ctrl. Crossed over: come back here. Otherwise go to the last PC used (else
    /// the first connected one), arriving in the middle of the monitor we were last on over there.
    /// </summary>
    void OnShortcut()
    {
        // Being controlled: the shortcut sends the input home to that PC, never a crossing of our own.
        if (KvmRemote.HandBack("shortcut")) return;
        if (_active != null) { Release(BackPoint(), null, "shortcut"); return; }
        var zones = Pairing.All.Where(p => LinkFor(p.Fingerprint) != null).SelectMany(ZonesFor).ToList();
        if (zones.Count == 0) return;
        var peer = zones.Any(z => z.Peer == _lastPeer) ? _lastPeer : zones[0].Peer;
        var mine = zones.Where(z => z.Peer == peer).ToList();
        var zone = mine.FirstOrDefault(z => z.TargetDisplay == _lastTargetDisplay) ?? mine[0];
        // The zone's own monitor, and the point on it whose place along the edge maps to the middle of the target.
        var bounds = DisplayBounds(zone.Display);
        var f = zone.TargetTo == zone.TargetFrom ? (zone.From + zone.To) / 2
            : Math.Clamp(zone.From + (0.5 - zone.TargetFrom) / (zone.TargetTo - zone.TargetFrom) * (zone.To - zone.From), Math.Min(zone.From, zone.To), Math.Max(zone.From, zone.To));
        var at = KvmWire.OnEdge(zone.Side, f, bounds, 1);
        _source.Cross(zone, at);
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
                var left = KvmWire.ReadLeave(m);
                Release(ReturnPoint(left.fraction, left.display, left.side), null, "came back over the edge");
                if (_cameBack is { } back)
                {
                    // Our files' drag came back over the edge, button still held: it carries on here.
                    _cameBack = null;
                    _ui(() => KvmDrag.RunDrag(back.paths, back.image, back.offset, PressHere, _ => { }));
                }
                _outgoing = null;
                break;
            case KvmMsg.Displays or KvmMsg.Sync:
                KvmSync.OnMessage(link.Fingerprint, m);
                break;
            case KvmMsg.DragWant:
                KvmDrag.OnWant(KvmDrag.ReadToken(m), link.Fingerprint);
                break;
            case KvmMsg.DragCancel:
                var cancelled = KvmDrag.ReadToken(m);
                if (cancelled == _outgoing) _cameBack = KvmDrag.Mine(cancelled);
                KvmDrag.OnCancel(cancelled);
                break;
            case KvmMsg.DragMeta when KvmDrag.ReadMeta(m) is { } carried:
                _ui(() => StartIncoming(link, carried.meta, carried.image));
                break;
            case KvmMsg.Locked when _active == link:
                Release(BackPoint(), $"{link.Name} is locked. Unlock it with its own keyboard.", "they're locked");
                break;
            case KvmMsg.Busy when _active == link:
                Release(BackPoint(), $"{link.Name}'s mouse and keyboard are on another PC right now.", "they're busy");
                break;
            case KvmMsg.Paused when _active == link:
                Release(BackPoint(), $"Sharing is paused on {link.Name}: Beamer is running there.", "they're paused");
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

    // ---- dragging files across ----

    static bool LeftHeld() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>UI thread, every 40 ms: a file drag resting on a shared edge crosses over with its files.</summary>
    void DragWatch()
    {
        if (KvmRemote.Controlled || _active != null || _incoming != null || !LeftHeld()) { _strip.Away(); return; }
        var p = Cursor.Position;
        var displays = KvmZones.Current();
        var live = Pairing.All.Where(pc => LinkFor(pc.Fingerprint) != null).SelectMany(ZonesFor).ToList();
        if (KvmZones.Hit(live, displays, p) is { } zone
            && displays.FirstOrDefault(d => d.Index == zone.Display) is { } display
            && !KvmZones.InCorner(p, display.Bounds, Math.Max(30, Math.Min(display.Bounds.Width, display.Bounds.Height) * 3 / 100)))
        {
            _strip.ShowOn(zone.Side, display.Bounds);
            if (_strip.Holding is { } held && _strip.HeldFor >= 150)
            {
                _carryEdge = zone.Side;
                _carry = held;
                _source.Cross(zone, p);
            }
            return;
        }
        _strip.Away();
    }

    /// <summary>
    /// While crossed over, the button press went to the other PC, so Windows here thinks it's up.
    /// Press it again here (on the invisible window the drag starts from); letting go by hand ends it.
    /// </summary>
    static void PressHere() => Inject.Button(MouseKind.LeftDown);

    /// <summary>UI thread. Their files came over the edge with the pointer, button held: drag them here.</summary>
    void StartIncoming(KvmLink link, KvmDrag.Meta meta, Bitmap? image)
    {
        var st = KvmDrag.Stage(meta, link.Fingerprint);
        link.Send(KvmDrag.TokenMessage(KvmMsg.DragWant, meta.Token));
        _incoming = st;
        // Letting go before they've arrived waits for them; once they're here it goes straight through.
        _source.HoldLeftUp(true);
        _ = st.Done.Task.ContinueWith(_ => { if (_incoming == st && !_upHeld) _source.HoldLeftUp(false); }, TaskScheduler.Default);
        KvmDrag.RunDrag(st.Top, image, new Point(meta.OffsetX, meta.OffsetY), PressHere, dropped =>
        {
            if (_incoming == st) _incoming = null;
            _source.HoldLeftUp(false);
            if (!dropped)
            {
                link.Send(KvmDrag.TokenMessage(KvmMsg.DragCancel, st.Token));
                KvmDrag.Discard(st.Token);
            }
        });
    }

    /// <summary>The button was let go over a drop while their files are still coming: play it once they're here.</summary>
    void OnLeftUpHeld()
    {
        if (_incoming is not { } st) { Inject.Button(MouseKind.LeftUp); return; }
        _upHeld = true;
        _ = Task.Run(async () =>
        {
            string? error;
            try { error = await st.Done.Task.WaitAsync(TimeSpan.FromMinutes(30)); }
            catch (TimeoutException) { error = "it took too long"; }
            if (error != null)
            {
                Inject.Key(0x1B, 0x01, false, false);   // cancel the drop
                Inject.Key(0x1B, 0x01, true, false);
                _ui(() => _notify($"Couldn't bring the files over: {error}", true));
            }
            _upHeld = false;
            Inject.Button(MouseKind.LeftUp);
        });
    }

    /// <summary>Where the pointer comes back: where it left, or (they sent <paramref name="theirFraction"/>) through the zone it crossed.</summary>
    Point BackPoint() => ReturnPoint(null, null, null);

    Point ReturnPoint(double? theirFraction, int? theirDisplay, Edge? theirSide)
    {
        if (_crossedZone is not { } crossed) return Inject.CursorPos;
        // They say which monitor and side the pointer left by: come back through the zone that leads there.
        var zone = theirFraction is { } tf && Pairing.Find(crossed.Peer) is { } pc
            ? ZoneForLeave(ZonesFor(pc), crossed, theirDisplay, theirSide, tf)
            : crossed;
        var d = DisplayBounds(zone.Display);
        var fraction = theirFraction is { } f ? CrossingRule.Return(zone, f) : KvmWire.Along(zone.Side, _crossedAt, d);
        return KvmWire.OnEdge(zone.Side, fraction, d, ReturnInset);
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
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(nint token, int cls, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    public void Dispose()
    {
        Pairing.Changed -= Sync;
        KvmSync.SettingsChanged -= UpdateEdges;
        KvmSync.LocalChanged -= UpdateEdges;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        KvmLook.IsCrossed = () => false;
        KvmLook.IsLive = _ => false;   // no links any more: the glow and idle hiding stand down
        _beat.Dispose();
        TakeBack();
        _pointer.Writer.TryComplete();
        _capture.Dispose();
        _dragWatch.Dispose();
        _strip.Dispose();
        _wantHelper = false;
        Inject.Via = null;
        _source.Dispose();
        HiddenCursor.ShowAll();   // every reason: crossed over or idle, the pointer is back
        foreach (var l in _links.Values) l.Dispose();
        foreach (var h in _hellos.Values) h.Dispose();
        _hellos.Clear();
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

    // Why it is hidden: "crossing" (input is on the other PC) or "idle". It comes back only when none is left,
    // so the idle timer can never unhide a pointer that is crossed over.
    public const string Crossing = "crossing", Idle = "idle";
    static readonly HashSet<string> Reasons = [];

    // What actually changes the system cursors (tests swap these so they don't touch the real ones).
    internal static Action Blank = BlankCursors;
    internal static Action Restorer = Restore;

    public static void Hide(string reason = Crossing)
    {
        lock (Lock)
        {
            Reasons.Add(reason);
            if (_hidden) return;
            Blank();
            _hidden = true;
        }
    }

    static void BlankCursors()
    {
        try { File.WriteAllText(Flag, ""); } catch { }
        var and = Enumerable.Repeat((byte)0xFF, 128).ToArray();
        var xor = new byte[128];
        var module = Native.GetModuleHandle(null);
        foreach (var id in Ids)
        {
            var blank = CreateCursor(module, 0, 0, 32, 32, and, xor);
            if (blank != 0 && !SetSystemCursor(blank, id)) DestroyCursor(blank);
        }
    }

    public static void Show(string reason = Crossing)
    {
        lock (Lock)
        {
            Reasons.Remove(reason);
            if (!_hidden || Reasons.Count > 0) return;
            Restorer();
            _hidden = false;
        }
    }

    /// <summary>Brings the pointer back whatever still wants it hidden (shutting down).</summary>
    public static void ShowAll()
    {
        lock (Lock)
        {
            Reasons.Clear();
            if (!_hidden) return;
            Restorer();
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
