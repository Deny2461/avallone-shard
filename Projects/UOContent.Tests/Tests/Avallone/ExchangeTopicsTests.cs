using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Named;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Stage A3 step 2 (2026-11-10, step 2 design v2 + Codex design R1/R2): the brain's answer to an exchange step may carry
// CANDIDATE subjects. The shard commits them only when that line is really spoken (S2-D2), writes them on the
// talked_with fact only when the brain's ingest has confirmed it accepts them, and otherwise writes the five-key step-1
// row byte for byte. A queued row that carries topics is held, never sent to an ingest that has not confirmed.
// The model and the ledger are fakes.
[Collection("Sequential UOContent Tests")]
public class ExchangeTopicsTests : IDisposable
{
    private const string Opener = "Good day to you. And you, Elspeth - you look road-weary.";
    private static readonly Point3D Here = new(2110, 1500, 0);

    private readonly List<Mobile> _made = [];
    private readonly List<(Guid Npc, string Facts)> _recorded = [];
    private readonly Client _client = new();
    private readonly IAiNpcClient _oldClient = AiGateway.Client;
    private long _now = 1_000_000;

    private sealed class Client : IAiNpcClient
    {
        public readonly List<AiRequest> Steps = [];
        public Func<AiRequest, AiReply> Answer = r => AiReply.Success($"line {r.Exchange.Step + 1}", "test");
        public TaskCompletionSource<AiReply> HoldStep2;
        public string Name => "steps";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            if (request.Mode != "exchange")
            {
                return Task.FromResult(AiReply.Success("", "test"));
            }

            Steps.Add(request);
            if (HoldStep2 != null && request.Exchange.Step == 1)
            {
                var hold = HoldStep2;
                token.Register(() => hold.TrySetCanceled());
                return hold.Task;
            }

            return Task.FromResult(Answer(request));
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    public ExchangeTopicsTests()
    {
        AiGateway.ResetForTests();
        AiGateway.Client = _client;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => _now;
        NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 3;
        Exchange.Speak = (_, _) => { };
        Exchange.Online = _ => true;
        Exchange.NpcRest = TimeSpan.Zero;
        Exchange.RepliesBetween = 0;
        Exchange.Recorder = (npc, _, _, facts) =>
        {
            _recorded.Add((npc, JsonSerializer.Serialize(facts)));
            return true;
        };
        Exchange.Today = () => "2026-11-10";
        Exchange.EpochNow = () => 7;
        Exchange.MemoryClock = () => 79956.5;
        Exchange.TopicsIngestOk = () => true;
        Exchange.SetMemoryForTests(true);
    }

    public void Dispose()
    {
        Exchange.ResetForTests(false);
        NamedNpcs.ResetForTests(false);
        AiGateway.ResetForTests();
        AiGateway.SetEnabled(false, false);
        AiGateway.Client = _oldClient;
        AiGateway.Clock = () => Core.TickCount;
        foreach (var m in _made)
        {
            if (!m.Deleted)
            {
                m.Delete();
            }
        }
    }

    private (PlayerMobile P, Provisioner Osric, ElspethVale Elspeth) Scene()
    {
        var p = new PlayerMobile { Name = "Wren" };
        p.DefaultMobileInit();
        p.MoveToWorld(Here, Map.Felucca);
        var v = new Provisioner { Name = "Osric" };
        v.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(Here.X + 1, Here.Y, Here.Z), Map.Felucca);
        e.SetControlMaster(p);
        _made.AddRange([p, v, e]);
        return (p, v, e);
    }

    private bool Reply(Mobile npc, PlayerMobile p)
    {
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), Opener, false, Exchange.RevisionOf(p));
        _now += 60_000;
        return Exchange.Started > before;
    }

    // What each step's answer carries: null = the field is absent.
    private void Steps(params string[][] topics) =>
        _client.Answer = r => AiReply.Success($"line {r.Exchange.Step + 1}", "test") with { Topics = topics[r.Exchange.Step] };

    private static string Row(string otherKind, Guid other, int lines, string topics = null) =>
        $"{{\"day\":\"2026-11-10\",\"mpt\":79956.5,\"other_kind\":\"{otherKind}\",\"other_id\":\"{other}\",\"lines\":{lines}" +
        (topics == null ? "}" : $",\"topics\":{topics}}}");

    [Fact]
    public void TheSubjectsOfTheLastSpokenLineAreStoredOnBothRows()
    {
        var (p, osric, e) = Scene();
        Steps(["road"], ["road", "weather"], ["weather", "sea"]);

        Assert.True(Reply(osric, p));

        Assert.Equal(2, _recorded.Count);
        Assert.Equal(Row("townsperson", AiIdentity.GetOrCreate(osric), 3, "[\"weather\",\"sea\"]"), _recorded[0].Facts);
        Assert.Equal(Row("companion", e.AiId, 3, "[\"weather\",\"sea\"]"), _recorded[1].Facts);
        Assert.Equal((2, 2), (Exchange.TalksRecorded, Exchange.TalksWithTopics));
    }

    [Fact]
    public void AGoodAnswerThatTheLastCheckThrowsAwayCommitsNothing()
    {
        // Codex S2-D2: the brain answered step 2 perfectly well - but by then Osric had walked off. That line is never
        // said, so its subjects must not be attached to the one line that was.
        var (p, osric, _) = Scene();
        _client.Answer = r =>
        {
            if (r.Exchange.Step == 1)
            {
                osric.MoveToWorld(new Point3D(Here.X - 40, Here.Y, Here.Z), Map.Felucca);
                return AiReply.Success("About the tide and the wizard, then.", "test") with { Topics = ["sea", "magic"] };
            }

            return AiReply.Success("The road was long.", "test") with { Topics = ["road"] };
        };

        Assert.True(Reply(osric, p));

        Assert.False(Exchange.Running);
        Assert.Equal(2, _client.Steps.Count);                   // the second call WAS made and answered
        Assert.Equal(1, Exchange.LinesSaid);
        Assert.Equal(2, _recorded.Count);
        Assert.All(_recorded, r => Assert.EndsWith("\"lines\":1,\"topics\":[\"road\"]}", r.Facts));
    }

    [Fact]
    public void AFailedStepLeavesWhatWasCommitted()
    {
        var (p, osric, _) = Scene();
        _client.Answer = r => r.Exchange.Step == 0
            ? AiReply.Success("The road was long.", "test") with { Topics = ["road"] }
            : AiReply.Fail(AiOutcome.Failed, "refused (decency)");

        Assert.True(Reply(osric, p));

        Assert.All(_recorded, r => Assert.EndsWith("\"lines\":1,\"topics\":[\"road\"]}", r.Facts));
    }

    [Fact]
    public void CutOffWhileACallIsOutKeepsWhatWasCommitted()
    {
        var (p, osric, _) = Scene();
        Steps(["road"], ["sea"], ["sea"]);
        _client.HoldStep2 = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        Assert.True(Exchange.Running);
        Exchange.OnSpeech(new SpeechEventArgs(p, "hello there", MessageType.Regular, 0x3B2, []));

        Assert.False(Exchange.Running);
        Assert.Equal(2, _recorded.Count);
        Assert.All(_recorded, r => Assert.EndsWith("\"lines\":1,\"topics\":[\"road\"]}", r.Facts));
    }

    [Fact]
    public void AnAnswerWithoutTheFieldKeepsAndAnEmptyListClears()
    {
        var (p, osric, e) = Scene();
        Steps(["road"], null, null);                            // topics switched off part-way: what was true stays
        Assert.True(Reply(osric, p));
        Assert.All(_recorded, r => Assert.EndsWith("\"lines\":3,\"topics\":[\"road\"]}", r.Facts));

        _recorded.Clear();
        Exchange.ResetForTests(true);                           // a fresh day's worth of state, same rig
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 3;
        Exchange.Speak = (_, _) => { };
        Exchange.Online = _ => true;
        Exchange.NpcRest = TimeSpan.Zero;
        Exchange.RepliesBetween = 0;
        Exchange.Recorder = (npc, _, _, facts) =>
        {
            _recorded.Add((npc, JsonSerializer.Serialize(facts)));
            return true;
        };
        Exchange.Today = () => "2026-11-10";
        Exchange.EpochNow = () => 7;
        Exchange.MemoryClock = () => 79956.5;
        Exchange.TopicsIngestOk = () => true;
        Exchange.SetMemoryForTests(true);
        Steps(["road"], [], null);                              // "classified, nothing matched" clears; absent then keeps that
        Assert.True(Reply(osric, p));
        Assert.Equal(Row("townsperson", AiIdentity.GetOrCreate(osric), 3), _recorded[0].Facts);
        Assert.Equal(Row("companion", e.AiId, 3), _recorded[1].Facts);
        Assert.Equal(0, Exchange.TalksWithTopics);
    }

    [Fact]
    public void NoSubjectsOrAnUnconfirmedIngestWritesTheStepOneRowByteForByte()
    {
        var (p, osric, e) = Scene();
        Steps(null, null, null);                                // a step-1 brain, or topics off from the start
        Assert.True(Reply(osric, p));
        Assert.Equal(Row("townsperson", AiIdentity.GetOrCreate(osric), 3), _recorded[0].Facts);
        Assert.Equal(Row("companion", e.AiId, 3), _recorded[1].Facts);
    }

    [Fact]
    public void SubjectsAreHeldBackUntilTheIngestHasConfirmedItAcceptsThem()
    {
        var (p, osric, e) = Scene();
        Exchange.TopicsIngestOk = () => false;                  // the brain's memory never said it understands topics
        Steps(["road"], ["road"], ["road", "sea"]);

        Assert.True(Reply(osric, p));

        Assert.Equal(Row("townsperson", AiIdentity.GetOrCreate(osric), 3), _recorded[0].Facts);
        Assert.Equal(Row("companion", e.AiId, 3), _recorded[1].Facts);
        Assert.Equal((2, 0), (Exchange.TalksRecorded, Exchange.TalksWithTopics));   // the talk itself is still recorded
    }

    [Fact]
    public void TheDefaultGateIsTheLedgersOwnFlagAndItStartsUnconfirmed()
    {
        Exchange.ResetForTests(true);
        AiLedger.ResetForTests(7, 10);
        Assert.False(AiLedger.TopicsIngest);
        Assert.False(Exchange.TopicsIngestOk());
        AiLedger.SetTopicsIngestForTests(true);
        Assert.True(Exchange.TopicsIngestOk());
        AiLedger.ResetForTests(7, 10);                          // a restart forgets the confirmation
        Assert.False(Exchange.TopicsIngestOk());
    }

    [Fact]
    public void MemoryOffRecordsNothingWhateverTheBrainSends()
    {
        var (p, osric, _) = Scene();
        Exchange.SetMemoryForTests(false);
        Steps(["road"], ["road"], ["road"]);

        Assert.True(Reply(osric, p));
        Assert.Empty(_recorded);
    }

    [Theory]
    [InlineData("[\"road\",\"weather\"]", "road,weather")]
    [InlineData("[]", "")]
    [InlineData("[\"road\",\"road\",\"sea\",\"magic\"]", "road,sea")]                 // duplicates dropped, at most two
    [InlineData("[\"Road\",\"the road\",\"x1\",\"\",5,null,true,[\"sea\"],{\"a\":1},\"sea\"]", "sea")]
    [InlineData("[\"abcdefghijklmnopq\",\"abcdefghijklmnop\"]", "abcdefghijklmnop")]   // 17 letters no, 16 yes
    [InlineData("\"road\"", null)]
    [InlineData("{\"road\":1}", null)]
    [InlineData("5", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    public void OddMetadataIsCleanedNeverThrown(string json, string want)
    {
        using var doc = JsonDocument.Parse(json);
        var got = SidecarAiClient.CleanTopics(doc.RootElement);
        Assert.Equal(want, got == null ? null : string.Join(",", got));
    }

    [Fact]
    public void AMissingFieldIsNotOffered() => Assert.Null(SidecarAiClient.CleanTopics(null));

    [Fact]
    public void ARowWithTopicsIsNeverSentToAnIngestThatHasNotConfirmed()
    {
        static AiLedger.LedgerEvent Ev(long seq, string kind, string facts) =>
            new(AiLedger.EventId(7, seq), 7, seq, Guid.NewGuid(), Guid.NewGuid(), kind, facts, DateTime.UtcNow);

        var plain = "{\"day\":\"2026-11-10\",\"mpt\":1,\"other_kind\":\"companion\",\"other_id\":\"x\",\"lines\":3}";
        var withTopics = plain[..^1] + ",\"topics\":[\"road\"]}";
        Assert.False(AiLedger.CarriesTopics(Ev(1, "talked_with", plain)));
        Assert.True(AiLedger.CarriesTopics(Ev(1, "talked_with", withTopics)));
        Assert.False(AiLedger.CarriesTopics(Ev(1, "conversation", "{\"topics\":\"the road\"}")));   // another kind's own field

        var box = new List<AiLedger.LedgerEvent>
        {
            Ev(1, "traded", "{}"), Ev(2, "talked_with", plain), Ev(3, "talked_with", withTopics), Ev(4, "traded", "{}")
        };
        Assert.Equal(3, AiLedger.SendableCount(box, 50, true));     // S2-C1: no ordinary row rides behind a topic row
        Assert.Equal(2, AiLedger.SendableCount(box, 50, false));    // up to the held row; nothing overtakes it
        Assert.Equal(1, AiLedger.SendableCount(box, 1, false));
        box.RemoveRange(0, 2);
        Assert.Equal(0, AiLedger.SendableCount(box, 50, false));    // held at the head: an empty batch, nothing dropped
        Assert.Equal(2, box.Count);
        Assert.Equal(1, AiLedger.SendableCount(box, 50, true));     // and it goes out once a brain confirms - by itself (S2-C1):
        box.RemoveAt(0);                                            // the ordinary row behind it follows in the next batch
        Assert.Equal(1, AiLedger.SendableCount(box, 50, true));
    }

    // ---- play test 2 (2026-11-10): the request says when the companion cannot answer ------------------------------------
    // the owner, in game, to an NPC who had just greeted Elspeth while the pacing rule kept her silent
    // The shard now tells the brain up front (party.no_answer), and leaves the key out whenever she could answer.

    [Fact]
    public void ThePartySaysWhenThePacingRuleWillKeepTheCompanionSilent()
    {
        var (p, osric, _) = Scene();
        Exchange.RepliesBetween = 1;

        var free = TravelParty.For(osric, p);
        Assert.Null(free.NoAnswer);                                         // a player's first reply may start one
        Assert.DoesNotContain("no_answer", JsonSerializer.Serialize(free)); // absent, not false: the request is unchanged

        Assert.True(Reply(osric, p));                                       // an exchange ran: 0 of 1 replies since
        var held = TravelParty.For(osric, p);
        Assert.True(held.NoAnswer);
        Assert.Contains("\"no_answer\":true", JsonSerializer.Serialize(held));

        Assert.False(Reply(osric, p));                                      // the reply the rule asks for; none started
        Assert.Null(TravelParty.For(osric, p).NoAnswer);                    // 1 of 1: she can answer again
    }

    [Fact]
    public void AnNpcStillRestingFromAnExchangeIsToldSoButAnotherIsNot()
    {
        var (p, osric, _) = Scene();
        var hob = new Provisioner { Name = "Hob" };
        hob.MoveToWorld(new Point3D(Here.X, Here.Y + 1, Here.Z), Map.Felucca);
        _made.Add(hob);
        Exchange.NpcRest = TimeSpan.FromMinutes(4);

        Assert.True(Reply(osric, p));                                       // books Osric's rest; the clock moves 1 minute

        Assert.True(TravelParty.For(osric, p).NoAnswer);
        Assert.Null(TravelParty.For(hob, p).NoAnswer);
        _now += 4 * 60_000;
        Assert.Null(TravelParty.For(osric, p).NoAnswer);                    // rested
    }

    [Fact]
    public void WithExchangesOffThePartyNeverCarriesTheKey()
    {
        var (p, osric, _) = Scene();
        Exchange.ResetForTests(false);
        Exchange.RepliesBetween = 5;

        Assert.Null(TravelParty.For(osric, p).NoAnswer);
    }
}
