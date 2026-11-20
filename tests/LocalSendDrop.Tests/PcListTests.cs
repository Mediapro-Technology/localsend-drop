using LocalSendDrop;
using Xunit;

public class PcListTests
{
    static Device D(string fp, string alias, string type = "desktop") => new() { Fingerprint = fp, Alias = alias, DeviceType = type };
    static PairedPc P(string fp, string alias) => new() { Fingerprint = fp, Alias = alias };

    [Fact]
    public void NearbyUnpaired_IsAvailable()
    {
        var rows = PcList.Rows([D("a", "Studio")], [], _ => false);
        var r = Assert.Single(rows);
        Assert.Equal(("a", "Studio", "Available", false), (r.Fingerprint, r.Name, r.Status, r.Paired));
        Assert.NotNull(r.Device);
    }

    [Fact]
    public void Phones_LeftOut() =>
        Assert.Empty(PcList.Rows([D("p", "Phone", "mobile")], [], _ => false));

    [Fact]
    public void Paired_States()
    {
        var rows = PcList.Rows([D("a", "A"), D("b", "B")], [P("a", "A"), P("b", "B"), P("c", "C")], fp => fp == "a");
        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Fingerprint));
        Assert.All(rows, r => Assert.True(r.Paired));
        Assert.Equal("Paired · connected · on the right", rows[0].Status);
        Assert.Equal("Paired · connecting · on the right", rows[1].Status);
        Assert.Equal("Paired · not nearby · on the right", rows[2].Status);
        Assert.Null(rows[2].Device);
    }

    [Fact]
    public void Side_FromPairing_UnlessArranged()
    {
        var left = new PairedPc { Fingerprint = "a", Alias = "A", Edge = Edge.Left };
        Assert.Equal("Paired · connected · on the left", PcList.Rows([], [left], _ => true)[0].Status);
        Assert.Equal("Paired · connected · placed in Arrange screens", PcList.Rows([], [left], _ => true, _ => true)[0].Status);
    }

    [Fact]
    public void PairedFirst_ThenByName()
    {
        var rows = PcList.Rows([D("z", "Zed"), D("y", "Amy"), D("x", "Bob")], [P("x", "Bob")], _ => false);
        Assert.Equal(["x", "y", "z"], rows.Select(r => r.Fingerprint));
    }

    [Fact]
    public void Fingerprints_MatchIgnoringCase()
    {
        var rows = PcList.Rows([D("ABC", "A")], [P("abc", "A")], _ => false);
        Assert.Single(rows);
        Assert.True(rows[0].Paired);
    }
}
