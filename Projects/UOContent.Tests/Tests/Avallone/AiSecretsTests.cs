using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

// Phase 4b gate rules (shard-owned). Tier order is enforced; numbers are the defaults in AiSecrets.Rules.
public class AiSecretsTests
{
    [Fact]
    public void TierOneNeedsWarmingTrustFightsTalksAndADay()
    {
        Assert.True(AiSecrets.Earned(1, 25, 3, 10, 1, true));
        Assert.False(AiSecrets.Earned(1, 24, 3, 10, 1, true));
        Assert.False(AiSecrets.Earned(1, 25, 2, 10, 1, true));
        Assert.False(AiSecrets.Earned(1, 25, 3, 9, 1, true));
    }

    [Fact]
    public void TiersGoInOrder()
    {
        Assert.False(AiSecrets.Earned(2, 100, 100, 100, 100, false));
        Assert.True(AiSecrets.Earned(2, 50, 15, 0, 3, true));
        Assert.False(AiSecrets.Earned(3, 100, 100, 100, 100, false));
        Assert.True(AiSecrets.Earned(3, 75, 0, 0, 7, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void UnknownTiersNeverOpen(int tier) => Assert.False(AiSecrets.Earned(tier, 100, 100, 100, 100, true));
}
