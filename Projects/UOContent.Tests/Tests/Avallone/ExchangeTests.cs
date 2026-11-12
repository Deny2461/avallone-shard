using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Named;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Stage A2 (2026-11-09): the companion <-> NPC exchange owner. Codex Stage A design review F1 (a public-only request),
// F2 (the player's words always win - during the pause, while the call is out, and through the gateway) and F5 (at most
// three lines, one attempt each, admission rules). The model is a fake; pauses are instant unless a test holds one.
[Collection("Sequential UOContent Tests")]
public class ExchangeTests : IDisposable
{
    private const string Opener = "Good day to you. And you, Elspeth - you look road-weary.";
    private static readonly Point3D Here = new(2100, 1500, 0);

    private readonly List<Mobile> _made = [];
    private readonly List<(Mobile Who, string Text)> _said = [];
    private readonly StepClient _client = new();
    private readonly IAiNpcClient _oldClient = AiGateway.Client;
    private long _now = 1_000_000;

    private sealed class StepClient : IAiNpcClient
    {
        public readonly List<AiRequest> Steps = [];
        public Func<AiRequest, AiReply> Answer = r => AiReply.Success($"line {r.Exchange.Step + 1}", "test");
        public TaskCompletionSource<AiReply> Hold;          // an exchange step waits on this (or on its cancellation)
        public TaskCompletionSource<AiReply> HoldSummary;   // a visit summary waits on this
        public readonly List<AiRequest> Cancels = [];       // "this exchange is over" messages to the brain
        public TaskCompletionSource<AiReply> HoldCancel;    // the brain's answer to one waits on this (or on its time limit)
        public int Cancelled;
        public string Name => "steps";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            if (request.Mode == "summarize" && HoldSummary != null)
            {
                return HoldSummary.Task;
            }

            if (request.Mode == "exchange_cancel")
            {
                Cancels.Add(request);
                if (HoldCancel == null)
                {
                    return Task.FromResult(AiReply.Success("", "test"));
                }

                var wait = HoldCancel;
                token.Register(() => wait.TrySetCanceled());
                return wait.Task;
            }

            if (request.Mode != "exchange")
            {
                return Task.FromResult(AiReply.Success("Welcome back.", "test"));
            }

            Steps.Add(request);
            if (Hold == null)
            {
                return Task.FromResult(Answer(request));
            }

            var hold = Hold;
            token.Register(
                () =>
                {
                    if (hold.TrySetCanceled())
                    {
                        Interlocked.Increment(ref Cancelled);
                    }
                }
            );
            return hold.Task;
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    public ExchangeTests()
    {
        AiGateway.ResetForTests();
        AiGateway.Client = _client;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => _now;
        NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 3;                          // most tests are about a three-line exchange
        Exchange.Speak = (m, t) =>
        {
            lock (_said)
            {
                _said.Add((m, t));
            }
        };
        Exchange.Online = _ => true;
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

    private PlayerMobile Player(string name, int dx = 0)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(Here.X + dx, Here.Y, Here.Z), Map.Felucca);
        _made.Add(m);
        return m;
    }

    private Provisioner Vendor(string name, int dx = -1)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(Here.X + dx, Here.Y, Here.Z), Map.Felucca);
        _made.Add(v);
        return v;
    }

    private ElspethVale Companion(Mobile owner, int dx = 1)
    {
        var e = new ElspethVale();
        e.MoveToWorld(new Point3D(Here.X + dx, Here.Y, Here.Z), Map.Felucca);
        e.SetControlMaster(owner);
        _made.Add(e);
        return e;
    }

    // As if `npc` had just SAID `spoken` to `p`. True when an exchange started.
    // `openingRevision`: the player's speech count when the turn this reply answers began (default: nothing said since).
    private bool Reply(Mobile npc, PlayerMobile p, string spoken = Opener, bool excluded = false, int? openingRevision = null)
    {
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), spoken, excluded, openingRevision ?? Exchange.RevisionOf(p));
        return Exchange.Started > before;
    }

    // ---- Rumour step C1 (2026-11-11): her one-line reaction to a gossip reply that names her --------------------------
    // Names no other test uses: the request also carries the roster of current players (for the reply check), and a
    // player left over from another test must not look like a leak of this line.
    private const string Gossip = "They say Zorvald Quenby slew a wyvern near Shame. And you, Elspeth - good day to you.";

    private bool Gossiped(Mobile npc, PlayerMobile p, int? sort, string spoken = Gossip)
    {
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), spoken, true, Exchange.RevisionOf(p), sort);   // excluded: a gossip turn
        return Exchange.Started > before;
    }

    [Fact]
    public void AGossipReplyThatNamesHerGetsOneLineAndTheRequestCarriesNothingTheNpcSaid()
    {
        Exchange.SetReactionsForTests(true);
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);

        Assert.True(Gossiped(osric, p, 3));

        Assert.Equal(1, Said);                                   // exactly one line, hers
        Assert.Same(e, _said[0].Who);
        Assert.False(Exchange.Running);
        Assert.Equal((1, 1, 1, "complete"), (Exchange.Started, Exchange.ReactionsStarted, Exchange.Completed, Exchange.LastEnd));
        var r = Assert.Single(_client.Steps);
        Assert.Equal("exchange", r.Mode);
        Assert.Equal(3, r.Exchange.React);
        Assert.Empty(r.Exchange.Transcript);                     // not one word of the NPC's reply
        Assert.Null(r.Exchange.Known);                           // no memory read either
        Assert.True(r.Exchange.Last);
        Assert.Equal(0, r.Exchange.Step);
        Assert.Equal("companion", r.Exchange.Speaker.Kind);
        Assert.Equal("", r.Utterance);
        Assert.Empty(r.History);
        Assert.Null(r.Situation);
        Assert.Null(r.Rumours);
        Assert.Null(r.Party);
        var wire = SidecarAiClient.BodyJson(r, TimeSpan.FromSeconds(6));      // what really goes to the brain
        foreach (var word in new[] { "Zorvald", "Quenby", "wyvern", "Shame", "slew", "good day" })
        {
            Assert.DoesNotContain(word, wire);
        }

        Assert.Contains("\"react\":3", wire);
        Assert.Contains("\"transcript\":[]", wire);
    }

    [Fact]
    public void ControlAnOrdinaryExchangeDoesCarryTheNpcsLineAndNoReactField()
    {
        Exchange.SetReactionsForTests(true);
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.True(Reply(osric, p));
        var wire = SidecarAiClient.BodyJson(_client.Steps[0], TimeSpan.FromSeconds(6));
        Assert.Contains("road-weary", wire);                     // the check above would see the words if they were sent
        Assert.DoesNotContain("\"react\"", wire);                // and an ordinary step is the request an old brain knows
        Assert.Null(_client.Steps[0].Exchange.React);
    }

    [Fact]
    public void NoReactionWithTheSwitchOffOrWithoutASortOrWhenSheIsNotNamed()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.False(Gossiped(osric, p, 3));                     // switch off: a gossip reply is followed by silence, as before
        Exchange.SetReactionsForTests(true);
        Assert.False(Gossiped(osric, p, null));                  // the brain gave no sort
        Assert.False(Gossiped(osric, p, 6));                     // not one of the six
        Assert.False(Gossiped(osric, p, -1));
        Assert.False(Gossiped(osric, p, 2, "They say Zorvald Quenby slew a wyvern near Shame."));   // the line does not name her
        Assert.Equal(0, Said);
        Assert.Empty(_client.Steps);
    }

    [Fact]
    public void OnlyAGossipTurnPassesTheSortOn()
    {
        var gossip = AiReply.Success("x", "t") with { Rumour = new RumourInfo("", "", "", ["1-1"], "news"), ReactSort = 4 };
        Assert.Equal(4, Exchange.ReactSortOf(gossip));
        Assert.Null(Exchange.ReactSortOf(gossip with { Kind = "decency" }));
        Assert.Null(Exchange.ReactSortOf(gossip with { Kind = "shop" }));
        Assert.Null(Exchange.ReactSortOf(gossip with { Conduct = "hateful_language" }));
        Assert.Null(Exchange.ReactSortOf(AiReply.Success("x", "t") with { ReactSort = 4 }));       // no gossip: an ordinary exchange instead
        Assert.Null(Exchange.ReactSortOf(gossip with { ReactSort = null }));
    }

    [Fact]
    public void AReactionIsNeverExtendedAndNeverRecordedAsATalk()
    {
        Exchange.SetReactionsForTests(true);
        Exchange.SetMemoryForTests(true);
        var recorded = 0;
        Exchange.Recorder = (_, _, _, _) =>
        {
            recorded++;
            return true;
        };
        _client.Answer = _ => AiReply.Success("And how is the tanning trade, Osric?", "test") with { Topics = ["trade"] };
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.True(Gossiped(osric, p, 1));

        Assert.Equal(1, Said);                                   // a question, and still nobody is given a second line
        Assert.Single(_client.Steps);
        Assert.Equal(0, recorded);                               // not a "talked with" fact, and no subjects
        Assert.Equal(0, Exchange.TalksRecorded);
    }

    [Fact]
    public void AReactionObeysTheSpacingAndThePlayersWords()
    {
        Exchange.SetReactionsForTests(true);
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Assert.True(Gossiped(osric, p, 0));
        Assert.False(Gossiped(osric, p, 0));                     // straight after: the replies-between rule holds
        Assert.Equal(1, Said);

        Exchange.ResetForTests(true);
        Exchange.SetReactionsForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.Speak = (m, t) =>
        {
            lock (_said)
            {
                _said.Add((m, t));
            }
        };
        Exchange.Online = _ => true;
        _client.Hold = new TaskCompletionSource<AiReply>();      // her call is out ...
        Assert.True(Gossiped(osric, p, 0));
        Exchange.OnSpeech(Line(p, "all follow me"));             // ... and the player speaks
        Assert.False(Exchange.Running);
        _client.Hold.TrySetResult(AiReply.Success("Too late.", "test"));
        Assert.Equal(1, Said);                                   // the late line is never said
    }

    [Theory]
    [InlineData("{\"sort\":0}", 0)]
    [InlineData("{\"sort\":5}", 5)]
    [InlineData("{\"sort\":6}", null)]
    [InlineData("{\"sort\":-1}", null)]
    [InlineData("{\"sort\":\"2\"}", null)]
    [InlineData("{\"sort\":1.5}", null)]
    [InlineData("{\"text\":\"what the NPC said\"}", null)]
    [InlineData("3", null)]
    [InlineData("null", null)]
    [InlineData("[1]", null)]
    public void OnlyANumberFromZeroToFiveIsTakenFromTheBrain(string json, int? want)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(want, SidecarAiClient.CleanReact(doc.RootElement));
        Assert.Null(SidecarAiClient.CleanReact(null));
    }

    private int Said
    {
        get
        {
            lock (_said)
            {
                return _said.Count;
            }
        }
    }

    private static bool Soon(Func<bool> what) => SpinWait.SpinUntil(what, 3000);

    private static SpeechEventArgs Line(Mobile from, string text, MessageType type = MessageType.Regular, params int[] keywords) =>
        new(from, text, type, 0x3B2, keywords);

    [Fact]
    public void ThreeLinesAlternateAndTheRequestIsPublicOnly()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);

        Assert.True(Reply(osric, p));

        Assert.Equal(3, Said);                                  // companion -> NPC -> companion, then it is over
        Assert.Equal(new Mobile[] { e, osric, e }, new[] { _said[0].Who, _said[1].Who, _said[2].Who });
        Assert.Equal(new[] { "line 1", "line 2", "line 3" }, new[] { _said[0].Text, _said[1].Text, _said[2].Text });
        Assert.False(Exchange.Running);
        Assert.Equal((1, 1, "complete"), (Exchange.Started, Exchange.Completed, Exchange.LastEnd));
        Assert.Equal(3, _client.Steps.Count);                   // one call per line, no more

        foreach (var r in _client.Steps)
        {
            Assert.Equal("exchange", r.Mode);
            Assert.Equal(AiPriority.Background, r.Priority);
            Assert.Equal(Guid.Empty, r.SubjectId);              // no player id: nothing to look a memory up by
            Assert.Equal("", r.Utterance);
            Assert.Empty(r.History);
            Assert.Null(r.Situation);
            Assert.Null(r.Actions);
            Assert.Null(r.Secrets);
            Assert.Null(r.RevealSecretId);
            Assert.Null(r.Rumours);
            Assert.Null(r.Named);
            Assert.Null(r.Standing);
            Assert.Null(r.Party);
            Assert.Null(r.Persona);
        }

        var (r0, r1, r2) = (_client.Steps[0], _client.Steps[1], _client.Steps[2]);
        Assert.Equal((0, "companion", "Elspeth Vale", "the field healer"), (r0.Exchange.Step, r0.Exchange.Speaker.Kind, r0.Exchange.Speaker.Name, r0.Exchange.Speaker.Title));
        Assert.Equal(("townsperson", "Osric"), (r0.Exchange.Addressee.Kind, r0.Exchange.Addressee.Name));
        Assert.Equal("wary", r0.Exchange.TrustBand);            // her band toward Wren, for her own prompt
        Assert.Equal((e.AiId, e.PersonaKey), (r0.NpcId, r0.PersonaKey));
        Assert.Null(r0.PersonaVars);
        Assert.Single(r0.Exchange.Transcript);
        Assert.Equal(("Osric", Opener), (r0.Exchange.Transcript[0].Who, r0.Exchange.Transcript[0].Text));

        Assert.Equal((1, "townsperson", "Osric"), (r1.Exchange.Step, r1.Exchange.Speaker.Kind, r1.Exchange.Speaker.Name));
        Assert.Equal("", r1.Exchange.TrustBand);                // never in the townsperson's request
        Assert.Equal("townsfolk", r1.PersonaKey);
        Assert.Equal("Osric", r1.PersonaVars["name"]);
        Assert.Equal(2, r1.Exchange.Transcript.Length);
        Assert.Equal(("Elspeth Vale", "line 1"), (r1.Exchange.Transcript[1].Who, r1.Exchange.Transcript[1].Text));

        Assert.Equal((2, "companion"), (r2.Exchange.Step, r2.Exchange.Speaker.Kind));
        Assert.Equal(3, r2.Exchange.Transcript.Length);
        Assert.Equal("Osric", r2.Exchange.Transcript[2].Who);
    }

    [Fact]
    public void SwitchOffMeansNoExchange()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);

        Exchange.ResetForTests(false);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.Online = _ => true;
        Assert.False(Reply(osric, p));
        Assert.Empty(_client.Steps);

        AiGateway.SetEnabled(false, false);                     // the AI kill switch wins over the exchange switch
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.Online = _ => true;
        Assert.False(Reply(osric, p));
        Assert.Empty(_client.Steps);

        AiGateway.SetEnabled(true, false);                      // control: both on -> it runs
        Exchange.PickLength = () => 3;
        Assert.True(Reply(osric, p));
        Assert.Equal(3, _client.Steps.Count);
    }

    [Fact]
    public void OnlyALineThatNamesThePlayersOwnCompanionStartsOne()
    {
        var p = Player("Wren");
        var other = Player("Cody", 2);
        var osric = Vendor("Osric");
        Companion(p);

        Assert.False(Reply(osric, p, "Good day to you, traveller."));             // names nobody
        Assert.False(Reply(osric, p, "A fine healer you travel with."));          // by title only: no exchange
        Assert.False(Reply(osric, p, Opener, excluded: true));                    // gossip / decency / shop-line turn
        Assert.False(Reply(osric, other, Opener));                                // Cody has no companion of his own
        Assert.Empty(_client.Steps);
        Assert.True(Reply(osric, p));                                             // control
    }

    [Theory]
    [InlineData("Elspeth, follow me", MessageType.Regular, 0)]      // a plain order to the companion
    [InlineData("Osric buy", MessageType.Regular, 0x171)]           // a shop command
    [InlineData("vendor sell", MessageType.Regular, 0x177)]
    [InlineData("bank", MessageType.Regular, 0x2)]
    [InlineData("hmm", MessageType.Whisper, 0)]
    [InlineData("*waves*", MessageType.Emote, 0)]
    public void AnythingThePlayerSaysDuringThePauseEndsIt(string text, MessageType type, int keyword)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var gate = new TaskCompletionSource<bool>();
        Exchange.Pause = _ => gate.Task;

        Assert.True(Reply(osric, p));
        Assert.True(Exchange.Running);                          // waiting out the pause before the first line

        Exchange.OnSpeech(keyword == 0 ? Line(p, text, type) : Line(p, text, type, keyword));

        Assert.False(Exchange.Running);
        Assert.Equal("the player spoke", Exchange.LastEnd);
        gate.SetResult(true);
        Thread.Sleep(100);
        Assert.Empty(_client.Steps);                            // no call was ever made
        Assert.Equal(0, Said);                                  // and no line is said afterwards

        Exchange.Pause = _ => Task.CompletedTask;
        Assert.False(Reply(osric, p));                          // nothing restarts it on the next reply
    }

    [Fact]
    public async Task ThePlayersLineWhileTheCallIsOutCancelsItAndIsAnswered()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        Assert.Single(_client.Steps);                           // the first step's call is out
        Assert.True(Exchange.Running);

        Exchange.OnSpeech(Line(p, "Elspeth, wait here"));
        Assert.False(Exchange.Running);
        Assert.True(Soon(() => _client.Cancelled == 1));        // the call was cancelled, not left to finish

        // Codex F2: her own lease was never held by the exchange - the player's line to her is answered, not "Busy".
        var reply = await AiGateway.RequestAsync(new AiRequest(e.AiId, AiIdentity.GetOrCreate(p), null, "wait here", AiPriority.Companion))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AiOutcome.Ok, reply.Outcome);

        Thread.Sleep(100);
        Assert.Equal(0, Said);                                  // the cancelled step's line is never said
        Assert.Single(_client.Steps);                           // and there was no second step
    }

    [Fact]
    public async Task AConversationRequestTakesTheModelButBackgroundWorkDoesNot()
    {
        var p = Player("Wren");
        var cody = Player("Cody", 2);
        var osric = Vendor("Osric");
        Companion(p);
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Reply(osric, p));

        var plan = await AiGateway.RequestAsync(new AiRequest(Guid.NewGuid(), Guid.NewGuid(), "p", "(deciding)", AiPriority.Background))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(plan.Ok);
        Assert.True(Exchange.Running);                          // control: background work does not end it

        var reply = await AiGateway.RequestAsync(new AiRequest(Guid.NewGuid(), AiIdentity.GetOrCreate(cody), "p", "hello", AiPriority.Conversation))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(reply.Ok);                                  // another player's conversation is answered at once
        Assert.False(Exchange.Running);
        Assert.Equal("a conversation needs the model", Exchange.LastEnd);
        Assert.True(Soon(() => _client.Cancelled == 1));
        Assert.Equal(0, Said);
    }

    [Fact]
    public void TwoPlayersAndOneTownsperson()
    {
        var wren = Player("Wren");
        var cody = Player("Cody", 2);
        var osric = Vendor("Osric");
        Companion(wren);
        Companion(cody, 3);
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, wren));
        Assert.False(Reply(osric, cody));                       // one exchange at a time, shard-wide
        Assert.Equal(wren, Exchange.Current.Player);

        Exchange.NoteSpeech(cody);                              // Cody talking does not end Wren's exchange...
        Assert.True(Exchange.Running);
        Exchange.NoteSpeech(wren);                              // ...Wren's own words do
        Assert.False(Exchange.Running);

        _client.Hold = null;
        Assert.False(Reply(osric, cody));                       // Osric rests after an exchange (booked at its start)
        _now += (long)Exchange.NpcRest.TotalMilliseconds + 1;
        Assert.True(Reply(osric, cody));                        // control: rested -> Cody's companion gets her turn
        Assert.Equal(3, Said);
    }

    public enum Gone
    {
        CompanionOutOfEarshot,
        CompanionHidden,
        CompanionChangedOwner,
        NpcDeleted,
        PlayerLeft,
        SwitchedOff
    }

    [Theory]
    [InlineData(Gone.CompanionOutOfEarshot, true)]
    [InlineData(Gone.CompanionOutOfEarshot, false)]
    [InlineData(Gone.CompanionHidden, true)]
    [InlineData(Gone.CompanionHidden, false)]
    [InlineData(Gone.CompanionChangedOwner, true)]
    [InlineData(Gone.CompanionChangedOwner, false)]
    [InlineData(Gone.NpcDeleted, true)]
    [InlineData(Gone.NpcDeleted, false)]
    [InlineData(Gone.PlayerLeft, true)]
    [InlineData(Gone.PlayerLeft, false)]
    [InlineData(Gone.SwitchedOff, true)]
    [InlineData(Gone.SwitchedOff, false)]
    public void AParticipantLeavingEndsItBeforeTheNextCallOrLine(Gone what, bool whileCallIsOut)
    {
        var p = Player("Wren");
        var cody = Player("Cody", 2);
        var osric = Vendor("Osric");
        var e = Companion(p);
        var gate = new TaskCompletionSource<bool>();
        if (whileCallIsOut)
        {
            _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else
        {
            Exchange.Pause = _ => gate.Task;
        }

        Assert.True(Reply(osric, p));
        Assert.Equal(whileCallIsOut ? 1 : 0, _client.Steps.Count);

        switch (what)
        {
            case Gone.CompanionOutOfEarshot:
                e.MoveToWorld(new Point3D(Here.X + 30, Here.Y, Here.Z), Map.Felucca);
                break;
            case Gone.CompanionHidden:
                e.Hidden = true;
                break;
            case Gone.CompanionChangedOwner:
                e.ControlMaster = cody;
                break;
            case Gone.NpcDeleted:
                osric.Delete();
                break;
            case Gone.PlayerLeft:
                Exchange.Online = _ => false;
                break;
            case Gone.SwitchedOff:
                AiGateway.SetEnabled(false, false);
                break;
        }

        if (whileCallIsOut)
        {
            _client.Hold.SetResult(AiReply.Success("Too late.", "test"));   // the line arrives - and must not be said
        }
        else
        {
            gate.SetResult(true);
        }

        Assert.True(Soon(() => !Exchange.Running));
        Thread.Sleep(50);
        Assert.Equal(0, Said);
        Assert.Equal(whileCallIsOut ? 1 : 0, _client.Steps.Count);
        Assert.NotEqual("complete", Exchange.LastEnd);
    }

    [Fact]
    public void AtMostOneInThreeRepliesAndTheNpcRests()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var nell = Vendor("Nell", -2);
        Companion(p);

        Assert.True(Reply(osric, p));                           // 1st exchange
        Assert.False(Reply(osric, p));                          // 0 replies since
        Assert.False(Reply(nell, p));                           // 1 reply since - a different NPC makes no difference
        Assert.False(Reply(osric, p));                          // 2 since, but Osric is resting
        Assert.True(Reply(nell, p));                            // Nell is not: 2nd exchange
        Assert.Equal(2, Exchange.Started);

        _now += (long)Exchange.NpcRest.TotalMilliseconds + 1;
        Assert.False(Reply(osric, p));
        Assert.False(Reply(osric, p));
        Assert.True(Reply(osric, p));                           // rested, and two replies heard in between
        Assert.Equal(3, Exchange.Started);
        Assert.Equal(9, Said);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStepThatGivesNoLineEndsItWithoutARetry(bool empty)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        _client.Answer = r => r.Exchange.Step != 1 ? AiReply.Success("Weary enough.", "test") :
            empty ? AiReply.Success("   ", "test") : AiReply.Fail(AiOutcome.Failed, "refused (frame)");

        Assert.True(Reply(osric, p));

        Assert.Equal(1, Said);                                  // her line was said; his was refused; no third
        Assert.Equal(2, _client.Steps.Count);                   // Codex F5: the refused step is not asked again
        Assert.Equal(0, Exchange.Completed);
        Assert.StartsWith("step 2 gave no line", Exchange.LastEnd);
        Assert.False(Exchange.Running);
    }

    [Fact]
    public void AnActionInAStepIsNeverApplied()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        var order = e.ControlOrder;
        _client.Answer = _ => AiReply.Success("I had best be off home.", "test") with { Action = "go_home" };

        Assert.True(Reply(osric, p));

        Assert.Equal(3, Said);
        Assert.Equal(p, e.ControlMaster);                       // talk only: she is still with him
        Assert.Equal(order, e.ControlOrder);
    }

    [Fact]
    public void APetsNameIsMaskedInTheRecord()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var pip = new Horse { Name = "Pip" };
        pip.MoveToWorld(new Point3D(Here.X + 2, Here.Y, Here.Z), Map.Felucca);
        pip.SetControlMaster(p);
        _made.Add(pip);

        Assert.True(Reply(osric, p, "A fine beast, that Pip. And you, Elspeth - how fares the road?"));

        var first = _client.Steps[0].Exchange.Transcript[0].Text;
        Assert.DoesNotContain("Pip", first);
        Assert.Contains("the horse", first);
    }

    // the owner's play test 2026-11-09: the lines came too fast. The pause is reading time for the line being answered.
    [Fact]
    public void ThePauseGrowsWithTheLineBeingAnsweredAndIsCapped()
    {
        // numbers after the owner's A3 play test (2026-11-10): 2-3 s + 10 ms a character, cap 4.5 s
        Assert.Equal(TimeSpan.FromSeconds(2), Exchange.PauseFor(null, 0));
        Assert.Equal(TimeSpan.FromSeconds(3), Exchange.PauseFor("", 1));
        Assert.Equal(TimeSpan.FromSeconds(3), Exchange.PauseFor(new string('a', 100), 0));
        Assert.Equal(Exchange.PauseCap, Exchange.PauseFor(new string('a', 400), 1));
        Assert.True(Exchange.PauseFor(new string('a', 100), 0.5) > Exchange.PauseFor(new string('a', 40), 0.5));
    }

    [Fact]
    public void EveryLineWaitsAtLeastThePauseFloorAndLongEnoughToFitTheDeadline()
    {
        var waits = new List<TimeSpan>();
        Exchange.Pause = d =>
        {
            waits.Add(d);
            return Task.CompletedTask;
        };
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Assert.True(Reply(osric, p));
        Assert.True(Soon(() => !Exchange.Running));

        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.InRange(w, Exchange.PauseMin, Exchange.PauseCap));
        Assert.True(waits[0] >= Exchange.PauseMin + Exchange.PausePerChar * Opener.Length);   // first wait reads the opening line
        // the slowest possible exchange (every pause at the cap, every call at its budget) still ends inside the deadline
        for (var n = 1; n <= Exchange.MaxSteps; n++)
        {
            Assert.True((Exchange.PauseCap + Exchange.StepBudget) * n < Exchange.DeadlineFor(n));
        }
    }

    [Fact]
    public void TheWholeExchangeHasADeadline()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Reply(osric, p));

        _now += (long)Exchange.DeadlineFor(3).TotalMilliseconds + 1;
        _client.Hold.SetResult(AiReply.Success("Sorry, I was miles away.", "test"));

        Assert.True(Soon(() => !Exchange.Running));
        Assert.Equal("took too long", Exchange.LastEnd);
        Assert.Equal(0, Said);
    }

    [Fact]
    public void MirabelCanOpenOne()
    {
        var p = Player("Wren");
        var mirabel = new Mirabel();
        mirabel.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(mirabel);
        var e = Companion(p);

        Assert.True(Reply(mirabel, p, "Welcome back. Elspeth, sit - you look done in."));

        Assert.Equal(new Mobile[] { e, mirabel, e }, new[] { _said[0].Who, _said[1].Who, _said[2].Who });
        var r1 = _client.Steps[1];
        Assert.Equal(("named", "mirabel", mirabel.AiId), (r1.Exchange.Speaker.Kind, r1.PersonaKey, r1.NpcId));
        Assert.Null(r1.PersonaVars);
    }

    [Fact]
    public void ASummaryAndAnExchangeNeverRunTogether()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var mirabel = new Mirabel();
        mirabel.MoveToWorld(new Point3D(Here.X - 3, Here.Y, Here.Z), Map.Felucca);
        _made.Add(mirabel);
        NamedNpcs.ResetForTests(true);
        NamedNpcs.Job Job() => new(mirabel, mirabel.AiId, "mirabel", p, AiIdentity.GetOrCreate(p), NamedNpcs.Gen(p),
            [new AiLine("player", "I slew a wyrm")], Core.Now);

        // An exchange step is out: the summary waits.
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Reply(osric, p));
        NamedNpcs.EnqueueJob(Job());
        Assert.False(NamedNpcs.MayDispatch(Core.Now));
        Exchange.NoteSpeech(p);
        Assert.True(NamedNpcs.MayDispatch(Core.Now));           // control: the same job may go once the exchange is over

        // A summary's call is out: no exchange starts.
        _client.Hold = null;
        _client.HoldSummary = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        NamedNpcs.DispatchForTests(Core.Now);
        Assert.True(NamedNpcs.SummaryRunning);
        var nell = Vendor("Nell", -2);
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.Online = _ => true;
        Assert.False(Reply(nell, p));
        Assert.Empty(_client.Steps.FindAll(s => s.Exchange.Addressee.Name == "Nell"));
        _client.HoldSummary.SetResult(AiReply.Fail(AiOutcome.Failed, "done"));
        Assert.True(Soon(() => !NamedNpcs.SummaryRunning));
        Assert.True(Reply(nell, p));                            // control: with the summary finished, it starts
    }

    // ---- Codex "Avallone Stage A2 Code R1" --------------------------------------------------------------------------

    // A2-R1: the reply to a line the player has since talked over may still be said, but it never starts an exchange.
    [Theory]
    [InlineData("vendor buy", MessageType.Regular, 0x3C)]      // a shop command
    [InlineData("Elspeth, wait here", MessageType.Regular, 0)] // a plain order
    [InlineData("farewell", MessageType.Regular, 0)]
    [InlineData("hmm", MessageType.Whisper, 0)]                // a line some cooldown would ignore
    public void AReplyToALineThePlayerHasSinceTalkedOverNeverStartsOne(string text, MessageType type, int keyword)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.OnSpeech(Line(p, "Good day, Osric"));          // the opening line is counted first (EventSink.Speech)
        var opening = Exchange.RevisionOf(p);                   // ...then the townsperson reads the count and asks the model

        Exchange.OnSpeech(keyword == 0 ? Line(p, text, type) : Line(p, text, type, keyword));   // while that call is out

        Assert.False(Reply(osric, p, openingRevision: opening)); // the late reply names Elspeth - and starts nothing
        Assert.False(Exchange.Running);
        Assert.Equal(0, Exchange.Started);
        Assert.Empty(_client.Steps);
    }

    [Fact]
    public void AReplyToTheLatestLineStillStartsOne()            // control for the test above
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.OnSpeech(Line(p, "Good day, Osric"));
        var opening = Exchange.RevisionOf(p);

        Assert.True(Reply(osric, p, openingRevision: opening));
        Assert.True(Soon(() => !Exchange.Running));
        Assert.Equal(3, Said);
    }

    [Fact]
    public void ALateReplyFromTheNpcThePlayerLeftNeverStartsOne()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var nell = Vendor("Nell", -2);
        Companion(p);
        Exchange.OnSpeech(Line(p, "Good day, Osric"));
        var toOsric = Exchange.RevisionOf(p);
        Exchange.OnSpeech(Line(p, "Nell, how is trade?"));       // the player turns to someone else
        var toNell = Exchange.RevisionOf(p);

        Assert.False(Reply(osric, p, openingRevision: toOsric)); // Osric's answer comes in late: said, but nothing more
        Assert.Equal(0, Exchange.Started);
        Assert.True(Reply(nell, p, openingRevision: toNell));    // control: the partner the player is talking to can
    }

    [Fact]
    public void MirabelsLateReplyNeverStartsOneEither()
    {
        var p = Player("Wren");
        var mirabel = new Mirabel();
        mirabel.MoveToWorld(new Point3D(Here.X - 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(mirabel);
        Companion(p);
        Exchange.OnSpeech(Line(p, "Evening, Mirabel"));
        var opening = Exchange.RevisionOf(p);
        Exchange.OnSpeech(Line(p, "Mirabel buy", MessageType.Regular, 0x3C));

        Assert.False(Reply(mirabel, p, "Welcome back. Elspeth, sit - you look done in.", openingRevision: opening));
        Assert.Equal(0, Exchange.Started);
        Assert.True(Reply(mirabel, p, "Welcome back. Elspeth, sit - you look done in."));   // control
    }

    // A2-R2 (shard half): hanging up on the brain is not enough.
    [Fact]
    public void ACutOffWhileTheCallIsOutTellsTheBrainAndNothingAsksForTheModelUntilItAnswers()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var nell = Vendor("Nell", -2);
        Companion(p);
        Exchange.RepliesBetween = 0;
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.HoldCancel = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        Assert.Single(_client.Steps);                           // the first step's call is out
        var id = _client.Steps[0].Exchange.Id;

        Exchange.OnSpeech(Line(p, "all follow me"));            // a command: no conversation request will follow it

        Assert.False(Exchange.Running);
        Assert.Single(_client.Cancels);                         // the brain is told which exchange is over
        Assert.Equal(("exchange_cancel", id), (_client.Cancels[0].Mode, _client.Cancels[0].Exchange.Id));
        Assert.True(Exchange.Settling);                         // ...and until it answers, the model is not ours to ask
        _client.Hold = null;
        Assert.False(Reply(nell, p));
        Assert.Single(_client.Steps);

        _client.HoldCancel.SetResult(AiReply.Success("", "test"));
        Assert.True(Soon(() => !Exchange.Settling));
        Assert.True(Reply(nell, p));                            // control: once the brain has let go
    }

    [Fact]
    public void ABrainThatNeverAnswersTheCutOffOnlyHoldsThingsForAMoment()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.CancelWait = TimeSpan.FromMilliseconds(80);
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.HoldCancel = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        Exchange.OnSpeech(Line(p, "all follow me"));

        Assert.True(Exchange.Settling);
        Assert.True(Soon(() => !Exchange.Settling));
    }

    [Fact]
    public void NoCutOffMessageWhenNoCallWasOut()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var gate = new TaskCompletionSource<bool>();
        Exchange.Pause = _ => gate.Task;

        Assert.True(Reply(osric, p));
        Exchange.OnSpeech(Line(p, "all follow me"));            // during the pause: nothing was sent to the brain
        gate.SetResult(true);

        Assert.False(Exchange.Running);
        Assert.Empty(_client.Cancels);
        Assert.False(Exchange.Settling);

        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.RepliesBetween = 0;
        Assert.True(Reply(Vendor("Nell", -2), p));              // and one that runs to its end sends none either (Osric is resting)
        Assert.True(Soon(() => !Exchange.Running));
        Assert.Equal("complete", Exchange.LastEnd);
        Assert.Empty(_client.Cancels);
    }

    // Codex "Avallone Stage A2 Code R2", A2-R2-T: giving up on a step is not the brain having finished it.
    [Fact]
    public void AStepThatTimesOutTellsTheBrainAndHoldsOptionalWorkUntilItAnswers()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var nell = Vendor("Nell", -2);
        Companion(p);
        var mirabel = new Mirabel();
        mirabel.MoveToWorld(new Point3D(Here.X - 3, Here.Y, Here.Z), Map.Felucca);
        _made.Add(mirabel);
        NamedNpcs.ResetForTests(true);
        Exchange.RepliesBetween = 0;
        Exchange.StepBudget = TimeSpan.FromMilliseconds(80);     // the step's own time limit, nobody interrupts
        _client.Hold = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.HoldCancel = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(Reply(osric, p));
        var id = _client.Steps[0].Exchange.Id;
        Assert.True(Soon(() => !Exchange.Running));

        Assert.StartsWith("step 1 gave no line", Exchange.LastEnd);
        Assert.Equal(0, Said);
        Assert.Single(_client.Cancels);                          // the brain is told, exactly as for an interruption
        Assert.Equal(id, _client.Cancels[0].Exchange.Id);
        Assert.True(Exchange.Settling);
        NamedNpcs.EnqueueJob(new NamedNpcs.Job(mirabel, mirabel.AiId, "mirabel", p, AiIdentity.GetOrCreate(p), NamedNpcs.Gen(p),
            [new AiLine("player", "I slew a wyrm")], Core.Now));
        Assert.False(NamedNpcs.MayDispatch(Core.Now));           // a visit summary waits for the brain to let go
        _client.Hold = null;
        Assert.False(Reply(nell, p));                            // and so does a new exchange
        Assert.Single(_client.Steps);                            // no retry of the step either

        _client.HoldCancel.SetResult(AiReply.Success("", "test"));
        Assert.True(Soon(() => !Exchange.Settling));
        Assert.True(NamedNpcs.MayDispatch(Core.Now));            // control: the following summary may go
        Assert.True(Reply(nell, p));
    }

    [Fact]
    public void AStepWhoseConnectionFailsTellsTheBrainToo()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        _client.Answer = _ => throw new System.Net.Http.HttpRequestException("connection reset");

        Assert.True(Reply(osric, p));
        Assert.True(Soon(() => !Exchange.Running));

        Assert.StartsWith("step 1 gave no line", Exchange.LastEnd);
        Assert.Single(_client.Cancels);
        Assert.Equal(_client.Steps[0].Exchange.Id, _client.Cancels[0].Exchange.Id);
        Assert.True(Soon(() => !Exchange.Settling));
    }

    [Fact]
    public void AStepTheBrainAnsweredNeedsNoCutOff()             // control: a confirmed refusal, and a success
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var nell = Vendor("Nell", -2);
        Companion(p);
        Exchange.RepliesBetween = 0;
        _client.Answer = _ => AiReply.Fail(AiOutcome.Failed, "refused (decency)");

        Assert.True(Reply(osric, p));
        Assert.True(Soon(() => !Exchange.Running));
        Assert.StartsWith("step 1 gave no line", Exchange.LastEnd);
        Assert.Empty(_client.Cancels);
        Assert.False(Exchange.Settling);

        _client.Answer = r => AiReply.Success($"line {r.Exchange.Step + 1}", "test");
        Assert.True(Reply(nell, p));
        Assert.True(Soon(() => !Exchange.Running));
        Assert.Equal("complete", Exchange.LastEnd);
        Assert.Empty(_client.Cancels);
    }

    // ---- the owner, chat, 2026-11-09: -----

    [Theory]
    [InlineData(0.0, 0.0, 1)]
    [InlineData(0.0, 0.34, 2)]
    [InlineData(0.699, 0.999, 3)]
    [InlineData(0.70, 0.0, 4)]
    [InlineData(0.80, 0.5, 5)]
    [InlineData(0.899, 0.999, 6)]
    [InlineData(0.90, 0.0, 7)]
    [InlineData(0.95, 0.3, 8)]
    [InlineData(0.95, 0.6, 9)]
    [InlineData(0.999, 0.999, 10)]
    public void TheLengthBands(double band, double within, int lines) =>
        Assert.Equal(lines, Exchange.LengthFor(band, within, Exchange.MaxSteps));

    [Fact]
    public void TheLengthsAreWeightedAndCapped()
    {
        var rng = new Random(11);
        var counts = new int[Exchange.MaxSteps + 1];
        for (var i = 0; i < 20000; i++)
        {
            counts[Exchange.LengthFor(rng.NextDouble(), rng.NextDouble(), Exchange.MaxSteps)]++;
        }

        Assert.Equal(0, counts[0]);
        Assert.InRange((counts[1] + counts[2] + counts[3]) / 20000.0, 0.68, 0.72);
        Assert.InRange((counts[4] + counts[5] + counts[6]) / 20000.0, 0.18, 0.22);
        Assert.InRange((counts[7] + counts[8] + counts[9] + counts[10]) / 20000.0, 0.08, 0.12);
        Assert.All(counts[1..], c => Assert.True(c > 0));         // every length from 1 to 10 turns up
        Assert.Equal(4, Exchange.LengthFor(0.99, 0.99, 4));       // the setting caps it
        Assert.Equal(1, Exchange.LengthFor(0.99, 0.99, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(10)]
    public void AnExchangeRunsToItsDrawnLengthAndOnlyItsLastLineIsMarkedLast(int lines)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var e = Companion(p);
        Exchange.PickLength = () => lines;

        Assert.True(Reply(osric, p));
        Assert.True(Soon(() => !Exchange.Running));

        Assert.Equal("complete", Exchange.LastEnd);
        Assert.Equal(lines, Said);
        Assert.Equal(lines, _client.Steps.Count);
        for (var i = 0; i < lines; i++)
        {
            Assert.Equal(i == lines - 1, _client.Steps[i].Exchange.Last);
            Assert.Equal(i + 1, _client.Steps[i].Exchange.Transcript.Length);
            Assert.Same(i % 2 == 0 ? e : osric, _said[i].Who);     // the companion first, then turn about
        }
    }

    [Fact]
    public void TheSettingCapsTheDrawnLength()
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        Exchange.MaxLines = 2;
        Exchange.PickLength = () => 9;

        Assert.True(Reply(osric, p));
        Assert.True(Soon(() => !Exchange.Running));

        Assert.Equal(2, Said);
        Assert.True(_client.Steps[1].Exchange.Last);
    }
}
