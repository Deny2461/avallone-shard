using System;
using System.Reflection;
using Server.Misc;
using Xunit;

namespace Server.Tests.Skills;

// Avallone: GainStat was rewritten so every stat roll's outcome can be logged (the owner, 2026-10-27: no stat gains seen).
// These pin the stock rules. Unit tests do not run SkillCheck.Configure(), so the live values are injected here.
[Collection("Sequential UOContent Tests")]
public class StatGainTests : IDisposable
{
    private static readonly string[] Fields =
        { "_statMax", "_statGainChanceMultiplier", "_skillGainChanceMultiplier", "_primaryStatGainChance", "_statGainDelay", "_petStatGainDelay" };

    private readonly object[] _saved = new object[Fields.Length];

    private static FieldInfo F(string n) => typeof(SkillCheck).GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!;

    public StatGainTests()
    {
        for (var i = 0; i < Fields.Length; i++)
        {
            _saved[i] = F(Fields[i]).GetValue(null);
        }

        F("_statMax").SetValue(null, 125);
        F("_statGainChanceMultiplier").SetValue(null, 1.0);
        F("_skillGainChanceMultiplier").SetValue(null, 1.5);
        F("_primaryStatGainChance").SetValue(null, 0.75);
        F("_statGainDelay").SetValue(null, TimeSpan.FromMinutes(8.5));
        F("_petStatGainDelay").SetValue(null, TimeSpan.FromMinutes(4.25));
    }

    public void Dispose()
    {
        for (var i = 0; i < Fields.Length; i++)
        {
            F(Fields[i]).SetValue(null, _saved[i]);
        }
    }

    // Racer X on 2026-10-28: Str 33 Dex 23 Int 63, Dex locked.
    private static Mobile RacerLike()
    {
        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        m.Player = true;
        m.RawStr = 33; m.RawDex = 23; m.RawInt = 63;
        m.StrLock = StatLockType.Up; m.DexLock = StatLockType.Locked; m.IntLock = StatLockType.Up;
        return m;
    }

    [Fact]
    public void FirstRollRaisesTheStat_SecondWithinTheDelayIsBlocked()
    {
        var m = RacerLike();
        SkillCheck.GainStat(m, Stat.Int);
        Assert.Equal(64, m.RawInt);
        var stamp = m.LastIntGain;
        Assert.NotEqual(DateTime.MinValue, stamp);

        SkillCheck.GainStat(m, Stat.Int);
        Assert.Equal(64, m.RawInt);          // blocked by the 8.5 min timer
        Assert.Equal(stamp, m.LastIntGain);  // a blocked roll does not restart the timer (stock)
    }

    [Fact]
    public void AnOldGainDoesNotBlock()
    {
        var m = RacerLike();
        m.LastStrGain = Core.Now - TimeSpan.FromMinutes(8.6);
        SkillCheck.GainStat(m, Stat.Str);
        Assert.Equal(34, m.RawStr);
    }

    [Fact]
    public void ARecentGainBlocks()
    {
        var m = RacerLike();
        m.LastStrGain = Core.Now - TimeSpan.FromMinutes(8.4);
        SkillCheck.GainStat(m, Stat.Str);
        Assert.Equal(33, m.RawStr);
    }

    [Fact]
    public void ALockedStatDoesNotRise_ButItsTimerStarts()
    {
        var m = RacerLike();
        SkillCheck.GainStat(m, Stat.Dex);
        Assert.Equal(23, m.RawDex);
        Assert.NotEqual(DateTime.MinValue, m.LastDexGain); // stock: the timestamp is set before the lock is checked
    }

    [Fact]
    public void StatsAreIndependent()
    {
        var m = RacerLike();
        SkillCheck.GainStat(m, Stat.Int);
        SkillCheck.GainStat(m, Stat.Str);
        Assert.Equal(64, m.RawInt);
        Assert.Equal(34, m.RawStr);
    }

    [Theory]
    [InlineData(SkillName.AnimalLore, 0, 1)] // Int only
    [InlineData(SkillName.Swords, 1, 0)]     // Str (Dex is locked)
    public void ThousandsOfRealUses_GiveExactlyOneGainPerStat(SkillName name, int strUp, int intUp)
    {
        var m = RacerLike();
        var skill = m.Skills[name];

        for (var i = 0; i < 3000; i++)
        {
            skill.Base = 44.0;
            SkillCheck.CheckSkill(m, skill, null, 0.5);
        }

        Assert.Equal(33 + strUp, m.RawStr);
        Assert.Equal(23, m.RawDex);
        Assert.Equal(63 + intUp, m.RawInt);
    }
}
