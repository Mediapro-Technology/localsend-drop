using System.Drawing;
using LocalSendDrop;
using Xunit;

/// <summary>Fixes from the review of the Beamer work (T-008). Named after the card's steps.</summary>
[Collection("KvmSync")]
public class T008Tests
{
    static readonly DisplayInfo[] Two = [new(0, new Rectangle(0, 0, 1920, 1080)), new(1, new Rectangle(1920, 0, 1920, 1080))];

    sealed class Rig
    {
        public CrossingSettings State = new();
        public readonly List<byte[]> Sent = [];
        public long Now = 1000;

        public Rig(CrossingSettings? start = null)
        {
            if (start != null) State = start;
            KvmSync.Reset();
            KvmSync.Load = () => State;
            KvmSync.Store = (c, _) => State = c;
            KvmSync.Me = () => "ME";
            KvmSync.Clock = () => Now;
        }
    }

    // ---- 1: local changes apply now ----

    [Fact]
    public void Item1_Set_RaisesLocalChanged()
    {
        _ = new Rig();
        var raised = 0;
        void On() => raised++;
        KvmSync.LocalChanged += On;
        try
        {
            KvmSync.Set((c, at) => c with { Glow = at.Of(false) });
            Assert.Equal(1, raised);
            KvmSync.Set((c, at) => c);   // nothing changed
            Assert.Equal(1, raised);
        }
        finally { KvmSync.LocalChanged -= On; }
    }

    // ---- 2: edge change / unpair drop that peer's zones ----

    [Fact]
    public void Item2_EdgeChange_ZoneFollows()
    {
        var s = new Settings
        {
            Paired = [new PairedPc { Fingerprint = "A", Edge = Edge.Left }],
            Zones = [new Zone(0, Edge.Left, 0, 1, "A", 0, Edge.Right, 0, 1), new Zone(0, Edge.Top, 0, 1, "B", 0, Edge.Bottom, 0, 1)],
        };
        Pairing.DropZonesForSave(s, "A", Edge.Right);
        Assert.DoesNotContain(s.Zones, z => z.Peer == "A");
        Assert.Contains(s.Zones, z => z.Peer == "B");   // other PCs keep theirs
        var now = Kvm.ZonesFor(new PairedPc { Fingerprint = "A", Edge = Edge.Right }, s.Zones, Two);
        Assert.All(now, z => Assert.Equal(Edge.Right, z.Side));
        Assert.Equal(1, Assert.Single(now).Display);   // the outer display on the right
    }

    [Fact]
    public void Item2_SameEdge_KeepsLayout()
    {
        var s = new Settings
        {
            Paired = [new PairedPc { Fingerprint = "A", Edge = Edge.Left }],
            Zones = [new Zone(0, Edge.Left, 0.2, 0.6, "A", 0, Edge.Right, 0, 1)],
        };
        Pairing.DropZonesForSave(s, "A", Edge.Left);
        Assert.Single(s.Zones);
    }

    [Fact]
    public void Item2_PairingOverAnotherOnTheSameEdge_DropsTheReplacedOnesZones()
    {
        var s = new Settings
        {
            Paired = [new PairedPc { Fingerprint = "OLD", Edge = Edge.Left }],
            Zones = [new Zone(0, Edge.Left, 0, 1, "OLD", 0, Edge.Right, 0, 1)],
        };
        Pairing.DropZonesForSave(s, "NEW", Edge.Left);
        Assert.Empty(s.Zones);
    }

    [Fact]
    public void Item2_Unpair_DropsZones()
    {
        var s = new Settings { Zones = [new Zone(0, Edge.Left, 0, 1, "A", 0, Edge.Right, 0, 1)] };
        Pairing.ForgetZones(s, "A");
        Assert.Empty(s.Zones);
    }

    // ---- 3: Beamer pause on the controlled side ----

    [Fact]
    public void Item3_Paused_RefusesEnterBeforeLookingAtTheDesktop()
    {
        var asked = false;
        Assert.Equal(KvmMsg.Paused, KvmRemote.EnterRefusal(true, () => { asked = true; return false; }));
        Assert.False(asked);
        Assert.Equal(KvmMsg.Locked, KvmRemote.EnterRefusal(false, () => true));
        Assert.Null(KvmRemote.EnterRefusal(false, () => false));
    }

    // ---- 4: migrated zones send a plain Enter ----

    [Fact]
    public void Item4_MigratedZone_SendsPlainEnter()
    {
        var pc = new PairedPc { Fingerprint = "FP", Edge = Edge.Right };
        var zone = Assert.Single(KvmZones.Migrate(pc, Two));
        Assert.True(zone.TargetDisplay < 0);
        var m = Kvm.EnterMessage(zone, 0.5, peerKnown: true);
        Assert.Equal((byte)KvmMsg.Enter, m[0]);
    }

    [Fact]
    public void Item4_LayoutZone_SendsEnterAt()
    {
        var zone = new Zone(0, Edge.Right, 0, 1, "FP", 1, Edge.Left, 0, 1);
        Assert.Equal((byte)KvmMsg.EnterAt, Kvm.EnterMessage(zone, 0.5, peerKnown: true)[0]);
        Assert.Equal((byte)KvmMsg.Enter, Kvm.EnterMessage(zone, 0.5, peerKnown: false)[0]);
    }

    // ---- 5: Leave carries display and side ----

    [Fact]
    public void Item5_Leave_RoundTripsAndOldFormStillReads()
    {
        var m = KvmWire.LeaveAt(1, Edge.Left, 0.25);
        Assert.Equal((byte)KvmMsg.Leave, m[0]);
        var read = KvmWire.ReadLeave(m);
        Assert.Equal(0.25, read.fraction, 3);
        Assert.Equal(1, read.display);
        Assert.Equal(Edge.Left, read.side);
        Assert.Equal(0.25, KvmWire.ReadFraction(m), 3);   // an older controller still reads the fraction

        var old = KvmWire.Fraction(KvmMsg.Leave, 0.75);
        var oldRead = KvmWire.ReadLeave(old);
        Assert.Equal(0.75, oldRead.fraction, 3);
        Assert.Null(oldRead.display);
        Assert.Null(oldRead.side);
    }

    [Fact]
    public void Item5_LeaveViaSecondZone_ReturnsBesideIt()
    {
        var first = new Zone(0, Edge.Right, 0, 0.5, "FP", 0, Edge.Left, 0, 1);
        var second = new Zone(0, Edge.Right, 0.5, 1, "FP", 1, Edge.Left, 0, 1);
        var picked = Kvm.ZoneForLeave([first, second], first, 1, Edge.Left, 0.5);
        Assert.Equal(second, picked);
        Assert.Equal(first, Kvm.ZoneForLeave([first, second], first, 0, Edge.Left, 0.5));
        // Old form, or nothing matches: the zone it crossed through.
        Assert.Equal(first, Kvm.ZoneForLeave([first, second], first, null, null, 0.5));
        Assert.Equal(second, Kvm.ZoneForLeave([first, second], second, 7, Edge.Top, 0.5));
        // Another PC's zones are never used.
        var other = second with { Peer = "OTHER" };
        Assert.Equal(first, Kvm.ZoneForLeave([other], first, 1, Edge.Left, 0.5));
    }

    // ---- 6: a stale last point can't skip the resistance ----

    [Fact]
    public void Item6_StaleLastPoint_DoesNotSkipResistance()
    {
        var delta = InputEngine.StepDelta(new Point(40, 500), new Point(1919, 500), null);
        var settings = new CrossingSettings();
        Assert.True(CrossingRule.Outward(Edge.Right, delta) < settings.ResistancePx.Value);
    }

    [Fact]
    public void Item6_PinnedAndStalled_CountsOnePixel()
    {
        Assert.Equal(new Point(1, 0), InputEngine.StepDelta(new Point(1919, 500), new Point(1919, 500), Edge.Right));
        Assert.Equal(new Point(3, -2), InputEngine.StepDelta(new Point(10, 10), new Point(13, 8), null));
    }

    // ---- 7: link registration ----

    [Fact]
    public void Item7_Reconnect50Times_OneEntry()
    {
        _ = new Rig();
        IDisposable? last = null;
        for (var i = 0; i < 50; i++)
        {
            last?.Dispose();
            last = KvmSync.Hello(_ => true);
        }
        Assert.Equal(1, KvmSync.LinkCount);
        last!.Dispose();
        last.Dispose();   // twice is fine
        Assert.Equal(0, KvmSync.LinkCount);
    }

    [Fact]
    public void Item7_ResendDisplays_PrunesFailures()
    {
        _ = new Rig();
        var calls = 0;
        KvmSync.Hello(_ => { calls++; return calls == 1 || calls == 2; });   // two hello messages work, then it breaks
        Assert.Equal(1, KvmSync.LinkCount);
        KvmSync.ResendDisplays();
        Assert.Equal(0, KvmSync.LinkCount);
    }

    // ---- 8: shutdown shows the pointer ----

    [Fact]
    public void Item8_ShowAll_ClearsEveryReason()
    {
        var blanked = 0; var restored = 0;
        var (oldBlank, oldRestore) = (HiddenCursor.Blank, HiddenCursor.Restorer);
        HiddenCursor.Blank = () => blanked++;
        HiddenCursor.Restorer = () => restored++;
        try
        {
            HiddenCursor.ShowAll();   // start clean
            (blanked, restored) = (0, 0);
            HiddenCursor.Hide(HiddenCursor.Crossing);
            HiddenCursor.Hide(HiddenCursor.Idle);
            Assert.Equal(1, blanked);
            HiddenCursor.Show(HiddenCursor.Crossing);
            Assert.True(HiddenCursor.IsHidden);   // idle still holds it
            HiddenCursor.ShowAll();
            Assert.False(HiddenCursor.IsHidden);
            Assert.Equal(1, restored);
            HiddenCursor.Show(HiddenCursor.Idle);   // nothing left to do
            Assert.Equal(1, restored);
        }
        finally { (HiddenCursor.Blank, HiddenCursor.Restorer) = (oldBlank, oldRestore); }
    }

    // ---- 9: look uses Kvm's zones; idle clock forgets ----

    [Fact]
    public void Item9_ZonesFor_FallsBackToMigrate()
    {
        var pc = new PairedPc { Fingerprint = "A", Edge = Edge.Left };
        var zones = Kvm.ZonesFor(pc, [], Two);
        Assert.Equal(0, Assert.Single(zones).Display);
        var saved = new Zone(1, Edge.Top, 0, 1, "A", 0, Edge.Bottom, 0, 1);
        Assert.Equal(saved, Assert.Single(Kvm.ZonesFor(pc, [saved], Two)));
    }

    [Fact]
    public void Item9_IdleClockReset_StartsOver()
    {
        var clock = new IdleClock();
        var p = new Point(5, 5);
        clock.Tick(p, false, 0);
        Assert.Equal(IdleAction.Hide, clock.Tick(p, false, IdleClock.StillMs + 10));
        Assert.True(clock.Hidden);
        clock.Reset();
        Assert.False(clock.Hidden);
        Assert.Equal(IdleAction.None, clock.Tick(p, false, IdleClock.StillMs + 20));   // counted from now
    }

    // ---- 10: a peer's message is not trusted ----

    [Fact]
    public void Item10_SetAtFromPeer_IsClamped_AndLocalChangeStillWins()
    {
        var rig = new Rig();
        rig.Now = 5_000;
        KvmSync.OnMessage("peer", KvmWire.JsonMsg(KvmMsg.Sync, new CrossingSettings { Glow = new(false, long.MaxValue, "peer") }));
        Assert.True(rig.State.Glow.SetAt <= rig.Now + 86_400_000L);
        KvmSync.Set((c, at) => c with { Glow = at.Of(true) });
        Assert.True(rig.State.Glow.Value);
        Assert.Equal("ME", rig.State.Glow.By);
    }

    [Fact]
    public void Item10_NullMembers_AreDefaults_AndNeverThrow()
    {
        var rig = new Rig();
        var json = System.Text.Encoding.UTF8.GetBytes("{\"Shortcut\":null,\"Layout\":null,\"Glow\":{\"Value\":false,\"SetAt\":5,\"By\":null}}");
        var m = KvmWire.WithPayload(KvmMsg.Sync, json);
        var ex = Record.Exception(() => KvmSync.OnMessage("peer", m));
        Assert.Null(ex);
        Assert.Equal("RightCtrl", rig.State.Shortcut.Value);
        Assert.False(rig.State.Glow.Value);
    }

    [Fact]
    public void Item10_BadDisplays_NeverThrow()
    {
        _ = new Rig();
        var m = KvmWire.WithPayload(KvmMsg.Displays, System.Text.Encoding.UTF8.GetBytes("[null,{\"Index\":0,\"Bounds\":\"x\"}]"));
        Assert.Null(Record.Exception(() => KvmSync.OnMessage("peer", m)));
        var n = KvmWire.WithPayload(KvmMsg.Displays, System.Text.Encoding.UTF8.GetBytes("[null]"));
        Assert.Null(Record.Exception(() => KvmSync.OnMessage("peer", n)));
        Assert.All(KvmSync.PeerDisplays.GetValueOrDefault("peer") ?? [], d => Assert.NotNull(d));
    }

    [Fact]
    public void Item10_LocalZones_UsesTheSenderNotTheWire()
    {
        var forged = new Zone(0, Edge.Right, 0, 1, "SOMEONE_ELSE", 1, Edge.Left, 0, 1);
        // The peer claims it was set by us.
        var stored = KvmSync.LocalZones(new Stamped<Zone[]>([forged], 1, "ME"), "ME", "peer");
        Assert.Equal("peer", Assert.Single(stored).Peer);
        var turned = KvmSync.LocalZones(new Stamped<Zone[]>([forged], 1, "peer"), "ME", "peer");
        Assert.Equal("peer", Assert.Single(turned).Peer);
    }

    [Fact]
    public void Item10_PeerLayoutClaimingToBeOurs_IsTreatedAsTheirs()
    {
        var mineZone = new Zone(0, Edge.Right, 0, 1, "peer", 0, Edge.Left, 0, 1);
        var forged = new Zone(1, Edge.Top, 0, 1, "X", 0, Edge.Bottom, 0, 1);
        var rig = new Rig(new CrossingSettings { Layout = new([mineZone], 10, "ME") });
        string? storedFor = null;
        KvmSync.Store = (c, peer) => { rig.State = c; storedFor = peer; };
        KvmSync.OnMessage("peer", KvmWire.JsonMsg(KvmMsg.Sync, new CrossingSettings { Layout = new([forged], 50, "ME") }));
        Assert.Equal("peer", storedFor);
        Assert.Equal("peer", rig.State.Layout.By);
    }
}
