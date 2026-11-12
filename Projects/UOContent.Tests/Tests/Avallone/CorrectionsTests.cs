using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Rumours;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Rumour step, Part A (2026-11-11; design sections 3 and 12): a player corrects a story about themselves.
// The shard keeps a correction TICKET only after a reply that really told exactly one story about the player, uses it
// up on the player's very next line to that NPC, and speaks the accepting line ONLY once the correction is in its saved
// outbox - after checking everything again. A correction row is held, never dropped, for a brain that does not accept it.
[Collection("Sequential UOContent Tests")]
public class CorrectionsTests : IDisposable
{
    private static readonly Point3D Here = new(5200, 3950, 0);
    private readonly List<Mobile> _made = [];
    private readonly long _playedBefore = PlayedClock.Seconds;
    private DateTime _now = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);

    public CorrectionsTests()
    {
        AiLedger.ResetForTests(7, 100);
        AiLedger.SetCorrectionsIngestForTests(true);
        RumourSpeech.ResetForTests(true);
        RumourSpeech.ResetRosterForTests();
        Corrections.ResetForTests(true);
        Corrections.Now = () => _now;
        PlayedClock.SetSecondsForTests(50_000);
    }

    public void Dispose()
    {
        Corrections.ResetForTests(false);
        RumourSpeech.ResetForTests(false);
        AiLedger.ResetForTests(0, 2000);
        PlayedClock.SetSecondsForTests(_playedBefore);
        foreach (var m in _made)
        {
            if (!m.Deleted)
            {
                m.Delete();
            }
        }

        RumourSpeech.ResetRosterForTests();
    }

    private PlayerMobile Player(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(Here, Map.Felucca);
        AiIdentity.GetOrCreate(m);
        _made.Add(m);
        RumourSpeech.ResetRosterForTests();
        return m;
    }

    private Provisioner Npc(string name)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(Here.X + 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(v);
        return v;
    }

    // A reply that told ONE story (id, deed kind) about the player it is said to.
    private static AiReply Told(string id = "7-3", string type = "fished_boots", params string[] ids) =>
        AiReply.Success("gossip", "m") with
        {
            Rumour = new RumourInfo("", "", "a fisher", ids.Length > 0 ? ids : [id], "news"),
            Correctable = new CorrectableStory(id, type)
        };

    private static AiReply Candidate(string story = "7-3", int? count = 4, string thing = null, string place = null,
        string accept = "Four, was it? Then that is the tale I will tell.") =>
        AiReply.Success("Hm. Folk tell it as they tell it.", "fixed") with
        {
            Correction = new CorrectionOffer(story, count, thing, place, accept)
        };

    // The player says a line (counted like every line), and the turn's opening revision is read.
    private static int Says(PlayerMobile p)
    {
        Exchange.NoteSpeech(p);
        return Exchange.RevisionOf(p);
    }

    // A story is told to `p` by `npc`; then `p` answers that NPC. Returns the used-up ticket (or null).
    private Corrections.Use ToldThenAnswered(Mobile npc, PlayerMobile p, AiReply? told = null)
    {
        var rev = Says(p);
        Corrections.AfterReply(npc, p, told ?? Told(), rev);
        return Corrections.Take(npc, p, Says(p));
    }

    private static List<JsonElement> CorrectionRows() =>
        AiLedger.OutboxForTests.Where(e => e.Kind == Corrections.Kind).Select(e => JsonDocument.Parse(e.FactsJson).RootElement).ToList();

    // ---- the ticket ------------------------------------------------------------------------------------------------

    [Fact]
    public void ATicketOnlyAfterAReplyThatToldExactlyOneStoryAboutThePlayer()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        var use = ToldThenAnswered(npc, p);
        Assert.NotNull(use);
        Assert.Equal(("7-3", "fished_boots"), (use.Ticket.Story, use.Ticket.Type));
        Assert.Equal(["count"], use.Ticket.Fields);
        Assert.Equal(1, Corrections.Issued);
        Assert.Equal(0, Corrections.TicketsForTests);                                          // used up

        Assert.Null(ToldThenAnswered(npc, p, AiReply.Success("just talk", "m")));               // no story told
        Assert.Null(ToldThenAnswered(npc, p, Told(ids: ["7-3", "7-9"])));                       // two stories
        Assert.Null(ToldThenAnswered(npc, p, Told(ids: [])with { Rumour = new RumourInfo("", "", "", [], "greet") }));   // a teaser
        Assert.Null(ToldThenAnswered(npc, p, Told(ids: ["7-9"])));                              // told another story
        Assert.Null(ToldThenAnswered(npc, p, AiReply.Success("gossip", "m") with { Rumour = new RumourInfo("", "", "", ["mu-1a2b3c4d-br-5"], "news") }));   // someone else's / made-up: not marked
        Assert.Null(ToldThenAnswered(npc, p, Told(type: "gm_planted")));
        Assert.Null(ToldThenAnswered(npc, p, Told(type: "fished_bottle")));                     // shows no detail
        Assert.Null(ToldThenAnswered(npc, p, Told(id: "mu-1a2b3c4d-br-5")));                    // not a deed's id
        Assert.Null(ToldThenAnswered(npc, p, Told() with { Kind = "decency" }));
        Assert.Equal(1, Corrections.Issued);
    }

    [Fact]
    public void AMurderOrADeathGetsATicketWithNoDetails_SoTheTaleStands()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        foreach (var type in new[] { "murder", "died_notable", "died_pathetic" })
        {
            var use = ToldThenAnswered(npc, p, Told(type: type));
            Assert.NotNull(use);
            Assert.Empty(use.Ticket.Fields);
            Assert.Empty(Corrections.AskFor(use).Fields);
            Assert.Null(Corrections.Commit(npc, p, use, Candidate(place: "Minoc", count: null)));
        }

        Assert.Empty(CorrectionRows());
    }

    [Fact]
    public void TheTicketIsUsedUpByTheVeryNextLineToThatNpc_AndOnlyThat()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        var other = Npc("Wilfrid");

        var rev = Says(p);
        Corrections.AfterReply(npc, p, Told(), rev);
        Says(p);                                                    // a line to somebody else in between
        Assert.Null(Corrections.Take(npc, p, Says(p)));
        Assert.Equal(0, Corrections.TicketsForTests);

        rev = Says(p);
        Corrections.AfterReply(npc, p, Told(), rev);
        Assert.Null(Corrections.Take(other, p, Says(p)));           // the next line went to another NPC: no ticket there
        Assert.Null(Corrections.Take(npc, p, Says(p)));             // and back with the first, one line too late

        rev = Says(p);
        Corrections.AfterReply(npc, p, Told(), rev);
        _now += TimeSpan.FromSeconds(121);
        Assert.Null(Corrections.Take(npc, p, Says(p)));             // too late

        rev = Says(p);
        Says(p);                                                    // the player spoke again before the reply was said
        Corrections.AfterReply(npc, p, Told(), rev);
        Assert.Equal(0, Corrections.TicketsForTests);               // a stale reply leaves no ticket

        rev = Says(p);
        Corrections.AfterReply(npc, p, Told(), rev);
        Corrections.AfterReply(npc, p, AiReply.Success("more talk", "m"), Says(p));   // another reply from that NPC ends it
        Assert.Equal(0, Corrections.TicketsForTests);
    }

    [Fact]
    public void NoTicketWhileSwitchedOffOrWhileTheBrainHasNotSaidItAcceptsCorrections()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        Corrections.ResetForTests(false);
        Corrections.Now = () => _now;
        Assert.Null(ToldThenAnswered(npc, p));
        Corrections.ResetForTests(true);
        Corrections.Now = () => _now;
        AiLedger.SetCorrectionsIngestForTests(false);
        Assert.Null(ToldThenAnswered(npc, p));
        AiLedger.SetCorrectionsIngestForTests(true);
        RumourSpeech.ResetForTests(false);
        Assert.Null(ToldThenAnswered(npc, p));
        RumourSpeech.ResetForTests(true);
        Assert.NotNull(ToldThenAnswered(npc, p));
    }

    // ---- the commit ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAcceptingLineOnlyOnceTheCorrectionIsInTheOutbox()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        var use = ToldThenAnswered(npc, p);
        var line = Corrections.Commit(npc, p, use, Candidate());
        Assert.Equal("Four, was it? Then that is the tale I will tell.", line);
        var row = Assert.Single(CorrectionRows());
        Assert.Equal("{\"story\":\"7-3\",\"pt\":50000,\"count\":4}", row.GetRawText());   // typed values only - nothing the player said
        var ev = AiLedger.OutboxForTests.Single(e => e.Kind == Corrections.Kind);
        Assert.Equal(AiLedger.WorldId, ev.NpcId);
        Assert.Equal(AiIdentity.GetOrCreate(p), ev.CharId);
        Assert.Equal(1, Corrections.Accepted);
    }

    [Fact]
    public void TwoDetailsInOneLineAreOneRecord()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        var use = ToldThenAnswered(npc, p, Told(type: "kill_notable"));
        Assert.NotNull(Corrections.Commit(npc, p, use, Candidate(count: 3, thing: "drake", place: "Covetous", accept: "Three drakes near Covetous, was it?")));
        var row = Assert.Single(CorrectionRows());
        Assert.Equal("{\"story\":\"7-3\",\"pt\":50000,\"count\":3,\"thing\":\"drake\",\"place\":\"Covetous\"}", row.GetRawText());
        Assert.Equal(1, Corrections.Accepted);
    }

    [Fact]
    public void EverythingIsCheckedAgain_AndARefusalRecordsNothing()
    {
        var p = Player("Aldren Thorn");
        Player("Brynn Ashby");
        var npc = Npc("Osric");

        string Try(AiReply candidate, string type = "kill_notable", Action between = null)
        {
            var use = ToldThenAnswered(npc, p, Told(type: type));
            Assert.NotNull(use);
            between?.Invoke();
            return Corrections.Commit(npc, p, use, candidate);
        }

        Assert.Null(Try(Candidate(place: "Minoc", count: null), "fished_boots"));               // a detail that story never shows
        Assert.Null(Try(Candidate(count: 0)));
        Assert.Null(Try(Candidate(count: 1000)));
        Assert.Null(Try(Candidate(count: null, thing: "mule")));                                // not a creature of this shard
        Assert.Null(Try(Candidate(count: null, thing: "Brynn Ashby")));
        Assert.Null(Try(Candidate(count: null, place: "Mordor")));
        Assert.Null(Try(Candidate(count: null, place: "minoc")));                               // the list's own spelling only
        Assert.Null(Try(Candidate(count: 4, place: "Mordor")));                                 // all or nothing
        Assert.Null(Try(Candidate(story: "7-9")));                                              // another story
        Assert.Null(Try(Candidate(count: null)));                                               // no change
        Assert.Null(Try(Candidate(accept: "Brynn Ashby said four, so four it is.")));           // a real player's name
        Assert.Null(Try(Candidate(accept: "⟦P-1a2b⟧ said four.")));
        Assert.Null(Try(Candidate(accept: "")));
        Assert.Null(Try(Candidate() with { Kind = "decency" }));
        Assert.Null(Try(Candidate() with { Rumour = new RumourInfo("", "", "", [], "news") }));
        Assert.Null(Try(Candidate(), between: () => Says(p)));                                  // the player spoke again
        Assert.Null(Try(Candidate(), between: () => Corrections.SetTakingForTests(false)));     // switched off while in flight
        Corrections.SetTakingForTests(true);
        Assert.Null(Try(Candidate(), between: () => AiLedger.SetCorrectionsIngestForTests(false)));   // the brain no longer says it accepts them
        AiLedger.SetCorrectionsIngestForTests(true);
        Assert.Null(Corrections.Commit(npc, p, null, Candidate()));                             // no ticket was used
        Assert.Null(Try(AiReply.Success("just talk", "m")));                                    // no candidate

        Assert.Empty(CorrectionRows());
        Assert.Equal(0, Corrections.Accepted);
        Assert.NotNull(Try(Candidate()));                                                       // control: the same path does accept
        Assert.Single(CorrectionRows());
    }

    [Fact]
    public void AFullOrSwitchedOffOutboxMeansTheNeutralLine()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        AiLedger.ResetForTests(7, 0);                                // an outbox with no room
        AiLedger.SetCorrectionsIngestForTests(true);
        var use = ToldThenAnswered(npc, p);
        Assert.Null(Corrections.Commit(npc, p, use, Candidate()));
        Assert.Empty(CorrectionRows());

        AiLedger.ResetForTests(0, 100);                              // memory not started: nothing can be recorded
        AiLedger.SetCorrectionsIngestForTests(true);
        use = ToldThenAnswered(npc, p);
        Assert.Null(Corrections.Commit(npc, p, use, Candidate()));
        Assert.Empty(CorrectionRows());
        Assert.Equal(0, Corrections.Accepted);
    }

    [Fact]
    public void OnePlayerTwoNpcs_AnOlderSlowReplyNeverOverwritesANewerChange()
    {
        var p = Player("Aldren Thorn");
        var first = Npc("Osric");
        var second = Npc("Wilfrid");

        var rev = Says(p);
        Corrections.AfterReply(first, p, Told(), rev);
        var slow = Corrections.Take(first, p, Says(p));              // "it was four" to the first NPC - its reply is slow
        Assert.NotNull(slow);

        rev = Says(p);                                               // meanwhile: news from the second NPC, same story
        Corrections.AfterReply(second, p, Told(), rev);
        var fast = Corrections.Take(second, p, Says(p));             // "it was six" - answered at once
        Assert.NotNull(fast);
        Assert.True(fast.Order > slow.Order);                        // the order was fixed when each ticket was used

        Assert.NotNull(Corrections.Commit(second, p, fast, Candidate(count: 6, accept: "Six, was it?")));
        Assert.Null(Corrections.Commit(first, p, slow, Candidate(count: 4)));   // the slow one arrives last: not taken
        var row = Assert.Single(CorrectionRows());
        Assert.Equal(6, row.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ThreeCorrectionsPerPlayerPerPlayedHour()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        for (var i = 0; i < Corrections.PerPlayedHour; i++)
        {
            Assert.NotNull(Corrections.Commit(npc, p, ToldThenAnswered(npc, p), Candidate(count: 4 + i)));
        }

        Assert.Null(Corrections.Commit(npc, p, ToldThenAnswered(npc, p), Candidate(count: 9)));
        Assert.Equal(Corrections.PerPlayedHour, CorrectionRows().Count);
        PlayedClock.SetSecondsForTests(50_000 + 3600);
        Assert.NotNull(Corrections.Commit(npc, p, ToldThenAnswered(npc, p), Candidate(count: 9)));

        var q = Player("Corwin Vale");                                // another player has their own three
        Assert.NotNull(Corrections.Commit(npc, q, ToldThenAnswered(npc, q), Candidate(count: 2)));
    }

    // ---- the lists -------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryListedThingIsACreatureOfThisShard_AndNeverAPerson()
    {
        var kinds = Corrections.KindsOfThisShard();
        Assert.DoesNotContain(Corrections.Things, t => !kinds.Contains(t));
        Assert.All(Corrections.Things, t => Assert.True(Deeds.IsWord(t) && t == t.ToLowerInvariant(), t));
        Assert.All(Corrections.Places, t => Assert.True(Deeds.IsWord(t), t));
        Assert.DoesNotContain("banker", Corrections.Things);
        Assert.DoesNotContain("elspeth vale", Corrections.Things);
        Assert.Contains("dragon", Corrections.Things);
        Assert.Contains("Minoc", Corrections.Places);
    }

    [Fact]
    public void TheBrainHoldsTheSameLists()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "brain", "corrections.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);                                          // the brain's files sit in the same repository
        using var lists = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "brain", "corrections.json")));
        using var rumours = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "brain", "rumours.json")));
        var things = lists.RootElement.GetProperty("things").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        var places = lists.RootElement.GetProperty("places").EnumerateArray().Select(x => x.GetString())
            .Concat(rumours.RootElement.GetProperty("towns").EnumerateObject().Select(t => t.Name)).ToHashSet();
        Assert.True(things.SetEquals(Corrections.Things), string.Join(", ", things.Except(Corrections.Things).Concat(Corrections.Things.Except(things))));
        Assert.True(places.SetEquals(Corrections.Places), string.Join(", ", places.Except(Corrections.Places).Concat(Corrections.Places.Except(places))));
    }

    // ---- the wire --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRequestCarriesTheTicketOnlyWhenOneWasUsed()
    {
        var plain = new AiRequest(Guid.NewGuid(), Guid.NewGuid(), null, "Good day.", AiPriority.Conversation);
        Assert.DoesNotContain("correction", SidecarAiClient.BodyJson(plain, TimeSpan.FromSeconds(8)));
        var p = Player("Aldren Thorn");
        var use = ToldThenAnswered(Npc("Osric"), p, Told(type: "kill_notable"));
        var body = SidecarAiClient.BodyJson(plain with { Correction = Corrections.AskFor(use) }, TimeSpan.FromSeconds(8));
        using var doc = JsonDocument.Parse(body);
        var c = doc.RootElement.GetProperty("correction");
        Assert.Equal("7-3", c.GetProperty("story").GetString());
        Assert.Equal("kill_notable", c.GetProperty("type").GetString());
        Assert.Equal(["count", "thing", "place"], c.GetProperty("fields").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(50_000, c.GetProperty("pt").GetInt64());
        Assert.Contains(c.GetProperty("roster").EnumerateArray(), x => x.GetProperty("name").GetString() == "Aldren Thorn");
    }

    [Theory]
    [InlineData("{\"story\":\"7-3\",\"changes\":{\"count\":4},\"accept\":\"Four it is.\"}", true)]
    [InlineData("{\"story\":\"7-3\",\"changes\":{\"count\":4,\"thing\":\"drake\",\"place\":\"Minoc\"},\"accept\":\"x\"}", true)]
    [InlineData("{\"story\":\"7-3\",\"changes\":{},\"accept\":\"Four it is.\"}", false)]
    [InlineData("{\"story\":\"7-3\",\"changes\":{\"count\":\"4\"},\"accept\":\"Four it is.\"}", false)]
    [InlineData("{\"story\":\"7-3\",\"changes\":{\"count\":4,\"who\":\"Brynn\"},\"accept\":\"Four it is.\"}", false)]
    [InlineData("{\"story\":\"7-3\",\"changes\":[4],\"accept\":\"Four it is.\"}", false)]
    [InlineData("{\"story\":\"7-3\",\"changes\":{\"count\":4}}", false)]
    [InlineData("{\"changes\":{\"count\":4},\"accept\":\"Four it is.\"}", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("null", false)]
    public void ABrokenCandidateIsNoCandidate(string json, bool usable)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(usable, SidecarAiClient.CleanCorrection(doc.RootElement) != null);
    }

    [Fact]
    public void TheStoryMarkOnAReplyIsCleanedToo()
    {
        using var good = JsonDocument.Parse("{\"id\":\"7-3\",\"type\":\"fished_boots\"}");
        Assert.Equal(new CorrectableStory("7-3", "fished_boots"), SidecarAiClient.CleanCorrectable(good.RootElement));
        using var bad = JsonDocument.Parse("{\"id\":7,\"type\":\"fished_boots\"}");
        Assert.Null(SidecarAiClient.CleanCorrectable(bad.RootElement));
        Assert.Null(SidecarAiClient.CleanCorrectable(null));
    }

    [Fact]
    public void TheGmListShowsBothVersions()
    {
        using var doc = JsonDocument.Parse(
            "{\"town\":\"Britain\",\"text\":\"Aldren fished up 4 old boots in one sitting\",\"id\":\"7-3\",\"hops\":0," +
            "\"was\":\"Aldren fished up 40 old boots in one sitting\"}"
        );
        var line = RumourCommands.ListLine(doc.RootElement);
        Assert.Contains("4 old boots", line);
        Assert.Contains("CORRECTED by its player; before: Aldren fished up 40 old boots", line);
    }

    // ---- held, never dropped ---------------------------------------------------------------------------------------

    private sealed class Brain : HttpMessageHandler
    {
        public string Mode = "new";                         // old (refuses corrections, says nothing) | new | down
        public readonly List<string[]> Batches = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Mode == "down")
            {
                throw new HttpRequestException("connection refused");
            }

            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult());
            var acked = new List<string>();
            var rejected = new List<string>();
            foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
            {
                var bad = e.GetProperty("kind").GetString() == Corrections.Kind &&
                          (Mode == "old" || e.GetProperty("facts").TryGetProperty("said", out _));
                (bad ? rejected : acked).Add(e.GetProperty("event_id").GetString());
            }

            Batches.Add(acked.Concat(rejected).ToArray());
            var body = JsonSerializer.Serialize(new { ok = true, acked, rejected });
            if (Mode == "new")
            {
                body = body[..^1] + ",\"talked_with_topics\":1,\"rumour_corrected\":1}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static AiLedger.LedgerEvent Ev(long seq, string kind, string facts = "{}") =>
        new(AiLedger.EventId(7, seq), 7, seq, Guid.NewGuid(), Guid.NewGuid(), kind, facts, DateTime.UtcNow);

    private const string Fix = "{\"story\":\"7-3\",\"pt\":50000,\"count\":4}";

    [Fact]
    public void ACorrectionRowWaitsForABrainThatAcceptsIt_AndNothingOvertakesIt()
    {
        var outbox = new List<AiLedger.LedgerEvent>
        {
            Ev(1, "visited"), Ev(2, Corrections.Kind, Fix), Ev(3, Corrections.Kind, Fix), Ev(4, "visited"),
            Ev(5, "talked_with", "{\"lines\":3,\"topics\":[\"food\"]}"), Ev(6, Corrections.Kind, Fix)
        };
        Assert.Equal(1, AiLedger.SendableCount(outbox, 50, true, false));    // stops BEFORE the first correction row
        Assert.Equal(3, AiLedger.SendableCount(outbox, 50, true, true));     // ordinary, then corrections - and nothing after them
        outbox.RemoveRange(0, 3);
        Assert.Equal(2, AiLedger.SendableCount(outbox, 50, true, true));     // ordinary, then the topic row - the correction waits:
        outbox.RemoveRange(0, 2);                                            // the two kinds of special row never share a batch
        Assert.Equal(1, AiLedger.SendableCount(outbox, 50, true, true));
        Assert.Equal(0, AiLedger.SendableCount(outbox, 50, true, false));
    }

    [Fact]
    public async Task ABrainReplacedByAnOlderOneDoesNotCostTheCorrection()
    {
        var brain = new Brain();
        AiLedger.SetHttpForTests(brain);
        AiLedger.AddForTests(Ev(1, "visited"));
        AiLedger.AddForTests(Ev(2, Corrections.Kind, Fix));
        AiLedger.AddForTests(Ev(3, "visited"));
        AiLedger.SetCorrectionsIngestForTests(true);          // a new brain confirmed earlier...
        brain.Mode = "old";                                   // ...and was rolled back while the shard ran

        await AiLedger.SendForTests();                        // [visited, correction] goes out; the old brain refuses the correction
        Assert.False(AiLedger.CorrectionsIngest);
        Assert.Equal([Corrections.Kind, "visited"], AiLedger.OutboxForTests.Select(e => e.Kind).ToArray());   // KEPT, in order
        Assert.False(Corrections.Ready);                      // and no new correction is taken meanwhile

        await AiLedger.SendForTests();                        // held: an empty batch only asks again
        Assert.Empty(brain.Batches[^1]);
        Assert.Equal(2, AiLedger.OutboxForTests.Count);

        brain.Mode = "down";
        await AiLedger.SendForTests();
        Assert.Equal(2, AiLedger.OutboxForTests.Count);

        brain.Mode = "new";                                   // the right brain is back
        await AiLedger.SendForTests();                        // the empty probe finds out
        Assert.True(AiLedger.CorrectionsIngest);
        await AiLedger.SendForTests();                        // the correction goes - alone
        await AiLedger.SendForTests();                        // then what waited behind it
        Assert.Empty(AiLedger.OutboxForTests);
        Assert.Equal([AiLedger.EventId(7, 2)], brain.Batches[^2]);
    }

    [Fact]
    public async Task ACapableBrainsRefusalIsStillARefusal()
    {
        var brain = new Brain();
        AiLedger.SetHttpForTests(brain);
        AiLedger.SetCorrectionsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, Corrections.Kind, "{\"story\":\"7-3\",\"pt\":1,\"count\":4,\"said\":\"it was four\"}"));
        await AiLedger.SendForTests();
        Assert.Empty(AiLedger.OutboxForTests);                // a genuinely bad row is dropped, as any refused row is
    }

    [Fact]
    public void AHeldCorrectionSurvivesASave()
    {
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        Assert.NotNull(Corrections.Commit(npc, p, ToldThenAnswered(npc, p), Candidate()));
        var before = AiLedger.OutboxForTests.Single(e => e.Kind == Corrections.Kind);
        var copy = AiLedger.RoundTripForTests();
        var after = copy.Single(e => e.Kind == Corrections.Kind);
        Assert.Equal((before.EventId, before.NpcId, before.CharId, before.FactsJson), (after.EventId, after.NpcId, after.CharId, after.FactsJson));
    }
}
