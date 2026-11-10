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

// Stage A3 (2026-11-10, design v2 + Codex design R2): when an exchange said a line, each speaker gets one ledger fact
// "talked_with" - who, the day, the memory clock, how many lines. Never a name, never a word that was said. The switch
// avallone.ai.npcMemory is off by default: off means no fact, no read request, no companion id - the requests are exactly
// Stage A2's. The model and the ledger are fakes.
[Collection("Sequential UOContent Tests")]
public class ExchangeMemoryTests : IDisposable
{
    private const string Opener = "Good day to you. And you, Elspeth - you look road-weary.";
    private static readonly Point3D Here = new(2110, 1500, 0);

    private readonly List<Mobile> _made = [];
    private readonly List<(Guid Npc, Mobile Owner, string Kind, string Facts)> _recorded = [];
    private readonly Client _client = new();
    private readonly IAiNpcClient _oldClient = AiGateway.Client;
    private long _now = 1_000_000;
    private string _today = "2026-11-10";
    private Func<Guid, bool> _accept = _ => true;

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

    public ExchangeMemoryTests()
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
        Exchange.Recorder = (npc, owner, kind, facts) =>
        {
            if (!_accept(npc))
            {
                return false;
            }

            _recorded.Add((npc, owner, kind, JsonSerializer.Serialize(facts)));
            return true;
        };
        Exchange.Today = () => _today;
        Exchange.EpochNow = () => 7;
        Exchange.MemoryClock = () => 79956.5;
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

    private PlayerMobile Player(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(Here, Map.Felucca);
        _made.Add(m);
        return m;
    }

    private Provisioner Vendor(string name)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(v);
        return v;
    }

    private ElspethVale Companion(Mobile owner)
    {
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(Here.X + 1, Here.Y, Here.Z), Map.Felucca);
        e.SetControlMaster(owner);
        _made.Add(e);
        return e;
    }

    private bool Reply(Mobile npc, PlayerMobile p)
    {
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), Opener, false, Exchange.RevisionOf(p));
        _now += 60_000;
        return Exchange.Started > before;
    }

    private static string Facts(string day, string otherKind, Guid other, int lines) =>
        $"{{\"day\":\"{day}\",\"mpt\":79956.5,\"other_kind\":\"{otherKind}\",\"other_id\":\"{other}\",\"lines\":{lines}}}";

    [Fact]
    public void SwitchOffIsExactlyStageA2()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.False(Exchange.MemoryOn);                        // the default
        var party = TravelParty.For(osric, p);
        Assert.Null(party.Known);
        Assert.Null(party.Companions[0].Id);
        Assert.Null(Exchange.KnownNow(Guid.NewGuid()));

        Assert.True(Reply(osric, p));
        Assert.Equal(3, _client.Steps.Count);
        Assert.All(_client.Steps, r => Assert.Null(r.Exchange.Known));
        Assert.Empty(_recorded);
        Assert.Equal((0, 0), (Exchange.TalksRecorded, Exchange.TalksNotRecorded));
    }

    [Fact]
    public void AnExchangeRecordsOneFactForEachSpeaker()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        var osricId = AiIdentity.GetOrCreate(osric);
        Exchange.SetMemoryForTests(true);

        Assert.True(Reply(osric, p));

        Assert.Equal(2, _recorded.Count);
        Assert.All(_recorded, r => Assert.Equal(("talked_with", (Mobile)p), (r.Kind, r.Owner)));   // the same owner in both
        Assert.Equal((e.AiId, Facts("2026-11-10", "townsperson", osricId, 3)), (_recorded[0].Npc, _recorded[0].Facts));
        Assert.Equal((osricId, Facts("2026-11-10", "companion", e.AiId, 3)), (_recorded[1].Npc, _recorded[1].Facts));
        Assert.DoesNotContain("Elspeth", _recorded[1].Facts);   // no name, and nothing that was said
        Assert.DoesNotContain("line", _recorded[0].Facts.Replace("\"lines\"", ""));
        Assert.Equal((2, 0), (Exchange.TalksRecorded, Exchange.TalksNotRecorded));
    }

    [Fact]
    public void TheDefaultWriterIsTheOneThatNeverCountsTowardSecretsOrPersonality()
    {
        Exchange.ResetForTests(true);
        Assert.Equal((Func<Guid, Mobile, string, object, bool>)AiLedger.TryRecord, Exchange.Recorder);
    }

    [Fact]
    public void OncePerPairPerDay()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.SetMemoryForTests(true);

        Assert.True(Reply(osric, p));
        Assert.True(Reply(osric, p));                           // the same evening
        Assert.Equal(2, _recorded.Count);

        _today = "2026-11-11";
        Assert.True(Reply(osric, p));
        Assert.Equal(4, _recorded.Count);
        Assert.Contains("\"day\":\"2026-11-11\"", _recorded[3].Facts);
    }

    [Fact]
    public void NoLineSaidMeansNoFact()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.SetMemoryForTests(true);
        _client.Answer = _ => AiReply.Fail(AiOutcome.Failed, "nothing to add");

        Assert.True(Reply(osric, p));
        Assert.False(Exchange.Running);
        Assert.Empty(_recorded);
    }

    [Fact]
    public void CutOffAfterALineStillCounts()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        Exchange.SetMemoryForTests(true);
        _client.HoldStep2 = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        Assert.True(Exchange.Running);                          // line 1 said, step 2's call is out
        Exchange.OnSpeech(new SpeechEventArgs(p, "hello there", MessageType.Regular, 0x3B2, []));

        Assert.False(Exchange.Running);
        Assert.Equal(2, _recorded.Count);
        Assert.Equal(Facts("2026-11-10", "townsperson", AiIdentity.GetOrCreate(osric), 1), _recorded[0].Facts);
        Assert.Equal(e.AiId, _recorded[0].Npc);
    }

    [Fact]
    public void EachDirectionIsMarkedByItself()
    {
        // Codex design R2: the outbox fills between the two writes. The first is recorded and marked; the second is
        // not marked, so the next exchange that day records it - and only it.
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        var osricId = AiIdentity.GetOrCreate(osric);
        Exchange.SetMemoryForTests(true);
        _accept = npc => npc == e.AiId;

        Assert.True(Reply(osric, p));
        Assert.Single(_recorded);
        Assert.Equal(e.AiId, _recorded[0].Npc);
        Assert.Equal((1, 1), (Exchange.TalksRecorded, Exchange.TalksNotRecorded));

        _accept = _ => true;
        Assert.True(Reply(osric, p));
        Assert.Equal(2, _recorded.Count);
        Assert.Equal(osricId, _recorded[1].Npc);
        Assert.Equal((2, 1), (Exchange.TalksRecorded, Exchange.TalksNotRecorded));
    }

    [Fact]
    public void AWriterThatThrowsCostsTheFactNotTheExchange()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.SetMemoryForTests(true);
        Exchange.Recorder = (_, _, _, _) => throw new InvalidOperationException("ledger down");

        Assert.True(Reply(osric, p));
        Assert.False(Exchange.Running);
        Assert.Equal((1, "complete"), (Exchange.Completed, Exchange.LastEnd));
        Assert.Equal(2, Exchange.TalksNotRecorded);
        Assert.True(Reply(osric, p));                           // and the next one starts as usual
    }

    [Fact]
    public void EveryStepAsksAboutTheOtherSpeaker()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        var osricId = AiIdentity.GetOrCreate(osric);
        Exchange.SetMemoryForTests(true);

        Assert.True(Reply(osric, p));

        Assert.Equal(3, _client.Steps.Count);
        Assert.Equal(new KnownAsk(7, 79956.5, osricId.ToString()), _client.Steps[0].Exchange.Known);   // Elspeth speaks
        Assert.Equal(new KnownAsk(7, 79956.5, e.AiId.ToString()), _client.Steps[1].Exchange.Known);    // Osric speaks
        Assert.Equal(e.AiId, _client.Steps[0].NpcId);
        Assert.Equal(Guid.Empty, _client.Steps[0].SubjectId);   // still no player id in an exchange step
    }

    [Fact]
    public void NoReadRequestWithoutAnEpochOrAClock()
    {
        Exchange.SetMemoryForTests(true);
        var other = Guid.NewGuid();
        Assert.NotNull(Exchange.KnownNow(other));
        Assert.Null(Exchange.KnownNow(Guid.Empty));
        Exchange.EpochNow = () => 0;
        Assert.Null(Exchange.KnownNow(other));
        Exchange.EpochNow = () => 7;
        Exchange.MemoryClock = () => double.NaN;
        Assert.Null(Exchange.KnownNow(other));
        Exchange.MemoryClock = () => -1;
        Assert.Null(Exchange.KnownNow(other));
    }

    [Fact]
    public void ThePartyCarriesTheCompanionsIdOnlyWhenTheSwitchIsOn()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        Exchange.SetMemoryForTests(true);

        var party = TravelParty.For(osric, p);
        Assert.Equal(new KnownAsk(7, 79956.5), party.Known);    // the other id rides on the companion
        Assert.Equal(e.AiId.ToString(), party.Companions[0].Id);

        Exchange.EpochNow = () => 0;                            // memory not started: exactly A1's request
        party = TravelParty.For(osric, p);
        Assert.Null(party.Known);
        Assert.Null(party.Companions[0].Id);
    }

    [Fact]
    public void WireFormat()
    {
        var snake = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var id = "55555555-5555-4555-8555-555555555555";
        var party = new TravelParty.PartyAsk(
            [new TravelParty.PartyCompanion("Elspeth Vale", "the field healer", id)], [], false, null, new KnownAsk(7, 79956.5)
        );
        Assert.Equal(
            "{\"companions\":[{\"name\":\"Elspeth Vale\",\"title\":\"the field healer\",\"id\":\"" + id +
            "\"}],\"pets\":[],\"nudge\":false,\"roster\":null,\"known\":{\"epoch\":7,\"mpt\":79956.5}}",
            JsonSerializer.Serialize(party, snake)
        );
        Assert.Equal(
            "{\"epoch\":7,\"mpt\":79956.5,\"other_id\":\"" + id + "\"}",
            JsonSerializer.Serialize(new KnownAsk(7, 79956.5, id), snake)
        );
        // switch off: neither field is in the request at all
        var off = JsonSerializer.Serialize(
            new ExchangeAsk("x1", 0, new ExchangeVoice("companion", "Elspeth Vale", ""), new ExchangeVoice("townsperson", "Osric", ""), "", [], []),
            snake
        );
        Assert.DoesNotContain("known", off);
        Assert.DoesNotContain(
            "\"id\"", JsonSerializer.Serialize(new TravelParty.PartyAsk([new TravelParty.PartyCompanion("A B", "")], [], false), snake)
        );
    }

    // ---- the owner's A3 play test, 2026-11-10 -----------------------------------------------------------------------------

    [Fact]
    public void AClosingQuestionGetsOneAnswerAndOnlyOne()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        Exchange.PickLength = () => 2;
        _client.Answer = r => AiReply.Success(r.Exchange.Step >= 1 ? "Is that a new cloak you are wearing?" : "Well enough.", "test");

        Assert.True(Reply(osric, p));

        Assert.Equal(3, _client.Steps.Count);                   // the drawn two, plus one answer to the closing question
        Assert.Equal(new[] { false, true, true }, new[] { _client.Steps[0].Exchange.Last, _client.Steps[1].Exchange.Last, _client.Steps[2].Exchange.Last });
        Assert.Equal(e.AiId, _client.Steps[2].NpcId);           // the one who was asked answers
        Assert.Equal("complete", Exchange.LastEnd);             // her answer was a question too: no second extension
    }

    [Fact]
    public void AClosingStatementEndsItAndTheCapStillHolds()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.PickLength = () => 2;

        Assert.True(Reply(osric, p));
        Assert.Equal(2, _client.Steps.Count);                   // "line 2" is no question

        Exchange.MaxLines = 2;                                  // at the cap a closing question earns nothing more
        _client.Steps.Clear();
        _client.Answer = _ => AiReply.Success("And you?", "test");
        Assert.True(Reply(osric, p));
        Assert.Equal(2, _client.Steps.Count);
    }

    [Theory]
    [InlineData(12, true)]    // twelve steps from the player: still in earshot, the exchange goes on
    [InlineData(13, false)]   // thirteen: out of earshot, it ends there
    public void EarshotIsTwelveSteps(int steps, bool goesOn)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Assert.Equal(12, Exchange.Range);
        _client.HoldStep2 = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        // (not through Reply(): that moves the clock on a minute, past the exchange deadline)
        Exchange.AfterReply(osric, p, TravelParty.For(osric, p), Opener, false, Exchange.RevisionOf(p));
        Assert.True(Exchange.Running);                          // line 1 said; Osric's line is being written
        osric.MoveToWorld(new Point3D(Here.X - steps, Here.Y, Here.Z), Map.Felucca);   // he wanders off meanwhile
        _client.HoldStep2.SetResult(AiReply.Success("line 2", "test"));

        Assert.True(SpinWait.SpinUntil(() => !Exchange.Running, 3000));
        Assert.Equal(goesOn ? "complete" : "Osric is out of earshot", Exchange.LastEnd);
        Assert.Equal(goesOn ? 3 : 2, _client.Steps.Count);
    }

    [Fact]
    public void ThePauseIsTwoToFourAndAHalfSeconds()
    {
        Exchange.ResetForTests(true);
        Assert.Equal(TimeSpan.FromSeconds(2), Exchange.PauseFor("", 0));
        Assert.Equal(TimeSpan.FromSeconds(3), Exchange.PauseFor("", 1));
        Assert.Equal(TimeSpan.FromSeconds(3.5), Exchange.PauseFor(new string('a', 100), 0.5));   // 2 + 0.5 + 100 x 10 ms
        Assert.Equal(TimeSpan.FromSeconds(4.5), Exchange.PauseFor(new string('a', 400), 1));     // the cap
    }
}
