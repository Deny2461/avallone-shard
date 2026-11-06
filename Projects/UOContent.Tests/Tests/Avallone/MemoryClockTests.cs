using System;
using System.Linq;
using Server.Avallone.Rumours;
using Xunit;

namespace UOContent.Tests;

// 5c-2 step 2a (design v1.2 D2b, Codex round 2 F2/F3; the owner): the memory clock's rate,
// the effective number of players, no fractional loss, and the 7-day window.
[Collection("Sequential UOContent Tests")]
public class MemoryClockTests : IDisposable
{
    public MemoryClockTests() => PlayedClock.ResetMemoryForTests();

    public void Dispose() => PlayedClock.ResetMemoryForTests();

    [Theory]
    [InlineData(1, 1, 1.0)]
    [InlineData(2, 1, 2.0 / 3)]
    [InlineData(3, 1, 0.5)]
    [InlineData(10, 1, 2.0 / 11)]
    [InlineData(4, 0, 0.25)]          // k = 0 holds it exactly 1:1 with a solo player
    [InlineData(0.5, 1, 1.0)]         // x below 1 counts as 1
    [InlineData(2, -1, 2.0 / 3)]      // a bad k falls back to 1
    [InlineData(2, double.NaN, 2.0 / 3)]
    public void RateFor(double x, double k, double expected) =>
        Assert.Equal(expected, PlayedClock.RateFor(x, k), 6);

    [Fact]
    public void EffectivePlayersIsWeightedByPlayTime()
    {
        Assert.Equal(1.0, PlayedClock.EffectiveFrom([]), 6);
        Assert.Equal(2.0, PlayedClock.EffectiveFrom([10, 10]), 6);
        Assert.Equal(4.0, PlayedClock.EffectiveFrom([5, 5, 5, 5]), 6);
        // the owner + nine 10-minute visitors: barely more than one player (Codex round 2 F3's case)
        Assert.Equal(1.155, PlayedClock.EffectiveFrom(new[] { 20.0 }.Concat(Enumerable.Repeat(1 / 6.0, 9))), 3);
        Assert.Equal(1.471, PlayedClock.EffectiveFrom([20, 5]), 3);
    }

    [Fact]
    public void TwentyPlayersNeverTruncateToZero()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var accounts = Enumerable.Range(0, 20).Select(i => $"acct{i}").ToArray();
        PlayedClock.CountTick(accounts, now, 3600);
        PlayedClock.RefreshRate(now);
        Assert.Equal(20.0, PlayedClock.EffectivePlayers, 6);
        var before = PlayedClock.MemorySeconds;
        PlayedClock.TickForTests(accounts, now);
        Assert.Equal(10 * 2.0 / 21, PlayedClock.MemorySeconds - before, 6);   // ~0.95 s, kept
    }

    [Fact]
    public void OnlyTheLastSevenDaysCount()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        PlayedClock.CountTick(["wren"], now, 7200);
        PlayedClock.CountTick(["visitor"], now.AddDays(-8), 7200);   // too old
        PlayedClock.RefreshRate(now);
        Assert.Equal(1.0, PlayedClock.EffectivePlayers, 6);
        PlayedClock.CountTick(["visitor"], now.AddDays(-6), 7200);   // inside the window
        PlayedClock.RefreshRate(now);
        Assert.Equal(2.0, PlayedClock.EffectivePlayers, 6);
        Assert.Equal(2.0 / 3, PlayedClock.Rate, 6);
        PlayedClock.RefreshRate(now.AddDays(7));                     // a week later, nobody has played
        Assert.Equal(1.0, PlayedClock.EffectivePlayers, 6);
    }

    [Fact]
    public void AdvanceMovesBothClocksByTheSameRule()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        PlayedClock.CountTick(["a", "b"], now, 3600);
        PlayedClock.RefreshRate(now);
        var (pt, mpt) = (PlayedClock.Seconds, PlayedClock.MemorySeconds);
        Assert.True(PlayedClock.Advance(1));
        Assert.Equal(3600, PlayedClock.Seconds - pt);
        Assert.Equal(3600 * 2.0 / 3, PlayedClock.MemorySeconds - mpt, 3);
        Assert.True(PlayedClock.MemorySeconds <= PlayedClock.Seconds);
    }
}
