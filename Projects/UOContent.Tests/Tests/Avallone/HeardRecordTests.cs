using System;
using System.Collections.Generic;
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

// Step 2e (design §8.1, §9.1; Codex design R6 = GO): a story really SPOKEN to a player is recorded as a rumour_heard row,
// after the line is said, by any teller. The row is a third capability-held kind: held until the brain confirms it,
// kept (never dropped) if an older brain refuses it, never in a batch with another kind of special row.
[Collection("Sequential UOContent Tests")]
public class HeardRecordTests : IDisposable
{
    private static readonly Point3D Here = new(5210, 3950, 0);
    private readonly List<Mobile> _made = [];

    public HeardRecordTests()
    {
        AiLedger.ResetForTests(7, 100);
        RumourSpeech.ResetForTests(true);
        RumourSpeech.ResetRosterForTests();
    }

    public void Dispose()
    {
        RumourSpeech.ResetForTests(false);
        AiLedger.ResetForTests(0, 2000);
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
        return m;
    }

    private Provisioner Npc(string name)
    {
        var v = new Provisioner { Name = name };
        v.MoveToWorld(new Point3D(Here.X + 1, Here.Y, Here.Z), Map.Felucca);
        _made.Add(v);
        return v;
    }

    private static AiLedger.LedgerEvent Ev(long seq, string kind, string facts = "{}") =>
        new(AiLedger.EventId(7, seq), 7, seq, Guid.NewGuid(), Guid.NewGuid(), kind, facts, DateTime.UtcNow);

    private const string Heard = "{\"story\":\"7-3\",\"hk\":\"s:7-3@0123456789abcdef\",\"town\":\"Britain\",\"pt\":1,\"mpt\":1}";
    private const string Fix = "{\"story\":\"7-3\",\"pt\":50000,\"count\":4}";

    private static AiReply Spoke(params RumourDelivered[] delivered) =>
        AiReply.Success("gossip", "m") with
        {
            Rumour = new RumourInfo("", "", "a fisher", delivered.Select(d => d.Story).DefaultIfEmpty("7-3").ToArray(), "news", delivered)
        };

    private static List<JsonElement> HeardRows() =>
        AiLedger.OutboxForTests.Where(e => e.Kind == "rumour_heard").Select(e => JsonDocument.Parse(e.FactsJson).RootElement).ToList();

    // ---- the capability hold -----------------------------------------------------------------------------------------

    [Fact]
    public void TheKindNeedsItsOwnCapability()
    {
        Assert.Equal(3, AiLedger.Need(Ev(1, "rumour_heard", Heard)));
        Assert.True(AiLedger.Confirmed(0, false, false, false));
        Assert.False(AiLedger.Confirmed(3, true, true, false));
        Assert.True(AiLedger.Confirmed(3, false, false, true));
        Assert.False(AiLedger.Confirmed(2, true, false, true));
    }

    [Fact]
    public void AHeardRowWaits_NothingOvertakesIt_AndSpecialKindsNeverShareABatch()
    {
        var outbox = new List<AiLedger.LedgerEvent>
        {
            Ev(1, "visited"), Ev(2, "rumour_heard", Heard), Ev(3, "rumour_heard", Heard), Ev(4, "visited"),
            Ev(5, Corrections.Kind, Fix)
        };
        Assert.Equal(1, AiLedger.SendableCount(outbox, 50, true, true, false));   // stops before the first heard row
        Assert.Equal(3, AiLedger.SendableCount(outbox, 50, true, true, true));    // ordinary, then heard rows - nothing after
        outbox.RemoveRange(0, 3);
        Assert.Equal(2, AiLedger.SendableCount(outbox, 50, true, true, true));    // ordinary, then one kind of special row
        var mixed = new List<AiLedger.LedgerEvent> { Ev(6, "rumour_heard", Heard), Ev(7, Corrections.Kind, Fix) };
        Assert.Equal(1, AiLedger.SendableCount(mixed, 50, true, true, true));     // two KINDS of special row never share
    }

    [Fact]
    public async Task ABrainRolledBackToOneWithoutTheKindKeepsTheRows()
    {
        var brain = new Brain();
        AiLedger.SetHttpForTests(brain);
        AiLedger.AddForTests(Ev(1, "visited"));
        AiLedger.AddForTests(Ev(2, "rumour_heard", Heard));
        AiLedger.AddForTests(Ev(3, "visited"));
        AiLedger.SetHeardIngestForTests(true);                 // a 2e brain confirmed earlier...
        brain.Mode = "old";                                    // ...and was rolled back while the shard ran

        await AiLedger.SendForTests();                         // [visited, heard] goes; the old brain refuses the heard row
        Assert.False(AiLedger.HeardIngest);
        Assert.Equal(["rumour_heard", "visited"], AiLedger.OutboxForTests.Select(e => e.Kind).ToArray());   // KEPT, in order

        await AiLedger.SendForTests();                         // held: an empty batch only asks again
        Assert.Empty(brain.Batches[^1]);

        brain.Mode = "new";
        await AiLedger.SendForTests();                         // the probe finds a brain that accepts it
        Assert.True(AiLedger.HeardIngest);
        await AiLedger.SendForTests();                         // the heard row - alone
        await AiLedger.SendForTests();                         // then what waited behind it
        Assert.Empty(AiLedger.OutboxForTests);
        Assert.Equal([AiLedger.EventId(7, 2)], brain.Batches[^2]);
    }

    // ---- recording after speech ----------------------------------------------------------------------------------

    [Fact]
    public void ASpokenStoryIsRecordedOnce_WithItsKeyAndTown()
    {
        AiLedger.SetHeardIngestForTests(true);
        var p = Player("Aldren Thorn");
        var npc = Npc("Osric");
        RumourSpeech.OnSpoken(npc, p, Spoke(new RumourDelivered("7-3", "s:7-3@0123456789abcdef", "Britain")));
        var rows = HeardRows();
        Assert.Single(rows);
        Assert.Equal("7-3", rows[0].GetProperty("story").GetString());
        Assert.Equal("s:7-3@0123456789abcdef", rows[0].GetProperty("hk").GetString());
        Assert.Equal("Britain", rows[0].GetProperty("town").GetString());
        Assert.Equal(
            new[] { "hk", "mpt", "pt", "story", "town" },
            rows[0].EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray()   // ids and clocks only, never words
        );
    }

    [Fact]
    public void ATeaserOrAThreadMarkRecordsNothing()
    {
        AiLedger.SetHeardIngestForTests(true);
        var p = Player("Aldren Thorn");
        RumourSpeech.OnSpoken(Npc("Osric"), p, Spoke());                     // ids only, nothing delivered
        Assert.Empty(HeardRows());
    }

    [Fact]
    public void NothingIsWrittenForABrainThatHasNotSaidItAcceptsTheKind()
    {
        AiLedger.SetHeardIngestForTests(false);
        var p = Player("Aldren Thorn");
        RumourSpeech.OnSpoken(Npc("Osric"), p, Spoke(new RumourDelivered("7-3", "s:7-3@0123456789abcdef", "Britain")));
        Assert.Empty(HeardRows());                                           // so no queue is ever held behind it
    }

    [Fact]
    public void MalformedKeysAndAMadeUpStoryByContent()
    {
        AiLedger.SetHeardIngestForTests(true);
        var p = Player("Aldren Thorn");
        RumourSpeech.OnSpoken(Npc("Osric"), p, Spoke(
            new RumourDelivered("7-3", "s:7-3@XYZ", "Britain"),                         // not the brain's shape
            new RumourDelivered("7-3", "s:7-3@0123456789abcdef", "Brit<ain"),          // odd town
            new RumourDelivered("mu-9a36a163-ba79-c", "mk:9a36a163", "Britain")        // fine: made-up, by content
        ));
        var rows = HeardRows();
        Assert.Single(rows);
        Assert.Equal("mk:9a36a163", rows[0].GetProperty("hk").GetString());
    }

    private sealed class Brain : HttpMessageHandler
    {
        public string Mode = "new";                         // old (refuses rumour_heard, says nothing) | new | down
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
                var bad = e.GetProperty("kind").GetString() == "rumour_heard" && Mode == "old";
                (bad ? rejected : acked).Add(e.GetProperty("event_id").GetString());
            }

            Batches.Add(acked.Concat(rejected).ToArray());
            var body = JsonSerializer.Serialize(new { ok = true, acked, rejected });
            if (Mode == "new")
            {
                body = body[..^1] + ",\"talked_with_topics\":1,\"rumour_corrected\":1,\"rumour_heard\":1}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
