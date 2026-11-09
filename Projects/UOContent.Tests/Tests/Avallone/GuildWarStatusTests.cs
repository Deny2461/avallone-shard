using Server.Guilds;
using Xunit;

namespace UOContent.Tests;

// 2026-11-09: a timed-out war tells the other side the opposite result. The old (status + 1 % 2) turned Lose into Draw.
public class GuildWarStatusTests
{
    [Theory]
    [InlineData(WarStatus.Win, WarStatus.Lose)]
    [InlineData(WarStatus.Lose, WarStatus.Win)]
    [InlineData(WarStatus.Draw, WarStatus.Draw)]
    [InlineData(WarStatus.InProgress, WarStatus.InProgress)]
    public void OpponentHearsTheOppositeResult(WarStatus mine, WarStatus theirs) =>
        Assert.Equal(theirs, Guild.OpponentWarStatus(mine));
}
