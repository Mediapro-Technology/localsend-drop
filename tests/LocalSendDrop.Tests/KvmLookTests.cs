using System.Drawing;
using LocalSendDrop;
using Xunit;

public class KvmLookTests
{
    static readonly Point P = new(100, 100);

    [Fact]
    public void Still3s_Hides()
    {
        var c = new IdleClock();
        Assert.Equal(IdleAction.None, c.Tick(P, false, 0));
        Assert.Equal(IdleAction.None, c.Tick(P, false, 2900));
        Assert.Equal(IdleAction.Hide, c.Tick(P, false, 3000));
        Assert.Equal(IdleAction.None, c.Tick(P, false, 3100));
    }

    [Fact]
    public void Move_Shows()
    {
        var c = new IdleClock();
        c.Tick(P, false, 0);
        c.Tick(P, false, 3000);
        Assert.Equal(IdleAction.Show, c.Tick(new Point(101, 100), false, 3030));
        Assert.Equal(IdleAction.None, c.Tick(new Point(101, 100), false, 3060));
    }

    [Fact]
    public void Crossed_NeverShows()
    {
        var c = new IdleClock();
        c.Tick(P, false, 0);
        c.Tick(P, false, 3000);
        Assert.Equal(IdleAction.None, c.Tick(new Point(300, 300), true, 3030));
        Assert.Equal(IdleAction.None, c.Tick(new Point(400, 300), true, 3060));
        Assert.Equal(IdleAction.None, c.Tick(new Point(400, 300), true, 9000));   // and never hides on its own either
    }

    [Fact]
    public void NoLink_DoesNotHide()
    {
        var c = new IdleClock();
        c.Tick(P, false, 0, live: false);
        Assert.Equal(IdleAction.None, c.Tick(P, false, 5000, live: false));
    }

    [Fact]
    public void Proximity_OnlyNearLiveSpan()
    {
        var d = new Rectangle(0, 0, 1920, 1080);
        var z = new Zone(0, Edge.Right, 0.5, 1, "pc", 0, Edge.Left, 0, 0.5);
        Assert.Equal(1.0, KvmLook.Proximity(z, d, new Point(1919, 800), 40), 3);
        Assert.Equal(0.5, KvmLook.Proximity(z, d, new Point(1899, 800), 40), 3);
        Assert.Equal(0.0, KvmLook.Proximity(z, d, new Point(1919, 200), 40));    // outside the span
        Assert.Equal(0.0, KvmLook.Proximity(z, d, new Point(1000, 800), 40));    // far
    }

    [Fact]
    public void SpanRect_FollowsTheSpan()
    {
        var d = new Rectangle(0, 0, 1920, 1080);
        var z = new Zone(0, Edge.Right, 0.5, 1, "pc", 0, Edge.Left, 0, 0.5);
        var r = KvmLook.SpanRect(z, d, 10);
        Assert.Equal(1910, r.Left);
        Assert.Equal(10, r.Width);
        Assert.InRange(r.Top, 539, 540);
        Assert.InRange(r.Bottom, 1079, 1080);
    }
}
