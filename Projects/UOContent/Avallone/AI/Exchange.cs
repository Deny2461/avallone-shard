using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Server.Avallone.Companions;
using Server.Avallone.Named;
using Server.Commands;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Stage A2 (2026-11-09, Projects/Avallone-Stage-A-NPCs-Address-Companions.md "A2 design v2"): a short spoken exchange
// between two AI characters who stand within earshot - today a player's companion and the townsperson (or Mirabel) who
// just spoke to her by name; later any two (Phase 7 villager talk needs only a new starter).
//
// The rules (Codex Stage A design review F1 / F2 / F5; the owner, chat, 2026-11-09):
// - ONE exchange at a time on the whole shard, owned here. At most MaxLines lines after the opening one (the number is drawn per exchange)
//   (companion -> NPC -> companion), one model call each, never retried: a refused, empty, failed or slow step ends it.
// - The player always wins. Every line a player says (any kind, before any routing or cooldown) bumps that player's
//   speech revision; the exchange beside them ends at once - a running call is cancelled, a pending line is never said,
//   and nothing restarts it. Any conversation request from anyone ends it too (the model is needed elsewhere).
// - A step never enters the gateway queue and never holds a conversation lease: it calls the brain directly (as the visit
//   summaries do), only while the gateway is idle and no summary runs, on its own cancellation token. So the player's
//   next line is never "Busy" behind it.
// - Public only: the request carries the two speakers' names, titles and personas, a companion's coarse trust band (for
//   her own prompt only) and what was said aloud. No history, memory, secrets, gear or dials. Nothing about an exchange
//   is recorded anywhere - not in the memory ledger, not in either character's conversation history with the player.
// - Talk only: no action from an exchange step is ever applied.
// - The switch avallone.ai.exchanges is OFF by default (the owner: on after his first live play). RAM only.
public static class Exchange
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Exchange));

    public const int MaxSteps = 10;     // the most lines an exchange can ever have after the opening one
    public const int Range = 8;         // both speakers stay this close to the player

    public static bool Enabled { get; private set; }
    public static TimeSpan NpcRest { get; set; } = TimeSpan.FromMinutes(4);   // per townsperson, booked before the first call
    public static int RepliesBetween { get; set; } = 2;                       // NPC replies a player hears between exchanges
    // the owner, chat, 2026-11-09 (after play test 2: every exchange was exactly three lines)
    // Drawn once per exchange; MaxLines (setting
    // avallone.ai.exchangeMaxLines) caps it.
    public static int MaxLines { get; set; } = MaxSteps;
    public static TimeSpan CancelWait { get; set; } = TimeSpan.FromSeconds(3);   // waiting for the brain to confirm a cut-off

    internal static int LengthFor(double band, double within, int max)
    {
        var (lo, hi) = band < 0.70 ? (1, 3) : band < 0.90 ? (4, 6) : (7, 10);
        return Math.Clamp(Math.Min(lo + (int)(Math.Clamp(within, 0, 0.999999) * (hi - lo + 1)), hi), 1, Math.Clamp(max, 1, MaxSteps));
    }

    // The whole exchange: the slowest it can possibly run (every pause at the cap, every call at its budget), plus slack.
    public static TimeSpan DeadlineFor(int steps) => (PauseCap + StepBudget) * steps + TimeSpan.FromSeconds(6);
    public static TimeSpan StepBudget { get; set; } = TimeSpan.FromSeconds(6);

    // the owner's play test 2026-11-09: 1.5-2.5 s between lines read as. Now the pause
    // before a line is time to read the line it answers: PauseMin..PauseMin+PauseSpread, plus PausePerChar for each
    // character of that line, never more than PauseCap.
    public static TimeSpan PauseMin { get; set; } = TimeSpan.FromSeconds(3);
    public static TimeSpan PauseSpread { get; set; } = TimeSpan.FromSeconds(1.5);
    public static TimeSpan PausePerChar { get; set; } = TimeSpan.FromMilliseconds(20);
    public static TimeSpan PauseCap { get; set; } = TimeSpan.FromSeconds(7);

    internal static TimeSpan PauseFor(string answered, double roll)
    {
        var t = PauseMin + PauseSpread * Math.Clamp(roll, 0, 1) + PausePerChar * (answered?.Length ?? 0);
        return t > PauseCap ? PauseCap : t;
    }

    // Tests swap these: the pause before each line, and how a line is said.
    internal static Func<TimeSpan, Task> Pause = DefaultPause;
    internal static Func<int> PickLength = DefaultLength;
    internal static Action<Mobile, string> Speak = (m, text) => m.Say(text);
    internal static Func<PlayerMobile, bool> Online = pm => pm.NetState != null;

    public sealed record Voice(
        Mobile Mobile, string Kind, Guid AiId, string PersonaKey, Dictionary<string, string> Vars, string Name, string Title,
        Mobile Master
    );

    internal sealed class Run
    {
        public string Id;
        public PlayerMobile Player;
        public Voice[] Voices;          // [0] answers first, [1] said the opening line
        public int Revision;            // the player's speech revision when it began
        public int Lines;               // lines said by this exchange (the opening line is not one)
        public int Steps;               // how many lines this exchange may run to (drawn at its start)
        public bool CallOut;            // a step's model call is out right now
        public bool Unanswered;         // the last call ended WITHOUT the brain's answer (timeout, transport failure)
        public readonly List<ExchangeLine> Transcript = [];
        public string[] Roster;
        public long DeadlineAt;
        public readonly CancellationTokenSource Cts = new();
        public bool Ended;
        public string EndReason;
    }

    private static Run _active;
    private static int _settling;       // cut-offs the brain has not confirmed yet (Codex A2-R2)
    private static readonly Dictionary<Serial, int> _revision = new();
    private static readonly Dictionary<Serial, long> _restUntil = new();
    private static readonly Dictionary<Serial, int> _since = new();
    private static readonly Regex Word = new(@"[a-z0-9']+", RegexOptions.Compiled);

    public static bool Running => _active != null;

    // Codex A2-R2: an exchange that was cut off while its model call was out is not finished until the brain says the
    // call is gone (or CancelWait passes). Until then no new exchange and no visit summary asks for the model.
    public static bool Settling => _settling > 0;
    internal static Run Current => _active;
    public static int Started { get; private set; }
    public static int Completed { get; private set; }
    public static int LinesSaid { get; private set; }
    public static string LastEnd { get; private set; } = "";

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.ai.exchanges", false);
        NpcRest = TimeSpan.FromMinutes(Math.Max(0, ServerConfiguration.GetOrUpdateSetting("avallone.ai.exchangeNpcRestMinutes", 4)));
        RepliesBetween = Math.Max(0, ServerConfiguration.GetOrUpdateSetting("avallone.ai.exchangeRepliesBetween", 2));
        PauseMin = TimeSpan.FromSeconds(Math.Clamp(ServerConfiguration.GetOrUpdateSetting("avallone.ai.exchangePauseSeconds", 3), 0, 6));
        MaxLines = Math.Clamp(ServerConfiguration.GetOrUpdateSetting("avallone.ai.exchangeMaxLines", MaxSteps), 1, MaxSteps);
        EventSink.Speech += OnSpeech;
        CommandSystem.Register("Exchanges", AccessLevel.Administrator, Exchanges_OnCommand);
    }

    public static void Initialize() =>
        logger.Information(
            "Avallone exchanges: {State} (NPC rest {Rest}, {Between} replies between, up to {Steps} lines, {Pause} s+ between lines)",
            Enabled ? "ON" : "off", NpcRest, RepliesBetween, MaxLines, PauseMin.TotalSeconds
        );

    [Usage("Exchanges [on|off|status]")]
    [Description("Avallone: a companion and the townsperson who just spoke to her may trade a line or two. on/off is saved.")]
    public static void Exchanges_OnCommand(CommandEventArgs e)
    {
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";
        if (verb is "on" or "off")
        {
            Enabled = verb == "on";
            ServerConfiguration.SetSetting("avallone.ai.exchanges", Enabled ? "true" : "false");
            if (!Enabled)
            {
                Preempt("switched off");
            }
        }

        e.Mobile.SendMessage(
            $"Exchanges are {(Enabled ? "ON" : "OFF")} (saved); {(Running ? "one is running" : "none running")}; " +
            $"{Started} started, {Completed} ran to the end, {LinesSaid} lines said; last end: {(LastEnd.Length > 0 ? LastEnd : "-")}."
        );
    }

    // ---- the player always wins -------------------------------------------------------------------------------------

    internal static void OnSpeech(SpeechEventArgs e)
    {
        if (e.Mobile is PlayerMobile pm)
        {
            NoteSpeech(pm);
        }
    }

    // Every line a player says, whatever it is and whoever it is for.
    internal static void NoteSpeech(PlayerMobile pm)
    {
        _revision[pm.Serial] = Revision(pm) + 1;
        var run = _active;
        if (run != null && run.Player == pm)
        {
            End(run, "the player spoke");
        }
    }

    internal static int Revision(Mobile pm) => _revision.TryGetValue(pm.Serial, out var r) ? r : 0;

    // Codex A2-R1: a townsperson or Mirabel reads this when the player's turn begins (EventSink.Speech has already counted
    // that line - it fires before any mobile hears it) and hands it back to AfterReply with the reply.
    public static int RevisionOf(Mobile m) => m == null ? 0 : Revision(m);

    // A conversation is about to ask the model (AiGateway.Enqueue), or the switch went off.
    public static void Preempt(string reason)
    {
        var run = _active;
        if (run != null)
        {
            End(run, reason);
        }
    }

    // ---- starting ---------------------------------------------------------------------------------------------------

    // Called by a townsperson or Mirabel right after a reply to `from` was really SAID. `excluded`: a gossip, decency
    // or shop-line turn. `openingRevision`: RevisionOf(from) read when the turn this reply answers began - if the
    // player has said anything since (an order, a shop word, a farewell, a line some cooldown ignored), the reply is
    // stale and never starts an exchange (Codex A2-R1). Never throws into the reply path.
    public static void AfterReply(Mobile npc, Mobile from, TravelParty.PartyAsk party, string spoken, bool excluded, int openingRevision)
    {
        if (from is not PlayerMobile pm || npc == null)
        {
            return;
        }

        try
        {
            var since = _since.TryGetValue(pm.Serial, out var n) ? n : RepliesBetween;
            _since[pm.Serial] = TryStart(npc, pm, party, spoken, excluded, since, openingRevision) ? 0 : Math.Min(since + 1, 1000);
            if (_since.Count > 5000)
            {
                _since.Clear(); // bound memory; losing counts only allows an exchange a little sooner
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Exchange: could not start");
        }
    }

    private static bool TryStart(
        Mobile npc, PlayerMobile pm, TravelParty.PartyAsk party, string spoken, bool excluded, int since, int openingRevision
    )
    {
        if (!Enabled || !AiGateway.Enabled || excluded || string.IsNullOrEmpty(spoken) || party?.Companions is not { Length: > 0 })
        {
            return false;
        }

        var companion = NamedCompanion(pm, party, spoken);
        if (companion == null)
        {
            return false; // the line did not speak to a companion by name
        }

        var a = VoiceOf(companion);
        var b = VoiceOf(npc);
        var now = AiGateway.Clock();
        var why = Revision(pm) != openingRevision ? "the player has spoken again since the line this reply answers" :
            _active != null ? "another exchange is running" :
            since < RepliesBetween ? $"{since} of {RepliesBetween} replies since this player's last exchange" :
            _restUntil.TryGetValue(npc.Serial, out var until) && now < until ? "this NPC spoke in an exchange a moment ago" :
            !ModelFree() ? "the model is busy" :
            a == null || b == null ? "not two AI speakers" : null;

        Run run = null;
        if (why == null)
        {
            run = new Run
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Player = pm,
                Voices = [a, b],
                Revision = openingRevision,
                Roster = party.Roster ?? [],
                Steps = Math.Clamp(PickLength(), 1, Math.Clamp(MaxLines, 1, MaxSteps))
            };
            run.DeadlineAt = now + (long)DeadlineFor(run.Steps).TotalMilliseconds;
            // What the NPC said aloud, with a pet's owner-given name masked as in its stored history (Codex A1-1).
            run.Transcript.Add(new ExchangeLine(b.Name, TravelParty.ForHistory(spoken, party)));
            _active = run;
            if (!StillGood(run, out why))
            {
                _active = null;
                run.Cts.Dispose();
            }
        }

        if (why != null)
        {
            logger.Information("Exchange: not started beside {Player} ({Npc} named {Companion}) - {Reason}", pm.Name, npc.Name, companion.Name, why);
            return false;
        }

        // Booked BEFORE the first model call (uo-llm-npc lesson): a failed exchange still costs the NPC its rest.
        _restUntil[npc.Serial] = now + (long)NpcRest.TotalMilliseconds;
        if (_restUntil.Count > 2000)
        {
            _restUntil.Clear();
            _restUntil[npc.Serial] = now + (long)NpcRest.TotalMilliseconds;
        }

        Started++;
        logger.Information("Exchange start {Id}: {A} <-> {B} beside {Player}, up to {Steps} line(s)", run.Id, a.Name, b.Name, pm.Name, run.Steps);
        _ = RunAsync(run);
        return true;
    }

    // The player's own companion the spoken line names (first name as a whole word), standing with them.
    private static BaseCompanion NamedCompanion(PlayerMobile pm, TravelParty.PartyAsk party, string spoken)
    {
        var words = new HashSet<string>();
        foreach (Match m in Word.Matches(spoken.ToLowerInvariant()))
        {
            words.Add(m.Value.Trim('\''));
        }

        foreach (var c in BaseCompanion.All)
        {
            if (c.Deleted || c.ControlMaster != pm || !c.Alive || c.Map != pm.Map || !c.InRange(pm, Range) ||
                string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            var name = c.Name.Trim();
            if (Array.Exists(party.Companions, p => p.Name == name) && words.Contains(c.FirstName.ToLowerInvariant()))
            {
                return c;
            }
        }

        return null;
    }

    // Who may speak in an exchange, and with which public persona. Null = not an AI speaker.
    public static Voice VoiceOf(Mobile m)
    {
        if (m == null || m.Deleted || string.IsNullOrWhiteSpace(m.Name))
        {
            return null;
        }

        var name = m.Name.Trim();
        var title = m.Title?.Trim() ?? "";
        return m switch
        {
            BaseCompanion c when c.AiId != Guid.Empty =>
                new Voice(c, "companion", c.AiId, c.PersonaKey, null, name, title, c.ControlMaster),
            INamedNpc n when n.AiId != Guid.Empty && !string.IsNullOrEmpty(n.NamedKey) =>
                new Voice(m, "named", n.AiId, n.NamedKey, null, name, title, null),
            BaseCreature bc when TownsfolkTalk.IsTownsperson(bc) =>
                new Voice(bc, "townsperson", AiIdentity.GetOrCreate(bc), "townsfolk", TownsfolkTalk.PersonaVarsFor(bc), name, title, null),
            _ => null
        };
    }

    // ---- running ----------------------------------------------------------------------------------------------------

    private static bool ModelFree() =>
        AiGateway.Enabled && AiGateway.Client != null && AiGateway.IsIdle && !AiGateway.BreakerIsOpen &&
        !AiGateway.InQuietWindow && !NamedNpcs.SummaryRunning && _settling == 0;

    // Checked before every model call AND before every line is said.
    internal static bool StillGood(Run run, out string why)
    {
        var pm = run.Player;
        why =
            run.Ended || _active != run ? "no longer the active exchange" :
            !Enabled || !AiGateway.Enabled ? "switched off" :
            Revision(pm) != run.Revision ? "the player spoke" :
            AiGateway.Clock() > run.DeadlineAt ? "took too long" :
            pm.Deleted || !Online(pm) || !pm.Alive || pm.Map == null || pm.Map == Map.Internal ? "the player left" : null;

        for (var i = 0; why == null && i < run.Voices.Length; i++)
        {
            var v = run.Voices[i];
            var m = v.Mobile;
            why =
                m.Deleted || !m.Alive ? $"{v.Name} is gone" :
                m.Hidden ? $"{v.Name} is out of sight" :
                m.Map != pm.Map || !m.InRange(pm, Range) ? $"{v.Name} is out of earshot" :
                (m as BaseCreature)?.ControlMaster != v.Master ? $"{v.Name} changed owner" : null;
        }

        return why == null;
    }

    private static async Task RunAsync(Run run)
    {
        try
        {
            for (var step = 0; step < run.Steps; step++)
            {
                await Pause(PauseFor(run.Transcript.Count > 0 ? run.Transcript[^1].Text : null, Utility.RandomDouble()));

                if (!StillGood(run, out var why))
                {
                    End(run, why);
                    return;
                }

                if (!ModelFree())
                {
                    End(run, "the model is busy"); // F5: continue only when nobody else needs it
                    return;
                }

                var speaker = run.Voices[step % 2];
                var other = run.Voices[(step + 1) % 2];
                var request = new AiRequest(speaker.AiId, Guid.Empty, null, "", AiPriority.Background)
                {
                    Mode = "exchange",
                    PersonaKey = speaker.PersonaKey,
                    PersonaVars = speaker.Vars,
                    Timeout = StepBudget,
                    Exchange = new ExchangeAsk(
                        run.Id,
                        step,
                        new ExchangeVoice(speaker.Kind, speaker.Name, speaker.Title),
                        new ExchangeVoice(other.Kind, other.Name, other.Title),
                        speaker.Mobile is BaseCompanion c ? CompanionTrust.Band(c.GetTrust(run.Player)) : "",
                        run.Transcript.ToArray(),
                        run.Roster,
                        step == run.Steps - 1
                    )
                };

                AiReply reply;
                var began = AiGateway.Clock();
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
                    limit.CancelAfter(StepBudget);
                    run.CallOut = true;
                    reply = await AiGateway.Client.SendAsync(request, StepBudget, limit.Token); // resumes on the game thread
                }
                catch (OperationCanceledException)
                {
                    // Codex A2-R2-T: we stopped waiting - that is not the brain having finished. The request may still be
                    // on its way or running there, so the exchange must end through the cancel + settle path (End).
                    run.Unanswered = true;
                    reply = AiReply.Fail(AiOutcome.Timeout, "cancelled or timed out");
                }
                catch (Exception ex)
                {
                    run.Unanswered = true; // a transport failure: whether the brain got the request is unknown
                    reply = AiReply.Fail(AiOutcome.Failed, ex.GetBaseException().Message);
                }
                finally
                {
                    run.CallOut = false;
                }

                if (run.Ended)
                {
                    return; // pre-empted while the call was out: the line is never said
                }

                if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Text))
                {
                    End(run, $"step {step + 1} gave no line ({reply.Error ?? reply.Outcome.ToString()})"); // F5: no retry
                    return;
                }

                if (!StillGood(run, out why))
                {
                    End(run, why);
                    return;
                }

                // Talk only: reply.Action is never read here.
                var text = reply.Text.Trim();
                speaker.Mobile.Direction = speaker.Mobile.GetDirectionTo(other.Mobile);
                Speak(speaker.Mobile, text);
                run.Lines++;
                LinesSaid++;
                run.Transcript.Add(new ExchangeLine(speaker.Name, text));
                logger.Information(
                    "Exchange step {Id} {Step}/{Max}: {Speaker} said \"{Text}\" ({Elapsed} ms)",
                    run.Id, step + 1, run.Steps, speaker.Name, text, AiGateway.Clock() - began
                );
            }

            End(run, "complete");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Exchange {Id}: stopped by an error", run.Id);
            End(run, "error");
        }
        finally
        {
            run.Cts.Dispose();
        }
    }

    private static void End(Run run, string reason)
    {
        if (run.Ended)
        {
            return;
        }

        run.Ended = true;
        run.EndReason = reason;
        if (_active == run)
        {
            _active = null;
        }

        // A call that is out now, or one we gave up on without the brain's answer. A step the brain answered - a line,
        // or a confirmed refusal - needs no cancel.
        var callOut = run.CallOut || run.Unanswered;
        run.Unanswered = false;
        try
        {
            run.Cts.Cancel(); // a call in flight stops waiting; its line is never said
        }
        catch (ObjectDisposedException)
        {
        }

        if (callOut)
        {
            _ = CancelAtBrain(run.Id); // Codex A2-R2: hanging up is not enough - tell the brain to drop that call
        }

        LastEnd = reason;
        if (reason == "complete")
        {
            Completed++;
        }

        logger.Information("Exchange end {Id} ({Reason}) after {Lines} line(s)", run.Id, reason, run.Lines);
    }

    // Codex A2-R2: the brain is told the exchange is over - it drops the running step, and refuses any step of this
    // exchange that is still on its way. Counted in _settling until the brain answers (or CancelWait passes), so nothing
    // else here asks for the model in between. Never throws.
    private static async Task CancelAtBrain(string id)
    {
        var client = AiGateway.Client;
        if (client == null)
        {
            return;
        }

        _settling++;
        try
        {
            using var limit = new CancellationTokenSource(CancelWait);
            var request = new AiRequest(Guid.Empty, Guid.Empty, null, "", AiPriority.Background)
            {
                Mode = "exchange_cancel",
                Timeout = CancelWait,
                Exchange = new ExchangeAsk(id, 0, null, null, "", [], [])
            };
            await client.SendAsync(request, CancelWait, limit.Token);
        }
        catch (Exception ex)
        {
            logger.Debug("Exchange {Id}: the brain did not confirm the cut-off ({Error})", id, ex.GetBaseException().Message);
        }
        finally
        {
            _settling = Math.Max(0, _settling - 1);
        }
    }

    private static int DefaultLength() => LengthFor(Utility.RandomDouble(), Utility.RandomDouble(), MaxLines);

    private static Task DefaultPause(TimeSpan delay)
    {
        var tcs = new TaskCompletionSource<bool>();
        Timer.StartTimer(delay, () => tcs.TrySetResult(true));
        return tcs.Task;
    }

    // Test support only.
    public static void ResetForTests(bool enabled)
    {
        var run = _active;
        if (run != null)
        {
            End(run, "test reset");
        }

        Enabled = enabled;
        _active = null;
        _revision.Clear();
        _restUntil.Clear();
        _since.Clear();
        Started = Completed = LinesSaid = 0;
        LastEnd = "";
        NpcRest = TimeSpan.FromMinutes(4);
        RepliesBetween = 2;
        StepBudget = TimeSpan.FromSeconds(6);
        PauseMin = TimeSpan.FromSeconds(3);
        MaxLines = MaxSteps;
        CancelWait = TimeSpan.FromSeconds(3);
        _settling = 0;
        Pause = DefaultPause;
        PickLength = DefaultLength;
        Speak = (m, text) => m.Say(text);
        Online = pm => pm.NetState != null;
    }
}
