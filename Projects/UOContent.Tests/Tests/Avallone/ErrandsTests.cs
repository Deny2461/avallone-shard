using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Avallone.Errands;
using Server.Avallone.Townsfolk;
using Server.Engines.Spawners;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Xunit;

namespace UOContent.Tests;

// Errands build 1, the shard side (design v5 3.1-3.10; Codex R2-R5 required cases). A stand-in for the brain's fixed
// code answers proofs; no model exists anywhere in these tests.
[Collection("Sequential UOContent Tests")]
public class ErrandsTests : IDisposable
{
    private static readonly Point3D HomeSpot = new(5910, 110, 0);
    private static readonly Point3D AwaySpot = new(5975, 110, 0);
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "errand-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IEntity> _made = [];
    private readonly TownRegion _home, _away;
    private readonly FakeBrain _brain = new();
    private readonly List<(Guid Npc, string Kind, string Facts)> _recorded = [];
    private readonly Func<Mobile, bool> _online = TownsfolkTalk.Online;
    private readonly Tailor _sender, _receiver;
    private readonly PlayerMobile _p;
    private readonly Guid _senderId, _receiverId, _char;
    private DateTime _now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private long _ownTime = 100_000;
    private int _revision = 10;
    private string _stamp = "stamp-1";
    private bool _recordFails;
    private bool _ledgerReady = true;
    private readonly List<string> _told = [];          // v6: the system text of the reminder paths
    private object _session = new();                    // v6: the player's connection, as [Errands show sees it

    public ErrandsTests()
    {
        Directory.CreateDirectory(_dir);
        _home = new TownRegion("Hometown", Map.Felucca, 50, new Rectangle3D(5900, 100, -128, 40, 40, 256));
        _away = new TownRegion("Awaytown", Map.Felucca, 50, new Rectangle3D(5960, 100, -128, 40, 40, 256));
        _home.Register();
        _away.Register();
        Seats.ResetForTests(true, Path.Combine(_dir, "seat-register.json"));
        var reg = new SeatRegister { MigrationComplete = true };
        reg.Commit(Seats.RegisterPath);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(true, reg.RegisterId, reg.Counter));

        _sender = Seat("Tomas", HomeSpot);
        _receiver = Seat("Anna", AwaySpot);
        _p = new PlayerMobile { Name = "Gabby" };
        _p.DefaultMobileInit();
        if (_p.Backpack == null)
        {
            _p.AddItem(new Backpack());
        }

        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        _made.Add(_p);
        _senderId = AiIdentity.GetOrCreate(_sender);
        _receiverId = AiIdentity.GetOrCreate(_receiver);
        _char = AiIdentity.GetOrCreate(_p);

        TownsfolkTalk.Online = _ => true;
        Errands.ResetForTests();
        Errands.Brain = _brain;
        Errands.Recorder = (npc, who, kind, facts) =>
        {
            if (_recordFails)
            {
                return false;
            }

            _recorded.Add((npc, kind, JsonSerializer.Serialize(facts, Snake)));
            return true;
        };
        Errands.LedgerReady = () => _ledgerReady;
        Errands.Stamp = () => _stamp;
        Errands.Now = () => _now;
        Errands.Dice = _ => 0;
        Errands.OwnTime = _ => _ownTime;
        Errands.Revision = _ => _revision;
        Errands.SeatsRevision = () => Seats.Revision;
        Errands.AwardKarma = Server.Misc.Titles.AwardKarma;
        Errands.DropInPack = (pack, m, item) => pack.TryDropItem(m, item, false);
        Errands.Tell = (_, text) => _told.Add(text);
        Errands.Session = _ => _session;
    }

    public void Dispose()
    {
        foreach (var e in _made)
        {
            if (!e.Deleted)
            {
                e.Delete();
            }
        }

        Errands.ResetForTests(false, false);
        Errands.Recorder = AiLedger.TryRecord;
        Errands.LedgerReady = () => AiLedger.HasRoom && AiLedger.ErrandsIngest;
        Errands.Stamp = TownRoster.StampForRequest;
        Errands.Now = () => Core.Now;
        Errands.Dice = Utility.Random;
        Errands.OwnTime = pm => (long)pm.GameTime.TotalSeconds;
        Errands.Revision = Exchange.RevisionOf;
        Errands.SeatsRevision = () => Seats.Revision;
        Errands.AwardKarma = Server.Misc.Titles.AwardKarma;
        Errands.DropInPack = (pack, m, item) => pack.TryDropItem(m, item, false);
        Errands.Tell = (pm, text) => pm.SendMessage(text);
        Errands.Session = pm => pm.NetState;
        TownsfolkTalk.Online = _online;
        Seats.ResetForTests(false, null);
        _home.Unregister();
        _away.Unregister();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // a leftover temp folder is harmless
        }
    }

    private Tailor Seat(string name, Point3D at)
    {
        var sp = new Spawner(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, "Tailor");
        sp.MoveToWorld(at, Map.Felucca);
        _made.Add(sp);
        var t = new Tailor { Name = name };
        t.MoveToWorld(at, Map.Felucca);
        t.Home = at;
        t.HomeMap = Map.Felucca;
        t.Spawner = sp;
        _made.Add(t);
        Seats.OnSpawned(sp, t);
        return t;
    }

    // The brain's fixed code, as far as the shard can tell: it echoes what it was sent and returns the same digest for
    // the same errand. Tests bend single fields to see the shard refuse.
    private sealed class FakeBrain : IErrandBrain
    {
        public readonly List<JsonElement> Proofs = [];
        public int Probes;
        public bool Down;
        public string Boot = "boot-1", Data = "data-1";
        public bool StampOk = true;
        public Action<ErrandProof> Bend;
        public bool NoProof;
        public TaskCompletionSource<bool> Gate;
        public long? ProbeEcho;
        public bool Throw;                                 // v6: breaks the "never throws" promise, for [Errands show

        public Task<ErrandReady> ReadyAsync(long probe, int gen, string stamp)
        {
            Probes++;
            return Task.FromResult(Down ? null : new ErrandReady(ProbeEcho ?? probe, gen, true, Boot, Data, StampOk));
        }

        public async Task<(ErrandProof Proof, string DataGen, bool Failed)> ProofAsync(object body)
        {
            var b = JsonDocument.Parse(JsonSerializer.Serialize(body, Snake)).RootElement.Clone();
            Proofs.Add(b);
            if (Gate != null)
            {
                await Gate.Task;
            }

            if (Throw)
            {
                throw new InvalidOperationException("the stand-in brain threw");
            }

            if (Down)
            {
                return (null, null, true);
            }

            if (NoProof)
            {
                return (null, Data, false);
            }

            var p = Make(b, Data);
            Bend?.Invoke(p);
            return (p, Data, false);
        }

        public static ErrandProof Make(JsonElement b, string data)
        {
            string S(string n) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? I(string n) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            var step = S("step");
            var p = new ErrandProof
            {
                Step = step, Kind = S("kind"), Route = S("route"), Sender = S("sender"), Receiver = S("receiver"),
                About = S("about"), LineKey = S("line_key"), LineHash = S("line_key") == null ? null : new string('a', 64),
                ListsVersion = 1, Digest = Digest(S("kind"), S("sender"), S("receiver")), DataGen = data, Band = "near",
                ReceiverMap = "Felucca", ReceiverTown = "Awaytown", SenderMap = "Felucca", SenderTown = "Hometown",
                YesLine = "That is good of you. Safe roads.", NoLine = "No matter. It will keep.",
                AskLine = AskText,
                // v6: the remind step gives back the sentence that was spoken - the ask for the sender, the outward
                // delivery sentence for the relative
                Line = step == "return" ? "So — that all is well there. Thank you." :
                    step == "remind" && S("role") == "sender" ? AskText : DeliverText,
                Gen = I("gen"), Row = I("row"), Version = I("version"), SpeechRev = I("speech_rev"), Request = S("request"),
                Ticket = S("ticket"), Char = S("char"), Stamp = S("stamp")
            };
            if (step == "deliver" && p.Kind == "ask_after")
            {
                p.Answer = new ErrandAnswerFix("a01", new string('b', 64), new string('c', 64));
            }

            return p;
        }

        public const string AskText = "Would you carry a word to my cousin Anna, a tailor in Awaytown? Tell her that I send my love.";
        public const string DeliverText = "Word from my cousin Tomas? Thank you for bringing it.";

        public static string Digest(string kind, string sender, string receiver) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{kind}|{sender}|{receiver}"))).ToLowerInvariant();
    }

    private async Task Lease() => await Errands.ProbeForTests();

    private ErrandProof AskProof(Errands.Turn turn, string kind = "word", string route = "cousin")
    {
        var ask = JsonDocument.Parse(JsonSerializer.Serialize(turn.Ask, Snake)).RootElement;
        var body = new
        {
            step = "ask", gen = ask.GetProperty("gen").GetInt32(), request = ask.GetProperty("request").GetString(),
            ticket = ask.GetProperty("ticket").GetString(), @char = ask.GetProperty("char").GetString(),
            speech_rev = ask.GetProperty("speech_rev").GetInt32(), stamp = ask.GetProperty("stamp").GetString(), kind, route,
            sender = _senderId.ToString(), receiver = _receiverId.ToString(), line_key = kind == "word" ? "m11" : null
        };
        return FakeBrain.Make(JsonDocument.Parse(JsonSerializer.Serialize(body, Snake)).RootElement, _brain.Data);
    }

    // Tomas asks; the ask line is said; a ticket waits for the player's next line.
    private async Task<Errands.Turn> Asked(string kind = "word", string route = "cousin")
    {
        await Lease();
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false);
        Assert.NotNull(turn.Ask);
        Assert.Equal(AskProof(turn, kind, route).AskLine, Errands.AfterReply(_sender, _p, turn, AskProof(turn, kind, route), true, _revision));
        return turn;
    }

    // ... and the player says yes: a row in state accepted.
    private async Task<ErrandRow> Accepted(string kind = "word", string route = "cousin")
    {
        await Asked(kind, route);
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        Assert.True(turn.Handled && turn.Spoke, "the yes was not honoured");
        return Assert.Single(Errands.RowsForTests);
    }

    private async Task<Errands.Turn> AtReceiver(string text = "hello")
    {
        _p.MoveToWorld(new Point3D(AwaySpot.X + 1, AwaySpot.Y, 0), Map.Felucca);
        _revision++;
        return await Errands.BeforeTurnAsync(_receiver, _p, text, _revision, false);
    }

    private async Task<Errands.Turn> BackAtSender(string text = "hello")
    {
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        _revision++;
        return await Errands.BeforeTurnAsync(_sender, _p, text, _revision, false);
    }

    private int GoldInPack() => _p.Backpack.GetAmount(typeof(Gold));

    // ---- switches and readiness ----

    [Fact]
    public async Task WithTheSwitchesOffNothingHappensAndTheBrainIsNotCalled()
    {
        Errands.ResetForTests(false, false);
        Errands.Brain = _brain;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false);
        Assert.False(turn.Handled);
        Assert.Null(turn.Ask);
        Assert.Empty(_brain.Proofs);
        Assert.False(Errands.CanComplete || Errands.CanOffer || Errands.PayAllowed);
    }

    [Fact]
    public async Task ReadinessStartsFromColdThroughTheProbeAndIsFourSeparateConditions()
    {
        Assert.False(Errands.LeaseHeld);
        Assert.False(Errands.CanComplete);
        Assert.True(Errands.PayAllowed);                 // pay allowed needs no brain
        await Lease();
        Assert.True(Errands.LeaseHeld && Errands.CanComplete && Errands.CanOffer);

        Errands.Switch("offer", false);                  // offers off, completion on: open jobs can still finish
        Assert.True(Errands.CanComplete);
        Assert.False(Errands.CanOffer);

        _ledgerReady = false;
        Assert.False(Errands.CanComplete);
        Assert.True(Errands.PayAllowed);
        _ledgerReady = true;

        _now += Errands.LeaseLife + TimeSpan.FromSeconds(1);   // the lease runs out by itself
        Assert.False(Errands.LeaseHeld || Errands.CanComplete);
        await Lease();                                         // ... and an idle expired lease is found again
        Assert.True(Errands.CanComplete);
    }

    [Fact]
    public async Task AProbeAnswerRenewsOnlyIfItIsForTheNewestProbeAndVouchesForErrands()
    {
        _brain.ProbeEcho = 999;                          // an answer to some other probe
        await Lease();
        Assert.False(Errands.LeaseHeld);
        _brain.ProbeEcho = null;
        _brain.StampOk = false;                          // the brain holds another town list
        await Lease();
        Assert.False(Errands.LeaseHeld);
        _brain.StampOk = true;
        _brain.Down = true;
        await Lease();
        Assert.False(Errands.LeaseHeld);
        _brain.Down = false;
        await Lease();
        Assert.True(Errands.LeaseHeld);
    }

    [Fact]
    public async Task AChangedBrainOrALostLeaseMovesTheGenerationOn()
    {
        await Lease();
        var gen = Errands.Generation;
        await Lease();
        Assert.Equal(gen, Errands.Generation);           // the same brain: nothing moves
        _brain.Boot = "boot-2";
        await Lease();
        Assert.Equal(gen + 1, Errands.Generation);
        _brain.Down = true;
        await Lease();
        Assert.Equal(gen + 2, Errands.Generation);
        Assert.False(Errands.LeaseHeld);
    }

    // ---- asking ----

    [Fact]
    public async Task AnAskIsMadeOnlyWhenEverythingHoldsAndSetsTheCooldownAndATicket()
    {
        var turn = await Asked();
        Assert.NotNull(Errands.TicketForTests(_sender, _char));
        Assert.Equal(1, Errands.WatchedForTests);
        Assert.Empty(Errands.RowsForTests);              // no "offered" row
        Assert.NotNull(turn.TicketId);

        // within the cooldown the same townsperson does not ask again
        _revision++;
        var again = await Errands.BeforeTurnAsync(_sender, _p, "and how is trade", _revision, false);
        Assert.NotNull(again.Ask);                        // the ticket was used up: this is the closing line, not an ask
        Assert.Contains("closed", JsonSerializer.Serialize(again.Ask, Snake));
        _revision++;
        var third = await Errands.BeforeTurnAsync(_sender, _p, "well then", _revision, false);
        Assert.Null(third.Ask);
        _ownTime += Errands.CooldownSeconds;
        _revision++;
        Assert.NotNull((await Errands.BeforeTurnAsync(_sender, _p, "hello again", _revision, false)).Ask);
    }

    [Fact]
    public async Task NoAskWithoutTheDiceTheOfferSwitchOrALease()
    {
        await Lease();
        Errands.Dice = _ => 1;
        Assert.Null((await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false)).Ask);
        Errands.Dice = _ => 0;
        Errands.Switch("offer", false);
        Assert.Null((await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false)).Ask);
        Errands.Switch("offer", true);
        _now += Errands.LeaseLife + TimeSpan.FromSeconds(1);
        Assert.Null((await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false)).Ask);
    }

    [Fact]
    public async Task TheAskLineIsNotSaidUnlessTheReplyIsPlainAndTheProofIsAcceptedWhole()
    {
        await Lease();
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false);
        ErrandProof P(Action<ErrandProof> bend = null)
        {
            var p = AskProof(turn);
            bend?.Invoke(p);
            return p;
        }

        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(), false, _revision));                 // not a plain reply
        Assert.Null(Errands.AfterReply(_sender, _p, turn, null, true, _revision));                 // no proof
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Ticket = "other"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Gen += 1), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Stamp = "stamp-0"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.DataGen = "data-0"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Char = Guid.NewGuid().ToString()), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Receiver = _senderId.ToString()), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Receiver = Guid.NewGuid().ToString()), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.ReceiverTown = "Hometown"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Kind = "goods"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Route = "standin").Kind == "x" ? null : P(p => { p.Kind = "ask_after"; p.Route = "standin"; p.LineKey = null; }), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.AskLine = "Take 5 of them to Anna."), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.AskLine = "There is gold in it for you."), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.AskLine = new string('x', 301)), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(p => p.Digest = "short"), true, _revision));
        Assert.Null(Errands.AfterReply(_sender, _p, turn, P(), true, _revision + 1));               // the player spoke again
        Assert.Null(Errands.TicketForTests(_sender, _char));
        Assert.NotNull(Errands.AfterReply(_sender, _p, turn, P(), true, _revision));                // and the good one is said
    }

    // ---- saying yes ----

    [Fact]
    public async Task YesMakesOneVerificationCallAndThenARow()
    {
        var row = await Accepted();
        Assert.Equal((ErrandState.Accepted, "word", "cousin", _senderId, _receiverId), (row.State, row.Kind, row.Route, row.Sender, row.Receiver));
        Assert.Equal(("Felucca", "Awaytown", "near"), (row.ReceiverMap, row.ReceiverTown, row.Band));
        Assert.Equal(Errands.DeadlineSeconds, row.DueAt - row.AcceptedAt);
        var call = Assert.Single(_brain.Proofs);
        Assert.Equal("accept", call.GetProperty("step").GetString());
        Assert.False(call.TryGetProperty("utterance", out _));                   // a matched yes carries no free text
        Assert.Equal(0, Errands.WatchedForTests);
        Assert.Null(Errands.TicketForTests(_sender, _char));
    }

    [Fact]
    public async Task NoMakesNothingAndCallsNobody()
    {
        await Asked();
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "no thanks", _revision, false);
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Equal("No matter. It will keep.", turn.Said);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Theory]
    [InlineData("maybe later")]
    [InlineData("yes, but tell me of the road first")]   // was "yes, but what does it pay": since 2026-11-22 a pay word keeps the offer open
    [InlineData("sure")]
    public async Task AnythingElseClosesTheMatterAndGoesToAnOrdinaryTurn(string text)
    {
        await Asked();
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, text, _revision, false);
        Assert.False(turn.Handled);
        Assert.Equal("{\"step\":\"closed\"}", JsonSerializer.Serialize(turn.Ask, Snake));
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);   // the ticket is gone
        Assert.Empty(Errands.RowsForTests);
    }

    [Fact]
    public async Task AYesIsNotHonouredAfterOtherSpeechAfterTheTicketsLifeOrInAnotherGeneration()
    {
        await Asked();
        _revision += 2;                                   // the player said something to someone else in between
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);

        _ownTime += Errands.CooldownSeconds;
        await Asked();
        _now += Errands.TicketLife + TimeSpan.FromSeconds(1);
        await Lease();
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);

        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Errands.Switch("offer", false);                   // off then on while the ticket lives: the generation moved
        Errands.Switch("offer", true);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task AChangedTownListVoidsTheTicket()
    {
        await Asked();
        _stamp = "stamp-2";                               // a seat, the list or a quarantine changed since the ask
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task SteppingAwayAndStraightBackBreaksTheTicketForGood()
    {
        await Asked();
        _p.MoveToWorld(new Point3D(5935, 135, 0), Map.Felucca);     // out of talking range ...
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);     // ... and straight back, between two timer ticks
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        Assert.False(turn.Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task MovingAboutWithinRangeKeepsTheTicketAndAnAddressedYesIsHonoured()
    {
        await Asked();
        _p.MoveToWorld(new Point3D(5912, 111, 0), Map.Felucca);
        _sender.MoveToWorld(new Point3D(5910, 111, 0), Map.Felucca);
        Assert.False(Errands.TicketForTests(_sender, _char).Broken);
        _revision++;
        Assert.True((await Errands.BeforeTurnAsync(_sender, _p, "yes, Tomas", _revision, false)).Spoke);
        Assert.Single(Errands.RowsForTests);
    }

    [Fact]
    public async Task TheTownspersonWalkingOffAnotherMapALogoutOrADeathBreakIt()
    {
        await Asked();
        _sender.MoveToWorld(new Point3D(5939, 139, 0), Map.Felucca);
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);
        _sender.MoveToWorld(HomeSpot, Map.Felucca);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);   // broken: used up, nothing made

        foreach (var breakIt in new Action[]
                 {
                     () => { _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Trammel); _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca); },
                     () => Errands.Left(_p), () => Errands.Left(_sender)
                 })
        {
            _ownTime += Errands.CooldownSeconds;
            await Asked();
            breakIt();
            Assert.True(Errands.TicketForTests(_sender, _char).Broken);
            _revision++;
            Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        }

        Assert.Empty(Errands.RowsForTests);
    }

    // ---- Codex Code R1 C2: every way an encounter can be interrupted and put back -------------------------------------

    // A region whose entry moves one mobile on, once - the engine runs this BEFORE it tells the mobile it has moved.
    private sealed class Bounce : Region
    {
        public Mobile Who;
        public Point3D To;
        public bool Done;

        public Bounce(string name, Point3D at) : base(name, Map.Felucca, 90, new Rectangle3D(at.X, at.Y, -128, 1, 1, 256))
        {
        }

        public override void OnEnter(Mobile m)
        {
            if (m == Who && !Done)
            {
                Done = true;
                m.MoveToWorld(To, Map.Felucca);
            }
        }
    }

    // One outer move to `near`; the engine's own region code carries the mobile out to `far` and straight back to `near`
    // before the outer move's notification runs. Every notification but the innermost sees the mobile in range.
    private void BounceOutAndBack(Mobile who, Point3D near, Point3D far)
    {
        var outward = new Bounce("BounceOut", near) { Who = who, To = far };
        var back = new Bounce("BounceBack", far) { Who = who, To = near };
        outward.Register();
        back.Register();
        try
        {
            who.MoveToWorld(near, Map.Felucca);
            Assert.True(outward.Done && back.Done, "the engine did not run the region code as the test expects");
            Assert.Equal(near, who.Location);
        }
        finally
        {
            outward.Unregister();
            back.Unregister();
        }
    }

    [Fact]
    public async Task ABounceOutAndBackInsideOneMoveBreaksTheTicket_ThePlayer()
    {
        await Asked();
        BounceOutAndBack(_p, new Point3D(5913, 112, 0), new Point3D(5936, 136, 0));
        Assert.True(TownsfolkTalk.CanDeliver(_sender, _p));                   // they stand together again ...
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);           // ... and the break is on record all the same
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task ABounceOutAndBackInsideOneMoveBreaksTheTicket_TheTownsperson()
    {
        await Asked();
        BounceOutAndBack(_sender, new Point3D(5912, 112, 0), new Point3D(5936, 136, 0));
        Assert.True(TownsfolkTalk.CanDeliver(_sender, _p));
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
    }

    [Fact]
    public async Task ABounceWhileTheVerificationIsOutBreaksItToo()
    {
        await Asked();
        _brain.Gate = new TaskCompletionSource<bool>();
        _revision++;
        var pending = Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        BounceOutAndBack(_p, new Point3D(5913, 112, 0), new Point3D(5936, 136, 0));
        _brain.Gate.SetResult(true);
        var turn = await pending;
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Empty(Errands.RowsForTests);
    }

    [Fact]
    public async Task TheOldPositionOrTheOldMapAloneIsEnoughToBreakIt()
    {
        await Asked();
        Errands.Moved(_p, new Point3D(5936, 136, 0));                         // it WAS far away; it is in range now
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);

        _revision++;
        await Errands.BeforeTurnAsync(_sender, _p, "hm", _revision, false);  // the broken ticket is used up
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Errands.MapChanged(_sender, Map.Trammel);                             // the townsperson WAS on another map
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);

        _revision++;
        await Errands.BeforeTurnAsync(_sender, _p, "hm", _revision, false);
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Errands.Moved(_p, new Point3D(5912, 111, 0));                         // controls: a move from in range to in range,
        Errands.MapChanged(_p, Map.Felucca);                                  // and a "change" from the map they share
        Assert.False(Errands.TicketForTests(_sender, _char).Broken);
    }

    [Fact]
    public async Task ASeatOrQuarantineChangeThatIsPutBackStillVoidsTheTicket()
    {
        await Asked();
        Seats.QuarantineForTests(_receiver);                                  // A -> B ...
        Seats.ReleaseForTests(_receiver);                                     // ... -> A: the town list reads as before
        Assert.Equal("stamp-1", Errands.Stamp());
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);

        _ownTime += Errands.CooldownSeconds;                                  // the same while the verification is out
        await Asked();
        _brain.Gate = new TaskCompletionSource<bool>();
        _revision++;
        var pending = Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        Seats.QuarantineForTests(_receiver);
        Seats.ReleaseForTests(_receiver);
        _brain.Gate.SetResult(true);
        Assert.Contains((await pending).Said, Errands.Neutral);
        Assert.Empty(Errands.RowsForTests);
    }

    [Fact]
    public async Task ATownListSeenToDifferBreaksTheTicketEvenIfItReadsTheSameAgainLater()
    {
        await Asked();
        _stamp = "stamp-2";
        Errands.TicketTickForTests();                                         // the one-second look sees the difference
        _stamp = "stamp-1";
        Assert.Null(Errands.TicketForTests(_sender, _char));                  // broken and cleared away
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);

        _ownTime += Errands.CooldownSeconds;                                  // and while the verification is out
        await Asked();
        _brain.Gate = new TaskCompletionSource<bool>();
        _revision++;
        var pending = Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        _stamp = "stamp-2";
        Errands.TicketTickForTests();
        _stamp = "stamp-1";
        _brain.Gate.SetResult(true);
        Assert.Contains((await pending).Said, Errands.Neutral);
        Assert.Empty(Errands.RowsForTests);
    }

    // Codex Code R2 note 5: every creature that is deleted passes through the seats code. One that holds no seat and
    // is not quarantined changes nothing - a monster dying across the world must not void anybody's ticket.
    [Fact]
    public async Task AnUnrelatedCreatureBeingDeletedDoesNotTouchTheCounterOrATicket()
    {
        await Asked();
        var before = Seats.Revision;
        var stray = new Tailor { Name = "Stray" };                            // never seated, never quarantined
        stray.MoveToWorld(new Point3D(5930, 130, 0), Map.Felucca);
        stray.Delete();
        var monster = new Mongbat();
        monster.MoveToWorld(new Point3D(5931, 131, 0), Map.Felucca);
        monster.Delete();
        Assert.Equal(before, Seats.Revision);
        _revision++;
        Assert.True((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Spoke);
        Assert.Single(Errands.RowsForTests);

        _ownTime += Errands.CooldownSeconds;                                  // control: a SEATED townsperson's deletion does
        var other = Seat("Corwin", new Point3D(5914, 114, 0));
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        var row = Errands.RowsForTests[0];
        row.State = ErrandState.Lapsed;                                       // (free the one-per-sender place)
        await Asked();
        var seated = Seats.Revision;
        other.Delete();
        Assert.True(Seats.Revision > seated);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
    }

    [Fact]
    public void TheSeatsCounterOnlyGoesUp()
    {
        var before = Seats.Revision;
        Seats.QuarantineForTests(_receiver);
        var mid = Seats.Revision;
        Seats.ReleaseForTests(_receiver);
        var other = Seat("Corwin", new Point3D(5914, 114, 0));
        var seated = Seats.Revision;
        other.Delete();
        Assert.True(before < mid && mid < Seats.Revision && mid < seated && seated < Seats.Revision);
    }

    // ---- Codex Code R1 C4: an exchange with the player's companion has the turn ---------------------------------------

    [Fact]
    public async Task AnExchangeStartingVoidsAnAnswerTicket()
    {
        await Asked();
        Errands.ExchangeStarted(_p);
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task ABreakWhileTheVerificationIsOutIsStillABreak()
    {
        await Asked();
        _brain.Gate = new TaskCompletionSource<bool>();
        _revision++;
        var pending = Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        Assert.Equal(1, Errands.WatchedForTests);                  // the latch outlives the used-up ticket
        _p.MoveToWorld(new Point3D(5935, 135, 0), Map.Felucca);
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        _brain.Gate.SetResult(true);
        var turn = await pending;
        Assert.True(turn.Handled);
        Assert.Contains(turn.Said, Errands.Neutral);               // a neutral stock line; nothing is created
        Assert.Empty(Errands.RowsForTests);
        Assert.Equal(0, Errands.WatchedForTests);
    }

    [Fact]
    public async Task AYesWhoseProofDoesNotComeBackWholeMakesNothing()
    {
        foreach (var bend in new Action[]
                 {
                     () => _brain.NoProof = true, () => _brain.Bend = p => p.Digest = new string('0', 64),
                     () => _brain.Bend = p => p.Gen += 1, () => _brain.Bend = p => p.Request = "q0",
                     () => _brain.Bend = p => p.Stamp = "stamp-0", () => _brain.Bend = p => p.DataGen = "data-9",
                     () => _brain.Bend = p => p.YesLine = "Here is 5 gold."
                 })
        {
            _ownTime += Errands.CooldownSeconds;
            await Asked();
            bend();
            _revision++;
            var turn = await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
            Assert.True(turn.Handled);
            Assert.Contains(turn.Said, Errands.Neutral);
            Assert.Empty(Errands.RowsForTests);
            _brain.NoProof = false;
            _brain.Bend = null;
        }
    }

    [Fact]
    public async Task AFailedCallEndsTheLeaseAtOnce()
    {
        await Asked();
        var gen = Errands.Generation;
        _brain.Down = true;
        _revision++;
        await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false);
        Assert.False(Errands.LeaseHeld);
        Assert.Equal(gen + 1, Errands.Generation);
        Assert.Empty(Errands.RowsForTests);
    }

    // ---- delivery, completion and pay ----

    [Fact]
    public async Task DeliveringWordRecordsThenMarksThenPays()
    {
        var row = await Accepted();
        _p.Karma = 0;
        var before = GoldInPack();
        var turn = await AtReceiver();
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Equal("Word from my cousin Tomas? Thank you for bringing it.", turn.Said);
        Assert.Equal("hello", _brain.Proofs[^1].GetProperty("utterance").GetString());   // so the brain can let the house rule win
        Assert.Equal((ErrandState.Done, PayState.Paid, PayState.Paid), (row.State, row.Coin, row.Karma));
        Assert.Equal((150, 50), (row.CoinDue, row.KarmaGained));
        Assert.Equal(before + 150, GoldInPack());
        Assert.Equal(50, _p.Karma);
        var (npc, kind, facts) = Assert.Single(_recorded);
        Assert.Equal((_senderId, "errand_done"), (npc, kind));                  // recorded under the SENDER's id
        var f = JsonDocument.Parse(facts).RootElement;
        Assert.Equal(14, System.Linq.Enumerable.Count(f.EnumerateObject()));
        Assert.Equal((1, row.Id, "word", "cousin"), (f.GetProperty("v").GetInt32(), f.GetProperty("errand").GetInt32(),
            f.GetProperty("kind").GetString(), f.GetProperty("route").GetString()));
        Assert.Equal(_senderId.ToString(), f.GetProperty("sender_id").GetString());
        Assert.Equal(_receiverId.ToString(), f.GetProperty("receiver_id").GetString());
        Assert.Equal(JsonValueKind.Number, f.GetProperty("memory").ValueKind);
        Assert.True(f.GetProperty("memory").TryGetInt64(out _));                 // whole seconds, never a fraction
        Assert.False(f.GetProperty("head_start").GetBoolean());
        Assert.DoesNotContain("150", facts);                                     // amounts are not in the event
        Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));
    }

    [Fact]
    public async Task IfTheRecordCannotBeWrittenNothingIsChanged()
    {
        var row = await Accepted();
        _recordFails = true;
        var before = (GoldInPack(), _p.Karma, row.Version);
        var turn = await AtReceiver();
        Assert.False(turn.Handled);                                              // an ordinary turn; the row waits
        Assert.Equal((ErrandState.Accepted, PayState.None, 0), (row.State, row.Coin, row.CoinDue));
        Assert.Equal(before, (GoldInPack(), _p.Karma, row.Version));
        Assert.NotNull(row.Waiting);
        _recordFails = false;
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal(ErrandState.Done, row.State);
    }

    [Fact]
    public async Task AStepWhoseProofIsRefusedLeavesTheRowAndSaysNothingForIt()
    {
        var row = await Accepted();
        foreach (var bend in new Action[]
                 {
                     () => _brain.NoProof = true, () => _brain.Bend = p => p.Digest = new string('0', 64),
                     () => _brain.Bend = p => p.Row += 1, () => _brain.Bend = p => p.Version += 1,
                     () => _brain.Bend = p => p.Stamp = "x", () => _brain.Bend = p => p.Line = "Take 5 of them."
                 })
        {
            bend();
            var turn = await AtReceiver();
            Assert.False(turn.Handled);
            Assert.Equal(ErrandState.Accepted, row.State);
            Assert.Empty(_recorded);
            _brain.NoProof = false;
            _brain.Bend = null;
        }

        _ledgerReady = false;                                                    // the shard cannot complete just now
        Assert.False((await AtReceiver()).Handled);
        _ledgerReady = true;
        Assert.True((await AtReceiver()).Spoke);
    }

    [Fact]
    public async Task AFullPackLeavesTheWholeSumOwedAndCollectPaysItOnceWithoutAnAllowancePlace()
    {
        var row = await Accepted();
        Errands.DropInPack = (_, _, _) => false;                                 // the stock code refuses: nothing changed
        var before = GoldInPack();
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal((ErrandState.Done, PayState.Owed, 150), (row.State, row.Coin, row.CoinDue));
        Assert.Equal(before, GoldInPack());
        Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));       // counted on the day it was EARNED

        Errands.Collect(_p);                                                     // still no room
        Assert.Equal(PayState.Owed, row.Coin);
        Errands.RoundTripForTests();                                             // a done row is kept while coin is owed
        row = Assert.Single(Errands.RowsForTests);

        Errands.Switch("completion", false);                                     // pay paused: collect is refused, coin kept
        Errands.DropInPack = (pack, m, item) => pack.TryDropItem(m, item, false);
        Errands.Collect(_p);
        Assert.Equal((PayState.Owed, before), (row.Coin, GoldInPack()));
        Errands.Switch("completion", true);
        Errands.RaiseFault("test");
        Errands.Collect(_p);
        Assert.Equal(PayState.Owed, row.Coin);
        Errands.ClearFault();

        Errands.Collect(_p);
        Assert.Equal((PayState.Paid, before + 150), (row.Coin, GoldInPack()));
        Errands.Collect(_p);                                                     // twice: the second finds nothing owed
        Assert.Equal(before + 150, GoldInPack());
        Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));
        Assert.Single(_recorded);                                                // collection writes no ledger event
    }

    [Fact]
    public async Task AThrowWhilePayingCoinKeepsTheClaimForReviewAndStopsLaterErrands()
    {
        var row = await Accepted();
        Errands.DropInPack = (_, _, _) => throw new InvalidOperationException("stock code");
        _p.Karma = 0;
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal((ErrandState.Done, PayState.Review, 150), (row.State, row.Coin, row.CoinDue));   // the amount due is KEPT
        Assert.Equal((PayState.Paid, 50), (row.Karma, row.KarmaGained));         // the other part was still attempted
        Assert.Contains("InvalidOperationException", row.Evidence);
        Assert.True(Errands.Faulted);
        Assert.False(Errands.CanComplete || Errands.PayAllowed);

        Errands.DropInPack = (pack, m, item) => pack.TryDropItem(m, item, false);
        var gold = GoldInPack();
        Errands.Collect(_p);                                                     // a part under review cannot be collected
        Assert.Equal((PayState.Review, gold), (row.Coin, GoldInPack()));
        Errands.RoundTripForTests();                                             // the fault and the row survive a restart
        Assert.True(Errands.Faulted);
        row = Assert.Single(Errands.RowsForTests);

        Assert.Contains("stay under review", Errands.ClearFault());             // clearing gives nothing and erases nothing
        Assert.Equal(PayState.Review, row.Coin);
        Assert.Equal("Nothing to settle there. Use: settle <n> coin paid|owed, or settle <n> karma closed.", Errands.Settle(row.Id, "karma", "closed", "Op"));
        Assert.Contains("settled as owed", Errands.Settle(row.Id, "coin", "owed", "Op"));
        Errands.Collect(_p);
        Assert.Equal((PayState.Paid, gold + 150), (row.Coin, GoldInPack()));
    }

    // Dev play test 2026-11-22: a character above 5,000 karma gains none (the stock routine takes 1 off per 100 held) and
    // the stock routine then says "You have lost a little karma". The stock message is off; ours is sent for a real gain only.
    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(6597, 0, 0)]
    public async Task TheKarmaMessageIsSentOnlyForARealGain(int held, int gained, int messages)
    {
        var row = await Accepted("introduce", "contact");
        var stockMessage = true;
        var said = new List<int>();
        Errands.AwardKarma = (m, n, message) =>
        {
            stockMessage = message;
            Server.Misc.Titles.AwardKarma(m, n, message);
        };
        Errands.KarmaGainMessage = (_, n) => said.Add(n);
        try
        {
            _p.Karma = held;
            Assert.True((await AtReceiver()).Spoke);
            Assert.False(stockMessage);
            Assert.Equal((PayState.Paid, gained, held + gained), (row.Karma, row.KarmaGained, _p.Karma));
            Assert.Equal(messages, said.Count);
        }
        finally
        {
            Errands.KarmaGainMessage = (m, n) => m.SendLocalizedMessage(n > 40 ? 1019062 : n > 20 ? 1019061 : n > 10 ? 1019060 : 1019059);
        }
    }

    // Codex "Errands 20" N1: a gain message that throws interrupts nothing - the pay state, the allowance place and the
    // townsperson's line are all as on a clean completion, and no fault is raised for a message.
    [Fact]
    public async Task AGainMessageThatThrowsLeavesTheBookkeepingWhole()
    {
        var row = await Accepted("introduce", "contact");
        Errands.KarmaGainMessage = (_, _) => throw new InvalidOperationException("the message could not be sent");
        try
        {
            _p.Karma = 0;
            Assert.True((await AtReceiver()).Spoke);
            Assert.Equal((ErrandState.Done, PayState.Paid, 50, 50), (row.State, row.Karma, row.KarmaGained, _p.Karma));
            Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));
            Assert.False(Errands.Faulted);
            Assert.Contains("karma message: InvalidOperationException", row.Evidence);
        }
        finally
        {
            Errands.KarmaGainMessage = (m, n) => m.SendLocalizedMessage(n > 40 ? 1019062 : n > 20 ? 1019061 : n > 10 ? 1019060 : 1019059);
        }
    }

    // ... and an award that threw still raises its fault, with the message failing after it.
    [Fact]
    public async Task AGainMessageThatThrowsDoesNotHideAnAwardFault()
    {
        var row = await Accepted("introduce", "contact");
        Errands.AwardKarma = (m, n, _) =>
        {
            m.Karma += n;
            throw new InvalidOperationException("the stock routine threw after assigning");
        };
        Errands.KarmaGainMessage = (_, _) => throw new InvalidOperationException("the message could not be sent");
        try
        {
            _p.Karma = 0;
            Assert.True((await AtReceiver()).Spoke);
            Assert.Equal((PayState.Review, 50), (row.Karma, row.KarmaGained));
            Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));
            Assert.True(Errands.Faulted);
        }
        finally
        {
            Errands.KarmaGainMessage = (m, n) => m.SendLocalizedMessage(n > 40 ? 1019062 : n > 20 ? 1019061 : n > 10 ? 1019060 : 1019059);
        }
    }

    [Fact]
    public async Task AThrowAfterKarmaChangedIsStillMeasuredAndCounted()
    {
        var row = await Accepted("introduce", "contact");
        Errands.AwardKarma = (m, n, _) =>
        {
            m.Karma += n;                                                        // the stock routine assigns first ...
            throw new InvalidOperationException("... and throws in its message or lock check");
        };
        _p.Karma = 0;
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal((PayState.None, 0), (row.Coin, row.CoinDue));               // an introduction pays no coin
        Assert.Equal((PayState.Review, 50), (row.Karma, row.KarmaGained));
        Assert.Equal(1, Errands.AllowanceForTests(_char, row.AccrualDay));       // karma was gained: a place was used
        Assert.True(Errands.Faulted);
        Assert.Contains("settled as closed", Errands.Settle(row.Id, "karma", "closed", "Op"));
        Assert.Equal(PayState.Paid, row.Karma);
    }

    [Fact]
    public async Task ARoundTripFixesTheAnswerOutwardAndPaysOnceOnTheReturn()
    {
        var row = await Accepted("ask_after", "cousin");
        var before = GoldInPack();
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal(ErrandState.Delivered, row.State);
        Assert.Equal(("a01", 64, 64), (row.AnswerKey, row.AnswerHash.Length, row.AnswerDigest.Length));
        Assert.Empty(_recorded);                                                 // outward: no event, no pay, no allowance
        Assert.Equal(before, GoldInPack());

        var ctx = await AtReceiver("and how is the weather");                    // the receiver again: an ordinary turn (v6:
        Assert.False(ctx.Handled);                                               // no line names the errand to the model)
        Assert.Null(ctx.Ask);
        Assert.Equal(Errands.SpeechHash(FakeBrain.DeliverText), row.DeliveryHash);

        var back = await BackAtSender();
        Assert.True(back.Spoke);
        var call = _brain.Proofs[^1];
        Assert.Equal("return", call.GetProperty("step").GetString());
        Assert.Equal("a01", call.GetProperty("answer").GetProperty("key").GetString());
        Assert.Equal((ErrandState.Done, 225), (row.State, row.CoinDue));         // 150 and half as much again
        Assert.Equal(before + 225, GoldInPack());
        Assert.Single(_recorded);
    }

    // Design 3.10: +50 is REQUESTED through the stock award; what is gained is max(50 - karma / 100, 0) in whole
    // numbers - 50 at 0, 25 at 2,500, nothing from 5,000 up, MORE below zero (100 at -5,000, 200 at -15,000).
    [Theory]
    [InlineData(0, 50)]
    [InlineData(2500, 25)]
    [InlineData(4999, 1)]
    [InlineData(5000, 0)]
    [InlineData(15000, 0)]
    [InlineData(-5000, 100)]
    [InlineData(-15000, 200)]
    [InlineData(-250, 52)]      // whole-number division cuts toward zero: -250 / 100 is -2, not -3
    [InlineData(-99, 50)]
    public void WhatTheStockAwardReallyGivesIsMeasuredAndRecorded(int karma, int gained)
    {
        var row = new ErrandRow { Id = 1, Character = _char, Kind = "introduce", KarmaRequested = 50 };
        _p.Karma = karma;
        Errands.PayKarma(_p, row, "2026-11-21");
        Assert.Equal((gained, PayState.Paid), (row.KarmaGained, row.Karma));
        Assert.Equal(karma + gained, _p.Karma);
        // an introduction (no coin) takes a place in the day's allowance only if karma was actually gained
        Assert.Equal(gained > 0 ? 1 : 0, Errands.AllowanceForTests(_char, "2026-11-21"));
        Assert.False(Errands.Faulted);
    }

    [Fact]
    public void ThreeAwardsFromTheVeryBottomGainFiveHundredAndNinetyFour()
    {
        _p.Karma = -15000;
        var total = 0;
        for (var i = 0; i < 3; i++)
        {
            var row = new ErrandRow { Id = i + 1, Character = _char, Kind = "word", CoinDue = 25, KarmaRequested = 50 };
            Errands.PayKarma(_p, row, "2026-11-21");
            total += row.KarmaGained;
        }

        Assert.Equal(594, total);                                                // 200 + 198 + 196 (Codex R2's arithmetic)
        Assert.Equal(0, Errands.AllowanceForTests(_char, "2026-11-21"));         // coin was due: the place is counted at mark, not here
    }

    [Fact]
    public void TheCoinTableAndRoundingDown()
    {
        int Coin(string kind, string band) => Errands.CoinFor(new ErrandRow { Kind = kind, Band = band });
        Assert.Equal((25, 150, 300), (Coin("word", "same"), Coin("word", "near"), Coin("word", "far")));
        Assert.Equal((37, 225, 450), (Coin("ask_after", "same"), Coin("ask_after", "near"), Coin("ask_after", "far")));
        Assert.Equal((0, 0), (Coin("introduce", "none"), Coin("introduce", "far")));
        Assert.Equal(0, Coin("word", "elsewhere"));
    }

    [Fact]
    public async Task PastTheDailyAllowanceAnErrandStillCompletesWithStandingOnly()
    {
        for (var i = 0; i < 4; i++)
        {
            _ownTime += Errands.CooldownSeconds;
            _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
            await Asked();
            _revision++;
            Assert.True((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Spoke);
            Assert.True((await AtReceiver()).Spoke);
        }

        Assert.Equal(4, _recorded.Count);
        var rows = Errands.RowsForTests;
        Assert.Equal(4, rows.Count);
        Assert.Equal((150, 150, 150, 0), (rows[0].CoinDue, rows[1].CoinDue, rows[2].CoinDue, rows[3].CoinDue));
        Assert.Equal((PayState.None, PayState.None, 0), (rows[3].Coin, rows[3].Karma, rows[3].KarmaRequested));
        Assert.Equal(3, Errands.AllowanceForTests(_char, rows[0].AccrualDay));
    }

    // ---- limits, deadlines, dropping ----

    [Fact]
    public async Task AnErrandLapsesWhenTheCharactersErrandTimeReachesItsDeadline()
    {
        var row = await Accepted();
        Errands.AddTimeForTests(_char, Errands.DeadlineSeconds - 1);
        Assert.False((await AtReceiver()).Handled == false && row.State != ErrandState.Done);     // one second early: it completes
        Assert.Equal(ErrandState.Done, row.State);

        _ownTime += Errands.CooldownSeconds;
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        var second = await AcceptedAgain();
        Errands.AddTimeForTests(_char, Errands.DeadlineSeconds);                                  // equal to the due value
        Assert.False((await AtReceiver()).Handled);
        Assert.Equal(ErrandState.Lapsed, second.State);
        Assert.Single(_recorded);
    }

    private async Task<ErrandRow> AcceptedAgain()
    {
        await Asked();
        _revision++;
        Assert.True((await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, false)).Spoke);
        return Errands.RowsForTests[^1];
    }

    [Fact]
    public async Task ARowSurvivesASaveAndLoadWithItsGuardsTimeAndStamps()
    {
        var row = await Accepted("introduce", "contact");
        Assert.True(Errands.IntroOfferedForTests(_senderId, _char));
        Errands.AddTimeForTests(_char, 500);
        Errands.RoundTripForTests();
        var loaded = Assert.Single(Errands.RowsForTests);
        Assert.NotSame(row, loaded);
        Assert.Equal((row.Id, row.Digest, row.Kind, row.Route, row.Sender, row.Receiver, row.DueAt, row.Band, row.SenderName),
            (loaded.Id, loaded.Digest, loaded.Kind, loaded.Route, loaded.Sender, loaded.Receiver, loaded.DueAt, loaded.Band, loaded.SenderName));
        Assert.True(Errands.IntroOfferedForTests(_senderId, _char));
        await Lease();
        Assert.True((await AtReceiver()).Spoke);                                                   // and it can be finished
        Assert.True(Errands.HeadStartForTests(_receiverId, _char));
        Assert.Contains("\"head_start\":true", _recorded[0].Facts);
        Errands.RoundTripForTests();
        Assert.True(Errands.HeadStartForTests(_receiverId, _char));                                // the guard outlives the row
        Assert.Empty(Errands.RowsForTests);                                                        // a settled done row is cleared
    }

    [Fact]
    public async Task AnIntroductionIsAskedOnceEverAndItsHeadStartIsGivenOnce()
    {
        await Accepted("introduce", "contact");
        Assert.True((await AtReceiver()).Spoke);
        Assert.Contains("\"head_start\":true", _recorded[0].Facts);

        // the same sender never asks this character for an introduction again; the brain is told so
        _ownTime += Errands.CooldownSeconds;
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        await Lease();
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false);
        var ask = JsonSerializer.Serialize(turn.Ask, Snake);
        Assert.Contains("\"intro_offered\":true", ask);
        Assert.Contains(_receiverId.ToString(), ask);                                              // and the receiver is blocked
        Assert.Null(Errands.AfterReply(_sender, _p, turn, AskProof(turn, "introduce", "contact"), true, _revision));
    }

    [Fact]
    public async Task AtMostThreeOpenAndNoneTwiceWithTheSameSender()
    {
        await Accepted();
        _ownTime += Errands.CooldownSeconds;
        await Lease();
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "good day", _revision, false);
        Assert.Null(turn.Ask);                                                                     // already carrying one for Tomas:
        Assert.Null(turn.TicketId);                                                                // an ordinary turn, and no new ask
        for (var i = 0; i < 2; i++)
        {
            Errands.AddRowForTests(new ErrandRow { Id = 90 + i, Character = _char, Sender = Guid.NewGuid(), State = ErrandState.Accepted, DueAt = long.MaxValue });
        }

        var other = Seat("Bertram", new Point3D(5912, 112, 0));
        _revision++;
        Assert.Null((await Errands.BeforeTurnAsync(other, _p, "good day", _revision, false)).Ask);  // three are open
    }

    [Fact]
    public async Task ErrandsYieldToAStoryCorrectionAndTheTicketIsStillUsedUp()
    {
        await Asked();
        _revision++;
        var turn = await Errands.BeforeTurnAsync(_sender, _p, "yes", _revision, true);
        Assert.False(turn.Handled);
        Assert.Null(turn.Ask);
        Assert.Null(Errands.TicketForTests(_sender, _char));
        Assert.Empty(Errands.RowsForTests);
    }

    [Fact]
    public async Task TheBrainSettingACompletionAsideRaisesTheFaultAndKillKeepsRows()
    {
        var row = await Accepted();
        AiLedger.ErrandFault = (id, why) => Errands.RaiseFault($"{why} ({id})");
        AiLedger.ErrandFault("8-5", "the brain set this completion record aside");
        Assert.True(Errands.Faulted);
        Assert.Contains("8-5", Errands.Fault);
        Assert.False((await AtReceiver()).Handled);
        Assert.Equal(ErrandState.Accepted, row.State);
        Errands.ClearFault();
        await Asked2ndTicketThenKill();
        Assert.Equal(ErrandState.Accepted, row.State);                                             // rows are kept and wait
    }

    private Task Asked2ndTicketThenKill()
    {
        var gen = Errands.Generation;
        Errands.Kill();
        Assert.False(Errands.Completion || Errands.Offer);
        Assert.Equal(gen + 1, Errands.Generation);
        Assert.Equal(0, Errands.WatchedForTests);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ADeletedCharactersRowsTimeAndGuardsGoWithIt()
    {
        await Accepted("introduce", "contact");
        Errands.Forget(_char);
        Assert.Empty(Errands.RowsForTests);
        Assert.False(Errands.IntroOfferedForTests(_senderId, _char));
    }

    // ---- through the real conversation code (TownsfolkTalk.ReplyAsync) ----

    // The model's side of a turn: an ordinary reply, and - only when the request carried an ask - the proof the brain's
    // fixed code would have put beside it.
    private sealed class Talker : IAiNpcClient
    {
        public int Calls;
        public readonly List<string> Errands = [];
        public readonly List<AiRequest> Requests = [];             // v6: what the model was really sent
        public Func<JsonElement, ErrandProof> Proof;
        public string Kind;
        public string Say = "Good day to you.";
        public int Exchanges;
        public TaskCompletionSource<AiReply> Hold;                 // a line that says "wait here" stays with the model

        public string Name => "talker";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, System.Threading.CancellationToken token)
        {
            if (Hold != null && request.Utterance == "wait here")
            {
                return Hold.Task;
            }

            if (request.Mode != "exchange")
            {
                Calls++;
                Requests.Add(request);
            }

            ErrandProof proof = null;
            if (request.Errand != null)
            {
                var ask = JsonDocument.Parse(JsonSerializer.Serialize(request.Errand, Snake)).RootElement.Clone();
                Errands.Add(ask.GetProperty("step").GetString());
                proof = ask.GetProperty("step").GetString() == "ask" ? Proof?.Invoke(ask) : null;
            }

            if (request.Mode == "exchange")
            {
                Exchanges++;
                return Task.FromResult(AiReply.Success("And good day to you, Tomas.", "test"));
            }

            return Task.FromResult(AiReply.Success(Say, "test") with { Errand = proof, Kind = Kind });
        }

        public Task WarmAsync(System.Threading.CancellationToken token) => Task.CompletedTask;
    }

    private Talker Talk()
    {
        var talker = new Talker();
        talker.Proof = ask => FakeBrain.Make(
            JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                step = "ask", gen = ask.GetProperty("gen").GetInt32(), request = ask.GetProperty("request").GetString(),
                ticket = ask.GetProperty("ticket").GetString(), @char = ask.GetProperty("char").GetString(),
                speech_rev = ask.GetProperty("speech_rev").GetInt32(), stamp = ask.GetProperty("stamp").GetString(),
                kind = "word", route = "cousin", sender = _senderId.ToString(), receiver = _receiverId.ToString(), line_key = "m11"
            }, Snake)).RootElement, _brain.Data);
        AiGateway.ResetForTests();
        AiGateway.Client = talker;
        Errands.Revision = Exchange.RevisionOf;
        return talker;
    }

    private async Task Says(Mobile npc, string text)
    {
        Exchange.NoteSpeech(_p);                                   // every line a player says is counted first
        await TownsfolkTalk.RespondForTests((BaseCreature)npc, _p, text, null);
    }

    [Fact]
    public async Task ThroughTheRealConversationCode_AskYesAndDeliveryWithOneModelCallInAll()
    {
        var talker = Talk();
        try
        {
            await Lease();
            await Says(_sender, "good day");                       // an ordinary reply, then the ask line
            Assert.Equal(1, talker.Calls);
            Assert.Equal(["ask"], talker.Errands);
            Assert.NotNull(Errands.TicketForTests(_sender, _char));

            await Says(_sender, "yes");                            // the shard's own match; no model call
            Assert.Equal(1, talker.Calls);
            var row = Assert.Single(Errands.RowsForTests);
            Assert.Equal(ErrandState.Accepted, row.State);

            await Says(_sender, "it looks like rain");             // still carrying it: an ordinary turn, nothing about the errand (v6)
            Assert.Equal(2, talker.Calls);
            Assert.Equal(["ask"], talker.Errands);

            _p.MoveToWorld(new Point3D(AwaySpot.X + 1, AwaySpot.Y, 0), Map.Felucca);
            var gold = GoldInPack();
            await Says(_receiver, "hello");                        // delivery: the code sentence, no model call
            Assert.Equal(2, talker.Calls);
            Assert.Equal(ErrandState.Done, row.State);
            Assert.Equal(gold + 150, GoldInPack());
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Fact]
    public async Task ThroughTheRealConversationCode_AnExchangeThatStartsHasTheTurnAndOneThatDoesNotLeavesTheAsk()
    {
        var talker = Talk();
        talker.Say = "Good day to you. And you, Elspeth - you look road-weary.";    // the reply speaks to her companion
        var companion = new ElspethVale();
        companion.MoveToWorld(new Point3D(5912, 110, 0), Map.Felucca);
        companion.SetControlMaster(_p);
        _made.Add(companion);
        var now = 1_000_000L;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => now;
        Server.Avallone.Named.NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 1;
        Exchange.Speak = (_, _) => { };
        Exchange.Online = _ => true;
        try
        {
            await Lease();
            var started = Exchange.Started;
            await Says(_sender, "good day");                       // an ask rode on this reply, AND an exchange started
            Assert.Equal(started + 1, Exchange.Started);
            Assert.Equal(["ask"], talker.Errands);
            Assert.Null(Errands.TicketForTests(_sender, _char));   // the exchange has the turn: nothing was asked
            Assert.Empty(Errands.RowsForTests);

            await Says(_sender, "yes");                            // so a yes now is only an ordinary line
            Assert.Empty(Errands.RowsForTests);
            Assert.Empty(_brain.Proofs);

            // That second reply named her too, but an exchange has just been had (pacing): none starts - and the ask is made.
            Assert.Equal(started + 1, Exchange.Started);
            Assert.NotNull(Errands.TicketForTests(_sender, _char));

            await Says(_sender, "yes");                            // and THIS yes answers a question that was really asked
            Assert.Equal(ErrandState.Accepted, Assert.Single(Errands.RowsForTests).State);
        }
        finally
        {
            Exchange.ResetForTests(false);
            Server.Avallone.Named.NamedNpcs.ResetForTests(false);
            AiGateway.SetEnabled(false, false);
            AiGateway.Clock = () => Core.TickCount;
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    // Codex Code R2 note 7: the model is busy with someone else's line - no exchange can start, so the ask is made.
    [Fact]
    public async Task ThroughTheRealConversationCode_WithTheModelBusyNoExchangeStartsAndTheAskIsMade()
    {
        var talker = Talk();
        talker.Say = "Good day to you. And you, Elspeth - you look road-weary.";
        talker.Hold = new TaskCompletionSource<AiReply>();
        var companion = new ElspethVale();
        companion.MoveToWorld(new Point3D(5912, 110, 0), Map.Felucca);
        companion.SetControlMaster(_p);
        _made.Add(companion);
        var now = 1_000_000L;
        var wasMax = AiGateway.MaxInFlight;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => now;
        AiGateway.MaxInFlight = 2;
        Server.Avallone.Named.NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.Speak = (_, _) => { };
        Exchange.Online = _ => true;
        try
        {
            await Lease();
            // somebody else's line is with the model, and stays there
            var elsewhere = AiGateway.RequestAsync(new AiRequest(Guid.NewGuid(), Guid.NewGuid(), null, "wait here", AiPriority.Conversation));
            Assert.Equal(1, AiGateway.InFlight);
            var started = Exchange.Started;
            await Says(_sender, "good day");
            Assert.Equal(started, Exchange.Started);               // the model is busy: nothing started ...
            Assert.NotNull(Errands.TicketForTests(_sender, _char)); // ... so the ask was made, as on any other turn
            talker.Hold.SetResult(AiReply.Success("Done.", "test"));
            await elsewhere;
        }
        finally
        {
            AiGateway.MaxInFlight = wasMax;
            Exchange.ResetForTests(false);
            Server.Avallone.Named.NamedNpcs.ResetForTests(false);
            AiGateway.SetEnabled(false, false);
            AiGateway.Clock = () => Core.TickCount;
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Fact]
    public async Task ThroughTheRealConversationCode_WithExchangesSwitchedOffTheAskIsMadeAsBefore()
    {
        var talker = Talk();
        talker.Say = "Good day to you. And you, Elspeth - you look road-weary.";
        var companion = new ElspethVale();
        companion.MoveToWorld(new Point3D(5912, 110, 0), Map.Felucca);
        companion.SetControlMaster(_p);
        _made.Add(companion);
        Exchange.ResetForTests(false);                             // exchanges off: none can start, whoever is named
        try
        {
            await Lease();
            var started = Exchange.Started;
            await Says(_sender, "good day");
            Assert.Equal(started, Exchange.Started);
            Assert.NotNull(Errands.TicketForTests(_sender, _char));
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Theory]
    [InlineData("decency")]
    [InlineData("shop")]
    public async Task ThroughTheRealConversationCode_NoAskAfterAReplyThatIsNotPlain(string kind)
    {
        var talker = Talk();
        talker.Kind = kind;
        try
        {
            await Lease();
            await Says(_sender, "good day");
            Assert.Equal(1, talker.Calls);
            Assert.Null(Errands.TicketForTests(_sender, _char));   // the ordinary reply was spoken alone
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Fact]
    public async Task ThroughTheRealConversationCode_WithTheSwitchesOffTheRequestCarriesNoErrand()
    {
        var talker = Talk();
        Errands.ResetForTests(false, false);
        Errands.Brain = _brain;
        try
        {
            await Says(_sender, "good day");
            Assert.Equal(1, talker.Calls);
            Assert.Empty(talker.Errands);
            Assert.Empty(_brain.Proofs);
            Assert.Equal(0, _brain.Probes);
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Fact]
    public void TheErrandFieldIsOnTheWireOnlyWhenSet()
    {
        var plain = new AiRequest(_senderId, _char, null, "hello", AiPriority.Conversation);
        Assert.DoesNotContain("\"errand\"", SidecarAiClient.BodyJson(plain, TimeSpan.FromSeconds(4)));
        var with = plain with { Errand = new { step = "ask", gen = 3, ticket = "t1", speech_rev = 7, intro_offered = false } };
        var body = SidecarAiClient.BodyJson(with, TimeSpan.FromSeconds(4));
        Assert.Contains("\"errand\":{\"step\":\"ask\",\"gen\":3,\"ticket\":\"t1\",\"speech_rev\":7,\"intro_offered\":false}", body);
    }

    [Fact]
    public void AProofOfTheWrongShapeIsNoProof()
    {
        Assert.Null(ErrandProof.Parse(null));
        Assert.Null(ErrandProof.Parse(JsonDocument.Parse("[1]").RootElement));
        var p = ErrandProof.Parse(JsonDocument.Parse(
            "{\"step\":\"ask\",\"kind\":7,\"digest\":\"d\",\"answer\":{\"key\":\"a01\",\"hash\":\"short\",\"digest\":\"x\"}," +
            "\"echo\":{\"gen\":\"3\",\"row\":12,\"ticket\":\"t\"}}").RootElement);
        Assert.Equal(("ask", null, "d"), (p.Step, p.Kind, p.Digest));
        Assert.Null(p.Answer);                                     // a hash that is not 64 characters: absent
        Assert.Equal((null, 12, "t"), (p.Gen, p.Row, p.Ticket));   // a number given as text: absent
    }

    [Theory]
    [InlineData("Word from my cousin Tomas? Thank you for bringing it.", true)]
    [InlineData("That is 5 more than I hoped.", false)]
    [InlineData("There is gold in it.", false)]
    [InlineData("I will pay you well.", false)]
    [InlineData("Good custom and fair weather to you.", true)]
    [InlineData("A golden morning, and a payment of thanks.", false)]
    [InlineData("A golden morning to you.", true)]
    [InlineData("", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("a {slot} left", false)]
    public void TheShardsOwnCheckOnACodeWrittenLine(string line, bool ok) => Assert.Equal(ok, Errands.Sayable(line));

    // ================================================================================================================
    // Design change v6 (design 12; Codex "Errands-Redesign" R1 and R2): code answers questions about an errand.
    // No model exists in these tests either; "no brain call" is counted on the stand-in's proof list.
    // ================================================================================================================

    private const string AskLine = FakeBrain.AskText;
    private const string DeliverLine = FakeBrain.DeliverText;
    private const string SeeLog = "Say [Errands to see what you are carrying.";
    private const string NotRecalled = "The exact words cannot be recalled just now.";

    private Task<Errands.Turn> Line(BaseCreature npc, string text)
    {
        _revision++;
        return Errands.BeforeTurnAsync(npc, _p, text, _revision, false);
    }

    private static string Step(JsonElement call) => call.GetProperty("step").GetString();

    // A row put straight into the store, shaped as the stand-in brain will prove it.
    private ErrandRow Row(int id, ErrandState state, Guid sender, Guid receiver, Guid? character = null, string kind = null)
    {
        kind ??= state == ErrandState.Delivered ? "ask_after" : "word";
        var row = new ErrandRow
        {
            Id = id, Version = 1, Kind = kind, Route = "cousin", Character = character ?? _char, Sender = sender,
            SenderMap = "Felucca", SenderTown = "Hometown", Receiver = receiver, ReceiverMap = "Felucca",
            ReceiverTown = "Awaytown", LineKey = kind == "word" ? "m11" : null, ListsVersion = 1,
            Digest = FakeBrain.Digest(kind, sender.ToString(), receiver.ToString()), Band = "near", State = state,
            DueAt = long.MaxValue, SenderName = "Tomas", ReceiverName = "Anna", AskSeed = "seed-" + id,
            AskHash = Errands.SpeechHash(AskLine)
        };
        if (state == ErrandState.Delivered)
        {
            (row.AnswerKey, row.AnswerHash, row.AnswerDigest) = ("a01", new string('b', 64), new string('c', 64));
            row.DeliveryHash = Errands.SpeechHash(DeliverLine);
        }

        Errands.AddRowForTests(row);
        return row;
    }

    // ---- the hashes --------------------------------------------------------------------------------------------

    [Fact]
    public void TheSpeechHashIsPlainSha256OfTheUtf8SentenceAndNothingIsNormalised()
    {
        // the same vector is asserted in the brain's tests (test_errands_brain.Remind): an em dash hashes alike on both sides
        Assert.Equal("4acc4fce1ab87956d626327d0d9536e09934eb6c91bd93212c04dfeaaf5be28f", Errands.SpeechHash("Hm. Never mind — it will keep."));
        Assert.NotEqual(Errands.SpeechHash("A word."), Errands.SpeechHash("A word. "));
        Assert.NotEqual(Errands.SpeechHash("A word."), Errands.SpeechHash("a word."));
        Assert.NotEqual(Errands.SpeechHash("A - word."), Errands.SpeechHash("A — word."));
        Assert.Null(Errands.SpeechHash(null));
        Assert.True(Errands.Sayable(Errands.RewardLine) && Errands.Sayable(Errands.AsHeardLine));
    }

    [Fact]
    public async Task AcceptingStoresTheSeedAndTheHashOfTheAskAsItWasSaidAndTheOutwardDeliveryItsOwn()
    {
        await Asked("ask_after", "cousin");
        var ticket = Errands.TicketForTests(_sender, _char).Id;
        var row = Assert.Single((await YesThenRows()));
        Assert.Equal((ticket, Errands.SpeechHash(AskLine), (string)null), (row.AskSeed, row.AskHash, row.DeliveryHash));
        Assert.True((await AtReceiver()).Spoke);
        Assert.Equal(Errands.SpeechHash(DeliverLine), row.DeliveryHash);
        Assert.Equal(ticket, row.AskSeed);                                       // never regenerated
    }

    private async Task<IReadOnlyList<ErrandRow>> YesThenRows()
    {
        Assert.True((await Line(_sender, "yes")).Spoke);
        return Errands.RowsForTests;
    }

    [Fact]
    public async Task TheSeedAndBothHashesSurviveASaveAndLoadAndTheReminderStillWorks()
    {
        var row = await Accepted("ask_after", "cousin");
        Assert.True((await AtReceiver()).Spoke);
        var (seed, ask, delivery) = (row.AskSeed, row.AskHash, row.DeliveryHash);
        Errands.RoundTripForTests();
        var loaded = Assert.Single(Errands.RowsForTests);
        Assert.NotSame(row, loaded);
        Assert.Equal((seed, ask, delivery), (loaded.AskSeed, loaded.AskHash, loaded.DeliveryHash));
        await Lease();
        var turn = await AtReceiver("what shall I tell them?");
        Assert.Equal(DeliverLine, turn.Said);
    }

    [Fact]
    public async Task ARowSavedBeforeV6LoadsWithoutThemAndIsNeverRepeatedWordForWord()
    {
        await Accepted();
        Errands.RoundTripAsVersionZeroForTests();
        var loaded = Assert.Single(Errands.RowsForTests);
        Assert.Equal(((string)null, (string)null, (string)null), (loaded.AskSeed, loaded.AskHash, loaded.DeliveryHash));
        Assert.Equal((ErrandState.Accepted, _senderId, "Tomas the tailor"), (loaded.State, loaded.Sender, loaded.SenderName));   // the rest is whole
        await Lease();
        var calls = _brain.Proofs.Count;
        var turn = await Line(_sender, "what was the errand?");
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal([SeeLog], _told);
        Assert.Equal(calls, _brain.Proofs.Count);                                // no call is made for it
        await Errands.ShowForTests(_p, loaded.Id);
        Assert.Equal(NotRecalled, _told[^1]);
        Assert.Equal(calls, _brain.Proofs.Count);
    }

    // ---- a question on a fresh ask: the offer stays open, once (decided by the owner 2026-11-21) -----------------------------

    [Theory]
    [InlineData("what would I get?", Errands.RewardLine)]
    [InlineData("What is in it for me, Tomas?", Errands.RewardLine)]
    [InlineData("How much does it pay?", Errands.RewardLine)]              // the owner on live, 2026-11-22: this closed the offer
    [InlineData("yes, but what does it pay", Errands.RewardLine)]          // not a yes; a pay word right after an ask
    [InlineData("what did you want?", AskLine)]
    [InlineData("say it again", AskLine)]
    public async Task OneQuestionOnAFreshAskIsAnsweredByCodeAndTheOfferStaysOpen(string text, string answer)
    {
        await Asked();
        var ticket = Errands.TicketForTests(_sender, _char);
        var (id, at, seats, stamp, gen, proof) = (ticket.Id, ticket.At, ticket.Seats, ticket.Stamp, ticket.Gen, ticket.Proof);
        var askedAt = Errands.AskedAtForTests(_senderId, _char);
        Assert.Equal(_ownTime, askedAt);
        _now += TimeSpan.FromSeconds(30);
        _ownTime += 30;
        var turn = await Line(_sender, text);
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Equal(answer, turn.Said);
        Assert.Null(turn.Ask);                                                   // no model turn
        Assert.Empty(_brain.Proofs);                                             // and no brain call
        Assert.Same(ticket, Errands.TicketForTests(_sender, _char));             // the SAME ticket, put back
        Assert.Equal((id, at, seats, stamp, gen), (ticket.Id, ticket.At, ticket.Seats, ticket.Stamp, ticket.Gen));
        Assert.Same(proof, ticket.Proof);
        Assert.True(ticket.AskedOnce && !ticket.Broken);
        Assert.Equal(_revision, ticket.Revision);                                // the next line is the one after the question
        Assert.Equal(1, Errands.WatchedForTests);
        Assert.Equal(askedAt, Errands.AskedAtForTests(_senderId, _char));        // the cooldown is not started again

        var row = Assert.Single(await YesThenRows());                            // yes, still within the original 120 seconds
        Assert.Equal((id, Errands.SpeechHash(AskLine)), (row.AskSeed, row.AskHash));
        Assert.Equal("accept", Step(Assert.Single(_brain.Proofs)));              // the full v5 verification, once
        Assert.Equal(0, Errands.WatchedForTests);
    }

    [Fact]
    public async Task TheOriginalHundredAndTwentySecondsAreNotExtendedByAQuestion()
    {
        await Asked();
        var at = Errands.TicketForTests(_sender, _char).At;
        _now += TimeSpan.FromSeconds(100);
        Assert.Equal(Errands.RewardLine, (await Line(_sender, "what does it pay?")).Said);
        Assert.Equal(at, Errands.TicketForTests(_sender, _char).At);
        _now += TimeSpan.FromSeconds(21);                                        // 121 seconds after the ask
        await Lease();
        Assert.False((await Line(_sender, "yes")).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task AQuestionAnsweredAtTheVeryEndOfTheTicketsLifeDoesNotPutBackADeadTicket()
    {
        await Asked();
        _now += Errands.TicketLife + TimeSpan.FromSeconds(1);
        var turn = await Line(_sender, "what does it pay?");                     // no longer live: an ordinary line, no ticket
        Assert.False(turn.Handled);
        Assert.Null(turn.Ask);
        Assert.Null(Errands.TicketForTests(_sender, _char));
        Assert.Equal(0, Errands.WatchedForTests);
    }

    [Fact]
    public async Task ASecondQuestionIsAnyOtherLineAndTheMatterIsClosed()
    {
        await Asked();
        Assert.True((await Line(_sender, "what did you want?")).Spoke);
        var second = await Line(_sender, "what would I get?");
        Assert.False(second.Handled);
        Assert.Equal("{\"step\":\"closed\"}", JsonSerializer.Serialize(second.Ask, Snake));
        Assert.Null(Errands.TicketForTests(_sender, _char));
        Assert.Equal(0, Errands.WatchedForTests);
        Assert.False((await Line(_sender, "yes")).Handled);
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task AfterAQuestionNoStillGetsTheNoLineAndAYesWithAQuestionMarkIsNeverConsent()
    {
        await Asked();
        Assert.True((await Line(_sender, "what would I get?")).Spoke);
        Assert.Equal("No matter. It will keep.", (await Line(_sender, "no thanks")).Said);
        Assert.Empty(Errands.RowsForTests);

        _ownTime += Errands.CooldownSeconds;
        await Asked();
        var turn = await Line(_sender, "yes?");
        Assert.False(turn.Handled);
        Assert.Equal("{\"step\":\"closed\"}", JsonSerializer.Serialize(turn.Ask, Snake));
        Assert.Empty(Errands.RowsForTests);

        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what would I get?")).Spoke);
        Assert.False((await Line(_sender, "yes?")).Handled);                     // after a question too
        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task EveryVoidingRuleIsCarriedOverTheQuestion()
    {
        // out of range and straight back
        await Asked();
        Assert.True((await Line(_sender, "what would I get?")).Spoke);
        _p.MoveToWorld(new Point3D(5935, 135, 0), Map.Felucca);
        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        Assert.True(Errands.TicketForTests(_sender, _char).Broken);              // still watched after it was put back
        Assert.False((await Line(_sender, "yes")).Handled);

        // a line to someone else between the question and the yes
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what would I get?")).Spoke);
        _revision++;
        Assert.False((await Line(_sender, "yes")).Handled);

        // the town list changed
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what did you want?")).Spoke);
        _stamp = "stamp-2";
        Assert.False((await Line(_sender, "yes")).Handled);
        _stamp = "stamp-1";

        // a seat or quarantine change (the counter that only goes up)
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what did you want?")).Spoke);
        var seats = Seats.Revision + 1;
        Errands.SeatsRevision = () => seats;
        Assert.False((await Line(_sender, "yes")).Handled);
        Errands.SeatsRevision = () => Seats.Revision;

        // the generation moved
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what did you want?")).Spoke);
        Errands.Switch("offer", false);
        Errands.Switch("offer", true);
        Assert.False((await Line(_sender, "yes")).Handled);

        // an exchange with the player's companion started
        _ownTime += Errands.CooldownSeconds;
        await Asked();
        Assert.True((await Line(_sender, "what did you want?")).Spoke);
        Errands.ExchangeStarted(_p);
        Assert.False((await Line(_sender, "yes")).Handled);

        Assert.Empty(Errands.RowsForTests);
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task ATicketThatIsNotLiveAnswersNoQuestion()
    {
        await Asked();
        Errands.TicketForTests(_sender, _char).Broken = true;
        var turn = await Line(_sender, "what did you want?");
        Assert.False(turn.Handled);
        Assert.Null(turn.Ask);
        Assert.Null(Errands.TicketForTests(_sender, _char));

        _ownTime += Errands.CooldownSeconds;
        await Asked();
        _revision++;                                                             // not the player's very next line
        Assert.False((await Line(_sender, "what did you want?")).Handled);
        Assert.Null(Errands.TicketForTests(_sender, _char));
    }

    [Fact]
    public async Task IfTheReplyCannotBeSpokenTheTicketIsNotPutBack()
    {
        await Asked();
        Errands.TicketForTests(_sender, _char).Proof.AskLine = "Carry 5 of them.";   // a line the shard's own check stops
        var turn = await Line(_sender, "what did you want?");
        Assert.True(turn.Handled);
        Assert.False(turn.Spoke);
        Assert.Null(Errands.TicketForTests(_sender, _char));
        Assert.Equal(0, Errands.WatchedForTests);
    }

    [Fact]
    public async Task ErrandsStillYieldAndTheTicketIsUsedUpEvenByAListedQuestion()
    {
        await Asked();
        _revision++;
        Assert.False((await Errands.BeforeTurnAsync(_sender, _p, "what did you want?", _revision, true)).Handled);
        Assert.Null(Errands.TicketForTests(_sender, _char));
    }

    [Fact]
    public async Task AListedPhraseWithNoTicketAndNoOpenRowIsOrdinaryTalk()
    {
        await Lease();
        Errands.Dice = _ => 1;                                                   // and no ask this time
        foreach (var text in new[] { "what does it pay?", "what was the errand?", "remind me" })
        {
            var turn = await Line(_sender, text);
            Assert.False(turn.Handled);
            Assert.Null(turn.Ask);
        }

        Assert.Empty(_brain.Proofs);
        Assert.Empty(_told);
    }

    // ---- exactly one open row with this townsperson --------------------------------------------------------------

    [Fact]
    public async Task TheSenderRepeatsTheAskWordForWordFromAProofMadeWithTheStoredSeed()
    {
        var row = await Accepted();
        var (version, state) = (row.Version, row.State);
        var turn = await Line(_sender, "what was it you wanted me to do again, Tomas?");
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Equal(AskLine, turn.Said);
        Assert.Null(turn.Ask);
        var call = _brain.Proofs[^1];
        Assert.Equal(2, _brain.Proofs.Count);                                    // accept, and this one
        Assert.Equal(("remind", "sender", row.AskSeed, row.Id, version),
            (Step(call), call.GetProperty("role").GetString(), call.GetProperty("seed").GetString(),
                call.GetProperty("row").GetInt32(), call.GetProperty("version").GetInt32()));
        Assert.False(call.TryGetProperty("utterance", out _));                   // ids and keys only
        Assert.Equal(JsonValueKind.Null, call.GetProperty("answer").ValueKind);
        Assert.Equal((version, state), (row.Version, row.State));                // nothing moved
        Assert.Empty(_recorded);
        Assert.Empty(_told);
    }

    [Fact]
    public async Task TheRewardQuestionGetsTheOneFixedLineWithNoBrainCallAtAll()
    {
        var row = await Accepted();
        var before = GoldInPack();
        _brain.Down = true;                                                      // the brain is not needed for it
        foreach (var text in new[] { "what does it pay?", "is there a reward", "What will you give me for it, Tomas?" })
        {
            var turn = await Line(_sender, text);
            Assert.True(turn.Handled && turn.Spoke);
            Assert.Equal(Errands.RewardLine, turn.Said);
        }

        Assert.Single(_brain.Proofs);                                            // the accept, long before
        Assert.Equal((1, ErrandState.Accepted, before), (row.Version, row.State, GoldInPack()));
        Assert.Empty(_told);
    }

    [Fact]
    public async Task TheBroadPhrasesAreNotReadOnceTheAskIsBehindUs()
    {
        await Accepted();
        foreach (var text in new[] { "remind me", "what do I get?", "tell me again", "what should I say?" })
        {
            var turn = await Line(_sender, text);
            Assert.False(turn.Handled);                                          // an ordinary turn
            Assert.Null(turn.Ask);
        }

        Assert.Single(_brain.Proofs);
    }

    // Codex RD1: under an EQUAL digest the rebuilt sentence can differ (a changed trade; the same facts with another
    // wording seed; an edited template). The stored hash is what refuses it.
    [Theory]
    [InlineData("Would you carry a word to my cousin Anna, a baker in Awaytown? Tell her that I send my love.")]
    [InlineData("A favour, if you are willing. My cousin Anna is a tailor in Awaytown. Tell her that I send my love.")]
    [InlineData("Would you carry a word to my cousin Anna, a tailor in Awaytown? Tell her that I send my love. ")]
    [InlineData("would you carry a word to my cousin Anna, a tailor in Awaytown? Tell her that I send my love.")]
    public async Task ASentenceThatWouldNowComeOutDifferentlyIsNotSaid(string rebuilt)
    {
        var row = await Accepted();
        _brain.Bend = p => p.Line = rebuilt;                                     // the digest still matches
        var turn = await Line(_sender, "what was the errand?");
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal([SeeLog], _told);
        Assert.Equal(ErrandState.Accepted, row.State);
    }

    [Fact]
    public async Task EveryProofCheckStillAppliesBesideTheHash()
    {
        var row = await Accepted();
        var bends = new Action<ErrandProof>[]
        {
            p => p.Digest = new string('0', 64), p => p.Step = "accept", p => p.Row = row.Id + 1, p => p.Version = 9,
            p => p.Gen = 99, p => p.Request = "q0", p => p.SpeechRev = 1, p => p.Stamp = "stamp-0", p => p.DataGen = "data-0",
            p => p.Kind = "ask_after", p => p.Route = "kin", p => p.Sender = Guid.NewGuid().ToString(),
            p => p.Receiver = Guid.NewGuid().ToString(), p => p.Line = null, p => p.Line = "Carry 5 of them.",
            p => p.Char = Guid.NewGuid().ToString(), p => p.Char = null         // Codex "Errands 15", note 1
        };
        foreach (var bend in bends)
        {
            _told.Clear();
            _brain.Bend = bend;
            await Lease();
            var turn = await Line(_sender, "what was the errand?");
            Assert.True(turn.Handled && turn.Spoke);
            Assert.Contains(turn.Said, Errands.Neutral);                         // the right words came back, and are not said
            Assert.Equal([SeeLog], _told);
        }

        _brain.Bend = null;
        await Lease();
        Assert.Equal(AskLine, (await Line(_sender, "what was the errand?")).Said);
        Assert.Equal((1, ErrandState.Accepted), (row.Version, row.State));
    }

    [Fact]
    public async Task WithTheBrainAwayNoProofOrNoLeaseTheReminderIsANeutralLineAndThePointerToTheLog()
    {
        await Accepted();
        _brain.NoProof = true;
        var turn = await Line(_sender, "what was the errand?");
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal([SeeLog], _told);

        _brain.NoProof = false;
        _brain.Down = true;
        turn = await Line(_sender, "what was the errand?");
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.False(Errands.LeaseHeld);                                         // a failed call ends the lease, as ever

        var calls = _brain.Proofs.Count;
        turn = await Line(_sender, "what was the errand?");                      // no lease: no call is even made
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal(calls, _brain.Proofs.Count);
        Assert.Equal([SeeLog, SeeLog, SeeLog], _told);
    }

    [Fact]
    public async Task AReminderThatComesBackLateOrAfterTheyPartedSaysNothingOfSubstance()
    {
        var row = await Accepted();
        _brain.Gate = new TaskCompletionSource<bool>();
        var pending = Line(_sender, "what was the errand?");
        _revision++;                                                             // the player spoke again while it was out
        _brain.Gate.SetResult(true);
        var turn = await pending;
        Assert.Contains(turn.Said, Errands.Neutral);

        _brain.Gate = new TaskCompletionSource<bool>();
        pending = Line(_sender, "what was the errand?");
        _p.MoveToWorld(new Point3D(AwaySpot.X + 1, AwaySpot.Y, 0), Map.Felucca);   // walked off
        _brain.Gate.SetResult(true);
        turn = await pending;
        Assert.True(turn.Handled);
        Assert.False(turn.Spoke);

        _p.MoveToWorld(new Point3D(5911, 110, 0), Map.Felucca);
        _brain.Gate = new TaskCompletionSource<bool>();
        pending = Line(_sender, "what was the errand?");
        row.Version++;                                                           // the row changed underneath
        _brain.Gate.SetResult(true);
        Assert.Contains((await pending).Said, Errands.Neutral);
    }

    [Fact]
    public async Task TheRelativeRepeatsTheirOwnSentenceFromTheStoredAnswerAndNeverANewOne()
    {
        var row = await Accepted("ask_after", "cousin");
        Assert.True((await AtReceiver()).Spoke);
        var version = row.Version;
        var turn = await AtReceiver("What shall I tell them?");
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Equal(DeliverLine, turn.Said);
        var call = _brain.Proofs[^1];
        Assert.Equal(("remind", "receiver", row.Id.ToString(), "a01"),
            (Step(call), call.GetProperty("role").GetString(), call.GetProperty("seed").GetString(),
                call.GetProperty("answer").GetProperty("key").GetString()));
        Assert.Equal((row.AnswerHash, row.AnswerDigest),
            (call.GetProperty("answer").GetProperty("hash").GetString(), call.GetProperty("answer").GetProperty("digest").GetString()));
        Assert.Equal((version, ErrandState.Delivered, "a01"), (row.Version, row.State, row.AnswerKey));
        Assert.Empty(_recorded);
        Assert.Empty(_told);
        Assert.Equal(Errands.RewardLine, (await AtReceiver("what does it pay?")).Said);
    }

    [Fact]
    public async Task ARelativeWhoseSentenceCanNoLongerBeSaidExactlySaysTheAsYouHeardItLine()
    {
        var row = await Accepted("ask_after", "cousin");
        Assert.True((await AtReceiver()).Spoke);
        _brain.Bend = p => (p.Line, p.AnswerChanged) = (null, true);             // the stored answer no longer binds
        var turn = await AtReceiver("what shall I tell them?");
        Assert.Equal(Errands.AsHeardLine, turn.Said);
        Assert.Equal([SeeLog], _told);

        _told.Clear();
        _brain.Bend = p => p.Line = "Word from my cousin Tomas? Thank you for bringing it";   // a template edited by one mark
        Assert.Equal(Errands.AsHeardLine, (await AtReceiver("what shall I tell them?")).Said);

        _brain.Bend = p => p.Digest = new string('0', 64);                        // not proved current: not even that
        Assert.Contains((await AtReceiver("what shall I tell them?")).Said, Errands.Neutral);
        Assert.Equal((ErrandState.Delivered, "a01"), (row.State, row.AnswerKey));
        Assert.Equal([SeeLog, SeeLog], _told);
    }

    [Fact]
    public async Task OnlyARowInPlayWithThisTownspersonCounts()
    {
        await Lease();
        Errands.Dice = _ => 1;
        var other = Seat("Bertram", new Point3D(5912, 112, 0));
        var otherId = AiIdentity.GetOrCreate(other);
        Row(50, ErrandState.Accepted, otherId, _receiverId);                     // another townsperson's errand
        Row(51, ErrandState.Accepted, _senderId, _receiverId, Guid.NewGuid());   // another character's
        Row(52, ErrandState.Done, _senderId, _receiverId);                       // done
        Row(53, ErrandState.Lapsed, _senderId, _receiverId);                     // lapsed
        foreach (var text in new[] { "what was the errand?", "what does it pay?", "what shall I tell them?" })
        {
            var turn = await Line(_sender, text);
            Assert.False(turn.Handled);                                          // an ordinary turn
            Assert.Null(turn.Ask);
        }

        Assert.Empty(_brain.Proofs);
        Assert.Empty(_told);
        Assert.Equal(AskLine, (await Line(other, "what was the errand?")).Said);   // ... and it IS in play with Bertram
    }

    [Fact]
    public async Task OnAnyOtherLineTheModelGetsNothingOfTheErrandAndNoNewAskIsMade()
    {
        var row = await Accepted("ask_after", "cousin");
        _ownTime += Errands.CooldownSeconds;
        var turn = await Line(_sender, "it looks like rain");
        Assert.False(turn.Handled);
        Assert.Null(turn.Ask);                                                   // the pointer line is off unless measured in
        Assert.Null(turn.TicketId);
        Assert.Single(_brain.Proofs);                                            // v5's proof call on every such turn is gone

        var generation = Errands.Generation;
        Errands.Switch("pointer", true);
        Assert.Equal(generation, Errands.Generation);                            // no row, ticket or proof depends on this switch
        Assert.Equal("{\"step\":\"pointer\",\"role\":\"sender\"}", JsonSerializer.Serialize((await Line(_sender, "and you?")).Ask, Snake));
        Assert.True((await AtReceiver()).Spoke);                                 // outward delivery
        var there = await AtReceiver("fine weather");
        Assert.Equal("{\"step\":\"pointer\",\"role\":\"receiver\"}", JsonSerializer.Serialize(there.Ask, Snake));
        Assert.Null(there.TicketId);                                             // the relative makes no ask of their own meanwhile

        // rows in both roles at one townsperson: the wording is fixed by rule - the sender's (Codex R2 note 6)
        Row(60, ErrandState.Accepted, _receiverId, _senderId);
        Assert.Equal("{\"step\":\"pointer\",\"role\":\"sender\"}", JsonSerializer.Serialize((await AtReceiver("hm")).Ask, Snake));
        Errands.Switch("pointer", false);
        Assert.Null((await AtReceiver("hm")).Ask);
        Assert.Equal(ErrandState.Delivered, row.State);
    }

    // ---- more than one row: no guess (Codex RD3) -----------------------------------------------------------------

    [Fact]
    public async Task TwoSendersRoundTripsAtOneRelativeAreNotGuessedBetween()
    {
        await Lease();
        var other = Seat("Bertram", new Point3D(5912, 112, 0));
        Row(70, ErrandState.Delivered, _senderId, _receiverId);
        Row(71, ErrandState.Delivered, AiIdentity.GetOrCreate(other), _receiverId);
        var turn = await AtReceiver("what shall I tell them?");
        Assert.True(turn.Handled && turn.Spoke);
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal(["You carry more than one errand that concerns Anna. Say [Errands to see them."], _told);
        Assert.Empty(_brain.Proofs);                                             // neither is asked about
        Assert.Equal(Errands.RewardLine, (await AtReceiver("what does it pay?")).Said);   // the same for every row
        Assert.Empty(_brain.Proofs);
    }

    [Fact]
    public async Task ATownspersonWhoIsSenderOfOneAndRelativeOfAnotherDoesNotGuessEither()
    {
        await Lease();
        Row(72, ErrandState.Delivered, _senderId, _receiverId);
        var hers = Row(73, ErrandState.Accepted, _receiverId, _senderId);        // Anna's own errand, to Tomas
        (hers.SenderTown, hers.ReceiverTown, hers.SenderName, hers.ReceiverName) = ("Awaytown", "Hometown", "Anna", "Tomas");
        var turn = await AtReceiver("what was the errand?");
        Assert.Contains(turn.Said, Errands.Neutral);
        Assert.Equal(["You carry more than one errand that concerns Anna. Say [Errands to see them."], _told);
        Assert.Empty(_brain.Proofs);

        // ... and [Errands show gives the exact words for each, chosen by number
        _told.Clear();
        await Errands.ShowForTests(_p, 72);
        Assert.Equal($"Anna said: \"{DeliverLine}\"", _told[^1]);
        _now += Errands.ShowCooldown;
        await Errands.ShowForTests(_p, 73);
        Assert.Equal($"Anna said: \"{AskLine}\"", _told[^1]);                    // (the stand-in brain's one ask sentence)
        Assert.Equal(["receiver", "sender"], _brain.Proofs.ConvertAll(c => c.GetProperty("role").GetString()));
    }

    // ---- [Errands show <number> ----------------------------------------------------------------------------------

    // the owner, dev play test 2026-11-22: both people are named with their trade, to help track them down.
    [Fact]
    public async Task TheLogNamesBothPeopleWithTheirTrade()
    {
        var (was, hers) = (_sender.Title, _receiver.Title);
        _sender.Title = "the innkeeper";
        _receiver.Title = " the armorer ";
        try
        {
            var row = await Accepted();
            Assert.Equal(("Tomas the innkeeper", "Anna the armorer"), (row.SenderName, row.ReceiverName));
            await Errands.ShowForTests(_p, row.Id);
            Assert.StartsWith($"{row.Id}. Carry word from Tomas the innkeeper of Hometown to Anna the armorer in Awaytown.", _told[0]);
            Assert.Null(Errands.NameWithTrade(null));
        }
        finally
        {
            _sender.Title = was;
            _receiver.Title = hers;
        }
    }

    [Fact]
    public async Task ShowGivesTheLogLineThenTheExactWordsFromAnywhereAndMovesNothing()
    {
        var row = await Accepted();
        _p.MoveToWorld(new Point3D(AwaySpot.X + 20, AwaySpot.Y + 20, 0), Map.Felucca);   // nowhere near Tomas
        var before = GoldInPack();
        await Errands.ShowForTests(_p, row.Id);
        Assert.Equal(2, _told.Count);
        Assert.StartsWith($"{row.Id}. Carry word from Tomas the tailor of Hometown to Anna the tailor in Awaytown.", _told[0]);
        Assert.Equal($"Tomas the tailor said: \"{AskLine}\"", _told[1]);
        var call = _brain.Proofs[^1];
        Assert.Equal(("remind", "sender", row.AskSeed), (Step(call), call.GetProperty("role").GetString(), call.GetProperty("seed").GetString()));
        Assert.Equal((1, ErrandState.Accepted, before), (row.Version, row.State, GoldInPack()));
        Assert.Empty(_recorded);
        Assert.Equal(0, Errands.ShowOutForTests);
    }

    [Fact]
    public async Task ShowRefusesWhatItCannotProveWordForWord()
    {
        var row = await Accepted();
        foreach (var bend in new Action<ErrandProof>[]
                 {
                     p => p.Line = AskLine + " ", p => p.Digest = new string('0', 64), p => p.Step = "accept",
                     p => p.Version = 7, p => p.Gen = 99, p => p.Request = "q0", p => p.Stamp = "stamp-0", p => p.DataGen = "data-0",
                     p => p.SpeechRev = -1, p => p.Char = Guid.NewGuid().ToString(), p => p.Char = null
                 })
        {
            _told.Clear();
            _brain.Bend = bend;
            await Lease();
            _now += Errands.ShowCooldown;
            await Errands.ShowForTests(_p, row.Id);
            Assert.Equal(2, _told.Count);
            Assert.Equal(NotRecalled, _told[1]);
        }

        _told.Clear();
        _now += Errands.ShowCooldown;
        await Errands.ShowForTests(_p, row.Id + 40);                              // no such row
        await Errands.ShowForTests(_p, Row(80, ErrandState.Accepted, _senderId, _receiverId, Guid.NewGuid()).Id);   // not theirs
        await Errands.ShowForTests(_p, Row(81, ErrandState.Done, _senderId, _receiverId).Id);                       // not open
        Assert.Equal(["You are carrying no errand with that number.", "You are carrying no errand with that number.",
            "You are carrying no errand with that number."], _told);
    }

    // Codex "Errands-Redesign R2", the must-fix: bounded before any request is sent.
    [Fact]
    public async Task RepeatedShowsWhileAReplyIsDelayedSendOneRequest()
    {
        var row = await Accepted();
        var calls = _brain.Proofs.Count;
        _brain.Gate = new TaskCompletionSource<bool>();
        var pending = Errands.ShowForTests(_p, row.Id);
        Assert.Equal(1, Errands.ShowOutForTests);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(Errands.ShowForTests(_p, row.Id).IsCompleted);           // nothing new was started
        }

        Assert.Equal(calls + 1, _brain.Proofs.Count);
        Assert.Equal(6, _told.Count);                                            // the log line each time, from the row
        Assert.All(_told, t => Assert.StartsWith($"{row.Id}. ", t));
        _brain.Gate.SetResult(true);
        await pending;
        Assert.Equal($"Tomas the tailor said: \"{AskLine}\"", _told[^1]);                   // and the one answer still arrives
        Assert.Equal(7, _told.Count);
        Assert.Equal(0, Errands.ShowOutForTests);
    }

    [Fact]
    public async Task AtTheShardWideLimitAShowFailsFastAndAFinishedReadFreesAPlace()
    {
        var mine = await Accepted();
        var second = new PlayerMobile { Name = "Other" };
        second.DefaultMobileInit();
        second.MoveToWorld(new Point3D(5913, 110, 0), Map.Felucca);
        _made.Add(second);
        var theirs = Row(90, ErrandState.Accepted, _senderId, _receiverId, AiIdentity.GetOrCreate(second));
        Errands.SetShowMaxForTests(1);
        var calls = _brain.Proofs.Count;

        _brain.Gate = new TaskCompletionSource<bool>();
        var pending = Errands.ShowForTests(_p, mine.Id);
        _told.Clear();
        Assert.True(Errands.ShowForTests(second, theirs.Id).IsCompleted);        // at the limit: refused at once, no queue
        Assert.Equal(2, _told.Count);
        Assert.Equal(NotRecalled, _told[1]);
        Assert.Equal(calls + 1, _brain.Proofs.Count);                            // and nothing was sent for it

        _brain.Gate.SetResult(true);
        await pending;
        _brain.Gate = null;
        _told.Clear();
        await Errands.ShowForTests(second, theirs.Id);                           // the place is free again
        Assert.Equal($"Tomas said: \"{AskLine}\"", _told[^1]);
        Assert.Equal(calls + 2, _brain.Proofs.Count);

        Errands.SetShowMaxForTests(2);                                           // with room for two, two may be out together
        _now += Errands.ShowCooldown;
        _brain.Gate = new TaskCompletionSource<bool>();
        var a = Errands.ShowForTests(_p, mine.Id);
        var b = Errands.ShowForTests(second, theirs.Id);
        Assert.Equal(2, Errands.ShowOutForTests);
        _brain.Gate.SetResult(true);
        await Task.WhenAll(a, b);
        Assert.Equal(0, Errands.ShowOutForTests);
    }

    [Fact]
    public async Task AFailedOrTimedOutReadGivesItsPlaceBackAndAShortPauseFollowsEveryRead()
    {
        var row = await Accepted();
        _brain.Down = true;                                                      // the stand-in for a failure or a timeout
        await Errands.ShowForTests(_p, row.Id);
        Assert.Equal(NotRecalled, _told[^1]);
        Assert.Equal(0, Errands.ShowOutForTests);                                // released

        _brain.Down = false;
        await Lease();
        var calls = _brain.Proofs.Count;
        _told.Clear();
        await Errands.ShowForTests(_p, row.Id);                                  // inside the pause: no request
        Assert.Equal(NotRecalled, _told[^1]);
        Assert.Equal(calls, _brain.Proofs.Count);
        _now += Errands.ShowCooldown;
        await Errands.ShowForTests(_p, row.Id);
        Assert.Equal($"Tomas the tailor said: \"{AskLine}\"", _told[^1]);
        Assert.Equal(calls + 1, _brain.Proofs.Count);

        _brain.Throw = true;                                                     // even a brain stand-in that throws
        _now += Errands.ShowCooldown;
        await Errands.ShowForTests(_p, row.Id);
        Assert.Equal(NotRecalled, _told[^1]);
        Assert.Equal(0, Errands.ShowOutForTests);
    }

    [Fact]
    public async Task ADropALogoutOrAChangedRowDuringTheReadDiscardsItsAnswerAndBlocksNothingElse()
    {
        var row = await Accepted();
        _brain.Gate = new TaskCompletionSource<bool>();
        var pending = Errands.ShowForTests(_p, row.Id);
        _told.Clear();
        Errands.Collect(_p);                                                     // other commands are not held up by the read
        Errands.DropForTests(_p, row.Id);
        Assert.Equal(ErrandState.Lapsed, row.State);
        _brain.Gate.SetResult(true);
        await pending;
        Assert.Empty(_told);                                                     // nothing is shown for a row that was dropped
        Assert.Equal(0, Errands.ShowOutForTests);

        var again = Row(95, ErrandState.Accepted, _senderId, _receiverId);
        _now += Errands.ShowCooldown;
        _brain.Gate = new TaskCompletionSource<bool>();
        pending = Errands.ShowForTests(_p, again.Id);
        _told.Clear();
        _session = new object();                                                 // logged out and in again meanwhile
        _brain.Gate.SetResult(true);
        await pending;
        Assert.Empty(_told);

        _now += Errands.ShowCooldown;
        _brain.Gate = new TaskCompletionSource<bool>();
        pending = Errands.ShowForTests(_p, again.Id);
        _told.Clear();
        again.Version++;                                                         // the row moved on underneath
        _brain.Gate.SetResult(true);
        await pending;
        Assert.Empty(_told);

        _now += Errands.ShowCooldown;
        _session = null;                                                         // no connection at all
        _brain.Gate = null;
        _told.Clear();
        await Errands.ShowForTests(_p, again.Id);
        Assert.Single(_told);                                                    // the log line only
    }

    // ---- through the real conversation code: what the model is and is not sent (design 12.3) -----------------------

    [Fact]
    public async Task ThroughTheRealConversationCode_NoErrandSentenceEverReachesTheModelsHistory()
    {
        var talker = Talk();
        try
        {
            await Lease();
            await Says(_sender, "good day");                       // model call 1; then the ask line, by code
            await Says(_sender, "what would I get?");              // code: the fixed line
            await Says(_sender, "yes");                            // code: the yes line
            await Says(_sender, "what was the errand?");           // code: the ask again, after a remind proof
            await Says(_sender, "what does it pay?");              // code: the fixed line
            Assert.Equal(1, talker.Calls);                         // ZERO model calls for any of those
            var row = Assert.Single(Errands.RowsForTests);
            Assert.Equal(["accept", "remind"], _brain.Proofs.ConvertAll(Step));

            await Says(_sender, "it looks like rain");             // model call 2: an ordinary turn
            Assert.Equal(2, talker.Calls);
            Assert.Equal(["ask"], talker.Errands);                 // no errand field on it at all
            var sent = talker.Requests[^1];
            Assert.Null(sent.Errand);
            var history = string.Join("\n", Array.ConvertAll(sent.History, l => l.Speaker + ": " + l.Text));
            Assert.Equal("player: good day\nnpc: Good day to you.", history);   // the one ordinary exchange, and nothing else
            foreach (var code in new[] { AskLine, Errands.RewardLine, "That is good of you. Safe roads.", "what would I get?", "yes", "what was the errand?", "what does it pay?" })
            {
                Assert.DoesNotContain(code, history);
            }

            Errands.Switch("pointer", true);
            await Says(_sender, "and the harvest?");
            Assert.Equal(["ask", "pointer"], talker.Errands);
            Assert.Equal("{\"step\":\"pointer\",\"role\":\"sender\"}", JsonSerializer.Serialize(talker.Requests[^1].Errand, Snake));
            Assert.Equal(ErrandState.Accepted, row.State);
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }

    [Fact]
    public async Task ThroughTheRealConversationCode_AfterAClosedAskTheModelsHistoryHoldsNoAsk()
    {
        var talker = Talk();
        try
        {
            await Lease();
            await Says(_sender, "good day");                       // the ask is spoken after this reply
            await Says(_sender, "maybe some other day");           // any other line: the closing instruction, an ordinary turn
            Assert.Equal(2, talker.Calls);
            Assert.Equal(["ask", "closed"], talker.Errands);
            var history = string.Join("\n", Array.ConvertAll(talker.Requests[^1].History, l => l.Text));
            Assert.Equal("good day\nGood day to you.", history);
            Assert.DoesNotContain(AskLine, history);
        }
        finally
        {
            AiGateway.Client = null;
            AiGateway.ResetForTests();
        }
    }
}
