using System.Collections.Concurrent;
using System.Text.Json;

namespace LocalSendDrop;

/// <summary>Hands out the stamp (when, by whom) for values changed together.</summary>
readonly record struct Stamper(long At, string By)
{
    public Stamped<T> Of<T>(T value) => new(value, At, By);
}

/// <summary>
/// Keeps the two paired PCs' view of each other current: which monitors each has, and the shared
/// crossing settings. Messages arrive on link reading threads.
/// </summary>
static class KvmSync
{
    /// <summary>The other PCs' monitors, by fingerprint.</summary>
    public static readonly ConcurrentDictionary<string, DisplayInfo[]> PeerDisplays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a peer's monitors changed.</summary>
    public static event Action<string>? DisplaysChanged;

    /// <summary>Raised when the crossing settings changed because of the other PC (any thread).</summary>
    public static event Action? SettingsChanged;

    // Seams for tests: where the settings live, how they are saved, who we are, what time it is.
    internal static Func<CrossingSettings> Load = () => Settings.Current.Crossing;
    internal static Action<CrossingSettings, string?> Store = StoreToSettings;
    internal static Func<string> Me = () => _me ??= Identity.LoadOrCreate().Fingerprint;
    internal static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string? _me;

    static readonly object Gate = new();
    static readonly List<Func<byte[], bool>> Links = [];

    internal static void Reset()
    {
        lock (Gate) Links.Clear();
        PeerDisplays.Clear();
    }

    /// <summary>A link just came up (either direction): tell them about our monitors and our settings.</summary>
    public static void Hello(Func<byte[], bool> send)
    {
        // Registered first, so a change made while we say hello still reaches this link.
        lock (Gate) Links.Add(send);
        try
        {
            send(KvmWire.JsonMsg(KvmMsg.Displays, KvmZones.Current()));
            send(KvmWire.JsonMsg(KvmMsg.Sync, Load()));
        }
        catch { }
    }

    /// <summary>Our monitors changed: tell every linked PC.</summary>
    public static void ResendDisplays()
    {
        Func<byte[], bool>[] links;
        lock (Gate) links = [.. Links];
        var m = KvmWire.JsonMsg(KvmMsg.Displays, KvmZones.Current());
        foreach (var send in links) try { send(m); } catch { }
    }

    /// <summary>Displays and Sync messages from <paramref name="fingerprint"/>.</summary>
    public static void OnMessage(string fingerprint, byte[] m)
    {
        if (m.Length < 2) return;
        switch ((KvmMsg)m[0])
        {
            case KvmMsg.Displays when KvmWire.ReadJson<DisplayInfo[]>(m) is { } displays:
                PeerDisplays[fingerprint] = displays;
                DisplaysChanged?.Invoke(fingerprint);
                break;
            case KvmMsg.Sync when KvmWire.ReadJson<CrossingSettings>(m) is { } theirs:
                MergeSettings(fingerprint, theirs);
                break;
        }
    }

    /// <summary>
    /// Changes settings: <paramref name="change"/> gets the current ones and a stamp for what it sets.
    /// The stamp is never older than anything seen, so a PC with a slow clock still wins with its latest change.
    /// </summary>
    public static void Set(Func<CrossingSettings, Stamper, CrossingSettings> change)
    {
        CrossingSettings next;
        lock (Gate)
        {
            var now = Load();
            var at = Math.Max(Clock(), NewestStamp(now) + 1);
            next = change(now, new Stamper(at, Me()));
            if (Same(now, next)) return;
            Store(next, null);
        }
        Broadcast(next);
    }

    static void MergeSettings(string fingerprint, CrossingSettings theirs)
    {
        CrossingSettings merged;
        bool changed;
        lock (Gate)
        {
            var mine = Load();
            merged = CrossingSettings.Merge(mine, theirs);
            changed = !Same(mine, merged);
            if (changed) Store(merged, fingerprint);
        }
        if (changed) { try { SettingsChanged?.Invoke(); } catch { } }
        // Only answer when they are behind; adopting theirs is not re-broadcast, so two PCs settle after one exchange.
        // Compared with their values as we'd accept them, so a peer sending something out of range can't start a ping-pong.
        if (!Same(merged, theirs.Sanitized())) Broadcast(merged);
    }

    static void Broadcast(CrossingSettings settings)
    {
        var message = KvmWire.JsonMsg(KvmMsg.Sync, settings);
        Func<byte[], bool>[] links;
        lock (Gate) links = [.. Links];
        foreach (var send in links)
        {
            bool ok;
            try { ok = send(message); } catch { ok = false; }
            if (!ok) lock (Gate) Links.Remove(send);
        }
    }

    static long NewestStamp(CrossingSettings c) => new[]
    {
        c.Shortcut.SetAt, c.DoubleTapMs.SetAt, c.ResistancePx.SetAt, c.Corners.SetAt, c.Notch.SetAt, c.Glow.SetAt,
        c.Arrival.SetAt, c.HideIdle.SetAt, c.PointerSpeed.SetAt, c.ScrollSpeed.SetAt, c.ReverseScroll.SetAt, c.Layout.SetAt,
    }.Max();

    /// <summary>Equal by value (records compare their arrays by reference).</summary>
    static bool Same(CrossingSettings a, CrossingSettings b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    /// <summary>
    /// This PC's zones for a layout. A layout is written from the setter's point of view; one set on the
    /// other PC is turned around (their display and side become the target).
    /// </summary>
    internal static Zone[] LocalZones(Stamped<Zone[]> layout, string me, string peer) =>
        layout.By == me
            ? layout.Value
            : [.. layout.Value.Select(z => new Zone(z.TargetDisplay, z.TargetSide, z.TargetFrom, z.TargetTo, peer, z.Display, z.Side, z.From, z.To))];

    static void StoreToSettings(CrossingSettings c, string? peer)
    {
        var s = Settings.Current;
        s.Crossing = c;
        if (c.Layout.SetAt > 0)
        {
            var fp = peer ?? s.Paired.FirstOrDefault()?.Fingerprint ?? "";
            s.Zones = [.. LocalZones(c.Layout, Me(), fp)];
        }
        try { s.Save(); } catch { }
    }
}
