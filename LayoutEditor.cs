namespace LocalSendDrop;

/// <summary>One monitor in the editor, on the shared plane where this PC's desktop sits at its real position.</summary>
record LayoutTile(bool Mine, int Display, Rectangle Bounds);

/// <summary>
/// Arranges the other PC's monitors next to this PC's, like Display settings. Where tiles touch, the
/// pointer crosses. Drag the other PC's tiles; they snap to edges.
/// </summary>
sealed class LayoutEditor : Drawn
{
    /// <summary>
    /// The zones (from this PC's point of view) where one of <paramref name="tiles"/> of the other PC touches
    /// one of this PC's: along the length they share, as spans of both edges.
    /// </summary>
    public static Zone[] ZonesFrom(IReadOnlyList<LayoutTile> tiles, string peer)
    {
        var zones = new List<Zone>();
        foreach (var m in tiles.Where(t => t.Mine))
            foreach (var p in tiles.Where(t => !t.Mine))
            {
                var a = m.Bounds;
                var b = p.Bounds;
                (Edge side, Edge target, bool vertical)? touch =
                    a.Right == b.Left ? (Edge.Right, Edge.Left, true) :
                    a.Left == b.Right ? (Edge.Left, Edge.Right, true) :
                    a.Top == b.Bottom ? (Edge.Top, Edge.Bottom, false) :
                    a.Bottom == b.Top ? (Edge.Bottom, Edge.Top, false) : null;
                if (touch is not { } t) continue;
                var (aStart, aLen, bStart, bLen) = t.vertical
                    ? (a.Top, a.Height, b.Top, b.Height)
                    : (a.Left, a.Width, b.Left, b.Width);
                var from = Math.Max(aStart, bStart);
                var to = Math.Min(aStart + aLen, bStart + bLen);
                if (to <= from || aLen <= 0 || bLen <= 0) continue;
                zones.Add(new Zone(m.Display, t.side, (from - aStart) / (double)aLen, (to - aStart) / (double)aLen,
                    peer, p.Display, t.target, (from - bStart) / (double)bLen, (to - bStart) / (double)bLen));
            }
        return [.. zones];
    }

    /// <summary>Where the other PC's monitors sit so that <paramref name="zone"/> holds: its target monitor touches ours.</summary>
    static Point PlaceBy(Zone zone, Rectangle mine, Rectangle anchor)
    {
        var along = zone.From * (zone.Side is Edge.Left or Edge.Right ? mine.Height : mine.Width);
        return zone.Side switch
        {
            Edge.Right => new Point(mine.Right, mine.Top + (int)(along - zone.TargetFrom * anchor.Height)),
            Edge.Left => new Point(mine.Left - anchor.Width, mine.Top + (int)(along - zone.TargetFrom * anchor.Height)),
            Edge.Top => new Point(mine.Left + (int)(along - zone.TargetFrom * anchor.Width), mine.Top - anchor.Height),
            _ => new Point(mine.Left + (int)(along - zone.TargetFrom * anchor.Width), mine.Bottom),
        };
    }

    static Rectangle Off(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    readonly List<LayoutTile> _mine = [];
    List<LayoutTile> _peer = [];
    string _peerFingerprint = "";
    string _peerName = "";
    RectangleF _world;
    double _scale = 1;
    bool _dragging;
    Point _grab;
    List<LayoutTile> _before = [];

    public LayoutEditor()
    {
        Cursor = Cursors.Default;
        KvmSync.DisplaysChanged += OnDisplaysChanged;
        KvmSync.SettingsChanged += OnSettingsChanged;
    }

    void OnDisplaysChanged(string _) => Refresh_();
    void OnSettingsChanged() => Refresh_();
    void Refresh_()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(Reload); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            KvmSync.DisplaysChanged -= OnDisplaysChanged;
            KvmSync.SettingsChanged -= OnSettingsChanged;
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Reload(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Reload(); }

    /// <summary>The layout as saved: places the other PC where the saved zones say, or to the right of this PC.</summary>
    public void Reload()
    {
        if (_dragging) return;
        _mine.Clear();
        foreach (var d in KvmZones.Current()) _mine.Add(new LayoutTile(true, d.Index, d.Bounds));
        var pc = Pairing.All.FirstOrDefault(p => KvmSync.PeerDisplays.ContainsKey(p.Fingerprint)) ?? Pairing.All.FirstOrDefault();
        _peer = [];
        _peerFingerprint = pc?.Fingerprint ?? "";
        _peerName = pc?.Alias ?? "";
        if (pc != null && KvmSync.PeerDisplays.TryGetValue(pc.Fingerprint, out var theirs) && theirs.Length > 0)
        {
            var origin = theirs.Min(d => d.Bounds.Left);
            var top = theirs.Min(d => d.Bounds.Top);
            var union = _mine.Select(t => t.Bounds).Aggregate(Rectangle.Union);
            var zone = Settings.Current.Zones.FirstOrDefault(z => z.Peer == pc.Fingerprint && _mine.Any(t => t.Display == z.Display));
            var anchorDisplay = theirs.FirstOrDefault(d => zone != null && d.Index == zone.TargetDisplay) ?? theirs[0];
            var place = zone != null
                ? PlaceBy(zone, _mine.First(t => t.Display == zone.Display).Bounds, anchorDisplay.Bounds)
                : new Point(union.Right, union.Top);
            var shift = new Size(place.X - anchorDisplay.Bounds.X, place.Y - anchorDisplay.Bounds.Y);
            _peer = [.. theirs.Select(d => new LayoutTile(false, d.Index, Off(d.Bounds, shift.Width, shift.Height)))];
        }
        // A fixed plane: this PC in the middle with room for the other on any side.
        var all = _mine.Select(t => t.Bounds).Aggregate(Rectangle.Union);
        var pw = _peer.Count > 0 ? _peer.Select(t => t.Bounds).Aggregate(Rectangle.Union) : new Rectangle(0, 0, all.Width, all.Height);
        _world = RectangleF.FromLTRB(all.Left - pw.Width, all.Top - pw.Height, all.Right + pw.Width, all.Bottom + pw.Height);
        _scale = Math.Min((Width - S(8)) / (double)Math.Max(1, _world.Width), (Height - S(8)) / (double)Math.Max(1, _world.Height));
        Invalidate();
    }

    Rectangle ToScreen(Rectangle r) => new(
        (int)((r.Left - _world.Left) * _scale) + S(4), (int)((r.Top - _world.Top) * _scale) + S(4),
        Math.Max(S(4), (int)(r.Width * _scale)), Math.Max(S(4), (int)(r.Height * _scale)));

    /// <summary>Moves the other PC's monitors together by (dx, dy) desktop pixels, snapping edges to this PC's.</summary>
    List<LayoutTile> Moved(List<LayoutTile> from, int dx, int dy)
    {
        var moved = from.Select(t => t with { Bounds = Off(t.Bounds, dx, dy) }).ToList();
        var snap = (int)(S(10) / _scale);
        int bestX = 0, bestY = 0, distX = snap + 1, distY = snap + 1;
        foreach (var p in moved)
            foreach (var m in _mine)
            {
                var a = m.Bounds;
                var b = p.Bounds;
                // Edge to edge (touching), and flush edges (aligned).
                foreach (var d in new[] { a.Right - b.Left, a.Left - b.Right, a.Left - b.Left, a.Right - b.Right })
                    if (Math.Abs(d) < distX) { distX = Math.Abs(d); bestX = d; }
                foreach (var d in new[] { a.Bottom - b.Top, a.Top - b.Bottom, a.Top - b.Top, a.Bottom - b.Bottom })
                    if (Math.Abs(d) < distY) { distY = Math.Abs(d); bestY = d; }
            }
        if (distX <= snap || distY <= snap)
        {
            var sx = distX <= snap ? bestX : 0;
            var sy = distY <= snap ? bestY : 0;
            moved = [.. moved.Select(t => t with { Bounds = Off(t.Bounds, sx, sy) })];
        }
        return moved;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _peer.Count == 0) return;
        if (!_peer.Any(t => ToScreen(t.Bounds).Contains(e.Location))) return;
        _dragging = true;
        _grab = e.Location;
        _before = _peer;
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _peer = Moved(_before, (int)((e.X - _grab.X) / _scale), (int)((e.Y - _grab.Y) / _scale));
            Invalidate();
        }
        else Cursor = _peer.Any(t => ToScreen(t.Bounds).Contains(e.Location)) ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        var zones = ZonesFrom([.. _mine, .. _peer], _peerFingerprint);
        KvmSync.Set((c, at) => c with { Layout = at.Of(zones) });
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBack(g);
        var tiles = ZonesFrom([.. _mine, .. _peer], _peerFingerprint);
        foreach (var t in _mine) DrawTile(g, t, false);
        foreach (var t in _peer) DrawTile(g, t, true);
        // Where the pointer crosses: an accent bar along the shared span of the touching edges.
        foreach (var z in tiles)
        {
            var m = _mine.First(t => t.Display == z.Display).Bounds;
            var r = ToScreen(m);
            var horizontal = z.Side is Edge.Top or Edge.Bottom;
            var len = horizontal ? r.Width : r.Height;
            var start = (int)(z.From * len);
            var span = Math.Max(S(2), (int)((z.To - z.From) * len));
            var bar = z.Side switch
            {
                Edge.Right => new Rectangle(r.Right - S(3), r.Top + start, S(4), span),
                Edge.Left => new Rectangle(r.Left - S(1), r.Top + start, S(4), span),
                Edge.Top => new Rectangle(r.Left + start, r.Top - S(1), span, S(4)),
                _ => new Rectangle(r.Left + start, r.Bottom - S(3), span, S(4)),
            };
            Ui.FillRound(g, Ui.Accent, bar, S(2));
        }
        if (_peer.Count == 0)
            Ui.DrawText(g, _peerFingerprint == "" ? "Pair another PC to arrange screens" : "Waiting for the other PC to connect",
                Ui.Caption, ClientRectangle, Ui.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawTile(Graphics g, LayoutTile t, bool peer)
    {
        var r = ToScreen(t.Bounds);
        Ui.FillRound(g, peer ? Ui.Card : Ui.ControlFill, r, S(3));
        Ui.StrokeRound(g, peer ? Ui.Accent : Ui.StrongStroke, r, S(3), peer ? 2 * Scale_ : Scale_);
        var count = (peer ? _peer : _mine).Count;
        var label = peer ? (count > 1 ? $"{_peerName} {t.Display + 1}" : _peerName) : (count > 1 ? $"This PC {t.Display + 1}" : "This PC");
        Ui.DrawText(g, label, Ui.Caption, r, peer ? Ui.Text : Ui.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
