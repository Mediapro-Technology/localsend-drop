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

    /// <summary>Per value, whichever side set it last.</summary>
    public static CrossingSettings Merge(CrossingSettings mine, CrossingSettings theirs) => new()
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
