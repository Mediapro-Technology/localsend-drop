using LocalSendDrop;
using Xunit;

[Collection("KvmSync")]
public class KvmSyncTests
{
    sealed class Rig
    {
        public CrossingSettings State = new();
        public readonly List<CrossingSettings> Saved = [];
        public readonly List<byte[]> Sent = [];
        public long Now = 1000;

        public Rig(CrossingSettings? start = null)
        {
            if (start != null) State = start;
            KvmSync.Reset();
            KvmSync.Load = () => State;
            KvmSync.Store = (c, _) => { State = c; Saved.Add(c); };
            KvmSync.Me = () => "ME";
            KvmSync.Clock = () => Now;
            KvmSync.Hello(m => { Sent.Add(m); return true; });
            Sent.Clear();   // forget the hello
        }

        public IEnumerable<CrossingSettings> SyncsSent() =>
            Sent.Where(m => m[0] == (byte)KvmMsg.Sync).Select(m => KvmWire.ReadJson<CrossingSettings>(m)!);

        public void Receive(CrossingSettings theirs) =>
            KvmSync.OnMessage("peer", KvmWire.JsonMsg(KvmMsg.Sync, theirs));
    }

    [Fact]
    public void Receive_NewerWins_Saves()
    {
        var rig = new Rig(new CrossingSettings { ResistancePx = new(50, 100, "ME") });
        rig.Receive(new CrossingSettings { ResistancePx = new(200, 200, "peer") });
        Assert.Single(rig.Saved);
        Assert.Equal(200, rig.State.ResistancePx.Value);
    }

    [Fact]
    public void Receive_Older_Ignored()
    {
        var rig = new Rig(new CrossingSettings { ResistancePx = new(50, 300, "ME") });
        rig.Receive(new CrossingSettings { ResistancePx = new(200, 200, "peer") });
        Assert.Empty(rig.Saved);
        Assert.Equal(50, rig.State.ResistancePx.Value);
        // They are behind, so they get ours.
        Assert.Equal(50, Assert.Single(rig.SyncsSent()).ResistancePx.Value);
    }

    [Fact]
    public void Echo_DoesNotLoop()
    {
        var rig = new Rig();
        var theirs = new CrossingSettings { ResistancePx = new(200, 200, "peer"), Layout = new([new Zone(0, Edge.Right, 0, 1, "p", 0, Edge.Left, 0, 1)], 200, "peer") };
        rig.Receive(theirs);
        Assert.Empty(rig.Sent);          // adopting theirs is not re-broadcast
        Assert.Single(rig.Saved);
        rig.Receive(KvmWire.ReadJson<CrossingSettings>(KvmWire.JsonMsg(KvmMsg.Sync, theirs))!);   // same again (arrays by value)
        Assert.Single(rig.Saved);        // nothing changed: no second save
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void Set_StampsAndSends()
    {
        var rig = new Rig();
        rig.Now = 5000;
        KvmSync.Set((c, at) => c with { ResistancePx = at.Of(300) });
        Assert.Equal(300, rig.State.ResistancePx.Value);
        Assert.Equal(5000, rig.State.ResistancePx.SetAt);
        Assert.Equal("ME", rig.State.ResistancePx.By);
        Assert.Equal(300, Assert.Single(rig.SyncsSent()).ResistancePx.Value);
    }

    [Fact]
    public void Set_BeatsAReceivedStampEvenWithSlowClock()
    {
        var rig = new Rig();
        rig.Now = 100;
        rig.Receive(new CrossingSettings { ResistancePx = new(200, 9000, "peer") });
        KvmSync.Set((c, at) => c with { ResistancePx = at.Of(10) });
        Assert.True(rig.State.ResistancePx.SetAt > 9000);
    }

    [Fact]
    public void Hello_ResendsAfterReconnect()
    {
        var rig = new Rig(new CrossingSettings { Glow = new(false, 10, "ME") });
        var again = new List<byte[]>();
        KvmSync.Hello(m => { again.Add(m); return true; });
        var sync = again.Single(m => m[0] == (byte)KvmMsg.Sync);
        Assert.False(KvmWire.ReadJson<CrossingSettings>(sync)!.Glow.Value);
    }

    [Fact]
    public void DeadLink_IsDropped()
    {
        var rig = new Rig();
        var calls = 0;
        KvmSync.Hello(_ => { calls++; return calls == 1; });   // hello works, then it breaks
        KvmSync.Set((c, at) => c with { Glow = at.Of(false) });
        var after = calls;
        KvmSync.Set((c, at) => c with { Glow = at.Of(true) });
        Assert.Equal(after, calls);
    }

    [Fact]
    public void LocalZones_MirrorsTheOtherPcsLayout()
    {
        var z = new Zone(0, Edge.Right, 0.25, 0.75, "x", 1, Edge.Left, 0, 0.5);
        var mine = KvmSync.LocalZones(new Stamped<Zone[]>([z], 1, "ME"), "ME", "peer");
        Assert.Equal([z with { Peer = "peer" }], mine);   // the peer is always the link's fingerprint
        var theirs = KvmSync.LocalZones(new Stamped<Zone[]>([z], 1, "THEM"), "ME", "peer");
        Assert.Equal(new Zone(1, Edge.Left, 0, 0.5, "peer", 0, Edge.Right, 0.25, 0.75), Assert.Single(theirs));
    }
}
