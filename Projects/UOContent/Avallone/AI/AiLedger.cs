using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Server.Logging;

namespace Server.Avallone.AI;

// Phase 4a1 - the shard half of the memory ledger (design v2, Projects/Avallone-AI-NPC-Plan.md).
//
// Every companion event gets an id (epoch, seq). Events wait in an outbox - in RAM and in the world save -
// until the brain acknowledges them, so a brain outage never loses one. Each world save also records the
// last event it contains (the high-water mark). On start, after the world loads, the shard asks the brain
// to reconcile: anything the brain holds that is newer than the loaded save is marked rolled back, so a
// restored world never leaves a companion remembering things that, in that world, never happened.
//
// The epoch is a boot counter kept OUTSIDE the saves (Distribution/Avallone/ai-epoch.txt), so restoring a
// save never rewinds it. If anything doesn't reconcile, memory switches off and says why - it never guesses.
//
// Saved as its own file in world saves ("AvalloneLedger"). Never delete this type once it is in saves.
public class AiLedger : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AiLedger));

    private const int Version = 0;
    public const int SchemaVersion = 1;
    private const int BatchSize = 50;

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public enum LedgerState
    {
        Starting, // waiting to reconcile with the brain; events are queued
        Live,
        Full,     // outbox at its cap: new events are NOT recorded (logged once) until it drains
        Off       // reconciliation failed: memory off until a GM fixes it and restarts
    }

    public sealed record LedgerEvent(
        string EventId,
        long Epoch,
        long Seq,
        Guid NpcId,
        Guid CharId,
        string Kind,
        string FactsJson,
        DateTime AtUtc
    );

    private static AiLedger _instance;
    private static readonly List<LedgerEvent> _outbox = [];
    private static long _savedEpoch;
    private static long _savedSeq;
    private static HttpClient _http;
    private static bool _inFlight;
    private static DateTime _nextTry;
    private static bool _warnedDown;
    private static bool _warnedHeld;

    public static long Epoch { get; private set; }
    public static long Seq { get; private set; }
    public static LedgerState State { get; private set; } = LedgerState.Starting;

    // Codex 2b code review round 2, finding A: true only after THIS run's reconcile succeeded. A full outbox is a queue
    // state, not proof of reconciliation - relationship reads (Relations.LedgerReady) need this flag.
    public static bool Reconciled { get; private set; }
    public static string StateReason { get; private set; } = "waiting for the brain";

    // Stage A3 step 2 (Codex design R2, answer 2): true only while the brain THIS shard delivers to has said its ingest
    // understands the optional `topics` field on talked_with (reconcile answer at every start, re-read from every
    // delivery answer). False -> new rows are written in the five-key step-1 shape, and a queued row that carries
    // topics is HELD, never sent and never dropped. It is acceptance, not the brain's on/off switch for topics.
    public static bool TopicsIngest { get; private set; }
    public static int TopicsHeld { get; private set; }
    public static int OutboxCount => _outbox.Count;
    public static int MaxOutbox { get; private set; }
    public static long SavedEpoch => _savedEpoch;
    public static long SavedSeq => _savedSeq;

    public AiLedger() : base("AvalloneLedger", 10)
    {
    }

    public static void Configure()
    {
        _instance = new AiLedger();
        MaxOutbox = ServerConfiguration.GetOrUpdateSetting("avallone.ai.memoryOutboxMax", 2000);
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
    }

    // After the world has loaded: take a new epoch, then start talking to the brain.
    public static void Initialize()
    {
        Epoch = NextEpoch(EpochFile, _savedEpoch);
        Seq = 0;
        logger.Information(
            "Avallone memory: epoch {Epoch}; world save holds events up to {SavedEpoch}-{SavedSeq}; {Outbox} unacknowledged in the outbox",
            Epoch, _savedEpoch, _savedSeq, _outbox.Count
        );
        Timer.StartTimer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), Tick);

        // Integration testing on a throwaway dev shard only: one synthetic event every 10 s for the first
        // companion. Never set on the live service.
        if (Environment.GetEnvironmentVariable("AVALLONE_LEDGER_SELFTEST") == "1")
        {
            logger.Warning("Avallone memory: SELFTEST - recording a synthetic event every 10 s");
            Timer.StartTimer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), () =>
            {
                foreach (var c in Server.Mobiles.BaseCompanion.All)
                {
                    Record(c, c, "region_entered", new { region = "selftest" });
                    break;
                }
            });
        }
    }

    private static string EpochFile => Path.Combine(Core.BaseDirectory, "Avallone", "ai-epoch.txt");

    // Boot counter outside the saves. Never goes below what the loaded save already used.
    public static long NextEpoch(string path, long savedEpoch)
    {
        long last = 0;
        try
        {
            if (File.Exists(path))
            {
                long.TryParse(File.ReadAllText(path).Trim(), out last);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Avallone memory: could not read {Path}", path);
        }

        var next = Math.Max(last, savedEpoch) + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, next.ToString());
        File.Move(tmp, path, true);
        return next;
    }

    public static string EventId(long epoch, long seq) => $"{epoch}-{seq}";

    // Record something that happened between a companion and a character. Game thread only.
    public static void Record(Mobile npc, Mobile character, string kind, object facts = null)
    {
        // Secret progress counts even when the ledger itself is off or full (it is game state, saved with the world).
        if (npc is Server.Mobiles.BaseCompanion counted && character is { Deleted: false } && Epoch != 0)
        {
            AiSecrets.Count(counted.AiId, AiIdentity.GetOrCreate(character), kind);
            AiPersonality.OnEvent(counted, character, kind);
        }

        if (State == LedgerState.Off || npc == null || character == null || Epoch == 0)
        {
            return; // same early exit as before 5a, before any id is created
        }

        // A companion's conversations are keyed by her own id, so her memories must be too (bug found 2026-10-02:
        // 4a1 used AiIdentity here, a different id; the brain remaps those first 17 events once at start).
        Enqueue(npc is Server.Mobiles.BaseCompanion companion ? companion.AiId : AiIdentity.GetOrCreate(npc), character, kind, facts);
    }

    // Phase 5c (Codex finding 2): record under exactly this NPC id - the same id its conversations use. No second id.
    public static void Record(Guid npcId, Mobile character, string kind, object facts = null)
    {
        if (npcId == Guid.Empty || character == null)
        {
            return;
        }

        Enqueue(npcId, character, kind, facts);
    }

    // Phase 5a: the "npc" of a world deed. Fixed forever - the brain finds deeds by it.
    public static readonly Guid WorldId = new("a7a110e0-0000-4000-8000-000000000001");

    // Record a public deed of a character (no NPC involved). Same outbox, ack and rollback rules as Record.
    public static void RecordWorld(Mobile character, string kind, object facts = null) => Enqueue(WorldId, character, kind, facts);

    // 5c-2 2b: true only when the event is really in the saved outbox - a gift takes the item only after this (Codex 2b F3).
    public static bool TryRecord(Guid npcId, Mobile character, string kind, object facts) => Enqueue(npcId, character, kind, facts);

    // Tests only: a fresh unreconciled run with a small outbox (the real path needs a brain).
    internal static void ResetForTests(long epoch, int maxOutbox)
    {
        _outbox.Clear();
        Epoch = epoch;
        Seq = 0;
        MaxOutbox = maxOutbox;
        State = LedgerState.Starting;
        Reconciled = false;
        TopicsIngest = false;
        TopicsHeld = 0;
    }

    internal static void SetTopicsIngestForTests(bool on) => TopicsIngest = on;

    internal static bool CarriesTopics(LedgerEvent e) =>
        e.Kind == "talked_with" && e.FactsJson != null && e.FactsJson.Contains("\"topics\"", StringComparison.Ordinal);

    // How many events from the head of the outbox may go out now. Order is kept: nothing overtakes a held row.
    // Codex step 2 code R1 (S2-C1): even with a confirmed ingest, a batch never carries an ordinary row AFTER a topic row.
    // The confirmation describes the brain that answered last, not necessarily the one that gets this batch; if an older
    // brain refuses the topic rows they are kept (SendAsync), and nothing later in the queue has then overtaken them.
    internal static int SendableCount(System.Collections.Generic.IReadOnlyList<LedgerEvent> outbox, int batchSize, bool topicsIngest)
    {
        var n = Math.Min(batchSize, outbox.Count);
        if (topicsIngest)
        {
            var seenTopics = false;
            for (var i = 0; i < n; i++)
            {
                if (CarriesTopics(outbox[i]))
                {
                    seenTopics = true;
                }
                else if (seenTopics)
                {
                    return i;
                }
            }

            return n;
        }

        for (var i = 0; i < n; i++)
        {
            if (CarriesTopics(outbox[i]))
            {
                return i;
            }
        }

        return n;
    }

    internal static int OutboxCountForTests => _outbox.Count;

    // Tests only: the real delivery path against an in-process HTTP handler.
    internal static void SetHttpForTests(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://brain.test/"), Timeout = TimeSpan.FromSeconds(10) };

    internal static void AddForTests(LedgerEvent e) => _outbox.Add(e);
    internal static Task SendForTests() => SendAsync();

    private static bool Enqueue(Guid npcId, Mobile character, string kind, object facts)
    {
        if (State == LedgerState.Off || character == null || Epoch == 0)
        {
            return false;
        }

        // A deleted character no longer gets an id from GetOrCreate; use the one it had.
        Guid charId;
        if (!character.Deleted)
        {
            charId = AiIdentity.GetOrCreate(character);
        }
        else if (!AiIdentity.TryGet(character, out charId))
        {
            return false;
        }

        if (_outbox.Count >= MaxOutbox)
        {
            // Codex round 2 A: before reconciliation the state stays Starting (so Tick still reconciles first).
            if (State != LedgerState.Full && State != LedgerState.Starting)
            {
                State = LedgerState.Full;
                StateReason = $"outbox full ({MaxOutbox}) - brain unreachable? New memories are not being recorded";
                logger.Warning("Avallone memory: {Reason}", StateReason);
            }

            return false;
        }

        Seq++;
        _outbox.Add(
            new LedgerEvent(
                EventId(Epoch, Seq),
                Epoch,
                Seq,
                npcId,
                charId,
                kind,
                facts == null ? "{}" : JsonSerializer.Serialize(facts, _json),
                DateTime.UtcNow
            )
        );
        return true;
    }

    private static int _ticks;

    private static void Tick()
    {
        if (++_ticks % 150 == 0) // every 5 minutes
        {
            Server.Mobiles.BaseCompanion.SweepDeletedCharacters();
        }

        if (_inFlight || Core.Now < _nextTry || State == LedgerState.Off)
        {
            return;
        }

        if (State == LedgerState.Starting)
        {
            _inFlight = true;
            _ = ReconcileAsync();
        }
        else if (_outbox.Count > 0)
        {
            _inFlight = true;
            _ = SendAsync();
        }
    }

    private sealed record ReconcileReply(bool Ok, string Verdict, int RolledBack, string Reason, string Error, int TalkedWithTopics = 0);

    private sealed record IngestReply(bool Ok, string[] Acked, string[] Rejected, string Error, int TalkedWithTopics = 0);

    // Awaits resume on the game thread (ModernUO's EventLoopContext), so state is only touched there.
    private static async Task ReconcileAsync()
    {
        try
        {
            long? outboxMin = null;
            foreach (var e in _outbox)
            {
                if (e.Epoch == _savedEpoch && (outboxMin == null || e.Seq < outboxMin))
                {
                    outboxMin = e.Seq;
                }
            }

            var response = await _http.PostAsJsonAsync(
                "v1/memory/reconcile",
                new { new_epoch = Epoch, saved_epoch = _savedEpoch, saved_seq = _savedSeq, outbox_min_seq = outboxMin }
            );
            var reply = await response.Content.ReadFromJsonAsync<ReconcileReply>(_json);

            if (reply is not { Ok: true })
            {
                throw new InvalidOperationException(reply?.Error ?? $"HTTP {(int)response.StatusCode}");
            }

            TopicsIngest = reply.TalkedWithTopics == 1;

            if (reply.Verdict == "conflict")
            {
                Reconciled = false;
                State = LedgerState.Off;
                StateReason = "reconcile conflict: " + reply.Reason;
                logger.Error("Avallone memory: OFF - {Reason}", StateReason);
                return;
            }

            State = _outbox.Count >= MaxOutbox ? LedgerState.Full : LedgerState.Live;
            Reconciled = true;
            StateReason = reply.Verdict == "fresh" ? reply.Reason : $"reconciled, {reply.RolledBack} rolled back";
            _warnedDown = false;
            logger.Information(
                "Avallone memory: {Verdict} - {RolledBack} event(s) rolled back; {Reason}",
                reply.Verdict, reply.RolledBack, reply.Reason ?? "ok"
            );
        }
        catch (Exception ex)
        {
            Backoff("reconcile", ex);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private static async Task SendAsync()
    {
        try
        {
            // Step 2: a row that carries topics goes only to an ingest that has confirmed it understands them. With
            // none sendable the batch is empty - that still asks the brain, and its answer re-reads the capability.
            var take = SendableCount(_outbox, BatchSize, TopicsIngest);
            var batch = _outbox.GetRange(0, take);
            var events = batch.ConvertAll(e => new
            {
                event_id = e.EventId,
                epoch = e.Epoch,
                seq = e.Seq,
                npc_id = e.NpcId,
                char_id = e.CharId,
                kind = e.Kind,
                facts = JsonDocument.Parse(e.FactsJson).RootElement,
                at_utc = e.AtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                schema = SchemaVersion
            });

            var response = await _http.PostAsJsonAsync("v1/memory/events", new { events });
            var reply = await response.Content.ReadFromJsonAsync<IngestReply>(_json);

            if (reply is not { Ok: true })
            {
                throw new InvalidOperationException(reply?.Error ?? $"HTTP {(int)response.StatusCode}");
            }

            // Codex step 2 code R1 (S2-C1): THIS answer says whether the brain that judged this batch understands topics.
            // A refusal of a topic row by a brain that does not is not a verdict on the row - the brain was replaced or
            // rolled back while the shard ran. Such a row is kept and held; only a refusal by a brain that confirms
            // topic support (a genuinely bad row), or of any other row, removes it.
            var confirms = reply.TalkedWithTopics == 1;
            var done = new HashSet<string>(reply.Acked ?? []);
            var kept = 0;
            foreach (var id in reply.Rejected ?? [])
            {
                if (!confirms && batch.Exists(e => e.EventId == id && CarriesTopics(e)))
                {
                    kept++;
                    continue;
                }

                done.Add(id);
                logger.Warning("Avallone memory: brain rejected event {EventId}", id);
            }

            _outbox.RemoveAll(e => done.Contains(e.EventId));
            _warnedDown = false;
            TopicsIngest = confirms;
            if (kept > 0)
            {
                logger.Warning(
                    "Avallone memory: {Kept} talked_with event(s) with topics were refused by a brain that does not accept " +
                    "them (replaced or rolled back?). KEPT, not dropped",
                    kept
                );
            }

            if ((take == 0 || kept > 0) && !TopicsIngest)
            {
                TopicsHeld = _outbox.Count;
                _nextTry = Core.Now + TimeSpan.FromSeconds(30);
                if (!_warnedHeld)
                {
                    _warnedHeld = true;
                    logger.Warning(
                        "Avallone memory: {Held} event(s) HELD - the brain's ingest does not accept talked_with topics. " +
                        "Nothing is dropped; they go out when a brain that accepts them answers",
                        _outbox.Count
                    );
                }
            }
            else
            {
                TopicsHeld = 0;
                _warnedHeld = false;
            }

            if (State == LedgerState.Full && _outbox.Count < MaxOutbox / 2)
            {
                State = LedgerState.Live;
                StateReason = "outbox drained";
                logger.Information("Avallone memory: recording again ({Outbox} still queued)", _outbox.Count);
            }
        }
        catch (Exception ex)
        {
            Backoff("delivery", ex);
        }
        finally
        {
            _inFlight = false;
        }
    }

    private static void Backoff(string what, Exception ex)
    {
        // Codex step 2 code R1 (S2-C1): a brain that did not answer may not be the brain that confirmed topic support.
        // Unconfirmed until an answer says so again; the next batch stops at the first topic row and re-reads it.
        TopicsIngest = false;
        _nextTry = Core.Now + TimeSpan.FromSeconds(30);
        if (!_warnedDown)
        {
            _warnedDown = true;
            logger.Warning("Avallone memory: {What} failed ({Error}); retrying every 30 s, {Outbox} queued", what, ex.Message, _outbox.Count);
        }
    }

    // GM status line for [ai memory.
    public static string Describe() =>
        $"memory {State} ({StateReason}); epoch {Epoch}, last event {Seq}, outbox {_outbox.Count}/{MaxOutbox}; " +
        $"loaded save held events up to {_savedEpoch}-{_savedSeq}" +
        $"; talked-with topics: ingest {(TopicsIngest ? "confirmed" : "not confirmed")}{(TopicsHeld > 0 ? $", {TopicsHeld} HELD" : "")}";

    public static async Task<string> BrainStatusAsync(Guid? npcId, Guid? charId)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("v1/memory/status", new { npc_id = npcId, char_id = charId });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            if (!r.GetProperty("ok").GetBoolean())
            {
                return "brain: " + r.GetProperty("error").GetString();
            }

            return $"brain: {r.GetProperty("live").GetInt32()} remembered, {r.GetProperty("rolled_back").GetInt32()} rolled back, " +
                   $"last {r.GetProperty("last_event_utc")}; {r.GetProperty("kinds")}";
        }
        catch (Exception ex)
        {
            return "brain unreachable: " + ex.Message;
        }
    }

    // The save holds every event up to (Epoch, Seq), plus the ones not yet acknowledged.
    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.Write(Epoch);
        writer.Write(Seq);
        writer.WriteEncodedInt(_outbox.Count);
        foreach (var e in _outbox)
        {
            writer.Write(e.Epoch);
            writer.Write(e.Seq);
            writer.Write(e.NpcId);
            writer.Write(e.CharId);
            writer.Write(e.Kind);
            writer.Write(e.FactsJson);
            writer.Write(e.AtUtc);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version
        _savedEpoch = reader.ReadLong();
        _savedSeq = reader.ReadLong();
        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var epoch = reader.ReadLong();
            var seq = reader.ReadLong();
            _outbox.Add(
                new LedgerEvent(
                    EventId(epoch, seq),
                    epoch,
                    seq,
                    reader.ReadGuid(),
                    reader.ReadGuid(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadDateTime()
                )
            );
        }
    }
}
