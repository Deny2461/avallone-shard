using Server.Avallone.Companions;
using Xunit;

namespace UOContent.Tests;

public class CompanionTrustTests
{
    [Fact]
    public void StartsWaryButWillingToTravel()
    {
        Assert.Equal("wary", CompanionTrust.Band(CompanionTrust.Start));
        Assert.True(CompanionTrust.WillJoin(CompanionTrust.Start));
        Assert.False(CompanionTrust.WillLeave(CompanionTrust.Start));
    }

    [Theory]
    [InlineData(0, "wary")]
    [InlineData(24, "wary")]
    [InlineData(25, "warming")]
    [InlineData(50, "trusting")]
    [InlineData(75, "devoted")]
    [InlineData(100, "devoted")]
    public void Bands(int trust, string band) => Assert.Equal(band, CompanionTrust.Band(trust));

    [Fact]
    public void ClampsToRange()
    {
        Assert.Equal(CompanionTrust.Max, CompanionTrust.Apply(98, 10));
        Assert.Equal(CompanionTrust.Min, CompanionTrust.Apply(3, CompanionTrust.Murder));
    }

    [Fact]
    public void OneMurderFromStartDoesNotMakeHerLeave_TwoDo()
    {
        var t = CompanionTrust.Apply(CompanionTrust.Start, CompanionTrust.Murder);
        Assert.False(CompanionTrust.WillLeave(t));
        t = CompanionTrust.Apply(t, CompanionTrust.Murder);
        Assert.True(CompanionTrust.WillLeave(t));
        Assert.False(CompanionTrust.WillJoin(t));
    }

    [Fact]
    public void CanBeTalkedRoundAfterLeaving()
    {
        var t = 0;
        var talks = 0;
        while (!CompanionTrust.WillJoin(t))
        {
            t = CompanionTrust.Apply(t, CompanionTrust.TalkedRound);
            talks++;
        }

        Assert.Equal(5, talks);
    }

    [Fact]
    public void LeaveAndRejoinThresholdsDoNotOverlap() =>
        Assert.True(CompanionTrust.RejoinAt > CompanionTrust.LeaveBelow);

    [Fact]
    public void DescriptionNamesThePlayerAndHasNoNumbers()
    {
        foreach (var t in new[] { 0, 30, 60, 90 })
        {
            var d = CompanionTrust.Describe(t, "Wren");
            Assert.Contains("Wren", d);
            Assert.DoesNotMatch("[0-9]", d);
        }
    }
}
