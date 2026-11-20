using System.Drawing;
using LocalSendDrop;
using Xunit;

public class PeerSizeTests
{
    [Fact]
    public void Hundred_KeepsBounds()
    {
        DisplayInfo[] d = [new(0, new Rectangle(0, 0, 1920, 1080))];
        Assert.Equal(d, LayoutEditor.Scaled(d, 100));
    }

    [Fact]
    public void Scales_SizeAndOffsets_FromTheirTopLeft()
    {
        // Two 1920x1080 side by side at 133%: each grows, the second moves along with it.
        DisplayInfo[] d = [new(0, new Rectangle(0, 0, 1920, 1080)), new(1, new Rectangle(1920, 0, 1920, 1080))];
        var s = LayoutEditor.Scaled(d, 133);
        Assert.Equal(new Rectangle(0, 0, 2554, 1436), s[0].Bounds);
        Assert.Equal(new Rectangle(2554, 0, 2554, 1436), s[1].Bounds);
    }

    [Fact]
    public void LaptopAt133_AsTallAsA1440Screen_CrossesWholeEdge()
    {
        var mine = new LayoutTile(true, 0, new Rectangle(0, 0, 2560, 1440));
        var their = LayoutEditor.Scaled([new DisplayInfo(0, new Rectangle(0, 0, 1920, 1080))], 133.4)[0].Bounds;
        var peer = new LayoutTile(false, 0, new Rectangle(-their.Width, 0, their.Width, their.Height));
        var z = Assert.Single(LayoutEditor.ZonesFrom([mine, peer], "pc"));
        Assert.Equal(Edge.Left, z.Side);
        Assert.True(z.To > 0.99);
    }

    [Theory]
    [InlineData(10, 25)]
    [InlineData(1000, 400)]
    public void Size_IsClamped(int asked, int kept) => Assert.Equal(kept, LayoutEditor.ClampSize(asked));
}
