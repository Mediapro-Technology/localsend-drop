using LocalSendDrop;
using Xunit;

/// <summary>One owner of the input at a time (like Beamer's owner): no two PCs controlling each other.</summary>
public class OwnerTests
{
    [Fact]
    public void WhileOurInputIsAway_RefuseBusy() =>
        Assert.Equal(KvmMsg.Busy, KvmRemote.EnterRefusal(false, () => false, away: () => true));

    [Fact]
    public void PausedAndLocked_StillComeFirst()
    {
        Assert.Equal(KvmMsg.Paused, KvmRemote.EnterRefusal(true, () => false, away: () => true));
        Assert.Equal(KvmMsg.Locked, KvmRemote.EnterRefusal(false, () => true, away: () => true));
    }

    [Fact]
    public void Home_Accepts() => Assert.Null(KvmRemote.EnterRefusal(false, () => false, away: () => false));

    [Fact]
    public void Busy_IsANewWireValue() => Assert.Equal(18, (int)KvmMsg.Busy);

    [Fact]
    public void HandBack_NothingControllingUs_IsFalse() => Assert.False(KvmRemote.HandBack("test"));

    [Fact]
    public void NotControlled_ByDefault() => Assert.False(KvmRemote.Controlled);
}
