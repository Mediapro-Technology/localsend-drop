using System.Drawing;
using LocalSendDrop;
using Xunit;

public class LayoutEditorTests
{
    // This PC: one 1920x1080 monitor at the origin. The other PC's tile is placed on the same plane.
    static readonly LayoutTile Mine = new(true, 0, new Rectangle(0, 0, 1920, 1080));

    [Fact]
    public void SideBySide_FullSpan()
    {
        var peer = new LayoutTile(false, 0, new Rectangle(1920, 0, 1920, 1080));
        var z = Assert.Single(LayoutEditor.ZonesFrom([Mine, peer], "pc"));
        Assert.Equal(new Zone(0, Edge.Right, 0, 1, "pc", 0, Edge.Left, 0, 1), z);
    }

    [Fact]
    public void ShorterInside_WholeEdge()
    {
        // Peer is half as tall and sits wholly within our right edge: all of our edge leads there (T-010).
        var peer = new LayoutTile(false, 0, new Rectangle(1920, 540, 1920, 540));
        var saved = Assert.Single(LayoutEditor.ZonesFrom([Mine, peer], "pc"));
        Assert.Equal(0.5, saved.From, 3);   // saved as placed, so the tile reloads there
        var z = Assert.Single(KvmZones.Widen([saved]));
        Assert.Equal(Edge.Right, z.Side);
        Assert.Equal((0.0, 1.0, 0.0, 1.0), (z.From, z.To, z.TargetFrom, z.TargetTo));
    }

    [Fact]
    public void Overhang_PartialSpan()
    {
        // Peer hangs past the bottom: only the lower half of our right edge leads to its top half.
        var peer = new LayoutTile(false, 0, new Rectangle(1920, 540, 1920, 1080));
        var z = Assert.Single(LayoutEditor.ZonesFrom([Mine, peer], "pc"));
        Assert.Equal(0.5, z.From, 3);
        Assert.Equal(1.0, z.To, 3);
        Assert.Equal(0.0, z.TargetFrom, 3);
        Assert.Equal(0.5, z.TargetTo, 3);
    }

    [Fact]
    public void Apart_NoZone()
    {
        var gap = new LayoutTile(false, 0, new Rectangle(2500, 0, 1920, 1080));
        var below = new LayoutTile(false, 0, new Rectangle(0, 1200, 1920, 1080));
        Assert.Empty(LayoutEditor.ZonesFrom([Mine, gap], "pc"));
        Assert.Empty(LayoutEditor.ZonesFrom([Mine, below], "pc"));
    }

    [Fact]
    public void Above_TopEdge()
    {
        var peer = new LayoutTile(false, 2, new Rectangle(0, -1080, 1920, 1080));
        var z = Assert.Single(LayoutEditor.ZonesFrom([Mine, peer], "pc"));
        Assert.Equal(new Zone(0, Edge.Top, 0, 1, "pc", 2, Edge.Bottom, 0, 1), z);
    }
}
