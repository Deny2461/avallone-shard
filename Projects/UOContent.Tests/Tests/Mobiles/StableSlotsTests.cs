using Server.Avallone;
using Server.Mobiles;
using Xunit;

namespace Server.Tests.Mobiles;

// Avallone: avallone.minStableSlots (the owner, 2026-10-27).
[Collection("Sequential UOContent Tests")]
public class StableSlotsTests
{
    private static Mobile NewMobile(double taming = 0, double lore = 0, double vet = 0)
    {
        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        m.Skills.AnimalTaming.Base = taming;
        m.Skills.AnimalLore.Base = lore;
        m.Skills.Veterinary.Base = vet;
        return m;
    }

    private static int WithFloor(int floor, Mobile m)
    {
        var saved = AvalloneRules.MinStableSlots;
        try
        {
            AvalloneRules.MinStableSlots = floor;
            return AnimalTrainer.GetMaxStabled(m);
        }
        finally
        {
            AvalloneRules.MinStableSlots = saved;
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 2)]        // stock minimum
    [InlineData(40.6, 44.4, 0, 2)]  // the owner's Racer X on 2026-10-27
    [InlineData(80, 80, 0, 3)]      // sum 160
    [InlineData(100, 100, 0, 6)]    // sum 200 -> 4, +1 +1 for each skill at 100
    [InlineData(120, 120, 120, 14)] // sum 360 -> 5, +3 +3 +3
    public void FloorOfZero_IsStock(double taming, double lore, double vet, int expected) =>
        Assert.Equal(expected, WithFloor(0, NewMobile(taming, lore, vet)));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(40.6, 44.4, 0)]
    [InlineData(120, 120, 120)]
    public void FloorOfThirty_GivesThirty(double taming, double lore, double vet) =>
        Assert.Equal(30, WithFloor(30, NewMobile(taming, lore, vet)));

    [Fact]
    public void SkillFormula_WinsWhenHigher() =>
        Assert.Equal(14, WithFloor(10, NewMobile(120, 120, 120)));

    [Fact]
    public void DefaultIsThirty() => Assert.Equal(30, AvalloneRules.MinStableSlots);
}
