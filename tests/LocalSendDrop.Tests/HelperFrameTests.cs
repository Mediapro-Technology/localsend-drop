using System.Text.Json;
using LocalSendDrop;
using Xunit;

/// <summary>The zones the app hands the admin helper must arrive there, or edge crossings silently stop.</summary>
public class HelperFrameTests
{
    [Fact]
    public void ZonesFrame_WithTodaysSettings_RoundTrips()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSendDrop", "settings.json");
        var rules = File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!.Crossing
            : new CrossingSettings();
        var zone = new Zone(0, Edge.Left, 0, 1, "P", KvmZones.UnknownDisplay, Edge.Right, 0, 1);
        var frame = AdminHelper.FrameSetZones([zone], [], rules);
        var back = JsonSerializer.Deserialize<AdminHelper.ZonesFrame>(frame.AsSpan(FrameHeader(frame)));
        Assert.NotNull(back);
        Assert.Single(back!.Live);
    }

    // Frame layout: find where the JSON starts (first '{').
    static int FrameHeader(byte[] f) => Array.IndexOf(f, (byte)'{');
}
