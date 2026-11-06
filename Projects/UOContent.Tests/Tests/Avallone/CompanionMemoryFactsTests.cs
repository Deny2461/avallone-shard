using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// 2026-10-06: richer memory facts. The numbered farm plots are one place to a companion.
public class CompanionMemoryFactsTests
{
    [Theory]
    [InlineData("A Wheatfield in Britain 3", "the farmland near Britain")]
    [InlineData("An Onion Field in Britain 1", "the farmland near Britain")]
    [InlineData("A Carrot Field in Skara Brae 2", "the farmland near Skara Brae")]
    [InlineData("A Field in Yew", "the farmland near Yew")]
    [InlineData("Britain", "Britain")]
    [InlineData("Moongates", "Moongates")]
    [InlineData("Britain Graveyard", "Britain Graveyard")]
    public void FarmPlotsAreOnePlace(string region, string place) =>
        Assert.Equal(place, BaseCompanion.PlaceName(region));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyRegionStaysEmpty(string region) => Assert.Equal(region, BaseCompanion.PlaceName(region));
}
