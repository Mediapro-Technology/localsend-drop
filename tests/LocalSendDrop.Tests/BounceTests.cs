using System.Drawing;
using LocalSendDrop;
using Xunit;

/// <summary>A crossing must not come straight back (2026-11-21: shortcut crossings returned within 4 ms).</summary>
public class BounceTests
{
    [Fact]
    public void ShortcutCrossing_MeasuresFromTheRealPointer()
    {
        // The shortcut crosses "at" a made-up edge point; the pointer is really mid-screen.
        var baseline = CrossingRule.Baseline(at: new Point(1, 1080), fromHook: false, cursor: new Point(1500, 700));
        Assert.Equal(new Point(1500, 700), baseline);
    }

    [Fact]
    public void EdgeCrossing_MeasuresFromTheEventPoint() =>
        Assert.Equal(new Point(0, 700), CrossingRule.Baseline(new Point(0, 700), fromHook: true, cursor: new Point(3, 700)));

    [Fact]
    public void OneHugeMove_DoesNotGoBack()
    {
        var push = new ReturnPush();
        push.Arrive(0);
        // Settled (moved enough), then one jump of 1500 px outward in a single move.
        Assert.False(push.Step(ReturnPush.Clip(1500), 0, 10, 400, 120));
    }

    [Fact]
    public void SteadyPush_StillGoesBack()
    {
        var push = new ReturnPush();
        push.Arrive(0);
        var back = false;
        for (var t = 400; t < 460 && !back; t += 4) back = push.Step(ReturnPush.Clip(20), 0, 10, t, 120);
        Assert.True(back);
    }

    [Theory]
    [InlineData(-5, -5)]
    [InlineData(30, 30)]
    [InlineData(400, ReturnPush.MaxStepPx)]
    public void Clip_CapsOneMove(int outward, int kept) => Assert.Equal(kept, ReturnPush.Clip(outward));
}
