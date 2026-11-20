using System.Net;
using LocalSendDrop;
using Xunit;

[Collection("KvmSync")]
public class ReviewFixTests
{
    static Zone Z(double from, double to) => new(0, Edge.Left, from, to, "peer", 0, Edge.Right, 0, 1);

    [Fact]
    public void Merge_ClampsPeerValues()
    {
        var theirs = new CrossingSettings
        {
            PointerSpeed = new(0, 10, "peer"),
            ScrollSpeed = new(double.NaN, 10, "peer"),
            DoubleTapMs = new(5, 10, "peer"),
            ResistancePx = new(1_000_000, 10, "peer"),
        };
        var merged = CrossingSettings.Merge(new CrossingSettings(), theirs);
        Assert.Equal(0.2, merged.PointerSpeed.Value);
        Assert.Equal(1.0, merged.ScrollSpeed.Value);
        Assert.Equal(150, merged.DoubleTapMs.Value);
        Assert.Equal(2000, merged.ResistancePx.Value);
    }

    [Fact]
    public void Merge_DropsBadZones()
    {
        var theirs = new CrossingSettings { Layout = new([Z(0, 1), Z(-0.5, 0.5), Z(0.6, 0.4), Z(0, double.NaN)], 10, "peer") };
        var merged = CrossingSettings.Merge(new CrossingSettings(), theirs);
        Assert.Single(merged.Layout.Value);
        Assert.Equal(Z(0, 1), merged.Layout.Value[0]);
    }

    [Fact]
    public void Hello_SetDuringHello_ReachesNewLink()
    {
        var state = new CrossingSettings();
        KvmSync.Reset();
        KvmSync.Load = () => state;
        KvmSync.Store = (c, _) => state = c;
        KvmSync.Me = () => "ME";
        KvmSync.Clock = () => 1000;
        var sent = new List<byte[]>();
        var first = true;
        KvmSync.Hello(m =>
        {
            sent.Add(m);
            if (first) { first = false; KvmSync.Set((c, s) => c with { ResistancePx = s.Of(77) }); }
            return true;
        });
        Assert.Contains(sent, m => m[0] == (byte)KvmMsg.Sync && KvmWire.ReadJson<CrossingSettings>(m)!.ResistancePx.Value == 77);
    }

    [Fact]
    public void DirectedBroadcast_Slash24()
    {
        var b = WakeOnLan.DirectedBroadcast(IPAddress.Parse("192.168.0.107"), IPAddress.Parse("255.255.255.0"));
        Assert.Equal(IPAddress.Parse("192.168.0.255"), b);
    }

    [Theory]
    [InlineData("3c:21:9c:1d:63:c5")]
    [InlineData("3C-21-9C-1D-63-C5")]
    [InlineData("3c219c1d63c5")]
    public void Parse_Formats(string mac) =>
        Assert.Equal(new byte[] { 0x3c, 0x21, 0x9c, 0x1d, 0x63, 0xc5 }, WakeOnLan.Parse(mac));

    [Fact]
    public void Parse_Bad_IsNull() => Assert.Null(WakeOnLan.Parse("not a mac"));

    [Fact]
    public void Zones_JsonRoundTrip()
    {
        Zone[] zones = [new(1, Edge.Top, 0.25, 0.75, "fp", 0, Edge.Bottom, 0, 1)];
        var back = KvmWire.ReadJson<Zone[]>(KvmWire.JsonMsg(KvmMsg.Displays, zones));
        Assert.Equal(zones, back);
    }
}
