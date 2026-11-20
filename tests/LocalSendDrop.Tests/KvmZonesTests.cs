using System.Drawing;
using LocalSendDrop;
using Xunit;

public class KvmZonesTests
{
    static readonly DisplayInfo[] Two =
    [
        new(0, new Rectangle(0, 0, 1920, 1080)),
        new(1, new Rectangle(1920, 0, 1920, 1080)),
    ];

    [Fact]
    public void Hit_PartialSpan()
    {
        // Lower half of display 1's right edge.
        var zone = new Zone(1, Edge.Right, 0.5, 1.0, "pc", 0, Edge.Left, 0, 1);
        Assert.Equal(zone, KvmZones.Hit([zone], Two, new Point(3839, 900)));
        Assert.Null(KvmZones.Hit([zone], Two, new Point(3839, 100)));   // upper half: not in the span
        Assert.Null(KvmZones.Hit([zone], Two, new Point(3800, 900)));   // not on the edge
    }

    [Fact]
    public void Hit_InnerEdgeBetweenMonitors_IsNull()
    {
        var zone = new Zone(0, Edge.Right, 0, 1, "pc", 0, Edge.Left, 0, 1);
        Assert.Null(KvmZones.Hit([zone], Two, new Point(1919, 500)));   // display 1 continues here
    }

    [Fact]
    public void Target_MapsSpans()
    {
        var zone = new Zone(0, Edge.Right, 0.5, 1.0, "pc", 0, Edge.Left, 0.0, 0.5);
        Assert.Equal(0.0, KvmZones.Target(zone, 0.5), 3);
        Assert.Equal(0.25, KvmZones.Target(zone, 0.75), 3);
        Assert.Equal(0.5, KvmZones.Target(zone, 1.0), 3);
        Assert.Equal(0.5, KvmZones.Target(zone, 5.0), 3);   // clamped
    }

    [Fact]
    public void Migrate_RightEdge_TwoMonitors()
    {
        var pc = new PairedPc { Fingerprint = "FP", Alias = "Dev", Edge = Edge.Right };
        var zones = KvmZones.Migrate(pc, Two);
        var z = Assert.Single(zones);
        Assert.Equal(1, z.Display);
        Assert.Equal(Edge.Right, z.Side);
        Assert.Equal(0, z.From);
        Assert.Equal(1, z.To);
        Assert.Equal("FP", z.Peer);
        Assert.Equal(Edge.Left, z.TargetSide);
        Assert.Equal(0, z.TargetFrom);
        Assert.Equal(1, z.TargetTo);
    }

    [Fact]
    public void InCorner_Works()
    {
        var d = Two[0].Bounds;
        Assert.True(KvmZones.InCorner(new Point(2, 2), d, 10));
        Assert.True(KvmZones.InCorner(new Point(1919, 1079), d, 10));
        Assert.False(KvmZones.InCorner(new Point(1919, 500), d, 10));
    }
}
