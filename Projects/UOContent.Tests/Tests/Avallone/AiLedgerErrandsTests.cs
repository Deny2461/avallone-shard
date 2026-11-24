using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Xunit;

namespace UOContent.Tests;

// Errands build 1 (design v5 3.8): "errand_done" is the fourth kind of record that needs a brain capability. These run
// the REAL AiLedger.SendAsync against an in-process handler that behaves like a brain from before errands, like one
// with errands, or like no brain - Codex R1/R2's compatibility cases and R3/R4's set-aside and refusal cases.
[Collection("Sequential UOContent Tests")]
public class AiLedgerErrandsTests : IDisposable
{
    private const string Done = "{\"v\":1,\"errand\":3}";

    private sealed class Brain : HttpMessageHandler
    {
        public string Mode = "errands";                     // old | errands | down
        public readonly HashSet<string> SetAside = [];
        public readonly HashSet<string> Refuse = [];
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
                var id = e.GetProperty("event_id").GetString();
                var errand = e.GetProperty("kind").GetString() == "errand_done";
                (Mode == "old" && errand || Refuse.Contains(id) ? rejected : acked).Add(id);   // an old brain: unknown kind
            }

            Batches.Add(acked.Concat(rejected).ToArray());
            var body = JsonSerializer.Serialize(new { ok = true, acked, rejected });
            if (Mode == "errands")
            {
                body = body[..^1] + ",\"errands\":1,\"errand_set_aside\":" + JsonSerializer.Serialize(acked.Where(SetAside.Contains)) + "}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly Brain _brain = new();
    private readonly List<(string Id, string Why)> _faults = [];
    private readonly Action<string, string> _was = AiLedger.ErrandFault;

    public AiLedgerErrandsTests()
    {
        AiLedger.ResetForTests(7, 100);
        AiLedger.SetHttpForTests(_brain);
        AiLedger.ErrandFault = (id, why) => _faults.Add((id, why));
    }

    public void Dispose()
    {
        AiLedger.ErrandFault = _was;
        AiLedger.ResetForTests(0, 2000);
    }

    private static AiLedger.LedgerEvent Ev(long seq, string kind, string facts = "{}") =>
        new(AiLedger.EventId(7, seq), 7, seq, Guid.NewGuid(), Guid.NewGuid(), kind, facts, DateTime.UtcNow);

    [Fact]
    public void ACompletionNeedsCapabilityFourAndIsHeldInOrderWithoutIt()
    {
        var done = Ev(2, "errand_done", Done);
        Assert.Equal(4, AiLedger.Need(done));
        Assert.False(AiLedger.Confirmed(4, true, true, true));
        Assert.True(AiLedger.Confirmed(4, false, false, false, true));
        var outbox = new List<AiLedger.LedgerEvent> { Ev(1, "traded"), done, Ev(3, "traded") };
        Assert.Equal(1, AiLedger.SendableCount(outbox, 50, true, true, true));               // held: nothing goes out past it
        Assert.Equal(2, AiLedger.SendableCount(outbox, 50, false, false, false, true));      // no ordinary row after a special one
        var mixed = new List<AiLedger.LedgerEvent> { done, Ev(4, "rumour_heard") };
        Assert.Equal(1, AiLedger.SendableCount(mixed, 50, true, true, true, true));          // a batch never mixes special kinds
    }

    [Fact]
    public async Task TheCapabilityIsReadFromEveryAnswerAndACompletionIsDelivered()
    {
        AiLedger.AddForTests(Ev(1, "traded"));
        AiLedger.AddForTests(Ev(2, "errand_done", Done));
        Assert.False(AiLedger.ErrandsIngest);
        await AiLedger.SendForTests();                       // the ordinary row goes; the answer says "errands"
        Assert.True(AiLedger.ErrandsIngest);
        Assert.Equal(["7-1"], _brain.Batches[0]);
        await AiLedger.SendForTests();
        Assert.Equal(["7-2"], _brain.Batches[1]);
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.Empty(_faults);
    }

    [Fact]
    public async Task ABrainSwappedForAnOlderOneKeepsTheCompletionInOrderAndTheRightBrainClearsIt()
    {
        AiLedger.SetErrandsIngestForTests(true);             // confirmed earlier ...
        AiLedger.AddForTests(Ev(1, "errand_done", Done));
        AiLedger.AddForTests(Ev(2, "traded"));
        _brain.Mode = "old";                                 // ... then replaced by a brain from before errands

        await AiLedger.SendForTests();
        Assert.Equal(["7-1"], _brain.Batches[0]);            // the ordinary row behind it did not overtake it
        Assert.Equal(2, AiLedger.OutboxCountForTests);       // refused by a brain that does not know the kind: KEPT
        Assert.False(AiLedger.ErrandsIngest);
        Assert.Empty(_faults);                               // not a verdict on the row: no fault

        await AiLedger.SendForTests();
        Assert.Empty(_brain.Batches[1]);                     // held: only an empty probe goes out
        Assert.Equal(2, AiLedger.OutboxCountForTests);

        _brain.Mode = "errands";
        await AiLedger.SendForTests();                       // the probe reads the capability ...
        await AiLedger.SendForTests();                       // ... the completion goes ...
        await AiLedger.SendForTests();                       // ... and then the row behind it
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.Equal(["7-1"], _brain.Batches[3]);
        Assert.Equal(["7-2"], _brain.Batches[4]);
    }

    [Fact]
    public async Task ATransportFailureClearsTheCapabilityWithTheOtherThree()
    {
        AiLedger.SetErrandsIngestForTests(true);
        AiLedger.SetHeardIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "errand_done", Done));
        _brain.Mode = "down";
        await AiLedger.SendForTests();
        Assert.False(AiLedger.ErrandsIngest || AiLedger.HeardIngest);
        Assert.Equal(1, AiLedger.OutboxCountForTests);
    }

    [Fact]
    public async Task ASetAsideCompletionLeavesTheOutboxOnItsAcknowledgementAndRaisesTheFault()
    {
        AiLedger.SetErrandsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "errand_done", Done));
        AiLedger.AddForTests(Ev(2, "errand_done", Done));
        AiLedger.AddForTests(Ev(3, "traded"));
        _brain.SetAside.Add("7-2");

        await AiLedger.SendForTests();
        Assert.Equal(["7-1", "7-2"], _brain.Batches[0]);
        Assert.Equal(1, AiLedger.OutboxCountForTests);       // both acknowledged and gone, the ordinary way
        var fault = Assert.Single(_faults);
        Assert.Equal("7-2", fault.Id);
        Assert.Contains("set this completion record aside", fault.Why);

        await AiLedger.SendForTests();                       // the memory queue is NOT held up
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.Single(_faults);
    }

    [Fact]
    public async Task ACompletionRefusedByACapableBrainIsDroppedAsEveryRefusedRowIsAndRaisesTheFault()
    {
        AiLedger.SetErrandsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "errand_done", Done));
        _brain.Refuse.Add("7-1");                            // different contents under a stored identity, or damage
        await AiLedger.SendForTests();
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        var fault = Assert.Single(_faults);
        Assert.Equal("7-1", fault.Id);
        Assert.Contains("refused", fault.Why);
        Assert.True(AiLedger.ErrandsIngest);
    }

    [Fact]
    public async Task AnOrdinaryRefusedRowRaisesNoErrandFault()
    {
        AiLedger.AddForTests(Ev(1, "traded"));
        _brain.Refuse.Add("7-1");
        await AiLedger.SendForTests();
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.Empty(_faults);
    }

    [Fact]
    public void ACompletionSurvivesASaveAndLoadWithItsIdentity()
    {
        var done = Ev(5, "errand_done", Done);
        AiLedger.AddForTests(done);
        var loaded = Assert.Single(AiLedger.RoundTripForTests());
        Assert.Equal((done.EventId, done.Epoch, done.Seq, done.NpcId, done.CharId, done.Kind, done.FactsJson),
            (loaded.EventId, loaded.Epoch, loaded.Seq, loaded.NpcId, loaded.CharId, loaded.Kind, loaded.FactsJson));
    }

    [Fact]
    public void HasRoomNeedsALiveReconciledLedgerWithSpace()
    {
        Assert.False(AiLedger.HasRoom);                      // starting: not reconciled
        AiLedger.SetLiveForTests();
        Assert.True(AiLedger.HasRoom);
        AiLedger.ResetForTests(7, 1);
        AiLedger.SetLiveForTests();
        AiLedger.AddForTests(Ev(1, "traded"));
        Assert.False(AiLedger.HasRoom);                      // full: a completion is not begun
    }
}
