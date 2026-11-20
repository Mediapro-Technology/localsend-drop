using LocalSendDrop;
using Xunit;

public class WidenTests
{
    static Zone Z(int display, Edge side, double from, double to, double tFrom, double tTo, int target = 0) =>
        new(display, side, from, to, "peer", target, side == Edge.Left ? Edge.Right : Edge.Left, tFrom, tTo);

    [Fact]
    public void SmallerScreenInside_GetsWholeEdge()
    {
        var z = Assert.Single(KvmZones.Widen([Z(0, Edge.Left, 0, 0.5, 0, 1)]));
        Assert.Equal((0.0, 1.0, 0.0, 1.0), (z.From, z.To, z.TargetFrom, z.TargetTo));
    }

    [Fact]
    public void OursInsideTheirs_TargetGetsWholeEdge()
    {
        var z = Assert.Single(KvmZones.Widen([Z(0, Edge.Left, 0, 1, 0.2, 0.7)]));
        Assert.Equal((0.0, 1.0, 0.0, 1.0), (z.From, z.To, z.TargetFrom, z.TargetTo));
    }

    [Fact]
    public void Overhang_KeepsPartialSpan()
    {
        var z = Assert.Single(KvmZones.Widen([Z(0, Edge.Left, 0.5, 1, 0, 0.5)]));
        Assert.Equal((0.5, 1.0, 0.0, 0.5), (z.From, z.To, z.TargetFrom, z.TargetTo));
    }

    [Fact]
    public void TwoOnOneEdge_KeepSpans()
    {
        Zone[] zones = [Z(0, Edge.Left, 0, 0.4, 0, 1, target: 0), Z(0, Edge.Left, 0.5, 0.9, 0, 1, target: 1)];
        Assert.Equal(zones, KvmZones.Widen(zones));
    }

    [Fact]
    public void ZonesFor_WidensSavedLayout()
    {
        var pc = new PairedPc { Fingerprint = "peer", Edge = Edge.Left };
        var z = Assert.Single(Kvm.ZonesFor(pc, [Z(0, Edge.Left, 0, 0.5, 0, 1)], []));
        Assert.Equal((0.0, 1.0), (z.From, z.To));
    }
}
