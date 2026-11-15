using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

// Step 2e D1 (rumour design C2 item 9; design §8.6/§9.7; Codex R1-7, R2 Q5): only a reply that FULLY delivered exactly
// one travelling made-up story - nothing else on offer beside it - hands that story to a companion exchange, by typed id.
public class HandoffTests
{
    private static RumourInfo Info(string[] ids, params RumourDelivered[] delivered) =>
        new("", "", "", ids, "news", delivered, delivered.Length == 1 ? new RumourHandoff(delivered[0].Story, "made_up") : null);

    // Code R1-2: what a mixed offer looks like on the wire - only the made-up story was told, so ids hold only it, but
    // the brain (which saw both on offer) gave no hand-off verdict.
    private static RumourInfo ToldFromAMixedOffer() =>
        new("", "", "", [Mu], "news", [new RumourDelivered(Mu, "mk:9a36a163", "Britain")]);

    private const string Mu = "mu-9a36a163-ba79-c";

    [Fact]
    public void OneFullyToldMadeUpStoryIsHandedOn()
    {
        var s = Exchange.MadeUpStory(Info([Mu], new RumourDelivered(Mu, "mk:9a36a163", "Britain")));
        Assert.NotNull(s);
        Assert.Equal((Mu, "made_up", "Britain"), (s.Id, s.Source, s.Town));
    }

    [Fact]
    public void AnythingElseIsNot()
    {
        Assert.Null(Exchange.MadeUpStory(null));
        Assert.Null(Exchange.MadeUpStory(ToldFromAMixedOffer()));                                               // code R1-2
        Assert.Null(Exchange.MadeUpStory(Info([Mu])));                                                        // teased only
        Assert.Null(Exchange.MadeUpStory(Info([Mu, "7-3"], new RumourDelivered(Mu, "mk:9a36a163", "Britain"))));  // mixed
        Assert.Null(Exchange.MadeUpStory(Info(["7-3"], new RumourDelivered("7-3", "s:7-3@0123456789abcdef", "Britain"))));
        Assert.Null(Exchange.MadeUpStory(Info(["mo-9a36a163"], new RumourDelivered("mo-9a36a163", "mk:9a36a163", "Britain"))));
        Assert.Null(Exchange.MadeUpStory(Info([Mu, "mu-11111111-ba79-c"],
            new RumourDelivered(Mu, "mk:9a36a163", "Britain"), new RumourDelivered("mu-11111111-ba79-c", "mk:11111111", "Britain"))));
    }
}
