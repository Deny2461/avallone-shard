using System;
using System.Text.Json;
using Server;
using Server.Avallone.AI;
using Server.Misc;
using Xunit;

namespace UOContent.Tests;

// Rumour step C3 (2026-11-11; the owner's carry-in): Weather.FallingAt answers
// "what is falling at this spot now" by the rules OnTick uses for a screen, changes nothing, and keeps
// unknown / dry / rain / snow apart. RealWeather.Word turns it into the one word the brain is sent.
[Collection("Sequential UOContent Tests")]
public class RealWeatherTests : IDisposable
{
    // Far corners of Trammel that no other weather test or static area uses.
    private static readonly Rectangle2D Here = new(5000, 3900, 40, 40);
    private static readonly Point3D Inside = new(5010, 3910, 0);
    private static readonly Point3D Outside = new(4900, 3910, 0);
    private readonly System.Collections.Generic.List<Weather> _made = [];

    public void Dispose()
    {
        foreach (var w in _made)
        {
            Weather.GetWeatherList(w.Facet)?.Remove(w);
        }
    }

    private Weather Make(int temperature, int chanceOfRain, int chanceOfExtreme, params Rectangle2D[] area)
    {
        // an hour between ticks: the test drives OnTick itself
        var w = new Weather(Map.Trammel, area, temperature, chanceOfRain, chanceOfExtreme, TimeSpan.FromHours(1));
        _made.Add(w);
        return w;
    }

    private static void Ticks(Weather w, int n)
    {
        for (var i = 0; i < n; i++)
        {
            w.OnTick();
        }
    }

    [Fact]
    public void NothingFallsBeforeTheFirstTickOrOnTheClearSkyTick()
    {
        var w = Make(15, 100, 0, Here);
        Assert.Equal(Weather.Falling.Dry, Weather.FallingAt(Map.Trammel, Inside));
        Ticks(w, 1);   // stage 0: the game sends its "clear sky" packet (density 0)
        Assert.Equal(Weather.Falling.Dry, Weather.FallingAt(Map.Trammel, Inside));
    }

    [Fact]
    public void RainWhereItIsWarmSnowWhereItIsCold()
    {
        var warm = Make(15, 100, 0, Here);
        Ticks(warm, 2);   // stage 1: density 5, temperature above zero
        Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, Inside));
        Assert.Equal(Weather.Falling.Dry, Weather.FallingAt(Map.Trammel, Outside));        // outside the area
        Assert.Equal(Weather.Falling.Dry, Weather.FallingAt(Map.Felucca, Inside));         // another facet
        Weather.GetWeatherList(Map.Trammel).Remove(warm);

        var cold = Make(-15, 100, 0, Here);
        Ticks(cold, 2);
        Assert.Equal(Weather.Falling.Snow, Weather.FallingAt(Map.Trammel, Inside));
    }

    [Fact]
    public void AnExtremeSpellFlipsRainToSnow()
    {
        var w = Make(15, 100, 100, Here);   // always "extreme": the game sends the temperature with its sign turned
        Ticks(w, 2);
        Assert.Equal(Weather.Falling.Snow, Weather.FallingAt(Map.Trammel, Inside));
    }

    [Fact]
    public void ItFallsForTheWholeSpellAndStopsWhenTheGameStopsSendingIt()
    {
        var w = Make(15, 100, 0, Here);
        Ticks(w, 1);
        for (var stage = 1; stage < 30; stage++)   // stages 1..29 all carry a density above zero
        {
            Ticks(w, 1);
            Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, Inside));
        }

        w.ChanceOfPrecipitation = 0;   // the next spell rolls dry
        Ticks(w, 1);
        Assert.Equal(Weather.Falling.Dry, Weather.FallingAt(Map.Trammel, Inside));
    }

    [Fact]
    public void AnAreaWithNoBoundsCoversTheWholeFacet()
    {
        var w = Make(15, 100, 0);
        Ticks(w, 2);
        Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, Outside));
        Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, new Point3D(10, 10, 0)));
    }

    [Fact]
    public void AskingChangesNothing()
    {
        var w = Make(15, 100, 0, Here);
        Ticks(w, 5);
        var lists = Weather.GetWeatherList(Map.Trammel).Count;
        var area = w.Area[0];
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, Inside));   // the same answer every time
            Weather.FallingAt(Map.Malas, Inside);
            Weather.FallingAt(null, Inside);
        }

        Assert.Equal(lists, Weather.GetWeatherList(Map.Trammel).Count);
        Assert.Equal(area, w.Area[0]);
        Ticks(w, 1);   // the spell carries on from where it was: stage 5 was not skipped or repeated by the asking
        Assert.Equal(Weather.Falling.Rain, Weather.FallingAt(Map.Trammel, Inside));
    }

    [Fact]
    public void UnknownIsOnlyWhenTheGameCannotPlaceTheSpot()
    {
        Assert.Equal(Weather.Falling.Unknown, Weather.FallingAt(null, Inside));
        Assert.Equal(Weather.Falling.Unknown, Weather.FallingAt(Map.Internal, Inside));
        Assert.Null(RealWeather.Word((Mobile)null));
    }

    [Fact]
    public void TheFourAnswersBecomeThreeWordsAndNothing()
    {
        Assert.Equal("dry", RealWeather.Word(Weather.Falling.Dry));
        Assert.Equal("rain", RealWeather.Word(Weather.Falling.Rain));
        Assert.Equal("snow", RealWeather.Word(Weather.Falling.Snow));
        Assert.Null(RealWeather.Word(Weather.Falling.Unknown));
    }

    [Fact]
    public void TheWordGoesOnTheWireAsFalling()
    {
        var request = new AiRequest(Guid.NewGuid(), Guid.NewGuid(), null, "Good day.", AiPriority.Conversation) { Falling = "snow" };
        using var doc = JsonDocument.Parse(SidecarAiClient.BodyJson(request, TimeSpan.FromSeconds(4)));
        Assert.Equal("snow", doc.RootElement.GetProperty("falling").GetString());
        Assert.Equal("Good day.", doc.RootElement.GetProperty("utterance").GetString());   // the rest of the body is as before

        var plain = new AiRequest(Guid.NewGuid(), Guid.NewGuid(), null, "Good day.", AiPriority.Conversation);
        using var doc2 = JsonDocument.Parse(SidecarAiClient.BodyJson(plain, TimeSpan.FromSeconds(4)));
        Assert.Equal(JsonValueKind.Null, doc2.RootElement.GetProperty("falling").ValueKind);   // unknown: the brain reads it as nothing
    }
}
