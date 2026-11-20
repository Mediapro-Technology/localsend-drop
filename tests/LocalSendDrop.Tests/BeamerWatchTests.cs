using LocalSendDrop;
using Xunit;

public class BeamerWatchTests
{
    [Fact]
    public void Changed_FiresOnStartAndStop()
    {
        var running = false;
        using var watch = new BeamerWatch(() => running, TimeSpan.FromHours(1));
        var seen = new List<bool>();
        watch.Changed += seen.Add;

        watch.Poll();
        Assert.Empty(seen);
        Assert.False(BeamerWatch.Paused);

        running = true;
        watch.Poll();
        watch.Poll();
        Assert.Equal([true], seen);
        Assert.True(BeamerWatch.Paused);

        running = false;
        watch.Poll();
        Assert.Equal([true, false], seen);
        Assert.False(BeamerWatch.Paused);
    }

    [Fact]
    public void StartsPausedWhenBeamerAlreadyRuns()
    {
        using var watch = new BeamerWatch(() => true, TimeSpan.FromHours(1));
        Assert.True(watch.Running);
        Assert.True(BeamerWatch.Paused);
    }
}
