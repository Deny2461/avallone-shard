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

// Codex "Stage A3 Step 2 Code R1", S2-C1 (2026-11-11): the shard cached "the brain's ingest accepts talked_with topics".
// If the brain was replaced by an older one while the shard kept running, a topic row was still sent, the older brain
// refused it, and the shard deleted it as if the refusal were a verdict on the row. These run the REAL AiLedger.SendAsync
// against an in-process HTTP handler that behaves like a step 1 brain (refuses topic rows, says nothing about topics),
// like a step 2 brain (accepts, confirms), or like no brain at all.
[Collection("Sequential UOContent Tests")]
public class AiLedgerTopicsDeliveryTests : IDisposable
{
    private const string Plain = "{\"day\":\"2026-11-10\",\"mpt\":1,\"other_kind\":\"companion\",\"other_id\":\"x\",\"lines\":3}";
    private static readonly string WithTopics = Plain[..^1] + ",\"topics\":[\"food\"]}";

    private sealed class Brain : HttpMessageHandler
    {
        public string Mode = "step2";                       // step1 | step2 | down | step2-rejects-all
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
                var topics = e.GetProperty("kind").GetString() == "talked_with" && e.GetProperty("facts").TryGetProperty("topics", out _);
                (Mode == "step2-rejects-all" || Mode == "step1" && topics ? rejected : acked).Add(id);
            }

            Batches.Add(acked.Concat(rejected).ToArray());
            var body = JsonSerializer.Serialize(new { ok = true, acked, rejected });
            if (Mode != "step1")
            {
                body = body[..^1] + ",\"talked_with_topics\":1}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly Brain _brain = new();

    public AiLedgerTopicsDeliveryTests()
    {
        AiLedger.ResetForTests(7, 100);
        AiLedger.SetHttpForTests(_brain);
    }

    public void Dispose() => AiLedger.ResetForTests(0, 2000);

    private static AiLedger.LedgerEvent Ev(long seq, string kind, string facts) =>
        new(AiLedger.EventId(7, seq), 7, seq, Guid.NewGuid(), Guid.NewGuid(), kind, facts, DateTime.UtcNow);

    [Fact]
    public async Task ABrainReplacedByAnOlderOneWhileTheShardRunsDoesNotCostTheTopicRow()
    {
        AiLedger.SetTopicsIngestForTests(true);              // a step 2 brain confirmed earlier
        AiLedger.AddForTests(Ev(1, "traded", "{}"));
        AiLedger.AddForTests(Ev(2, "talked_with", WithTopics));
        _brain.Mode = "step1";                               // ...and was swapped for step 1 at the same address

        await AiLedger.SendForTests();

        Assert.Equal(["7-1", "7-2"], _brain.Batches[0]);     // it did go out: the confirmation was stale
        Assert.Equal(1, AiLedger.OutboxCountForTests);       // refused, and KEPT
        Assert.False(AiLedger.TopicsIngest);
        Assert.Equal(1, AiLedger.TopicsHeld);

        await AiLedger.SendForTests();
        Assert.Empty(_brain.Batches[1]);                     // held: only an empty probe goes to the older brain
        Assert.Equal(1, AiLedger.OutboxCountForTests);

        _brain.Mode = "step2";                               // a brain that understands topics is back
        await AiLedger.SendForTests();                       // the probe reads the capability...
        Assert.True(AiLedger.TopicsIngest);
        await AiLedger.SendForTests();                       // ...and the row is delivered
        Assert.Equal(["7-2"], _brain.Batches[3]);
        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.Equal(0, AiLedger.TopicsHeld);
    }

    [Fact]
    public async Task AFailedDeliveryForgetsTheConfirmationSoTheRetryNeverReachesAnOlderBrain()
    {
        AiLedger.SetTopicsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "talked_with", WithTopics));
        _brain.Mode = "down";

        await AiLedger.SendForTests();

        Assert.False(AiLedger.TopicsIngest);                 // whoever answers next must confirm again
        Assert.Equal(1, AiLedger.OutboxCountForTests);

        _brain.Mode = "step1";                               // the brain that comes back is the older one
        await AiLedger.SendForTests();

        Assert.Empty(_brain.Batches[0]);                     // the topic row was not even sent
        Assert.Equal(1, AiLedger.OutboxCountForTests);
        Assert.Equal(1, AiLedger.TopicsHeld);
    }

    [Fact]
    public async Task ARefusalByABrainThatConfirmsTopicsStillRemovesTheRow()
    {
        AiLedger.SetTopicsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "talked_with", WithTopics));
        _brain.Mode = "step2-rejects-all";                   // a capable brain says this row is bad: that IS a verdict

        await AiLedger.SendForTests();

        Assert.Equal(0, AiLedger.OutboxCountForTests);
        Assert.True(AiLedger.TopicsIngest);
    }

    [Fact]
    public async Task AnOrdinaryRowRefusedByAnOlderBrainIsRemovedAsBefore()
    {
        AiLedger.AddForTests(Ev(1, "traded", "{}"));
        _brain.Mode = "step2-rejects-all";
        await AiLedger.SendForTests();
        Assert.Equal(0, AiLedger.OutboxCountForTests);
    }

    [Fact]
    public async Task NothingQueuedBehindATopicRowOvertakesItWhenAnOlderBrainRefusesIt()
    {
        AiLedger.SetTopicsIngestForTests(true);
        AiLedger.AddForTests(Ev(1, "traded", "{}"));
        AiLedger.AddForTests(Ev(2, "talked_with", WithTopics));
        AiLedger.AddForTests(Ev(3, "talked_with", WithTopics));
        AiLedger.AddForTests(Ev(4, "traded", "{}"));
        _brain.Mode = "step1";

        await AiLedger.SendForTests();

        Assert.Equal(["7-1", "7-2", "7-3"], _brain.Batches[0]);   // 7-4 stayed behind the topic rows
        Assert.Equal(3, AiLedger.OutboxCountForTests);            // 7-2, 7-3 kept; 7-4 never sent
        await AiLedger.SendForTests();
        Assert.Empty(_brain.Batches[1]);
        Assert.Equal(3, AiLedger.OutboxCountForTests);
    }
}
