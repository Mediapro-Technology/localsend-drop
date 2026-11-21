using System.Drawing;
using LocalSendDrop;
using Xunit;

/// <summary>HARSHID-PC on 2026-11-21: two 2560x1440 monitors, DEVLAP paired on the Left, no saved zones.</summary>
public class EdgeReplayTests
{
    static readonly DisplayInfo[] Two = [new(0, new Rectangle(0, 0, 2560, 1440)), new(1, new Rectangle(2560, 0, 2560, 1440))];

    [Fact]
    public void PushingTheLeftEdge_Crosses()
    {
        var pc = new PairedPc { Fingerprint = "P", Edge = Edge.Left };
        var zones = Kvm.ZonesFor(pc, [], Two);
        Assert.Single(zones);
        var rules = new CrossingSettings();
        var state = new CrossState();
        Zone? crossed = null;
        var last = new Point(20, 700);
        for (var i = 0; i < 300 && crossed == null; i++)
        {
            var p = new Point(Math.Max(0, last.X - 10), 700);
            var delta = InputEngine.StepDelta(last, p, state.Pinned?.Side);
            last = p;
            (state, crossed, _) = CrossingRule.Step(state, p, delta, new ZoneSet(zones, []), Two, rules, i * 4);
        }
        Assert.NotNull(crossed);
    }

    /// <summary>The hook reports where the pointer would go, before Windows clips it: past the left edge, X is negative.</summary>
    [Fact]
    public void PushPastTheEdge_ReportedOffScreen_StillCrosses()
    {
        var pc = new PairedPc { Fingerprint = "P", Edge = Edge.Left };
        var zones = Kvm.ZonesFor(pc, [], Two);
        var state = new CrossState();
        Zone? crossed = null;
        var last = new Point(20, 700);
        for (var i = 0; i < 300 && crossed == null; i++)
        {
            var raw = new Point(Math.Max(0, last.X) - 6, 700);   // e.g. 1 → -5, as measured on HARSHID-PC
            var delta = InputEngine.StepDelta(last, raw, state.Pinned?.Side);
            var p = CrossingRule.OnScreen(raw, last, Two);
            last = p;
            (state, crossed, _) = CrossingRule.Step(state, p, delta, new ZoneSet(zones, []), Two, new CrossingSettings(), i * 4);
        }
        Assert.NotNull(crossed);
    }

    [Fact]
    public void OnScreen_ClampsOntoTheMonitorItWasOn()
    {
        Assert.Equal(new Point(0, 700), CrossingRule.OnScreen(new Point(-5, 700), new Point(1, 700), Two));
        Assert.Equal(new Point(3000, 700), CrossingRule.OnScreen(new Point(3000, 700), new Point(2550, 700), Two));   // onto the next monitor: as is
        Assert.Equal(new Point(5119, 0), CrossingRule.OnScreen(new Point(5130, -4), new Point(5110, 2), Two));
    }

    [Fact]
    public void LiveSettingsAndScreens_Cross()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSendDrop", "settings.json");
        if (!File.Exists(path)) return;
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;
        var displays = KvmZones.Current();
        var zones = s.Paired.SelectMany(p => Kvm.ZonesFor(p, s.Zones, displays)).ToList();
        var info = $"displays: {string.Join(" ", displays.Select(d => $"{d.Index}:{d.Bounds}"))} zones: {string.Join(" ", zones)} resist {s.Crossing.ResistancePx.Value} corners {s.Crossing.Corners.Value}";
        Assert.True(zones.Count > 0, info);
        var state = new CrossState();
        Zone? crossed = null;
        var last = new Point(20, 700);
        for (var i = 0; i < 300 && crossed == null; i++)
        {
            var p = new Point(Math.Max(0, last.X - 10), 700);
            var delta = InputEngine.StepDelta(last, p, state.Pinned?.Side);
            last = p;
            (state, crossed, _) = CrossingRule.Step(state, p, delta, new ZoneSet(zones, []), displays, s.Crossing, i * 4);
        }
        Assert.True(crossed != null, info);
    }
}
