namespace LocalSendDrop;

/// <summary>One monitor of a PC: its index and where it sits on that PC's virtual desktop.</summary>
record DisplayInfo(int Index, Rectangle Bounds);

/// <summary>
/// A span (0 to 1 along it) of one edge of one of this PC's monitors, leading to a span of an
/// edge of one monitor of a paired PC. All crossing math goes through zones.
/// </summary>
record Zone(int Display, Edge Side, double From, double To, string Peer, int TargetDisplay, Edge TargetSide, double TargetFrom, double TargetTo);

/// <summary>Pure zone math: no hooks, no screens, so it can be tested.</summary>
static class KvmZones
{
    /// <summary>Raised (on the UI thread) where the pointer arrives on this PC; for the arrival effect.</summary>
    public static event Action<Point>? Arrived;
    public static void RaiseArrived(Point p) => Arrived?.Invoke(p);

    /// <summary>This PC's monitors, in the order Windows lists them.</summary>
    public static DisplayInfo[] Current() =>
        [.. Screen.AllScreens.Select((s, i) => new DisplayInfo(i, s.Bounds))];

    static bool Contains(Rectangle r, Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>The point one pixel past <paramref name="p"/> in the direction of <paramref name="side"/>.</summary>
    static Point Beyond(Edge side, Point p) => side switch
    {
        Edge.Left => new Point(p.X - 1, p.Y),
        Edge.Right => new Point(p.X + 1, p.Y),
        Edge.Top => new Point(p.X, p.Y - 1),
        _ => new Point(p.X, p.Y + 1),
    };

    /// <summary>
    /// The zone whose edge <paramref name="p"/> is on: on that monitor's edge, inside the zone's span,
    /// and not where another monitor continues past that edge.
    /// </summary>
    public static Zone? Hit(IEnumerable<Zone> zones, IReadOnlyList<DisplayInfo> displays, Point p)
    {
        foreach (var z in zones)
        {
            var d = displays.FirstOrDefault(x => x.Index == z.Display);
            if (d == null || !Contains(d.Bounds, p) || !KvmWire.AtEdge(z.Side, p, d.Bounds)) continue;
            var f = KvmWire.Along(z.Side, p, d.Bounds);
            if (f < Math.Min(z.From, z.To) || f > Math.Max(z.From, z.To)) continue;
            var next = Beyond(z.Side, p);
            if (displays.Any(x => Contains(x.Bounds, next))) continue;
            return z;
        }
        return null;
    }

    /// <summary>Where along the target span a pointer at <paramref name="fraction"/> along the source edge lands.</summary>
    public static double Target(Zone zone, double fraction)
    {
        var span = zone.To - zone.From;
        var t = span == 0 ? 0 : Math.Clamp((fraction - zone.From) / span, 0, 1);
        return Math.Clamp(zone.TargetFrom + t * (zone.TargetTo - zone.TargetFrom), 0, 1);
    }

    /// <summary>A zone's <c>TargetDisplay</c> when the other PC's monitors aren't known: it picks its own outer edge (plain Enter).</summary>
    public const int UnknownDisplay = -1;

    /// <summary>
    /// The old one-edge-per-PC pairing as zones: a full-span zone on every monitor whose edge is on the
    /// outside of the desktop on that side, leading to the matching full edge of the other PC (target
    /// monitor unknown: it uses its own outer edge).
    /// </summary>
    /// <summary>
    /// A screen that sits wholly within the other PC's edge, alone on that edge, takes the whole edge: a
    /// 1080p laptop beside a 1440p monitor crosses from anywhere on it, not just where the tiles touch.
    /// A tile hanging past an end, or sharing an edge with another, keeps its span.
    /// </summary>
    public static Zone[] Widen(IReadOnlyList<Zone> zones)
    {
        static bool Whole(double from, double to) => from <= 1e-6 && to >= 1 - 1e-6;
        return [.. zones.Select(z =>
        {
            var aloneHere = zones.Count(o => o.Display == z.Display && o.Side == z.Side) == 1;
            var aloneThere = zones.Count(o => o.TargetDisplay == z.TargetDisplay && o.TargetSide == z.TargetSide) == 1;
            if (!aloneHere || !aloneThere) return z;
            if (Whole(z.TargetFrom, z.TargetTo)) return z with { From = 0, To = 1 };
            if (Whole(z.From, z.To)) return z with { TargetFrom = 0, TargetTo = 1 };
            return z;
        })];
    }

    public static IReadOnlyList<Zone> Migrate(PairedPc pc, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return [];
        int Outer(DisplayInfo d) => pc.Edge switch
        {
            Edge.Left => -d.Bounds.Left,
            Edge.Right => d.Bounds.Right,
            Edge.Top => -d.Bounds.Top,
            _ => d.Bounds.Bottom,
        };
        var best = displays.Max(Outer);
        return [.. displays.Where(d => Outer(d) == best).Select(d =>
            new Zone(d.Index, pc.Edge, 0, 1, pc.Fingerprint, UnknownDisplay, Pairing.Opposite(pc.Edge), 0, 1))];
    }

    /// <summary>Whether <paramref name="p"/> is within <paramref name="size"/> px of a corner of <paramref name="d"/>.</summary>
    public static bool InCorner(Point p, Rectangle d, int size)
    {
        var nearX = p.X - d.Left < size || d.Right - 1 - p.X < size;
        var nearY = p.Y - d.Top < size || d.Bottom - 1 - p.Y < size;
        return nearX && nearY;
    }

    /// <summary>Whether <paramref name="p"/> is in the notch: the middle third of the top edge of <paramref name="d"/>.</summary>
    public static bool InNotch(Point p, Rectangle d, int depth) =>
        p.Y - d.Top < depth && p.X >= d.Left + d.Width / 3 && p.X < d.Right - d.Width / 3;
}
