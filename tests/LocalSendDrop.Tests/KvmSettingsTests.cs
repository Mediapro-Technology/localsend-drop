using LocalSendDrop;
using Xunit;

public class KvmSettingsTests
{
    [Fact]
    public void Merge_NewerWins()
    {
        var mine = new CrossingSettings { ResistancePx = new(50, 100, "A"), Glow = new(false, 300, "A") };
        var theirs = new CrossingSettings { ResistancePx = new(200, 200, "B"), Glow = new(true, 200, "B") };
        var m = CrossingSettings.Merge(mine, theirs);
        Assert.Equal(200, m.ResistancePx.Value);   // theirs newer
        Assert.False(m.Glow.Value);                // mine newer
    }

    [Fact]
    public void Merge_TieIsStable()
    {
        var a = new CrossingSettings { ResistancePx = new(50, 100, "A") };
        var b = new CrossingSettings { ResistancePx = new(70, 100, "B") };
        Assert.Equal(CrossingSettings.Merge(a, b).ResistancePx, CrossingSettings.Merge(b, a).ResistancePx);
    }

    [Fact]
    public void Defaults_MatchSpec()
    {
        var d = new CrossingSettings();
        Assert.Equal(300, d.DoubleTapMs.Value);
        Assert.Equal(120, d.ResistancePx.Value);
        Assert.False(d.Corners.Value);
        Assert.False(d.Notch.Value);
        Assert.True(d.Glow.Value);
        Assert.True(d.Arrival.Value);
        Assert.True(d.HideIdle.Value);
        Assert.Equal(1.0, d.PointerSpeed.Value);
        Assert.Equal(1.0, d.ScrollSpeed.Value);
        Assert.False(d.ReverseScroll.Value);
        Assert.Empty(d.Layout.Value);
    }
}
