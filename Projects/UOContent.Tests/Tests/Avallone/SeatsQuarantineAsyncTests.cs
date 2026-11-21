using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Named;
using Server.Avallone.Townsfolk;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Townsfolk step, Stage 1 - Codex code R1 F4: a quarantine that begins while work is already under way. The gate at
// selection is not enough: a reply that is out, an exchange between lines, an exchange waiting on the model and the
// final recording each captured the NPC or its id earlier. Every test has a control run without the quarantine, so a
// pass means the quarantine made the difference.
[Collection("Sequential UOContent Tests")]
public class SeatsQuarantineAsyncTests : IDisposable
{
    private const string Opener = "Good day to you. And you, Elspeth - you look road-weary.";
    private static readonly Point3D Here = new(2140, 1520, 0);

    private readonly List<Mobile> _made = [];
    private readonly List<(Mobile Who, string Text)> _said = [];
    private readonly Client _client = new();
    private readonly List<Guid> _accepted = []; // every "talked_with" row the real recording boundary accepted
    private readonly IAiNpcClient _oldClient = AiGateway.Client;
    private readonly Func<Mobile, bool> _oldOnline = TownsfolkTalk.Online;
    private long _now = 1_000_000;

    private sealed class Client : IAiNpcClient
    {
        public readonly List<AiRequest> Seen = [];
        public TaskCompletionSource<AiReply> Hold; // a call waits on this (continuations run inline when it is set)
        public string Name => "quarantine-tests";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            if (request.Mode == "exchange_cancel" || request.Mode == "summarize")
            {
                return Task.FromResult(AiReply.Success("", "test"));
            }

            Seen.Add(request);

            if (Hold == null)
            {
                return Task.FromResult(AiReply.Success($"line {Seen.Count}", "test"));
            }

            var hold = Hold;
            token.Register(() => hold.TrySetCanceled());
            return hold.Task;
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    public SeatsQuarantineAsyncTests()
    {
        Seats.ResetForTests(false, null);
        AiGateway.ResetForTests();
        AiGateway.Client = _client;
        AiGateway.SetEnabled(true, false);
        AiGateway.Clock = () => _now;
        NamedNpcs.ResetForTests(false);
        TravelParty.ResetForTests();
        Exchange.ResetForTests(true);
        Exchange.Pause = _ => Task.CompletedTask;
        Exchange.PickLength = () => 3;
        Exchange.Speak = (m, t) => _said.Add((m, t));
        Exchange.Online = _ => true;
        TownsfolkTalk.Online = _ => true;
        AiLedger.ResetForTests(7, 100);
        Exchange.SetMemoryForTests(true);
        Exchange.Recorder = (id, who, kind, facts) =>
        {
            var ok = AiLedger.TryRecord(id, who, kind, facts); // the real boundary, not a stand-in
            if (ok)
            {
                _accepted.Add(id);
            }

            return ok;
        };
    }

    public void Dispose()
    {
        Exchange.ResetForTests(false);
        NamedNpcs.ResetForTests(false);
        AiGateway.ResetForTests();
        AiGateway.SetEnabled(false, false);
        AiGateway.Client = _oldClient;
        AiGateway.Clock = () => Core.TickCount;
        TownsfolkTalk.Online = _oldOnline;
        AiLedger.ResetForTests(0, 100);
        Seats.ResetForTests(false, null);

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

    // As if `npc` had just said its reply to `p`: an exchange with the companion may begin.
    private bool Begin(Mobile npc, PlayerMobile p)
    {
        var before = Exchange.Started;
        Exchange.AfterReply(npc, p, TravelParty.For(npc, p), Opener, false, Exchange.RevisionOf(p));
        return Exchange.Started > before;
    }

    [Theory]
    [InlineData(false)] // the control: all three lines are said
    [InlineData(true)]
    public void Exchange_AQuarantineWhileTheModelCallIsOut_EndsIt_TheLateLineIsNeverSaid(bool quarantine)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        _client.Hold = new TaskCompletionSource<AiReply>();

        Assert.True(Begin(osric, p));
        Assert.Single(_client.Seen); // the first line's call is out

        if (quarantine)
        {
            Seats.QuarantineForTests(osric);
        }

        var first = _client.Hold;
        _client.Hold = null; // later calls answer at once
        first.TrySetResult(AiReply.Success("A line that arrives late.", "test"));

        if (quarantine)
        {
            Assert.Empty(_said);
            Assert.False(Exchange.Running);
            Assert.Single(_client.Seen); // and no further model call was made for it
            Assert.Empty(_accepted);     // nothing recorded, in either direction
        }
        else
        {
            Assert.Equal(3, _said.Count);
            Assert.Equal(2, _accepted.Count); // the control: both directions of the talk are recorded
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exchange_AQuarantineDuringAPause_EndsIt_NothingMoreIsSaidOrAsked(bool quarantine)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var pauses = 0;
        var saidAtQuarantine = -1;
        var askedAtQuarantine = -1;

        Exchange.Pause = _ =>
        {
            if (++pauses == 2 && quarantine) // between two lines
            {
                Seats.QuarantineForTests(osric);
                saidAtQuarantine = _said.Count;
                askedAtQuarantine = _client.Seen.Count;
            }

            return Task.CompletedTask;
        };

        Assert.True(Begin(osric, p));

        if (quarantine)
        {
            Assert.True(saidAtQuarantine >= 0, "the second pause was never reached - the test did not exercise the case");
            Assert.Equal(saidAtQuarantine, _said.Count);
            Assert.Equal(askedAtQuarantine, _client.Seen.Count);
            Assert.False(Exchange.Running);
            Assert.True(saidAtQuarantine > 0, "no line had been said before the quarantine - the recorder was not exercised");
            Assert.Empty(_accepted); // lines WERE said, yet neither direction is recorded (Codex code R2 #3)
        }
        else
        {
            Assert.Equal(3, _said.Count);
            Assert.Equal(2, _accepted.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exchange_AQuarantineJustBeforeTheFinalRecording_RecordsNothingUnderTheNpcsCapturedId(bool quarantine)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        Companion(p);
        var osricId = AiIdentity.GetOrCreate(osric); // the id the exchange captures for its voice

        Exchange.Speak = (m, t) =>
        {
            _said.Add((m, t));

            if (quarantine && _said.Count == 3) // the last line has just been said; only the recording is left
            {
                Seats.QuarantineForTests(osric);
            }
        };

        Assert.True(Begin(osric, p));
        Assert.Equal(3, _said.Count);

        if (quarantine)
        {
            Assert.Empty(_accepted); // not his row, and not the companion's row that would name him (Codex code R2 #3)
        }
        else
        {
            Assert.Contains(osricId, _accepted); // the control: without a quarantine both sides ARE recorded
            Assert.Equal(2, _accepted.Count);
        }
    }

    // Codex code R2 #3: a request that is already QUEUED behind another call when its NPC is quarantined.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_ARequestQueuedBehindAnotherCall_IsNeverSentOnceItsNpcIsQuarantined(bool quarantine)
    {
        var p = Player("Wren");
        var osric = Vendor("Osric");
        var osricId = AiIdentity.GetOrCreate(osric);
        _client.Hold = new TaskCompletionSource<AiReply>();

        var first = AiGateway.RequestAsync(new AiRequest(Guid.NewGuid(), Guid.NewGuid(), "p", "someone else's turn", AiPriority.Conversation));
        var queued = AiGateway.RequestAsync(new AiRequest(osricId, AiIdentity.GetOrCreate(p), "p", "hello Osric", AiPriority.Conversation));
        Assert.Single(_client.Seen); // the first call is out; Osric's request waits behind it

        if (quarantine)
        {
            Seats.QuarantineForTests(osric);
        }

        var hold = _client.Hold;
        _client.Hold = null;
        hold.TrySetResult(AiReply.Success("done", "test"));
        await first;
        var reply = await queued;

        if (quarantine)
        {
            Assert.False(reply.Ok);
            Assert.Single(_client.Seen); // it never reached the model
            var again = await AiGateway.RequestAsync(new AiRequest(osricId, AiIdentity.GetOrCreate(p), "p", "hello again", AiPriority.Conversation));
            Assert.False(again.Ok);      // its lease was released, and a new request is refused without a call
            Assert.Single(_client.Seen);
        }
        else
        {
            Assert.True(reply.Ok);
            Assert.Equal(2, _client.Seen.Count); // the control: it is sent when its turn comes
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reply_AQuarantineWhileTheReplyIsOut_TheLineIsNotSaid(bool quarantine)
    {
        var p = Player("Wren");
        var mady = Vendor("Mady");
        var e = new SpeechEventArgs(p, "Mady, any news?", MessageType.Regular, 0x3B2, [0x30]);
        TownsfolkTalk.ClaimForTests(e); // if her AI line is never said, the stock news is given once instead
        _client.Hold = new TaskCompletionSource<AiReply>();
        var fallbacks = TownsfolkTalk.NewsFallbacks;

        var pending = TownsfolkTalk.RespondForTests(mady, p, "Mady, any news?", e);
        Assert.Single(_client.Seen);

        if (quarantine)
        {
            Seats.QuarantineForTests(mady);
        }

        _client.Hold.TrySetResult(AiReply.Success("Good day to you, traveller.", "test"));
        await pending;

        if (quarantine)
        {
            Assert.Equal(fallbacks + 1, TownsfolkTalk.NewsFallbacks); // her line was not said
        }
        else
        {
            Assert.Equal(fallbacks, TownsfolkTalk.NewsFallbacks);     // the control: it was said
        }
    }
}
