using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Server.Logging;
using Server.Mobiles;
using Server.Network;

namespace Server.Avallone.AI;

// The single door between game code and any AI backend.
//
// Rules it enforces (Projects/Avallone-AI-NPC-Plan.md v2):
// - never blocks the game loop: callers await a Task, nothing here waits synchronously
// - kill switch (avallone.ai.enabled, default OFF) — off means every request fails fast as Disabled
// - one priority queue with a size cap; lower-priority work is pushed out first
// - a deadline set at enqueue time; work that waited too long is dropped, never sent
// - one conversation lease per NPC + subject
// - a hard timeout on every call
// - a circuit breaker after repeated failures
// - never throws into game code: every problem comes back as an AiReply outcome
// - a daily quiet window (another scheduled job's run on the model host) where requests return Quiet, not a failure
// - keeps the model warm only while a player is logged in
//
// All state is touched only on the game thread (awaits resume there through EventLoopContext).
public static class AiGateway
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AiGateway));

    private static readonly List<Pending> _queue = [];
    private static readonly HashSet<string> _leases = [];
    private static long _sequence;
    private static int _inFlight;
    private static int _consecutiveFailures;
    private static long _breakerOpenUntil;
    private static long _lastWarm = long.MinValue / 2;
    private static TimeZoneInfo _quietZone;

    public static bool Enabled { get; private set; }
    public static IAiNpcClient Client { get; set; }
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(4);
    public static TimeSpan QueueDeadline { get; set; } = TimeSpan.FromSeconds(6);

    // How long a companion waits on a reply before saying a short stalling line ("Well..."). Zero = never.
    public static TimeSpan ThinkingAfter { get; set; } = TimeSpan.FromSeconds(4);
    public static int MaxQueue { get; set; } = 16;
    public static int MaxInFlight { get; set; } = 1;
    public static int BreakerThreshold { get; set; } = 3;
    public static TimeSpan BreakerCooldown { get; set; } = TimeSpan.FromSeconds(60);

    // Local wall-clock window in QuietZone. Start == End means no window.
    public static TimeSpan QuietStart { get; set; } = TimeSpan.Zero;
    public static TimeSpan QuietEnd { get; set; } = TimeSpan.Zero;
    public static TimeSpan WarmInterval { get; set; } = TimeSpan.FromMinutes(4);
    public static Func<DateTime> UtcNow { get; set; } = () => Core.Now;

    // Milliseconds; replaceable so tests control time.
    public static Func<long> Clock { get; set; } = () => Core.TickCount;

    public static AiMetrics Metrics { get; private set; } = new();

    public static int QueueCount => _queue.Count;

    // Phase 5c: background summaries start only when nothing is queued or in flight.
    public static bool IsIdle => _queue.Count == 0 && _inFlight == 0;

    // Tests pin the quiet-window zone so the hold is checked the same way on any machine.
    internal static TimeZoneInfo QuietZone { get => _quietZone; set => _quietZone = value; }

    // Phase 5c: is `utc` inside the quiet window, or within `early` before it starts? (summaries hold off early)
    public static bool InQuietWindowAt(DateTime utc, TimeSpan early)
    {
        if (QuietStart == QuietEnd)
        {
            return false;
        }

        var local = _quietZone == null ? utc : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _quietZone);
        var t = local.TimeOfDay;
        var start = QuietStart - early;
        if (start < TimeSpan.Zero)
        {
            start += TimeSpan.FromDays(1);
        }

        return start < QuietEnd ? t >= start && t < QuietEnd : t >= start || t < QuietEnd;
    }
    public static int InFlight => _inFlight;
    public static bool BreakerIsOpen => Clock() < _breakerOpenUntil;
    public static long BreakerRemainingMs => Math.Max(0, _breakerOpenUntil - Clock());

    public static bool InQuietWindow
    {
        get
        {
            if (QuietStart == QuietEnd)
            {
                return false;
            }

            var local = _quietZone == null ? UtcNow() : TimeZoneInfo.ConvertTimeFromUtc(UtcNow(), _quietZone);
            var t = local.TimeOfDay;
            return QuietStart < QuietEnd ? t >= QuietStart && t < QuietEnd : t >= QuietStart || t < QuietEnd;
        }
    }

    // "morning", "afternoon", "evening" or "late at night" in the household's time zone (the quiet-window zone).
    public static string TimeOfDay(DateTime utc)
    {
        var local = _quietZone == null
            ? utc
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _quietZone);
        return local.Hour switch
        {
            >= 5 and < 12  => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 22 => "evening",
            _              => "late at night"
        };
    }

    public static string QuietDescription =>
        QuietStart == QuietEnd ? "none" : $"{QuietStart:hh\\:mm}-{QuietEnd:hh\\:mm} {_quietZone?.Id ?? "UTC"}";

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.ai.enabled", false);
        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        RequestTimeout = ServerConfiguration.GetOrUpdateSetting("avallone.ai.requestTimeout", TimeSpan.FromSeconds(4));
        QueueDeadline = ServerConfiguration.GetOrUpdateSetting("avallone.ai.queueDeadline", TimeSpan.FromSeconds(6));
        ThinkingAfter = ServerConfiguration.GetOrUpdateSetting("avallone.ai.thinkingAfter", TimeSpan.FromSeconds(4));
        MaxQueue = ServerConfiguration.GetOrUpdateSetting("avallone.ai.maxQueue", 16);
        MaxInFlight = ServerConfiguration.GetOrUpdateSetting("avallone.ai.maxInFlight", 1);
        BreakerThreshold = ServerConfiguration.GetOrUpdateSetting("avallone.ai.breakerThreshold", 3);
        BreakerCooldown = ServerConfiguration.GetOrUpdateSetting("avallone.ai.breakerCooldown", TimeSpan.FromSeconds(60));

        QuietStart = ServerConfiguration.GetOrUpdateSetting("avallone.ai.quietStart", new TimeSpan(4, 55, 0));
        QuietEnd = ServerConfiguration.GetOrUpdateSetting("avallone.ai.quietEnd", new TimeSpan(5, 50, 0));
        var zone = ServerConfiguration.GetOrUpdateSetting("avallone.ai.quietTimeZone", "America/Chicago");
        WarmInterval = ServerConfiguration.GetOrUpdateSetting("avallone.ai.warmInterval", TimeSpan.FromMinutes(4));

        try
        {
            _quietZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Avallone AI gateway: unknown time zone {Zone}, quiet window uses UTC", zone);
        }

        Client = new SidecarAiClient(new Uri(url));

        // Keep the model loaded only while someone is playing (the owner, 2026-09-30).
        Timer.StartTimer(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), WarmTick);

        logger.Information(
            "Avallone AI gateway: {State}, client {Client}, timeout {Timeout}, quiet {Quiet}",
            Enabled ? "ENABLED" : "disabled",
            Client.Name,
            RequestTimeout,
            QuietDescription
        );
    }

    // The kill switch. Off also cancels everything still queued.
    public static void SetEnabled(bool enabled, bool persist)
    {
        Enabled = enabled;

        if (persist)
        {
            ServerConfiguration.SetSetting("avallone.ai.enabled", enabled ? "true" : "false");
        }

        if (!enabled)
        {
            while (_queue.Count > 0)
            {
                var p = _queue[^1];
                _queue.RemoveAt(_queue.Count - 1);
                Complete(p, AiReply.Fail(AiOutcome.Disabled));
            }
        }

        logger.Information("Avallone AI gateway {State}{Persist}", enabled ? "ENABLED" : "DISABLED", persist ? "" : " (until restart)");
    }

    public static void ResetBreaker()
    {
        _consecutiveFailures = 0;
        _breakerOpenUntil = 0;
    }

    // Test support only.
    public static void ResetForTests()
    {
        _queue.Clear();
        _leases.Clear();
        _inFlight = 0;
        _sequence = 0;
        _lastWarm = long.MinValue / 2;
        _quietZone = null;
        QuietStart = QuietEnd = TimeSpan.Zero;
        ResetBreaker();
        Metrics = new AiMetrics();
        Enabled = true;
    }

    public static Task<AiReply> RequestAsync(AiRequest request)
    {
        var start = Stopwatch.GetTimestamp();

        try
        {
            return Enqueue(request);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Avallone AI gateway: enqueue failed");
            return Task.FromResult(Record(AiReply.Fail(AiOutcome.Failed, ex.Message)));
        }
        finally
        {
            Metrics.RecordHitch(Stopwatch.GetElapsedTime(start));
        }
    }

    // Codex 5c-1 code review finding 4: called before every non-background request, so a disposable background job
    // (a named NPC's visit summary) gives up the model at once. Set by NamedNpcs.Initialize; never reset by tests.
    public static Action BeforeConversation;

    private static Task<AiReply> Enqueue(AiRequest request)
    {
        if (!Enabled || Client == null)
        {
            return Task.FromResult(Record(AiReply.Fail(AiOutcome.Disabled)));
        }

        if (request.Priority != AiPriority.Background)
        {
            try
            {
                BeforeConversation?.Invoke();
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Avallone AI gateway: background pre-emption failed");
            }

            // Stage A2 (Codex Stage A design F2): a conversation always takes the model from a companion <-> NPC exchange.
            try
            {
                Exchange.Preempt("a conversation needs the model");
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Avallone AI gateway: exchange pre-emption failed");
            }
        }

        if (InQuietWindow)
        {
            return Task.FromResult(Record(AiReply.Fail(AiOutcome.Quiet)));
        }

        if (BreakerIsOpen)
        {
            return Task.FromResult(Record(AiReply.Fail(AiOutcome.BreakerOpen)));
        }

        if (_leases.Contains(request.LeaseKey))
        {
            return Task.FromResult(Record(AiReply.Fail(AiOutcome.Busy)));
        }

        if (_queue.Count >= MaxQueue)
        {
            // The worst queued item is last (lowest priority, newest). Push it out only for better work.
            var worst = _queue[^1];
            if (worst.Request.Priority <= request.Priority)
            {
                return Task.FromResult(Record(AiReply.Fail(AiOutcome.QueueFull)));
            }

            _queue.RemoveAt(_queue.Count - 1);
            Complete(worst, AiReply.Fail(AiOutcome.Evicted));
        }

        var pending = new Pending(request, ++_sequence, Clock() + (long)QueueDeadline.TotalMilliseconds);
        Insert(pending);
        _leases.Add(request.LeaseKey);
        Metrics.RecordQueueDepth(_queue.Count);

        var task = pending.Completion.Task;
        Pump();
        return task;
    }

    // Keep the queue ordered by priority, then arrival.
    private static void Insert(Pending pending)
    {
        var i = _queue.Count;
        while (i > 0 && _queue[i - 1].Request.Priority > pending.Request.Priority)
        {
            i--;
        }

        _queue.Insert(i, pending);
    }

    private static void Pump()
    {
        while (Enabled && _inFlight < MaxInFlight && _queue.Count > 0 && !BreakerIsOpen)
        {
            var pending = _queue[0];
            _queue.RemoveAt(0);

            if (Clock() > pending.Deadline)
            {
                Complete(pending, AiReply.Fail(AiOutcome.Expired));
                continue;
            }

            _inFlight++;
            _ = RunAsync(pending);
        }

        // Breaker opened while work was queued: those requests fail now rather than wait it out.
        if (BreakerIsOpen)
        {
            while (_queue.Count > 0)
            {
                var p = _queue[0];
                _queue.RemoveAt(0);
                Complete(p, AiReply.Fail(AiOutcome.BreakerOpen));
            }
        }
    }

    private static async Task RunAsync(Pending pending)
    {
        var start = Stopwatch.GetTimestamp();
        var client = Client;
        AiReply reply;

        var timeout = pending.Request.Timeout ?? RequestTimeout;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            reply = await client.SendAsync(pending.Request, timeout, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            reply = AiReply.Fail(AiOutcome.Timeout, $"no reply within {timeout.TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            reply = AiReply.Fail(AiOutcome.Failed, ex.GetBaseException().Message);
        }

        _inFlight--;
        reply = reply with { ElapsedMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds };

        if (reply.Ok)
        {
            _consecutiveFailures = 0;
            _lastWarm = Clock(); // a real call keeps the model loaded too
            Metrics.RecordLatency(reply.ElapsedMs);
        }
        else if (++_consecutiveFailures >= BreakerThreshold)
        {
            _breakerOpenUntil = Clock() + (long)BreakerCooldown.TotalMilliseconds;
            logger.Warning(
                "Avallone AI gateway: circuit breaker OPEN for {Cooldown} after {Count} failures (last: {Error})",
                BreakerCooldown,
                _consecutiveFailures,
                reply.Error
            );
        }

        if (!Enabled)
        {
            reply = AiReply.Fail(AiOutcome.Disabled) with { ElapsedMs = reply.ElapsedMs };
        }

        logger.Information(
            "AI call {Outcome} npc={Npc} prio={Priority} {Elapsed} ms model={Model}{Error}",
            reply.Outcome,
            pending.Request.NpcId,
            pending.Request.Priority,
            reply.ElapsedMs,
            reply.Model ?? "-",
            reply.Error == null ? "" : $" error=\"{reply.Error}\""
        );

        Complete(pending, reply);
        Pump();
    }

    public static int PlayersOnline()
    {
        var count = 0;
        foreach (var ns in NetState.Instances)
        {
            if (ns.Mobile is PlayerMobile)
            {
                count++;
            }
        }

        return count;
    }

    private static void WarmTick()
    {
        if (!Enabled || Client == null || InQuietWindow || BreakerIsOpen ||
            Clock() - _lastWarm < (long)WarmInterval.TotalMilliseconds || PlayersOnline() == 0)
        {
            return;
        }

        _lastWarm = Clock();
        _ = WarmAsync();
    }

    public static async Task WarmAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await Client.WarmAsync(cts.Token);
            Metrics.RecordWarmup(true);
        }
        catch (Exception ex)
        {
            Metrics.RecordWarmup(false);
            logger.Debug("Avallone AI gateway: warm-up failed: {Error}", ex.GetBaseException().Message);
        }
    }

    private static void Complete(Pending pending, AiReply reply)
    {
        _leases.Remove(pending.Request.LeaseKey);
        pending.Completion.TrySetResult(Record(reply));
    }

    private static AiReply Record(AiReply reply)
    {
        Metrics.RecordOutcome(reply.Outcome, reply.Error);
        return reply;
    }

    private sealed class Pending
    {
        public Pending(AiRequest request, long sequence, long deadline)
        {
            Request = request;
            Sequence = sequence;
            Deadline = deadline;
        }

        public AiRequest Request { get; }
        public long Sequence { get; }
        public long Deadline { get; }

        // Continuations run synchronously: callers resume inside Complete(), which is on the game thread.
        public TaskCompletionSource<AiReply> Completion { get; } = new();
    }
}
