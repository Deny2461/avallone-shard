using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModernUO.CodeGeneratedEvents;
using Server.Avallone.AI;
using Server.Avallone.Rumours;
using Server.Commands;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Named;

// Phase 5c (design v2 after Codex, 2026-10-30): the ten named NPCs remember visitors between visits and across restarts.
// - One id per named NPC (INamedNpc.AiId) for requests AND memories (Codex finding 2). Mirabel keeps her existing _aiId.
// - A visit's lines stay in RAM; when it goes quiet the brain writes claims-only notes from the PLAYER's lines, which the
//   shard records with AiLedger, so a world restore rolls them back (finding 1, 3).
// - Familiarity: one "visited" event per NPC + character + calendar day (America/Chicago); the brain counts distinct days.
// - Summaries never get in the way of conversation (finding 5): own queue, one at a time, only when the gateway is idle,
//   never within 10 minutes of the quiet window, own deadline, failures kept out of the conversation circuit breaker.
// - Deletion: every player deletion records one global tombstone (finding 3) via PlayerMobile.PlayerDeletedEvent.
public interface INamedNpc
{
    Guid AiId { get; }

    string NamedKey { get; }
}

public static class NamedNpcs
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(NamedNpcs));

    private const int MaxLines = 12;
    private const int LeaveRange = 12;
    private static readonly TimeSpan GoneGrace = TimeSpan.FromMinutes(1);

    public static TimeSpan IdleBeforeSummary { get; private set; } = TimeSpan.FromMinutes(10);
    public static int SummariesPerHour { get; private set; } = 12;
    public static int QueueMax { get; private set; } = 20;
    public static TimeSpan HoldBeforeQuiet { get; private set; } = TimeSpan.FromMinutes(10);
    public static TimeSpan SummaryBudget { get; private set; } = TimeSpan.FromSeconds(6);
    public static bool Enabled { get; private set; }

    private sealed class Visit
    {
        public Mobile Npc;
        public Guid NpcId;
        public string Key;
        public Mobile Player;
        public readonly List<AiLine> Lines = [];
        public DateTime LastLine;
    }

    // 5c-2 2a: Pt / Mpt / VisitId are taken when the visit ENDS and travel with the job, so queue delay and retries never
    // make the notes younger than the visit (Codex round 2 F2).
    internal sealed record Job(Mobile Npc, Guid NpcId, string Key, Mobile Player, Guid CharId, long Gen, AiLine[] Lines, DateTime QueuedAt)
    {
        public long Pt { get; init; }
        public double Mpt { get; init; }
        public Guid VisitId { get; init; } = Guid.NewGuid();

        public int Tries { get; init; }   // pre-empted by a conversation: retried once, then dropped
    }

    private static CancellationTokenSource _running;
    private static bool _preempted;
    public static int Preempted { get; private set; }

    private static readonly Dictionary<(Serial Npc, Serial Player), Visit> _visits = new();
    private static readonly Dictionary<(Guid Npc, Guid Chr), string> _visitedDay = new();
    private static readonly Dictionary<Serial, long> _playerGen = new();   // bumped when a player is deleted
    private static readonly List<Job> _queue = [];
    private static readonly Queue<DateTime> _sent = new();
    private static long _globalGen;                                        // bumped when 5c is switched off
    private static bool _inFlight;
    private static TimeZoneInfo _dayZone;

    // Where memories go (tests swap these to see exactly which id and kind would be recorded).
    internal static Action<Guid, Mobile, string, object> Recorder = AiLedger.Record;
    internal static Action<Mobile, string> WorldRecorder = (m, kind) => AiLedger.RecordWorld(m, kind);

    public static int Queued => _queue.Count;
    public static int Stored { get; private set; }
    public static int Empty { get; private set; }
    public static int Dropped { get; private set; }
    public static int Failed { get; private set; }
    public static int Discarded { get; private set; }
    public static DateTime LastSuccess { get; private set; }

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.named.enabled", true);
        IdleBeforeSummary = TimeSpan.FromMinutes(Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.named.summaryIdleMinutes", 10)));
        SummariesPerHour = Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.named.summariesPerHour", 12));
        QueueMax = Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.named.summaryQueueMax", 20));
        _dayZone = FindZone("America/Chicago");
        CommandSystem.Register("Named", AccessLevel.Administrator, OnCommand);
    }

    public static void Initialize()
    {
        AiGateway.BeforeConversation = Preempt;
        Timer.DelayCall(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), Tick);
        logger.Information("Avallone named NPCs: {Status}", Describe());
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The household's calendar day (America/Chicago) - one "visited" per NPC + character per day.
    public static string Today(DateTime utc)
    {
        var zone = _dayZone ??= FindZone("America/Chicago");
        var local = zone == null ? utc : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("yyyy-MM-dd");
    }

    // What a named NPC's request carries: its key, today, and the speaker (null = 5b behaviour, no memory).
    public static NamedAsk Ask(Mobile npc, Mobile from)
    {
        if (!Enabled || npc is not INamedNpc n || from is not PlayerMobile || n.AiId == Guid.Empty)
        {
            return null;
        }

        var today = Today(Core.Now);
        var key = (n.AiId, AiIdentity.GetOrCreate(from));
        if (!_visitedDay.TryGetValue(key, out var day) || day != today)
        {
            _visitedDay[key] = today;
            Recorder(n.AiId, from, "visited", new { day = today, mpt = PlayedClock.MemorySeconds }); // 2b: warmth fades on mpt
        }

        return new NamedAsk(n.NamedKey, today, from.Name, null, PlayedClock.Seconds, PlayedClock.MemorySeconds);
    }

    // A line the player said to a named NPC that got an answer: kept in RAM for this visit's notes (player lines only).
    public static void OnExchange(Mobile npc, Mobile from, string playerText)
    {
        if (!Enabled || npc is not INamedNpc n || from is not PlayerMobile || string.IsNullOrWhiteSpace(playerText))
        {
            return;
        }

        var k = (npc.Serial, from.Serial);
        if (!_visits.TryGetValue(k, out var v))
        {
            _visits[k] = v = new Visit { Npc = npc, NpcId = n.AiId, Key = n.NamedKey, Player = from };
        }

        v.Lines.Add(new AiLine("player", playerText.Trim()));
        if (v.Lines.Count > MaxLines)
        {
            v.Lines.RemoveRange(0, v.Lines.Count - MaxLines);
        }

        v.LastLine = Core.Now;
    }

    private static void Tick()
    {
        var now = Core.Now;
        List<(Serial, Serial)> done = null;
        foreach (var (k, v) in _visits)
        {
            var p = v.Player;
            var gone = p.Deleted || p.NetState == null || v.Npc.Deleted || p.Map != v.Npc.Map || !p.InRange(v.Npc, LeaveRange);
            if (now - v.LastLine >= IdleBeforeSummary || gone && now - v.LastLine >= GoneGrace)
            {
                (done ??= []).Add(k);
                if (!p.Deleted && AiIdentity.TryGet(p, out var charId))
                {
                    EnqueueJob(new Job(v.Npc, v.NpcId, v.Key, p, charId, Gen(p), v.Lines.ToArray(), now)
                    {
                        Pt = PlayedClock.Seconds, Mpt = PlayedClock.MemorySeconds
                    });
                }
            }
        }

        if (done != null)
        {
            foreach (var k in done)
            {
                _visits.Remove(k);
            }
        }

        TryDispatch(now);
    }

    internal static long Gen(Mobile p) => _globalGen * 1_000_000 + (_playerGen.TryGetValue(p.Serial, out var g) ? g : 0);

    internal static void EnqueueJob(Job job)
    {
        _queue.Add(job);
        while (_queue.Count > QueueMax)
        {
            _queue.RemoveAt(0); // oldest first; its RAM lines go with it
            Dropped++;
        }
    }

    // Codex finding 5: one at a time, only when nothing else is queued or in flight, never near the quiet window.
    internal static bool MayDispatch(DateTime now) =>
        Enabled && !_inFlight && _queue.Count > 0 && AiGateway.Enabled && AiGateway.Client != null &&
        AiGateway.IsIdle && !AiGateway.BreakerIsOpen && !AiGateway.InQuietWindowAt(now, HoldBeforeQuiet) && RateOk(now);

    private static bool RateOk(DateTime now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromHours(1))
        {
            _sent.Dequeue();
        }

        return _sent.Count < SummariesPerHour;
    }

    private static void TryDispatch(DateTime now)
    {
        if (!MayDispatch(now))
        {
            return;
        }

        var job = _queue[0];
        _queue.RemoveAt(0);
        _sent.Enqueue(now);
        _ = RunAsync(job);
    }

    private static async Task RunAsync(Job job)
    {
        _inFlight = true;
        try
        {
            var req = new AiRequest(job.NpcId, job.CharId, null, "", AiPriority.Background)
            {
                Mode = "summarize",
                PersonaKey = job.Key,
                History = job.Lines,
                Named = new NamedAsk(job.Key, Today(Core.Now), job.Player.Name, Rumours.RumourSpeech.CurrentRoster(), job.Pt, job.Mpt)
            };
            using var cts = new CancellationTokenSource(SummaryBudget);
            _running = cts;
            _preempted = false;
            AiReply reply;
            try
            {
                reply = await AiGateway.Client.SendAsync(req, SummaryBudget, cts.Token); // resumes on the game thread
            }
            catch (Exception ex)
            {
                reply = AiReply.Fail(AiOutcome.Failed, ex.GetType().Name);
            }

            if (_preempted)
            {
                // A conversation took the model (Codex 5c-1 finding 4): try this visit once more later, then let it go.
                Preempted++;
                if (job.Tries == 0 && Enabled)
                {
                    _queue.Insert(0, job with { Tries = 1 });
                }
                else
                {
                    Dropped++;
                }

                return;
            }

            Accept(job, reply);
        }
        finally
        {
            _running = null;
            _inFlight = false; // the conversation circuit breaker never hears about summaries
        }
    }

    // A conversation is about to ask the model: cancel the summary in flight, if any (the brain cuts its model call too).
    internal static void Preempt()
    {
        var cts = _running;
        if (cts != null && !cts.IsCancellationRequested)
        {
            _preempted = true;
            cts.Cancel();
        }
    }

    internal static void DispatchForTests(DateTime now) => TryDispatch(now);

    // Store the notes only if the character, the NPC and the visit are all still the ones the job was made for.
    internal static string Accept(Job job, AiReply reply)
    {
        if (!reply.Ok)
        {
            Failed++;
            return "failed";
        }

        if (job.Player is not PlayerMobile { Deleted: false } pm || !AiIdentity.TryGet(pm, out var cid) || cid != job.CharId ||
            job.Npc is not INamedNpc n || job.Npc.Deleted || n.AiId != job.NpcId || Gen(pm) != job.Gen || !Enabled)
        {
            Discarded++;
            return "discarded";
        }

        string[] claims, topics, items;
        try
        {
            using var doc = JsonDocument.Parse(reply.Text);
            claims = Strings(doc.RootElement, "claims", 3, 120);
            topics = Strings(doc.RootElement, "topics", 3, 40);
            items = Strings(doc.RootElement, "items", 6, 100); // 5c-2 2a: typed facts/requests made by code patterns
        }
        catch (Exception)
        {
            Failed++;
            return "failed";
        }

        if (claims.Length == 0 && topics.Length == 0 && items.Length == 0)
        {
            Empty++;
            return "empty";
        }

        // The visit's own clock values and id (Codex round 2 F2); the brain checks the shape again at its door.
        Recorder(job.NpcId, pm, "visit_summary", items.Length > 0
            ? new { claims, topics, items, pt = job.Pt, mpt = job.Mpt, visit = job.VisitId.ToString("N") }
            : new { claims, topics, pt = job.Pt, mpt = job.Mpt, visit = job.VisitId.ToString("N") });
        Stored++;
        LastSuccess = Core.Now;
        return "stored";
    }

    private static string[] Strings(JsonElement root, string name, int max, int size)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String || list.Count >= max)
                {
                    throw new FormatException(name);
                }

                var s = e.GetString()?.Trim() ?? "";
                if (s.Length > size)
                {
                    throw new FormatException(name);
                }

                if (s.Length > 0)
                {
                    list.Add(s);
                }
            }
        }

        return list.ToArray();
    }

    // Codex finding 3: any deleted character with an AI identity gets one global tombstone; its visits and jobs go too.
    [OnEvent(nameof(PlayerMobile.PlayerDeletedEvent))]
    public static void OnPlayerDeleted(PlayerMobile pm)
    {
        _playerGen[pm.Serial] = (_playerGen.TryGetValue(pm.Serial, out var g) ? g : 0) + 1;
        _queue.RemoveAll(j => j.Player == pm);
        var gone = new List<(Serial, Serial)>();
        foreach (var k in _visits.Keys)
        {
            if (k.Player == pm.Serial)
            {
                gone.Add(k);
            }
        }

        foreach (var k in gone)
        {
            _visits.Remove(k);
        }

        if (AiIdentity.TryGet(pm, out var cid))
        {
            _visitedDay.Keys.Where(k => k.Chr == cid).ToList().ForEach(k => _visitedDay.Remove(k));   // Codex optional 1
            WorldRecorder(pm, "character_deleted");
        }
    }

    public static int VisitDaysHeld => _visitedDay.Count;

    // 5c-2 2a (Codex round 2 F5): after a named NPC has really SAID a reply, record which memory cards it was offered -
    // ids only, no text - so the brain can let those cool down. A refused, retried or undelivered reply records nothing.
    public static void OnDelivered(Mobile npc, Mobile from, string[] cards)
    {
        if (!Enabled || npc is not INamedNpc n || from is not PlayerMobile || cards is not { Length: > 0 } || n.AiId == Guid.Empty)
        {
            return;
        }

        var ok = cards.Where(c => !string.IsNullOrEmpty(c) && c.Length <= 42).Take(6).ToArray();
        if (ok.Length > 0)
        {
            Recorder(n.AiId, from, "reply_delivered", new { cards = ok, pt = PlayedClock.Seconds, mpt = PlayedClock.MemorySeconds });
        }
    }

    public static void SetEnabled(bool on) => Switch(on, true);

    internal static void Switch(bool on, bool persist)
    {
        Enabled = on;
        if (!on)
        {
            _running?.Cancel();   // Codex round 2 optional: the kill switch frees the model at once (the result is discarded)
        }
        if (persist)
        {
            ServerConfiguration.SetSetting("avallone.named.enabled", on ? "true" : "false");
        }

        if (!on)
        {
            _globalGen++; // anything in flight is discarded
            _queue.Clear();
            _visits.Clear();
        }
    }

    public static void ResetForTests(bool enabled)
    {
        Enabled = enabled;
        _visits.Clear();
        _visitedDay.Clear();
        _playerGen.Clear();
        _queue.Clear();
        _sent.Clear();
        _inFlight = false;
        _running = null;
        _preempted = false;
        Stored = Empty = Dropped = Failed = Discarded = Preempted = 0;
    }

    public static string Describe()
    {
        var oldest = _queue.Count > 0 ? $"{(Core.Now - _queue[0].QueuedAt).TotalMinutes:0} min" : "-";
        var last = LastSuccess == default ? "never" : $"{(Core.Now - LastSuccess).TotalMinutes:0} min ago";
        return $"{(Enabled ? "ON" : "OFF")}; visits open {_visits.Count}, visit days held {_visitedDay.Count}; " +
               $"summaries queued {_queue.Count} (oldest {oldest}), " +
               $"stored {Stored}, empty {Empty}, dropped {Dropped}, failed {Failed}, discarded {Discarded}, pre-empted {Preempted}; " +
               $"last stored {last}; " +
               $"idle {IdleBeforeSummary.TotalMinutes:0} min, {SummariesPerHour}/h, queue max {QueueMax}";
    }

    // [Named home]: the NPC moves to the admin's spot, which becomes its home; it wanders at most `range` tiles from it.
    internal static string SetHome(Mobile npc, Point3D at, Map map, int range)
    {
        if (npc is not BaseCreature bc)
        {
            return $"{npc.Name} cannot be given a home.";
        }

        if (map == null || map == Map.Internal)
        {
            return "Stand on a real map first.";
        }

        range = Math.Clamp(range, 0, 10);
        bc.MoveToWorld(at, map);
        bc.Home = at;
        bc.RangeHome = range;
        return $"{npc.Name} now lives at {at} {map}, wandering at most {range} tile{(range == 1 ? "" : "s")} from it.";
    }

    private static void OnCommand(CommandEventArgs e)
    {
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";
        switch (verb)
        {
            case "on":
                SetEnabled(true);
                break;
            case "off":
                SetEnabled(false);
                break;
            case "status":
                break;
            case "why":   // 5c-2 2b design B8
                NamedWhy.Begin(e.Mobile, string.Join(" ", e.Arguments.Skip(1)));
                return;
            case "home":  // the owner, chat, 2026-11-02: ->
                {
                    var key = e.Length > 1 ? e.GetString(1).ToLowerInvariant() : "";
                    var range = e.Length > 2 ? e.GetInt32(2) : 2;
                    var named = World.Mobiles.Values.Where(m => m is INamedNpc n && !m.Deleted && n.NamedKey == key).ToList();
                    var msg = named.Count switch
                    {
                        0 => $"No named NPC \"{key}\". Usage: [Named home <key> [range 0-10]] - stand where they should live. " +
                             $"Keys: {string.Join(", ", World.Mobiles.Values.OfType<INamedNpc>().Select(n => n.NamedKey).Distinct())}",
                        > 1 => $"{named.Count} NPCs answer to \"{key}\" - [Named status lists them; nothing changed.",
                        _ => SetHome(named[0], e.Mobile.Location, e.Mobile.Map, range)
                    };
                    logger.Information("Named home: {Admin} {Key} at {Location} {Map} range {Range} - {Result}",
                        e.Mobile.Name, key, e.Mobile.Location, e.Mobile.Map, range, msg);
                    e.Mobile.SendMessage(msg);
                    return;
                }
            default:
                e.Mobile.SendMessage("Usage: [Named status|on|off|why <character name>|home <key> [range]");
                return;
        }

        e.Mobile.SendMessage($"Named NPCs: {Describe()}");
        foreach (var m in World.Mobiles.Values)
        {
            if (m is INamedNpc n && !m.Deleted)
            {
                e.Mobile.SendMessage($"  {n.NamedKey}: {m.Name} id {n.AiId} at {m.Location} {m.Map}");
            }
        }
    }
}
