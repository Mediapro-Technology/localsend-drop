using System.Drawing;
using LocalSendDrop;
using Xunit;

public class CrossingTests
{
    static readonly DisplayInfo[] Two =
    [
        new(0, new Rectangle(0, 0, 1920, 1080)),
        new(1, new Rectangle(1920, 0, 1920, 1080)),
    ];

    static readonly Zone RightEdge = new(1, Edge.Right, 0, 1, "pc", 0, Edge.Left, 0, 1);
    static readonly Zone TopEdge = new(0, Edge.Top, 0, 0.2, "pc", 0, Edge.Bottom, 0, 1);

    static CrossingSettings Settings(int resistance = 120, bool corners = false, bool notch = false) => new()
    {
        ResistancePx = new(resistance, 1, "t"),
        Corners = new(corners, 1, "t"),
        Notch = new(notch, 1, "t"),
    };

    static (CrossState state, Zone? cross, Zone? wake) Push(CrossState s, Point p, int dx, int dy, ZoneSet zones, CrossingSettings settings, long now = 0) =>
        CrossingRule.Step(s, p, new Point(dx, dy), zones, Two, settings, now);

    static readonly ZoneSet Live = new([RightEdge], []);

    [Fact]
    public void Resistance_Under120_DoesNotCross()
    {
        var s = new CrossState();
        var p = new Point(3839, 500);
        for (var i = 0; i < 3; i++)
        {
            var r = Push(s, p, 30, 0, Live, Settings());
            Assert.Null(r.cross);
            s = r.state;
        }
    }

    [Fact]
    public void Resistance_Over120_Crosses()
    {
        var s = new CrossState();
        var p = new Point(3839, 500);
        Zone? crossed = null;
        for (var i = 0; i < 5 && crossed == null; i++)
        {
            var r = Push(s, p, 30, 0, Live, Settings());
            s = r.state;
            crossed = r.cross;
        }
        Assert.Equal(RightEdge, crossed);
    }

    [Fact]
    public void Resistance_LeavingTheEdgeStartsOver()
    {
        var s = Push(new CrossState(), new Point(3839, 500), 100, 0, Live, Settings()).state;
        s = Push(s, new Point(3700, 500), -10, 0, Live, Settings()).state;   // off the edge
        var r = Push(s, new Point(3839, 500), 100, 0, Live, Settings());
        Assert.Null(r.cross);
    }

    [Fact]
    public void Resistance_Zero_CrossesOnTouch()
    {
        var r = Push(new CrossState(), new Point(3839, 500), 0, 0, Live, Settings(resistance: 0));
        Assert.Equal(RightEdge, r.cross);
    }

    [Fact]
    public void Corner_Off_StaysPut()
    {
        var r = Push(new CrossState(), new Point(3839, 2), 500, 0, Live, Settings(corners: false));
        Assert.Null(r.cross);
    }

    [Fact]
    public void Corner_On_Crosses()
    {
        var r = Push(new CrossState(), new Point(3839, 2), 500, 0, Live, Settings(corners: true));
        Assert.Equal(RightEdge, r.cross);
    }

    [Fact]
    public void Notch_On_Crosses()
    {
        // The zone covers only the left fifth of the top edge; the notch is the middle third.
        var zones = new ZoneSet([TopEdge], []);
        var inNotch = new Point(960, 0);
        Assert.Null(Push(new CrossState(), inNotch, 0, -500, zones, Settings(notch: false)).cross);
        Assert.Equal(TopEdge, Push(new CrossState(), inNotch, 0, -500, zones, Settings(notch: true)).cross);
    }

    [Fact]
    public void ButtonHeld_Blocks()
    {
        var s = new CrossState { Buttons = 1 };
        var r = Push(s, new Point(3839, 500), 500, 0, Live, Settings());
        Assert.Null(r.cross);
    }

    [Fact]
    public void InnerEdge_NeverCrosses()
    {
        var inner = new Zone(0, Edge.Right, 0, 1, "pc", 0, Edge.Left, 0, 1);
        var r = Push(new CrossState(), new Point(1919, 500), 500, 0, new ZoneSet([inner], []), Settings());
        Assert.Null(r.cross);
    }

    [Fact]
    public void OfflineZone_WakesOncePer30s()
    {
        var zones = new ZoneSet([], [RightEdge]);
        var p = new Point(3839, 500);
        var r = Push(new CrossState(), p, 500, 0, zones, Settings(), now: 1_000);
        Assert.Null(r.cross);
        Assert.Equal(RightEdge, r.wake);

        r = Push(r.state, p, 500, 0, zones, Settings(), now: 11_000);
        Assert.Null(r.wake);

        r = Push(r.state, p, 500, 0, zones, Settings(), now: 30_999);
        Assert.Null(r.wake);

        r = Push(r.state, p, 500, 0, zones, Settings(), now: 31_001);
        Assert.Equal(RightEdge, r.wake);
    }

    [Fact]
    public void Return_InvertsTarget()
    {
        var zone = new Zone(0, Edge.Right, 0.5, 1.0, "pc", 0, Edge.Left, 0.0, 0.5);
        Assert.Equal(0.75, CrossingRule.Return(zone, 0.25), 3);
        Assert.Equal(0.5, CrossingRule.Return(zone, -1), 3);
    }
}
