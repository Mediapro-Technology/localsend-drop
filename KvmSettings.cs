namespace LocalSendDrop;

/// <summary>A setting value with when it was set and by whom, so two PCs can agree on the newer one.</summary>
record Stamped<T>(T Value, long SetAt, string By)
{
    /// <summary>The newer of the two; on a tie the larger <c>By</c> wins, so both PCs pick the same one.</summary>
    public static Stamped<T> Newer(Stamped<T> a, Stamped<T> b)
    {
        if (a.SetAt != b.SetAt) return a.SetAt > b.SetAt ? a : b;
        return string.CompareOrdinal(a.By, b.By) >= 0 ? a : b;
    }
}

/// <summary>Crossing settings, kept the same on both paired PCs (each value stamped; the newer wins).</summary>
record CrossingSettings
{
    static Stamped<T> D<T>(T v) => new(v, 0, "");

    /// <summary>"RightCtrl" (double-tap) or "" for none.</summary>
    public Stamped<string> Shortcut { get; init; } = D("RightCtrl");
    public Stamped<int> DoubleTapMs { get; init; } = D(300);
    public Stamped<int> ResistancePx { get; init; } = D(120);
    public Stamped<bool> Corners { get; init; } = D(false);
    public Stamped<bool> Notch { get; init; } = D(false);
    public Stamped<bool> Glow { get; init; } = D(true);
    public Stamped<bool> Arrival { get; init; } = D(true);
    public Stamped<bool> HideIdle { get; init; } = D(true);
    public Stamped<double> PointerSpeed { get; init; } = D(1.0);
    public Stamped<double> ScrollSpeed { get; init; } = D(1.0);
    public Stamped<bool> ReverseScroll { get; init; } = D(false);
    public Stamped<Zone[]> Layout { get; init; } = D<Zone[]>([]);

    /// <summary>Per value, whichever side set it last. The peer's values are brought into range first.</summary>
    public static CrossingSettings Merge(CrossingSettings mine, CrossingSettings theirs, long? now = null) => Pick(mine, theirs.Sanitized(now));

    /// <summary>How far ahead of our clock a peer's stamp may be (a PC with a fast clock still works).</summary>
    public const long SlackMs = 24L * 60 * 60 * 1000;

    static double Speed(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.2, 4.0) : 1.0;
    static bool Span(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b <= 1 && a < b;

    /// <summary>A member from the wire: a missing one is the default, its stamp is in range, <c>By</c> is never null.</summary>
    static Stamped<T> Fix<T>(Stamped<T>? s, T fallback, Func<T, T> clamp, long latest) =>
        s == null
            ? new(fallback, 0, "")
            : new(s.Value == null ? fallback : clamp(s.Value), Math.Clamp(s.SetAt, 0, latest), s.By ?? "");

    /// <summary>
    /// The same settings with every value in range, stamps no further ahead than a day, null members replaced
    /// by defaults and broken zones dropped (a peer's message isn't trusted).
    /// </summary>
    public CrossingSettings Sanitized(long? now = null)
    {
        var latest = (now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + SlackMs;
        var d = new CrossingSettings();
        return new()
        {
            Shortcut = Fix(Shortcut, d.Shortcut.Value, v => v, latest),
            DoubleTapMs = Fix(DoubleTapMs, d.DoubleTapMs.Value, v => Math.Clamp(v, 150, 800), latest),
            ResistancePx = Fix(ResistancePx, d.ResistancePx.Value, v => Math.Clamp(v, 0, 2000), latest),
            Corners = Fix(Corners, d.Corners.Value, v => v, latest),
            Notch = Fix(Notch, d.Notch.Value, v => v, latest),
            Glow = Fix(Glow, d.Glow.Value, v => v, latest),
            Arrival = Fix(Arrival, d.Arrival.Value, v => v, latest),
            HideIdle = Fix(HideIdle, d.HideIdle.Value, v => v, latest),
            PointerSpeed = Fix(PointerSpeed, d.PointerSpeed.Value, Speed, latest),
            ScrollSpeed = Fix(ScrollSpeed, d.ScrollSpeed.Value, Speed, latest),
            ReverseScroll = Fix(ReverseScroll, d.ReverseScroll.Value, v => v, latest),
            Layout = Fix(Layout, d.Layout.Value, zones => zones.Where(z => z != null && z.Peer != null
                && Span(z.From, z.To) && Span(z.TargetFrom, z.TargetTo)
                && Enum.IsDefined(z.Side) && Enum.IsDefined(z.TargetSide)).ToArray(), latest),
        };
    }

    static CrossingSettings Pick(CrossingSettings mine, CrossingSettings theirs) => new()
    {
        Shortcut = Stamped<string>.Newer(mine.Shortcut, theirs.Shortcut),
        DoubleTapMs = Stamped<int>.Newer(mine.DoubleTapMs, theirs.DoubleTapMs),
        ResistancePx = Stamped<int>.Newer(mine.ResistancePx, theirs.ResistancePx),
        Corners = Stamped<bool>.Newer(mine.Corners, theirs.Corners),
        Notch = Stamped<bool>.Newer(mine.Notch, theirs.Notch),
        Glow = Stamped<bool>.Newer(mine.Glow, theirs.Glow),
        Arrival = Stamped<bool>.Newer(mine.Arrival, theirs.Arrival),
        HideIdle = Stamped<bool>.Newer(mine.HideIdle, theirs.HideIdle),
        PointerSpeed = Stamped<double>.Newer(mine.PointerSpeed, theirs.PointerSpeed),
        ScrollSpeed = Stamped<double>.Newer(mine.ScrollSpeed, theirs.ScrollSpeed),
        ReverseScroll = Stamped<bool>.Newer(mine.ReverseScroll, theirs.ReverseScroll),
        Layout = Stamped<Zone[]>.Newer(mine.Layout, theirs.Layout),
    };
}
