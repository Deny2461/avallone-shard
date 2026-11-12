using System;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Rumours;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Phase 5b (Codex 5b design review 2026-10-29): the shard fills the ONE sentinel with the CURRENT name, refuses any other
// sentinel, keeps gossip text out of history, and applies the unprompted caps (once per NPC per hour; a rumour a player
// heard in the last 2 hours is excluded across all NPCs).
[Collection("Sequential UOContent Tests")]
public class RumourSpeechTests
{
    private const string S = "⟦P-1a2b⟧";

    private static PlayerMobile NewPlayer(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(1495, 1629, 0), Map.Trammel);
        return m;
    }

    private static Banker NewNpc()
    {
        var b = new Banker();
        b.MoveToWorld(new Point3D(1496, 1629, 0), Map.Trammel);
        return b;
    }

    private static AiReply Reply(string text, RumourInfo info = null) =>
        AiReply.Success(text, "test") with { Rumour = info };

    [Theory]
    [InlineData("what news?", true)]
    [InlineData("waht news", true)]
    [InlineData("heard anything lately?", true)]
    [InlineData("any rumours", true)]
    [InlineData("Any rumors about the road?", true)]
    [InlineData("what's new", true)]
    [InlineData("Tidings?", true)]
    [InlineData("I heard you", false)]
    [InlineData("vendor buy", false)]
    [InlineData("hello", false)]
    public void NewsQuestions(string line, bool news) => Assert.Equal(news, RumourSpeech.IsNews(line));

    [Theory]
    [InlineData("Hail!", true)]
    [InlineData("hello there", true)]
    [InlineData("good morrow", true)]
    [InlineData("well met, smith", true)]
    [InlineData("how much is this", false)]
    [InlineData("this is high", false)]
    public void Greetings(string line, bool greeting) => Assert.Equal(greeting, RumourSpeech.IsGreeting(line));

    [Theory]
    [InlineData("tell me more", true)]
    [InlineData("Really?", true)]
    [InlineData("who was it?", true)]
    [InlineData("what happened then?", true)]
    [InlineData("oh... is that true?", true)]
    [InlineData("What is my mule's name?", false)]
    [InlineData("do you remember the song I sang you?", false)]
    [InlineData("a room for the night please", false)]
    [InlineData("who is the bravest adventurer in all of Britain and beyond, would you say?", false)] // too long
    public void FollowUpLines(string line, bool follow) => Assert.Equal(follow, RumourSpeech.IsFollowUp(line));

    [Fact]
    public void AnOrdinaryLineEndsTheGossipThread()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Gabby Johnson");
        var npc = NewNpc();
        try
        {
            Assert.Equal("follow", RumourSpeech.Ask(npc, p, "tell me more", false, "42-1", "Britain").Mode);
            foreach (var refuse in new[] { "No thanks, keep it to yourself.", "no thanks", "not interested", "never mind" })
            {
                Assert.False(RumourSpeech.IsFollowUp(refuse), refuse);   // Codex 2b code review (optional)
            }

            Assert.False(RumourSpeech.IsSmallTalkOpening("Hello, why is the sky blue?"));
            Assert.True(RumourSpeech.IsSmallTalkOpening("Hello, how are you?"));
            foreach (var bite in new[] { "no", "No, what happened?", "tell me", "what about them?", "I haven't, do tell" })
            {
                Assert.Equal("follow", RumourSpeech.Ask(npc, p, bite, false, "42-1", "Britain").Mode); // took the tease (2026-11-02)
            }
            Assert.Null(RumourSpeech.Ask(npc, p, "What is my mule's name?", false, "42-1", "Britain")); // ordinary turn
            Assert.Equal("news", RumourSpeech.Ask(npc, p, "any other news?", false, "42-1", "Britain").Mode);
        }
        finally
        {
            p.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void FillsTheOneSentinelWithTheCurrentName()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Brynn Handy");
        var npc = NewNpc();
        try
        {
            var id = AiIdentity.GetOrCreate(p).ToString();
            var info = new RumourInfo(S, id, "a fisher", ["1-9"], "news");
            Assert.Equal("They say Brynn Handy fished up boots.", RumourSpeech.Fill(npc, p, Reply($"They say {S} fished up boots.", info), "Britain"));

            p.Name = "Brynn Boots"; // renamed after the deed: the NEW name is said
            Assert.Equal("Brynn Boots's luck!", RumourSpeech.Fill(npc, p, Reply($"{S}'s luck!", info), "Britain"));

            // a sentinel it was not given, or a mangled one: not spoken
            Assert.Null(RumourSpeech.Fill(npc, p, Reply("⟦P-ffff⟧ fished up boots.", info), "Britain"));
            Assert.Null(RumourSpeech.Fill(npc, p, Reply($"{S[..^1]} fished up boots.", info), "Britain"));
            // no gossip offered: ordinary text passes untouched, a stray bracket does not
            Assert.Equal("Fine weather.", RumourSpeech.Fill(npc, p, Reply("Fine weather."), "Britain"));
            Assert.Null(RumourSpeech.Fill(npc, p, Reply("⟦P-1a2b⟧ hi"), "Britain"));
        }
        finally
        {
            p.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void DeletedOrUnknownCharacterBecomesTheDescriptor()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Racer X");
        var listener = NewPlayer("Listener");
        var npc = NewNpc();
        try
        {
            var id = AiIdentity.GetOrCreate(p).ToString();
            p.Delete();
            var info = new RumourInfo(S, id, "a tamer", ["1-1"], "news");
            Assert.Equal("They say a tamer slew a dragon.", RumourSpeech.Fill(npc, listener, Reply($"They say {S} slew a dragon.", info), "Britain"));
            var unknown = new RumourInfo(S, Guid.NewGuid().ToString(), "a bard", ["1-2"], "news");
            Assert.Equal("a bard sang.", RumourSpeech.Fill(npc, listener, Reply($"{S} sang.", unknown), "Britain"));
        }
        finally
        {
            listener.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    // the owner, chat, 2026-11-02: - greeting or idle talk only, never
    // with a question or request, and only about 1 in 4 (the stranger rule is brain side: brain/test_dials.py).
    [Fact]
    public void UnpromptedGossipOnlyOnSmallTalkAndOnlySometimes()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Racer X");
        var npc = NewNpc();
        try
        {
            Assert.Null(RumourSpeech.Ask(npc, p, "what do you like", true, null, "Britain"));       // the owner's case
            Assert.Null(RumourSpeech.Ask(npc, p, "hail, can you train me?", false, null, "Britain"));
            Assert.Null(RumourSpeech.Ask(npc, p, "hello, I want to buy a bow", false, null, "Britain"));
            Assert.True(RumourSpeech.IsSmallTalkOpening("Hail!"));
            Assert.True(RumourSpeech.IsSmallTalkOpening("how are you today?"));
            Assert.True(RumourSpeech.IsSmallTalkOpening("Fine weather, isn't it"));
            Assert.False(RumourSpeech.IsSmallTalkOpening("hail, where is the bank?"));

            RumourSpeech.GreetRoll = () => 99;                                                     // the roll fails
            Assert.Null(RumourSpeech.Ask(npc, p, "hail", false, null, "Britain"));
            Assert.Equal("news", RumourSpeech.Ask(npc, p, "any news?", false, null, "Britain").Mode); // asked: unchanged
            RumourSpeech.GreetRoll = () => 24;                                                     // just inside 25%
            Assert.Equal("greet", RumourSpeech.Ask(npc, p, "how are you?", false, null, "Britain").Mode);
            Assert.False(RumourSpeech.Ask(npc, p, "how are you?", false, null, "Britain").GreetUnrolled);

            RumourSpeech.SetGreetInBrainForTests(true);   // 2c (M7.6): no shard roll - the brain draws by mood
            RumourSpeech.GreetRoll = () => 99;
            var g = RumourSpeech.Ask(npc, p, "hail", false, null, "Britain");
            Assert.Equal("greet", g.Mode);
            Assert.True(g.GreetUnrolled);
            Assert.False(RumourSpeech.Ask(npc, p, "any news?", false, null, "Britain").GreetUnrolled);
            Assert.Null(RumourSpeech.Ask(npc, p, "hail, where is the bank?", false, null, "Britain"));
        }
        finally
        {
            p.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void AskModesAndCaps()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Racer X");
        var npc = NewNpc();
        var npc2 = NewNpc();
        try
        {
            Assert.Null(RumourSpeech.Ask(npc, p, "how much for the bow", false, null, "Britain")); // trade talk: no gossip
            Assert.Null(RumourSpeech.Ask(npc, p, "what news", false, null, null));                 // not in a town
            Assert.Null(RumourSpeech.Ask(npc, npc2, "what news", false, null, "Britain"));         // not a player
            Assert.Equal("news", RumourSpeech.Ask(npc, p, "waht news", false, null, "Britain").Mode);
            Assert.Equal("follow", RumourSpeech.Ask(npc, p, "tell me more", false, "1-5", "Britain").Mode);
            Assert.Equal("greet", RumourSpeech.Ask(npc, p, "hail", false, null, "Britain").Mode);
            Assert.Null(RumourSpeech.Ask(npc, p, "nice hat", true, null, "Britain")); // a first line alone no longer opens gossip (2026-11-02)

            // an unprompted mention used: this NPC waits an hour, and the rumour is excluded for this player everywhere
            Assert.NotNull(RumourSpeech.Fill(npc, p, Reply("Ha, you again!", new RumourInfo(S, "", "", ["1-7"], "greet")), "Britain"));
            Assert.Null(RumourSpeech.Ask(npc, p, "hail", false, null, "Britain"));
            var other = RumourSpeech.Ask(npc2, p, "hail", false, null, "Britain");
            Assert.Equal("greet", other.Mode);
            Assert.Contains("1-7", other.Exclude);
            Assert.Equal("news", RumourSpeech.Ask(npc, p, "any news?", false, null, "Britain").Mode); // asked: always fine

            // Codex 2026-10-30 finding 2: gossip in the prompt but not told -> spoken, history keeps only the marker,
            // and no cap or heard timer starts
            var npc3 = NewNpc();
            var untold = Reply("Good day to you.", new RumourInfo("", "", "", [], "greet"));
            Assert.Equal("Good day to you.", RumourSpeech.Fill(npc3, p, untold, "Britain"));
            Assert.Equal(RumourSpeech.Marker, RumourSpeech.HistoryText(untold, "Good day to you."));
            Assert.Null(RumourSpeech.FollowId(untold));
            Assert.Equal("greet", RumourSpeech.Ask(npc3, p, "hail", false, null, "Britain").Mode);   // cap not started

            // optional 5: a reply's gossip must match what was asked
            var asked = RumourSpeech.Ask(npc3, p, "what news", false, null, "Britain");
            Assert.Null(RumourSpeech.Fill(npc3, p, Reply("Hm.", new RumourInfo("", "", "", ["1-9"], "greet")), "Britain", asked, bind: true));
            Assert.Null(RumourSpeech.Fill(npc3, p, Reply("Hm.", new RumourInfo("", "", "", ["1-9"], "news")), "Britain", null, bind: true));
            Assert.Equal("Hm.", RumourSpeech.Fill(npc3, p, Reply("Hm.", new RumourInfo("", "", "", ["1-9"], "news")), "Britain", asked, bind: true));

            RumourSpeech.ResetForTests(false);
            Assert.Null(RumourSpeech.Ask(npc2, p, "what news", false, null, "Britain")); // speaking off
        }
        finally
        {
            p.Delete();
            npc.Delete();
            npc2.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    // Gossip by name (the owner, chat, 2026-10-30, dev play test 2: ->).
    [Fact]
    public void ALineNamingAPlayerAsksAboutThatPerson()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Gabby Johnson");
        var brynn = NewPlayer("Brynn Handy");
        var bryn = NewPlayer("Brynn");
        var npc = NewNpc();
        try
        {
            var id = AiIdentity.GetOrCreate(brynn).ToString();
            var ask = RumourSpeech.Ask(npc, p, "Do you know Brynn Handy?", false, null, "Britain");
            Assert.Equal("about", ask.Mode);
            Assert.Equal(id, ask.About);                                   // the longest match: Brynn Handy, not "Brynn"
            Assert.Equal("Brynn Handy", RumourSpeech.NamedIn("I hear brynn handy has shoes")?.Name);
            Assert.Equal("about", RumourSpeech.Ask(npc, p, "any news of Brynn?", false, null, "Britain").Mode);
            var self = RumourSpeech.Ask(npc, p, "what about gabby johnson", false, null, "Britain");
            Assert.Equal("about", self.Mode);
            Assert.Equal("", self.About);                                  // named, but no AI identity yet
            Assert.Null(RumourSpeech.NamedIn("the brynnish hills"));         // whole words only
            Assert.Null(RumourSpeech.NamedIn("how much for a room?"));
            Assert.Equal("news", RumourSpeech.Ask(npc, p, "what news?", false, null, "Britain").Mode);
            // a reply carrying "about" gossip is spoken only for an "about" request
            Assert.Null(RumourSpeech.Fill(npc, p, Reply("Hm.", new RumourInfo("", "", "", ["1-9"], "about")), "Britain",
                RumourSpeech.Ask(npc, p, "what news?", false, null, "Britain"), bind: true));
            Assert.Equal("Hm.", RumourSpeech.Fill(npc, p, Reply("Hm.", new RumourInfo("", "", "", ["1-9"], "about")), "Britain", ask, bind: true));
        }
        finally
        {
            p.Delete();
            brynn.Delete();
            bryn.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void AStoryJustHeardIsSentAsRecent()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Gabby Johnson");
        var npc = NewNpc();
        try
        {
            Assert.Empty(RumourSpeech.Ask(npc, p, "what news?", false, null, "Britain").Recent);
            RumourSpeech.Fill(npc, p, Reply("Boots, four of them.", new RumourInfo("", "", "", ["39-19"], "news")), "Britain");
            var ask = RumourSpeech.Ask(npc, p, "what news?", false, null, "Britain");
            Assert.Contains("39-19", ask.Recent);    // just heard: the brain skips it on "what news"
            Assert.Contains("39-19", ask.Exclude);   // and damps it for 2 hours as before
        }
        finally
        {
            p.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void AShopCommandFindsTheNamedVendorEvenMisspelt()
    {
        var p = NewPlayer("Gabby Johnson");
        var v = NewNpc();
        v.Name = "Zahra";
        try
        {
            Assert.Same(v, TownsfolkTalk.ShopTarget(p, "zahra buy", [0x171]));
            Assert.Same(v, TownsfolkTalk.ShopTarget(p, "Zabra buy", [0x171]));        // near spelling (the owner's typo)
            Assert.Same(v, TownsfolkTalk.ShopTarget(p, "zahra sell", [0x177]));
            Assert.Null(TownsfolkTalk.ShopTarget(p, "buy", [0x171]));                  // nobody named
            Assert.Null(TownsfolkTalk.ShopTarget(p, "vendor buy", [0x3C]));            // stock handles it
            Assert.Null(TownsfolkTalk.ShopTarget(p, "Zahra, where can I buy a sword?", [0x15, 0x171])); // talk
        }
        finally
        {
            p.Delete();
            v.Delete();
        }
    }

    // Codex 5c-1 code review finding 2: never decided by roster order.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExactShortNameBeatsSomeoneElsesFirstName(bool handyFirst)
    {
        RumourSpeech.ResetForTests(true);
        var a = NewPlayer(handyFirst ? "Brynn Handy" : "Brynn");
        var b = NewPlayer(handyFirst ? "Brynn" : "Brynn Handy");
        try
        {
            Assert.Equal("Brynn", RumourSpeech.NamedIn("What have you heard about Brynn?", out var amb)?.Name);
            Assert.False(amb);
            Assert.Equal("Brynn Handy", RumourSpeech.NamedIn("and Brynn Handy?", out amb)?.Name);
            Assert.False(amb);
        }
        finally
        {
            a.Delete();
            b.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    // Codex 5c-1 round 2 finding 2: two separate mentions are two people, whatever the order.
    [Theory]
    [InlineData(true, "Is Brynn friends with Brynn Handy?")]
    [InlineData(false, "Is Brynn friends with Brynn Handy?")]
    [InlineData(true, "What did Brynn Handy say about Brynn?")]
    [InlineData(false, "What did Brynn Handy say about Brynn?")]
    public void TwoSeparateMentionsAreTwoPeople(bool handyFirst, string line)
    {
        RumourSpeech.ResetForTests(true);
        var a = NewPlayer(handyFirst ? "Brynn Handy" : "Brynn");
        var b = NewPlayer(handyFirst ? "Brynn" : "Brynn Handy");
        try
        {
            Assert.Null(RumourSpeech.NamedIn(line, out var amb));
            Assert.True(amb);
            Assert.Equal("Brynn Handy", RumourSpeech.NamedIn("tell me of Brynn Handy", out amb)?.Name);   // one mention
            Assert.False(amb);
        }
        finally
        {
            a.Delete();
            b.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void ASharedFirstNameOrTwoPeopleIsAmbiguous()
    {
        RumourSpeech.ResetForTests(true);
        var p = NewPlayer("Gabby Johnson");
        var a = NewPlayer("Brynn Handy");
        var b = NewPlayer("Brynn Smith");
        var t = NewPlayer("Thandor");
        var npc = NewNpc();
        try
        {
            Assert.Null(RumourSpeech.NamedIn("Do you know Brynn?", out var amb));
            Assert.True(amb);                                                   // two Brynns: ask which
            Assert.Equal("Brynn Smith", RumourSpeech.NamedIn("Do you know Brynn Smith?", out amb)?.Name);
            Assert.False(amb);
            Assert.Null(RumourSpeech.NamedIn("Is Thandor friends with Brynn Handy?", out amb));
            Assert.True(amb);                                                   // two people named
            var ask = RumourSpeech.Ask(npc, p, "Do you know Brynn?", false, null, "Britain");
            Assert.Equal("about", ask.Mode);
            Assert.True(ask.Ambiguous);
            Assert.Equal("", ask.About);
        }
        finally
        {
            p.Delete();
            a.Delete();
            b.Delete();
            t.Delete();
            npc.Delete();
            RumourSpeech.ResetForTests(false);
        }
    }

    [Fact]
    public void HistoryNeverKeepsGossipText()
    {
        var info = new RumourInfo(S, "", "", ["1-3"], "news");
        Assert.Equal(RumourSpeech.Marker, RumourSpeech.HistoryText(Reply($"{S} did it", info), "Racer X did it"));
        Assert.Equal("Fine weather.", RumourSpeech.HistoryText(Reply("Fine weather."), "Fine weather."));
        Assert.Equal("1-3", RumourSpeech.FollowId(Reply("x", info)));

        AiLine[] history = [new("player", "what news"), new("npc", RumourSpeech.Marker), new("player", "and?")];
        RumourSpeech.ResetForTests(true);
        Assert.Equal(3, RumourSpeech.History(history).Length);
        RumourSpeech.ResetForTests(false); // speaking off: the marker leaves the prompt too
        Assert.DoesNotContain(RumourSpeech.History(history), l => l.Text == RumourSpeech.Marker);
    }

    // Rumour step C2 (2026-11-11): the GM listing labels a made-up story; a real one reads exactly as before.
    [Fact]
    public void TheGmListingLabelsAMadeUpStory()
    {
        using var real = System.Text.Json.JsonDocument.Parse(
            "{\"town\":\"Britain\",\"text\":\"Racer X slew a dragon near Destard\",\"id\":\"1-7\",\"hops\":2}");
        Assert.Equal("[Britain] Racer X slew a dragon near Destard (id 1-7, 2 hop(s))", RumourCommands.ListLine(real.RootElement));

        using var made = System.Text.Json.JsonDocument.Parse(
            "{\"town\":\"Britain\",\"text\":\"a sample piece of town gossip\",\"id\":\"mu-e143f2a7-9e24-o\",\"hops\":3,\"source\":\"made_up\"}");
        Assert.Equal(
            "[Britain] a sample piece of town gossip (id mu-e143f2a7-9e24-o, 3 hop(s), made-up)",
            RumourCommands.ListLine(made.RootElement)
        );
    }
}
