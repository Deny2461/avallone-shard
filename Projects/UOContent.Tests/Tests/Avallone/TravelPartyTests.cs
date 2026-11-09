using System;
using System.Text.Json;
using Server;
using Server.Avallone.AI;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Stage A1 (2026-11-09): what a townsperson is told about who travels with the player, and the "about 1 in 8" floor count.
[Collection("Sequential UOContent Tests")]
public class TravelPartyTests
{
    private static readonly Point3D Here = new(1500, 1600, 0);

    private static PlayerMobile NewPlayer(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(Here, Map.Felucca);
        return m;
    }

    private static T Place<T>(T bc, Mobile owner, int dx = 1) where T : BaseCreature
    {
        bc.MoveToWorld(new Point3D(Here.X + dx, Here.Y, Here.Z), Map.Felucca);
        if (owner != null)
        {
            bc.SetControlMaster(owner);
        }

        return bc;
    }

    private static Mobile NewNpc()
    {
        var npc = new Mobile { Name = "Tanner" };
        npc.DefaultMobileInit();
        npc.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        return npc;
    }

    [Fact]
    public void SeesOwnCompanionAndPetsOnly()
    {
        TravelParty.ResetForTests();
        var p = NewPlayer("Wren");
        var other = NewPlayer("Cody");
        var npc = NewNpc();
        var e = Place(new ElspethVale(), p);
        var pip = Place(new Horse { Name = "Pip" }, p, 2);
        var plain = Place(new Horse(), p, 2);
        var theirs = Place(new Horse { Name = "Notmine" }, other, 2);
        var hidden = Place(new Horse { Name = "Hid", Hidden = true }, p, 2);
        var far = Place(new Horse { Name = "Far" }, p, 20);
        var summoned = Place(new Horse { Name = "Sum" }, p, 2);
        summoned.Summoned = true;
        var wild = Place(new Horse { Name = "Wild" }, null, 2);
        try
        {
            var ask = TravelParty.For(npc, p);
            Assert.NotNull(ask);
            Assert.Single(ask.Companions);
            Assert.Equal("Elspeth Vale", ask.Companions[0].Name);
            Assert.Equal("the field healer", ask.Companions[0].Title);
            Assert.Equal(2, ask.Pets.Length);
            Assert.Contains(ask.Pets, x => x.Label == "Pip" && x.Species == "a horse" && !x.Ridden);
            Assert.Contains(ask.Pets, x => x.Label == null && x.Species == "a horse");   // never renamed: species only
            Assert.False(ask.Nudge);
            Assert.Null(TravelParty.For(npc, other) is { Companions.Length: > 0 } ? "companion leaked" : null);
        }
        finally
        {
            foreach (var m in new Mobile[] { p, other, npc, e, pip, plain, theirs, hidden, far, summoned, wild })
            {
                m.Delete();
            }
        }
    }

    [Fact]
    public void NobodyMeansNull()
    {
        var p = NewPlayer("Wren");
        var npc = NewNpc();
        try
        {
            Assert.Null(TravelParty.For(npc, p));
        }
        finally
        {
            p.Delete();
            npc.Delete();
        }
    }

    [Fact]
    public void RiddenMountCounts()
    {
        var p = NewPlayer("Wren");
        var npc = NewNpc();
        var h = new Horse { Name = "Dusty" };
        h.MoveToWorld(Here, Map.Felucca);
        h.SetControlMaster(p);
        h.Rider = p;
        try
        {
            var ask = TravelParty.For(npc, p);
            Assert.NotNull(ask);
            Assert.Contains(ask.Pets, x => x.Ridden && x.Label == "Dusty" && x.Species == "a horse");
        }
        finally
        {
            h.Rider = null;
            h.Delete();
            p.Delete();
            npc.Delete();
        }
    }

    [Fact]
    public void FloorNudgesAfterSevenMissesAndAMentionResets()
    {
        TravelParty.ResetForTests();
        var p = NewPlayer("Wren");
        try
        {
            var ask = new TravelParty.PartyAsk(
                [new TravelParty.PartyCompanion("Elspeth Vale", "the field healer")],
                [new TravelParty.PartyPet("a horse", "Pip", false)],
                false
            );
            for (var i = 0; i < TravelParty.NudgeAfter - 1; i++)
            {
                TravelParty.Note(p, ask, "Bread is two coppers.", false);
            }

            Assert.False(TravelParty.NudgeDue(p));
            TravelParty.Note(p, ask, "Gossip turn about Elspeth?", true);   // excluded: neither counts nor clears
            Assert.False(TravelParty.NudgeDue(p));
            TravelParty.Note(p, ask, "Fine weather.", false);
            Assert.True(TravelParty.NudgeDue(p));
            TravelParty.Note(p, ask, "Your healer looks tired.", false);  // title word = a mention
            Assert.False(TravelParty.NudgeDue(p));
            Assert.True(TravelParty.Mentions("Elspeth, sit down.", ask));
            Assert.True(TravelParty.Mentions("That horse of yours!", ask));
            Assert.True(TravelParty.Mentions("Pip looks fed.", ask));
            Assert.False(TravelParty.Mentions("Bread is two coppers.", ask));
            TravelParty.Note(p, null, "anything", false);                 // no party: no change
        }
        finally
        {
            p.Delete();
        }
    }

    [Fact]
    public void WireFormatIsOnlyThePublicFields()
    {
        var ask = new TravelParty.PartyAsk(
            [new TravelParty.PartyCompanion("Elspeth Vale", "the field healer")],
            [new TravelParty.PartyPet("a horse", "Pip", true)],
            true
        );
        var json = JsonSerializer.Serialize(ask, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.Equal(
            "{\"companions\":[{\"name\":\"Elspeth Vale\",\"title\":\"the field healer\"}],\"pets\":[{\"species\":\"a horse\",\"label\":\"Pip\",\"ridden\":true}],\"nudge\":true,\"roster\":null}",
            json
        );
    }

    // Codex A1-1: the HISTORY copy of a reply never carries a pet's owner-given name; the spoken line still does.
    [Fact]
    public void HistoryCopyMasksPetNames()
    {
        var ask = new TravelParty.PartyAsk(
            [new TravelParty.PartyCompanion("Elspeth Vale", "the field healer")],
            [new TravelParty.PartyPet("a horse", "Noble Steed", false), new TravelParty.PartyPet("a dog", "Pip", false),
             new TravelParty.PartyPet("a cat", null, false)],
            false
        );
        Assert.Equal(
            "Your horse the horse looks fine, and the dog too. Elspeth, rest.",
            TravelParty.ForHistory("Your horse noble steed looks fine, and Pip too. Elspeth, rest.", ask)
        );
        Assert.Equal("Pippa and Pipes stay.", TravelParty.ForHistory("Pippa and Pipes stay.", ask)); // whole names only
        // Codex A1 fixes review: possessives (straight, curly, any case) and quoted names are masked too
        Assert.Equal("the dog's coat looks glossy.", TravelParty.ForHistory("Pip's coat looks glossy.", ask));
        Assert.Equal("the dog'S saddle is sound.", TravelParty.ForHistory("PIP'S saddle is sound.", ask));
        Assert.Equal("the horse's coat looks glossy.", TravelParty.ForHistory("Noble Steed's coat looks glossy.", ask));
        Assert.Equal("the dog\u2019s tail wags.", TravelParty.ForHistory("Pip\u2019s tail wags.", ask));
        Assert.Equal("They call it 'the dog'.", TravelParty.ForHistory("They call it 'Pip'.", ask));
        Assert.Equal("the dog'll fetch and the dog'd eat.", TravelParty.ForHistory("Pip'll fetch and Pip'd eat.", ask));
        Assert.Equal("No party.", TravelParty.ForHistory("No party.", null));
        Assert.Null(TravelParty.ForHistory(null, ask));
    }

    // Codex A1-2: the party carries the player roster on every request (control: a known player is in it).
    [Fact]
    public void PartyCarriesThePlayerRoster()
    {
        var p = NewPlayer("Wren");
        var other = NewPlayer("Elspeth Gray");
        var npc = NewNpc();
        var pip = Place(new Horse { Name = "Pip" }, p, 2);
        try
        {
            Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();
            var ask = TravelParty.For(npc, p);
            Assert.NotNull(ask.Roster);
            Assert.Contains("Elspeth Gray", ask.Roster);
            Assert.Contains("Wren", ask.Roster);
        }
        finally
        {
            foreach (var m in new Mobile[] { p, other, npc, pip })
            {
                m.Delete();
            }
        }
    }
}
