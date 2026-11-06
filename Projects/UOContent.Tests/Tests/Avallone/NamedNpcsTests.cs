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

// Phase 5c-1 (Codex 5c design review 2026-10-30): one id per named NPC for requests and memories, visit days in the
// household's time zone, late summaries discarded, deletion tombstones for everyone, a bounded summary queue, the
// quiet-window hold, and named NPCs never answering as generic townsfolk.
[Collection("Sequential UOContent Tests")]
public class NamedNpcsTests : IDisposable
{
    private readonly List<(Guid Npc, Mobile Who, string Kind, object Facts)> _recorded = [];
    private readonly List<(Mobile Who, string Kind)> _world = [];
    private readonly Action<Guid, Mobile, string, object> _oldRec = NamedNpcs.Recorder;
    private readonly Action<Mobile, string> _oldWorld = NamedNpcs.WorldRecorder;

    public NamedNpcsTests()
    {
        NamedNpcs.ResetForTests(true);
        NamedNpcs.Recorder = (n, m, k, f) => _recorded.Add((n, m, k, f));
        NamedNpcs.WorldRecorder = (m, k) => _world.Add((m, k));
    }

    public void Dispose()
    {
        NamedNpcs.Recorder = _oldRec;
        NamedNpcs.WorldRecorder = _oldWorld;
        NamedNpcs.ResetForTests(false);
    }

    private static PlayerMobile NewPlayer(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(1495, 1629, 0), Map.Trammel);
        return m;
    }

    private static Mirabel NewMirabel()
    {
        var m = new Mirabel();
        m.MoveToWorld(new Point3D(1496, 1629, 0), Map.Trammel);
        return m;
    }

    private static NamedNpcs.Job Job(Mirabel npc, PlayerMobile p) =>
        new(npc, npc.AiId, "mirabel", p, AiIdentity.GetOrCreate(p), NamedNpcs.Gen(p), [new AiLine("player", "I slew a wyrm")], Core.Now);

    private static AiReply Notes(string json) => AiReply.Success(json, "test");

    [Fact]
    public void OneIdForRequestsAndMemories()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        Assert.NotEqual(Guid.Empty, npc.AiId);
        Assert.Equal(npc.AiId, ((INamedNpc)npc).AiId);
        var ask = NamedNpcs.Ask(npc, p);
        Assert.Equal("mirabel", ask.Key);
        Assert.Equal("Brynn Handy", ask.Speaker);
        Assert.Single(_recorded);
        Assert.Equal(npc.AiId, _recorded[0].Npc);                 // the same id her conversations use (finding 2)
        Assert.Equal("visited", _recorded[0].Kind);
        NamedNpcs.Ask(npc, p);
        Assert.Single(_recorded);                                  // once per day
        npc.Delete();
        p.Delete();
    }

    [Theory]
    [InlineData("2026-11-01T04:59:00Z", "2026-10-31")] // 23:59 CDT
    [InlineData("2026-11-01T05:00:00Z", "2026-11-01")] // midnight CDT
    [InlineData("2026-11-02T05:59:00Z", "2026-11-01")] // 23:59 CST, after fall-back
    [InlineData("2026-11-02T06:00:00Z", "2026-11-02")]
    [InlineData("2026-03-08T05:59:00Z", "2026-03-07")] // 23:59 CST, before spring-forward
    [InlineData("2026-03-09T04:59:00Z", "2026-03-08")] // 23:59 CDT
    [InlineData("2026-03-09T05:00:00Z", "2026-03-09")]
    public void VisitDayIsTheHouseholdsCalendarDay(string utc, string day) =>
        Assert.Equal(day, NamedNpcs.Today(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));

    [Fact]
    public void GoodNotesStoredUnderTheNpcId()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        Assert.Equal("stored", NamedNpcs.Accept(Job(npc, p), Notes("{\"claims\":[\"they slew a wyrm\"],\"topics\":[\"dragons\"]}")));
        Assert.Equal(npc.AiId, _recorded[^1].Npc);
        Assert.Equal("visit_summary", _recorded[^1].Kind);
        Assert.Equal("empty", NamedNpcs.Accept(Job(npc, p), Notes("{\"claims\":[],\"topics\":[]}")));
        Assert.Equal("failed", NamedNpcs.Accept(Job(npc, p), Notes("not json")));
        Assert.Equal("failed", NamedNpcs.Accept(Job(npc, p), Notes("{\"claims\":[\"a\",\"b\",\"c\",\"d\"],\"topics\":[]}")));
        Assert.Equal("failed", NamedNpcs.Accept(Job(npc, p), AiReply.Fail(AiOutcome.Failed, "x")));
        npc.Delete();
        p.Delete();
    }

    [Fact]
    public void LateNotesDiscardedAfterDeletionOrSwitchOff()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        var job = Job(npc, p);
        NamedNpcs.OnPlayerDeleted(p);                              // deleted while the summary was in flight
        Assert.Equal("discarded", NamedNpcs.Accept(job, Notes("{\"claims\":[\"x\"],\"topics\":[]}")));

        var q = NewPlayer("Thandor");
        var job2 = Job(npc, q);
        NamedNpcs.Switch(false, persist: false);
        NamedNpcs.Switch(true, persist: false);
        Assert.Equal("discarded", NamedNpcs.Accept(job2, Notes("{\"claims\":[\"x\"],\"topics\":[]}")));

        var job3 = Job(npc, q) with { NpcId = Guid.NewGuid() }; // an NPC whose id no longer matches
        Assert.Equal("discarded", NamedNpcs.Accept(job3, Notes("{\"claims\":[\"x\"],\"topics\":[]}")));
        Assert.DoesNotContain(_recorded, r => r.Kind == "visit_summary");
        npc.Delete();
        p.Delete();
        q.Delete();
    }

    [Fact]
    public void DeletionTombstonesAnyoneWithAnAiIdentity()
    {
        var met = NewPlayer("Alt One");
        AiIdentity.GetOrCreate(met);                               // only ever met Mirabel - no companion knows them
        NamedNpcs.OnPlayerDeleted(met);
        Assert.Contains(_world, w => w.Who == met && w.Kind == "character_deleted");

        var npc = NewMirabel();
        var held = NamedNpcs.VisitDaysHeld;
        NamedNpcs.Ask(npc, met);                                   // a visit day held in RAM for this character
        Assert.Equal(held + 1, NamedNpcs.VisitDaysHeld);
        NamedNpcs.OnPlayerDeleted(met);
        Assert.Equal(held, NamedNpcs.VisitDaysHeld);               // Codex optional 1: pruned on deletion
        npc.Delete();

        var never = NewPlayer("Never Talked");                     // no AI identity: nothing to tombstone
        NamedNpcs.OnPlayerDeleted(never);
        Assert.DoesNotContain(_world, w => w.Who == never);
        met.Delete();
        never.Delete();
    }

    [Fact]
    public void QueueIsBoundedOldestDropped()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        for (var i = 0; i < NamedNpcs.QueueMax + 3; i++)
        {
            NamedNpcs.EnqueueJob(Job(npc, p));
        }

        Assert.Equal(NamedNpcs.QueueMax, NamedNpcs.Queued);
        Assert.Equal(3, NamedNpcs.Dropped);
        npc.Delete();
        p.Delete();
    }

    [Fact]
    public void SummariesHoldTenMinutesBeforeTheQuietWindow()
    {
        var (s, e, z) = (AiGateway.QuietStart, AiGateway.QuietEnd, AiGateway.QuietZone);
        try
        {
            AiGateway.QuietZone = TimeZoneInfo.Utc;
            AiGateway.QuietStart = TimeSpan.Parse("04:55");
            AiGateway.QuietEnd = TimeSpan.Parse("05:50");
            bool At(string hm) => AiGateway.InQuietWindowAt(DateTime.Parse("2026-10-30T" + hm + ":00Z", null,
                System.Globalization.DateTimeStyles.AdjustToUniversal), TimeSpan.FromMinutes(10));
            Assert.False(At("04:44"));
            Assert.True(At("04:45"));
            Assert.True(At("05:49"));
            Assert.False(At("05:50"));
            Assert.False(At("12:00"));
        }
        finally
        {
            AiGateway.QuietStart = s;
            AiGateway.QuietEnd = e;
            AiGateway.QuietZone = z;
        }
    }

    // Codex 5c-1 code review finding 4: a summary that would hold the model forever never delays a conversation.
    private sealed class HoldingClient : IAiNpcClient
    {
        public int Summaries;
        public string Name => "holding";

        public Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
        {
            if (request.Mode != "summarize")
            {
                return Task.FromResult(AiReply.Success("Welcome back.", "test"));
            }

            Summaries++;
            var tcs = new TaskCompletionSource<AiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => tcs.TrySetResult(AiReply.Fail(AiOutcome.Failed, "cancelled")));
            return tcs.Task;   // only a cancellation ends it
        }

        public Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task AConversationPreemptsARunningSummary()
    {
        var client = new HoldingClient();
        var (oldClient, oldStart, oldEnd) = (AiGateway.Client, AiGateway.QuietStart, AiGateway.QuietEnd);
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        try
        {
            AiGateway.ResetForTests();
            AiGateway.Client = client;
            AiGateway.QuietStart = AiGateway.QuietEnd = TimeSpan.Zero;   // no quiet window during the test
            AiGateway.SetEnabled(true, false);
            AiGateway.BeforeConversation = NamedNpcs.Preempt;

            NamedNpcs.EnqueueJob(Job(npc, p));
            NamedNpcs.DispatchForTests(Core.Now);
            Assert.Equal(1, client.Summaries);                          // the summary is holding the model

            var reply = AiGateway.RequestAsync(new AiRequest(npc.AiId, AiIdentity.GetOrCreate(p), "mirabel", "hello", AiPriority.Conversation));
            var first = await reply.WaitAsync(TimeSpan.FromSeconds(2));  // the conversation does not wait behind it
            Assert.True(first.Ok);
            Assert.True(SpinWait.SpinUntil(() => NamedNpcs.Preempted == 1, 2000));
            Assert.Equal(1, NamedNpcs.Queued);                          // retried once later
            Assert.DoesNotContain(_recorded, r => r.Kind == "visit_summary");
            Assert.False(AiGateway.BreakerIsOpen);                      // never counted against conversations

            NamedNpcs.DispatchForTests(Core.Now);                       // the retry, pre-empted again: dropped
            await AiGateway.RequestAsync(new AiRequest(npc.AiId, AiIdentity.GetOrCreate(p), "mirabel", "hello", AiPriority.Conversation))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(SpinWait.SpinUntil(() => NamedNpcs.Preempted == 2, 2000));
            Assert.Equal(0, NamedNpcs.Queued);
        }
        finally
        {
            AiGateway.ResetForTests();
            AiGateway.SetEnabled(false, false);
            AiGateway.Client = oldClient;
            AiGateway.QuietStart = oldStart;
            AiGateway.QuietEnd = oldEnd;
            npc.Delete();
            p.Delete();
        }
    }

    [Fact]
    public void NamedNpcIsNotAGenericTownsperson()
    {
        var npc = NewMirabel();
        Assert.False(TownsfolkTalk.IsTownsperson(npc));
        npc.Delete();
    }

    [Fact]
    public void SwitchedOffMeansNoMemoryAndNoVisits()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        NamedNpcs.Switch(false, persist: false);
        Assert.Null(NamedNpcs.Ask(npc, p));
        NamedNpcs.OnExchange(npc, p, "hello");
        Assert.Empty(_recorded);
        NamedNpcs.Switch(true, persist: false);
        npc.Delete();
        p.Delete();
    }

    // 5c-2 step 2a: the clocks travel with requests and visits; typed facts are stored; delivered card ids recorded.
    [Fact]
    public void ClocksTypedFactsAndDeliveredCards()
    {
        var npc = NewMirabel();
        var p = NewPlayer("Brynn Handy");
        var ask = NamedNpcs.Ask(npc, p);
        Assert.Equal(Server.Avallone.Rumours.PlayedClock.Seconds, ask.Pt);
        Assert.Equal(Server.Avallone.Rumours.PlayedClock.MemorySeconds, ask.Mpt);
        _recorded.Clear();

        var job = Job(npc, p) with { Pt = 7200, Mpt = 3600.5 };
        Assert.Equal("stored", NamedNpcs.Accept(job, Notes("{\"claims\":[],\"topics\":[],\"items\":[\"pet|mule|Tonk\"]}")));
        var facts = System.Text.Json.JsonSerializer.SerializeToElement(_recorded[^1].Facts);
        Assert.Equal("visit_summary", _recorded[^1].Kind);
        Assert.Equal("pet|mule|Tonk", facts.GetProperty("items")[0].GetString());
        Assert.Equal(7200, facts.GetProperty("pt").GetInt64());
        Assert.Equal(3600.5, facts.GetProperty("mpt").GetDouble());
        Assert.Equal(job.VisitId.ToString("N"), facts.GetProperty("visit").GetString());
        Assert.Equal("failed", NamedNpcs.Accept(job, Notes("{\"claims\":[],\"topics\":[],\"items\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"]}")));

        _recorded.Clear();
        NamedNpcs.OnDelivered(npc, p, null);
        NamedNpcs.OnDelivered(npc, p, []);
        Assert.Empty(_recorded);                                   // nothing offered, nothing recorded
        NamedNpcs.OnDelivered(npc, p, ["f:pet:tonk", "e:1-2:0"]);
        Assert.Equal("reply_delivered", Assert.Single(_recorded).Kind);
        var d = System.Text.Json.JsonSerializer.SerializeToElement(_recorded[0].Facts);
        Assert.Equal(2, d.GetProperty("cards").GetArrayLength());
        npc.Delete();
        p.Delete();
    }
}
