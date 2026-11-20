using LocalSendDrop;
using Xunit;

public class ReturnPushTests
{
    [Fact]
    public void Grace_OutwardOnlyHolds()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        Assert.False(r.Step(outward: 500, inward: 0, moved: 0, now: 100, needed: 120));
    }

    [Fact]
    public void AfterGrace_EnoughPushGoesBack()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        Assert.False(r.Step(60, 0, 0, 400, 120));
        Assert.True(r.Step(60, 0, 0, 400, 120));
    }

    [Fact]
    public void MovingAway_EndsGraceEarly()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        Assert.False(r.Step(0, 5, 5, 50, 120));
        Assert.True(r.Step(130, 0, 5, 50, 120));
    }

    [Fact]
    public void Push_LeaksAway()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        Assert.False(r.Step(100, 0, 0, 400, 120));
        // 0.5 s later 150 px has leaked away: 30 more isn't enough.
        Assert.False(r.Step(30, 0, 0, 900, 120));
    }

    [Fact]
    public void Inward_TakesPushOff()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        Assert.False(r.Step(100, 0, 0, 400, 120));
        Assert.False(r.Step(0, 50, 0, 400, 120));
        Assert.False(r.Step(30, 0, 0, 400, 120));
    }

    [Fact]
    public void Wobble_NeverGoesBack()
    {
        var r = new ReturnPush();
        r.Arrive(0);
        for (var t = 0; t < 3000; t += 8)
            Assert.False(r.Step(t % 16 == 0 ? 2 : 0, t % 16 == 0 ? 0 : 2, 10, t, 120));
    }
}
