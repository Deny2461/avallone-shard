using System;
using Server.Misc;
using Xunit;
using Xunit.Abstractions;

namespace Server.Tests.Skills;

// Avallone: skills.gainChanceMultiplier (the owner, 2026-10-22: x1.5). Proves the multiplier changes the gain chance
// by exactly that factor, and that the real CheckSkill path gains about 1.5x as often.
[Collection("Sequential UOContent Tests")]
public class SkillGainRateTests
{
    private readonly ITestOutputHelper _out;

    public SkillGainRateTests(ITestOutputHelper output) => _out = output;

    private static Mobile NewMobile()
    {
        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        return m;
    }

    [Theory]
    [InlineData(10.0, 0.9, true)]
    [InlineData(30.0, 0.5, false)]
    [InlineData(50.0, 0.5, true)]
    [InlineData(70.0, 0.2, false)]
    [InlineData(95.0, 0.95, true)]
    public void Multiplier_ScalesChanceExactly(double baseSkill, double chance, bool success)
    {
        var m = NewMobile();
        var skill = m.Skills[SkillName.Magery];
        skill.Base = baseSkill;

        var stock = SkillCheck.ComputeSkillGainChance(m, skill, chance, success, 1.0);
        var avallone = SkillCheck.ComputeSkillGainChance(m, skill, chance, success, 1.5);

        Assert.True(stock > 0);
        Assert.Equal(stock * 1.5, avallone, 12);
    }

    [Fact]
    public void Multiplier_OneLeavesStockMath()
    {
        var m = NewMobile();
        var skill = m.Skills[SkillName.Magery];
        skill.Base = 50.0;

        // Stock formula written out by hand (SE era: Core.AOS true, success adds 0.5 * (1 - chance)).
        var gc = ((m.Skills.Cap - m.Skills.Total) / (double)m.Skills.Cap + (skill.Cap - skill.Base) / skill.Cap) / 2;
        gc = (gc + (1.0 - 0.5) * (Core.AOS ? 0.5 : 0.5)) / 2 * skill.Info.GainFactor;

        Assert.Equal(Math.Max(gc, 0.01), SkillCheck.ComputeSkillGainChance(m, skill, 0.5, true, 1.0), 12);
    }

    [Fact]
    public void Floor_IsAppliedBeforeMultiplier()
    {
        var m = NewMobile();
        var skill = m.Skills[SkillName.Magery];
        skill.Base = 99.9;
        skill.Cap = 100.0;

        // Near cap, easy use, failed: stock math is below 1%, so stock = 0.01 and Avallone = 0.015.
        var stock = SkillCheck.ComputeSkillGainChance(m, skill, 0.999, false, 1.0);
        if (stock <= 0.01)
        {
            Assert.Equal(0.015, SkillCheck.ComputeSkillGainChance(m, skill, 0.999, false, 1.5), 12);
        }
    }

    // Runs the real CheckSkill (success roll, gain roll, Gain) many times from the same starting point.
    private static int CountGains(double multiplier, int trials, double baseSkill, double chance)
    {
        var saved = SkillCheck.SkillGainChanceMultiplier;
        try
        {
            SkillCheck.SkillGainChanceMultiplier = multiplier;
            var m = NewMobile();
            var skill = m.Skills[SkillName.Magery];
            Assert.Equal(SkillLock.Up, skill.Lock);
            var gains = 0;

            for (var i = 0; i < trials; i++)
            {
                skill.Base = baseSkill;
                SkillCheck.CheckSkill(m, skill, null, chance);
                if (skill.Base > baseSkill)
                {
                    gains++;
                }
            }

            return gains;
        }
        finally
        {
            SkillCheck.SkillGainChanceMultiplier = saved;
        }
    }

    [Theory]
    [InlineData(40.0, 0.5)]
    [InlineData(70.0, 0.3)]
    [InlineData(90.0, 0.8)]
    public void RealCheckSkill_GainsAboutOneAndAHalfTimesAsOften(double baseSkill, double chance)
    {
        const int trials = 40000;
        var stock = CountGains(1.0, trials, baseSkill, chance);
        var avallone = CountGains(1.5, trials, baseSkill, chance);
        var ratio = avallone / (double)stock;

        _out.WriteLine($"base {baseSkill} chance {chance}: stock {stock}/{trials} = {stock * 100.0 / trials:F2}%, x1.5 {avallone}/{trials} = {avallone * 100.0 / trials:F2}%, ratio {ratio:F3}");

        Assert.True(stock > 500, $"too few stock gains to measure ({stock})");
        Assert.InRange(ratio, 1.40, 1.60);
    }
}
