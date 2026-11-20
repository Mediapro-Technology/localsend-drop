using LocalSendDrop;
using Xunit;

public class ShortcutTests
{
    const ushort RCtrl = 0xA3;
    const ushort C = 0x43;

    /// <summary>Feeds (vk, up, atMs) events; true if any fired.</summary>
    static bool Run(DoubleTap d, params (ushort vk, bool up, long t)[] events)
    {
        var fired = false;
        foreach (var e in events) fired |= d.Feed(e.vk, e.up, e.t);
        return fired;
    }

    [Fact]
    public void DoubleTap_Within300_Fires() =>
        Assert.True(Run(new DoubleTap(300), (RCtrl, false, 0), (RCtrl, true, 60), (RCtrl, false, 150), (RCtrl, true, 200)));

    [Fact]
    public void SingleTap_DoesNot() =>
        Assert.False(Run(new DoubleTap(300), (RCtrl, false, 0), (RCtrl, true, 60)));

    [Fact]
    public void CtrlChord_DoesNot()
    {
        Assert.False(Run(new DoubleTap(300),
            (RCtrl, false, 0), (C, false, 20), (C, true, 40), (RCtrl, true, 60), (RCtrl, false, 100), (RCtrl, true, 140)));
        // another key between the two taps
        Assert.False(Run(new DoubleTap(300),
            (RCtrl, false, 0), (RCtrl, true, 40), (C, false, 60), (C, true, 80), (RCtrl, false, 100), (RCtrl, true, 140)));
    }

    [Fact]
    public void SlowTap_DoesNot() =>
        Assert.False(Run(new DoubleTap(300), (RCtrl, false, 0), (RCtrl, true, 60), (RCtrl, false, 500), (RCtrl, true, 560)));

    [Fact]
    public void HeldKeyRepeat_IsOneTap() =>
        Assert.False(Run(new DoubleTap(300), (RCtrl, false, 0), (RCtrl, false, 30), (RCtrl, false, 60), (RCtrl, true, 90)));

    [Fact]
    public void Scale_Half_KeepsRemainder()
    {
        var s = new PointerScaler();
        Assert.Equal((0, 0), s.Scale(1, 1, 0.5));
        Assert.Equal((1, 1), s.Scale(1, 1, 0.5));
        Assert.Equal((5, -5), s.Scale(10, -10, 0.5));
    }

    [Fact]
    public void Scale_One_IsUntouched()
    {
        var s = new PointerScaler();
        Assert.Equal((7, -3), s.Scale(7, -3, 1.0));
    }

    [Fact]
    public void Reverse_FlipsWheel()
    {
        var s = new PointerScaler();
        Assert.Equal(120, s.Wheel(120, 1.0, false));
        Assert.Equal(-120, s.Wheel(120, 1.0, true));
        Assert.Equal(-60, s.Wheel(120, 0.5, true));
    }

    [Fact]
    public void MediaKeys_AreForwarded()
    {
        foreach (ushort vk in new ushort[] { 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3 })   // mute, vol down/up, next, prev, stop, play/pause
        {
            var m = KvmWire.Key(new KeyEvent(vk, 0, false, true, false));
            Assert.Equal(vk, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(1)));
            Assert.Equal(2, m[5] & 2);   // extended flag kept
        }
    }
}
