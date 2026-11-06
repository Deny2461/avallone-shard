using System;
using System.Collections.Generic;
using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

// Phase 4c1: turning points are rules on counts and trust bands; traits are a projection of the applied,
// un-revoked ones, clamped to -2..+2 (fake rules here - the real ones are approved unseen).
public class AiPersonalityTests
{
    private static AiPersonality.Rule R(string id, string band, Dictionary<string, int> counts, Dictionary<string, int> changes) =>
        new(id, band, counts, changes, "memory");

    private static readonly AiPersonality.Rule Thaw = R("thaw", "warming", [], new() { ["warmth"] = 1 });
    private static readonly AiPersonality.Rule Fights = R("fights", null, new() { ["fight_survived"] = 10 }, new() { ["warmth"] = 1, ["humour"] = 1 });
    private static readonly AiPersonality.Rule Cruel = R("cruel", null, new() { ["criminal_act"] = 3 }, new() { ["warmth"] = -1 });

    private static Dictionary<string, AiPersonality.Rule> ById(params AiPersonality.Rule[] rules)
    {
        var d = new Dictionary<string, AiPersonality.Rule>();
        foreach (var r in rules)
        {
            d[r.Id] = r;
        }

        return d;
    }

    [Fact]
    public void BandAndCountsMustBothHold()
    {
        var both = R("both", "trusting", new() { ["fight_survived"] = 5 }, new() { ["warmth"] = 1 });
        Assert.False(AiPersonality.Met(both, 1, new Dictionary<string, int> { ["fight_survived"] = 9 }));
        Assert.False(AiPersonality.Met(both, 2, new Dictionary<string, int> { ["fight_survived"] = 4 }));
        Assert.True(AiPersonality.Met(both, 2, new Dictionary<string, int> { ["fight_survived"] = 5 }));
        Assert.True(AiPersonality.Met(Thaw, 3, new Dictionary<string, int>()));
    }

    [Fact]
    public void TraitsAreClampedToTwo()
    {
        var more = R("more", null, [], new() { ["warmth"] = 1 });
        var traits = AiPersonality.Project(
            [new("thaw", DateTime.UtcNow, false), new("fights", DateTime.UtcNow, false), new("more", DateTime.UtcNow, false)],
            ById(Thaw, Fights, more));
        Assert.Equal(2, traits["warmth"]);
        Assert.Equal(1, traits["humour"]);
    }

    [Fact]
    public void UndoRecomputesFromWhatIsLeft()
    {
        var applied = new List<AiPersonality.Applied>
        {
            new("thaw", DateTime.UtcNow, false), new("cruel", DateTime.UtcNow, false), new("fights", DateTime.UtcNow, true)
        };
        var traits = AiPersonality.Project(applied, ById(Thaw, Fights, Cruel));
        Assert.Equal(0, traits["warmth"]);           // +1 thaw, -1 cruel, fights revoked
        Assert.False(traits.ContainsKey("humour"));  // only from the revoked one
    }

    [Fact]
    public void ReplayGivesTheSameTraits()
    {
        var applied = new List<AiPersonality.Applied> { new("thaw", DateTime.UtcNow, false), new("fights", DateTime.UtcNow, false) };
        Assert.Equal(AiPersonality.Project(applied, ById(Thaw, Fights)), AiPersonality.Project(applied, ById(Thaw, Fights)));
    }
}
