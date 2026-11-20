namespace LocalSendDrop;

/// <summary>The zones the pointer can cross over (their PC is connected) or wake (it is not).</summary>
record ZoneSet(IReadOnlyList<Zone> Live, IReadOnlyList<Zone> Offline);

/// <summary>What the edge check remembers between pointer moves.</summary>
record CrossState
{
    /// <summary>The zone the pointer is pushing against, and how far it has pushed past the edge so far.</summary>
    public Zone? Pinned { get; init; }
    public int Overshoot { get; init; }
    /// <summary>Mouse buttons held right now.</summary>
    public int Buttons { get; init; }
    /// <summary>When (ms) each offline peer was last sent a wake packet.</summary>
    public IReadOnlyDictionary<string, long> LastWake { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// The edge decision, pure so it can be tested: given where the pointer is and how it just moved,
/// should it cross over now, or wake an offline PC? All crossing math goes through zones.
/// </summary>
static class CrossingRule
{
    public const int WakeEveryMs = 30_000;

    static Zone? Find(IReadOnlyList<Zone> zones, IReadOnlyList<DisplayInfo> displays, Point p, CrossingSettings settings)
    {
        var hit = KvmZones.Hit(zones, displays, p);
        if (hit == null && settings.Notch.Value)
        {
            // The notch: the middle of a top edge leads across even outside the zone's span.
            var widened = zones.Where(z => z.Side == Edge.Top).Select(z => z with { From = 0, To = 1 }).ToList();
            var w = KvmZones.Hit(widened, displays, p);
            var d = w == null ? null : displays.FirstOrDefault(x => x.Index == w.Display);
            if (w != null && d != null && KvmZones.InNotch(p, d.Bounds, 1))
                return zones.First(z => z.Display == w.Display && z.Side == Edge.Top);
        }
        if (hit != null && !settings.Corners.Value)
        {
            var d = displays.First(x => x.Index == hit.Display).Bounds;
            if (KvmZones.InCorner(p, d, Math.Max(30, Math.Min(d.Width, d.Height) * 3 / 100))) return null;
        }
        return hit;
    }

    /// <summary>How far a move goes outward through <paramref name="side"/>.</summary>
    public static int Outward(Edge side, Point delta) => Math.Max(0, side switch
    {
        Edge.Left => -delta.X,
        Edge.Right => delta.X,
        Edge.Top => -delta.Y,
        _ => delta.Y,
    });

    /// <summary>
    /// One pointer move. <paramref name="delta"/> is how far the pointer tried to move; while it is
    /// pinned on a zone's edge, the outward part adds up until it passes the resistance.
    /// </summary>
    public static (CrossState state, Zone? cross, Zone? wake) Step(
        CrossState state, Point point, Point delta, ZoneSet zones, IReadOnlyList<DisplayInfo> displays,
        CrossingSettings settings, long nowMs)
    {
        var reset = state with { Pinned = null, Overshoot = 0 };
        if (state.Buttons > 0) return (reset, null, null);

        var live = true;
        var zone = Find(zones.Live, displays, point, settings);
        if (zone == null) { live = false; zone = Find(zones.Offline, displays, point, settings); }
        if (zone == null) return (reset, null, null);

        var overshoot = (state.Pinned == zone ? state.Overshoot : 0) + Outward(zone.Side, delta);
        var resistance = settings.ResistancePx.Value;
        if (resistance > 0 && overshoot < resistance)
            return (state with { Pinned = zone, Overshoot = overshoot }, null, null);

        if (live) return (reset, zone, null);

        if (state.LastWake.TryGetValue(zone.Peer, out var last) && nowMs - last < WakeEveryMs)
            return (state with { Pinned = zone, Overshoot = 0 }, null, null);
        var wakes = new Dictionary<string, long>(state.LastWake) { [zone.Peer] = nowMs };
        return (state with { Pinned = zone, Overshoot = 0, LastWake = wakes }, null, zone);
    }

    /// <summary>Where along the source span a pointer at <paramref name="fraction"/> along the target edge comes back.</summary>
    public static double Return(Zone zone, double fraction)
    {
        var span = zone.TargetTo - zone.TargetFrom;
        var t = span == 0 ? 0 : Math.Clamp((fraction - zone.TargetFrom) / span, 0, 1);
        return Math.Clamp(zone.From + t * (zone.To - zone.From), 0, 1);
    }
}
