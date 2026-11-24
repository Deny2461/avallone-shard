using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Server.Avallone.AI;
using Server.Avallone.Townsfolk;
using Server.Commands;
using Server.Items;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Errands;

// Errands build 1 - townsfolk ask the player a favour (design v5, Projects/Avallone-Errands-Step-Design.md in the wiki;
// Codex "Errands-Design R5" GO WITH NOTES 2026-11-21; lines approved by the owner 2026-11-21).
//
// THE SERVER DECIDES, the model only writes its ordinary reply. This class owns: the switches, readiness, the errand
// rows and their save record, tickets and consent, deadlines and limits, the completion operation and pay. It keeps
// NO family copy - every step that creates or moves a row needs a CURRENT PROOF from the brain's fixed code, obtained
// on that same turn (3.1). When anything is unsure, nothing happens: no errand is asked, accepted, delivered or paid.
//
// Design change v6 (design 12; Codex "Errands-Redesign" R1 and R2; lines page section I approved by the owner 2026-11-21):
// questions ABOUT an errand - what it gives, what it was - are answered here, by code, with no model call. The model is
// given no errand authority: no errand sentence goes into its history and no instruction names an errand's detail.
//
// Saved as its own file in world saves ("AvalloneErrands"). Never delete this type once it is in saves.
public class Errands : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Errands));
    private const int SaveVersion = 1; // 1: AskSeed, AskHash, DeliveryHash on every row (v6)

    // ---- settings (all starting values; the two switches are OFF by default) --------------------------------------
    public static bool Completion { get; private set; } // delivery, return, pay and collection together
    public static bool Offer { get; private set; }      // new asks; has effect only while Completion is on
    public static int MaxOpen { get; private set; } = 3;
    public static int AskOneIn { get; private set; } = 4;
    public static long DeadlineSeconds { get; private set; } = 14400;  // 4 hours of the character's errand time
    public static long CooldownSeconds { get; private set; } = 14400;  // 4 hours of the character's own time in the world
    public static int DailyAllowance { get; private set; } = 3;
    public static int KarmaRequest { get; private set; } = 50;
    public static int CoinSame { get; private set; } = 25;
    public static int CoinNear { get; private set; } = 150;
    public static int CoinFar { get; private set; } = 300;
    public const int SpeechMax = 300;
    public static bool Pointer { get; private set; }                  // v6 12.3: the fixed instruction line; OFF unless the measurement chooses it
    public static int ShowMaxOutstanding { get; private set; } = 2;   // v6 12.8: [Errands show reads outstanding, shard-wide
    public static readonly TimeSpan ShowCooldown = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TicketLife = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan LeaseLife = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(20);
    private const int TimeStep = 10; // seconds of errand time added per tick

    // The neutral stock lines (approved list C3): said when a yes could not be honoured. No fact, no promise.
    internal static readonly string[] Neutral =
    [
        "Hm. Never mind — it will keep.", "Ah, forgive me, my mind wandered. Another time.",
        "No, leave it for now. It is no great matter."
    ];

    // Design v6 12.2, lines page I.1 and I.2 (approved by the owner 2026-11-21). Said by code, with no brain call. They name
    // nothing and promise nothing; the same words are kept in the brain's errands_lists.json under its own checks.
    internal const string RewardLine = "I am no rich soul, friend, but a kindness done me seldom goes unpaid.";
    internal const string AsHeardLine = "I have said my piece. Carry it as you heard it.";
    internal const string SeeLogMessage = "Say [Errands to see what you are carrying.";
    internal const string NotRecalledMessage = "The exact words cannot be recalled just now.";

    // ---- seams for tests ------------------------------------------------------------------------------------------
    public static IErrandBrain Brain { get; set; }
    public static Func<Guid, Mobile, string, object, bool> Recorder { get; set; } = AiLedger.TryRecord;
    public static Func<bool> LedgerReady { get; set; } = () => AiLedger.HasRoom && AiLedger.ErrandsIngest;
    public static Func<string> Stamp { get; set; } = TownRoster.StampForRequest;
    public static Func<DateTime> Now { get; set; } = () => Core.Now;
    public static Func<int, int> Dice { get; set; } = Utility.Random;
    public static Func<Mobile, int> Revision { get; set; } = Exchange.RevisionOf; // the player's speech count
    public static Func<long> SeatsRevision { get; set; } = () => Seats.Revision;     // goes up at every seat or quarantine change
    public static Func<PlayerMobile, long> OwnTime { get; set; } = pm => (long)pm.GameTime.TotalSeconds;
    public static Action<Mobile, int, bool> AwardKarma { get; set; } = Server.Misc.Titles.AwardKarma;

    // The stock "you have gained ... karma" messages, by the stock bands (Titles.SendKarmaMessage). A seam for tests.
    public static Action<Mobile, int> KarmaGainMessage { get; set; } = (m, gained) => m.SendLocalizedMessage(
        gained > 40 ? 1019062 : gained > 20 ? 1019061 : gained > 10 ? 1019060 : 1019059);
    public static Func<Container, Mobile, Item, bool> DropInPack { get; set; } = (pack, m, item) => pack.TryDropItem(m, item, false);
    // v6: the system text of the reminder paths, and the player's connection (a late [Errands show answer is shown only
    // to the session that asked)
    public static Action<PlayerMobile, string> Tell { get; set; } = (pm, text) => pm.SendMessage(text);
    public static Func<PlayerMobile, object> Session { get; set; } = pm => pm.NetState;

    // ---- saved with the world -------------------------------------------------------------------------------------
    private static Errands _instance;
    private static readonly List<ErrandRow> _rows = [];
    private static int _nextId = 1;
    private static readonly Dictionary<Guid, long> _errandTime = new();                       // per character, seconds
    private static readonly Dictionary<(Guid Npc, Guid Char), long> _askedAt = new();         // own-time stamp of the last ask
    private static readonly Dictionary<Guid, (string Day, int Count)> _allowance = new();     // per character, one Central day
    private static readonly HashSet<(Guid Sender, Guid Char)> _introOffered = [];             // once, ever (3.10)
    private static readonly HashSet<(Guid Receiver, Guid Char)> _headStart = [];              // once, ever (3.10)
    private static string _fault;                                                             // null = none

    // ---- not saved ------------------------------------------------------------------------------------------------
    public static int Generation { get; private set; } = 1; // the activation generation (3.1)
    private static DateTime _leaseAt = DateTime.MinValue;
    private static string _leaseBoot, _leaseData;
    private static long _probe;          // the newest probe sent
    private static bool _probeOut;
    private static long _requests;       // request ids for proofs
    private static readonly Dictionary<(Serial Npc, Guid Char), Ticket> _tickets = new();
    private static int _watched;         // live tickets + verification calls outstanding: the movement hooks test this first

    internal sealed class Ticket
    {
        public string Id;
        public ErrandProof Proof;
        public int Gen;
        public int Revision;   // the player's speech count after both lines were spoken; their next line is Revision + 1
        public DateTime At;
        public Serial Npc, Player;
        public Guid Char;
        public bool Broken;    // set by a change itself, never cleared (3.6)
        public bool Verifying;
        public long Seats;     // Seats.Revision when the ask was said: any seat or quarantine change since voids the ticket
        public string Stamp;   // the town list's stamp the ask was proved against
        public bool AskedOnce; // v6 12.2: one listed question was answered on this ticket; a second is "any other line"
    }

    // v6 12.8 (Codex "Errands-Redesign R2", must-fix): [Errands show reads that are outstanding, by character, and when
    // each character may read again. Not saved.
    private static readonly HashSet<Guid> _showOut = [];
    private static readonly Dictionary<Guid, DateTime> _showNext = new();

    public Errands() : base("AvalloneErrands", 10)
    {
    }

    public static void Configure()
    {
        _instance = new Errands();
        Completion = ServerConfiguration.GetOrUpdateSetting("avallone.errands.completion", false);
        Offer = ServerConfiguration.GetOrUpdateSetting("avallone.errands.offer", false);
        MaxOpen = ServerConfiguration.GetOrUpdateSetting("avallone.errands.maxOpen", 3);
        AskOneIn = Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.errands.askOneIn", 4));
        DeadlineSeconds = ServerConfiguration.GetOrUpdateSetting("avallone.errands.deadlineSeconds", 14400);
        CooldownSeconds = ServerConfiguration.GetOrUpdateSetting("avallone.errands.cooldownSeconds", 14400);
        DailyAllowance = ServerConfiguration.GetOrUpdateSetting("avallone.errands.dailyAllowance", 3);
        KarmaRequest = ServerConfiguration.GetOrUpdateSetting("avallone.errands.karma", 50);
        CoinSame = ServerConfiguration.GetOrUpdateSetting("avallone.errands.coinSameTown", 25);
        CoinNear = ServerConfiguration.GetOrUpdateSetting("avallone.errands.coinNeighbour", 150);
        CoinFar = ServerConfiguration.GetOrUpdateSetting("avallone.errands.coinFurther", 300);
        Pointer = ServerConfiguration.GetOrUpdateSetting("avallone.errands.pointer", false);
        ShowMaxOutstanding = Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.errands.showMaxOutstanding", 2));
        // v6 12.1, the load check: lists that break a rule are refused and the feature stays off for this start.
        if (ErrandMatch.Validate() is { } bad)
        {
            logger.Error("Avallone errands: the phrase lists are refused ({Why}). Errands stay OFF for this start.", bad);
            Completion = Offer = false;
        }

        var url = ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/");
        Brain = new HttpErrandBrain(new Uri(url));
        AiLedger.ErrandFault = (id, why) => RaiseFault($"{why} ({id})");
        CommandSystem.Register("Errands", AccessLevel.Player, Errands_OnCommand);
    }

    public static void Initialize()
    {
        Timer.StartTimer(TimeSpan.FromSeconds(TimeStep), TimeSpan.FromSeconds(TimeStep), TimeTick);
        Timer.StartTimer(TimeSpan.FromSeconds(7), ProbeEvery, ProbeTick);
        Timer.StartTimer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TicketTick);
        EventSink.Logout += Left;
        EventSink.Disconnected += Left;
    }

    // ================================================================================================================
    // Readiness (3.9): four conditions, kept apart
    // ================================================================================================================

    public static bool LeaseHeld => _leaseData != null && Now() - _leaseAt <= LeaseLife;
    public static bool Faulted => _fault != null;
    public static string Fault => _fault;
    public static bool PayAllowed => Completion && !Faulted;
    public static bool CanComplete => Completion && !Faulted && LedgerReady() && LeaseHeld;
    public static bool CanOffer => Offer && CanComplete;

    private static void Bump(string why)
    {
        Generation++;
        logger.Information("Avallone errands: activation generation {Generation} ({Why})", Generation, why);
    }

    private static void DropLease(string why)
    {
        if (_leaseData == null)
        {
            return;
        }

        _leaseData = _leaseBoot = null;
        _leaseAt = DateTime.MinValue;
        Bump("lease lost: " + why);
    }

    // The probe (3.9): one timer for the whole module, whenever the completion switch is on - cold, idle or faulted
    // alike. It needs none of the four conditions, so it is what establishes them.
    private static void ProbeTick()
    {
        if (!Completion)
        {
            return;
        }

        if (_leaseData != null && Now() - _leaseAt > LeaseLife)
        {
            DropLease("ran out");
        }

        if (!_probeOut)
        {
            _ = ProbeAsync();
        }
    }

    internal static Task ProbeForTests() => ProbeAsync();

    private static async Task ProbeAsync()
    {
        _probeOut = true;
        var number = ++_probe;
        var gen = Generation;
        try
        {
            var r = await Brain.ReadyAsync(number, gen, Stamp());
            // Order: only the answer to the newest probe, in the generation it was sent in, may renew the lease.
            if (number != _probe || gen != Generation)
            {
                return;
            }

            Renew(r is { Capable: true, StampOk: true } && r.Probe == number && r.Gen == gen ? (r.Boot, r.DataGen) : null,
                r == null ? "no answer to the probe" : "the probe's answer does not vouch for errands");
        }
        finally
        {
            _probeOut = false;
        }
    }

    // A reply from the brain's errand code: renew, or lose, the lease. A changed boot id or data generation moves the
    // activation generation on, so nothing asked before the change can land after it.
    private static void Renew((string Boot, string Data)? seen, string whyNot)
    {
        if (seen is not { Boot: not null, Data: not null } s)
        {
            DropLease(whyNot);
            return;
        }

        if (_leaseData != null && (s.Boot != _leaseBoot || s.Data != _leaseData))
        {
            Bump("the brain's boot id or data generation changed");
        }

        (_leaseBoot, _leaseData, _leaseAt) = (s.Boot, s.Data, Now());
    }

    public static void RaiseFault(string why)
    {
        if (_fault == null)
        {
            Bump("fault raised");
        }

        _fault = why;
        logger.Error("Avallone errands: FAULT - {Why}. No ask, acceptance, delivery, return, pay or collection until [Errands fault clear", why);
    }

    // ================================================================================================================
    // People and places
    // ================================================================================================================

    // The map and town a townsperson belongs to - the town list's own rule (TownRoster.Add): the named town at the home
    // point on the home map. Null unless this is a seated, ordinary, eligible resident.
    internal static (string Map, string Town)? PlaceOf(Mobile m)
    {
        if (m is not BaseCreature bc || bc.Deleted || bc.Map == null || bc.Map == Map.Internal ||
            !Seats.TryGetSeat(bc, out _) || !TownsfolkTalk.IsTownsperson(bc))
        {
            return null;
        }

        var hasHome = bc.Home != Point3D.Zero;
        var map = hasHome && bc.HomeMap != null && bc.HomeMap != Map.Internal ? bc.HomeMap : bc.Map;
        var town = TownsfolkCensus.TownAt(map, hasHome ? bc.Home : bc.Location);
        return town == null ? null : (map.Name, town);
    }

    // Exactly one holder of this id, a seated ordinary resident standing on the named map and town.
    // the owner, after the dev play test of 2026-11-22
    // The name as it is kept on the row for [Errands and the system messages: the name and, when the
    // townsperson has one, their title ("the innkeeper"). Display only - never used for a decision.
    internal static string NameWithTrade(Mobile m)
    {
        if (m == null)
        {
            return null;
        }

        var title = m.Title?.Trim();
        return string.IsNullOrEmpty(title) ? m.Name : $"{m.Name} {title}";
    }

    internal static BaseCreature ResidentAt(Guid id, string map, string town)
    {
        BaseCreature found = null;
        var n = 0;
        foreach (var m in AiIdentity.Holders(id))
        {
            n++;
            found = m as BaseCreature;
        }

        return n == 1 && found != null && PlaceOf(found) is { } at && at.Map == map && at.Town == town ? found : null;
    }

    private static Guid Parse(string id) => Guid.TryParse(id, out var g) ? g : Guid.Empty;

    private static long TimeOf(Guid character) => _errandTime.TryGetValue(character, out var t) ? t : 0;

    private static int OpenCount(Guid character)
    {
        var n = 0;
        foreach (var r in _rows)
        {
            if (r.Character == character && r.Open)
            {
                n++;
            }
        }

        return n;
    }

    private static ErrandRow Oldest(Guid character, Func<ErrandRow, bool> want)
    {
        ErrandRow best = null;
        foreach (var r in _rows)
        {
            if (r.Character == character && want(r) && (best == null || r.Id < best.Id))
            {
                best = r;
            }
        }

        return best;
    }

    // ================================================================================================================
    // A turn (3.2): at most ONE errand thing, for ONE row, chosen here before the brain is called
    // ================================================================================================================

    public sealed class Turn
    {
        public bool Handled;     // the errand answered this line itself; no model call is made
        public bool Spoke;       // ... and the townsperson said something
        public string Said;      // what, for the conversation history
        public object Ask;       // the request's "errand" field for an ordinary turn, or null
        internal string TicketId;
        internal int Gen;
        internal string Request;
    }

    private static readonly Turn Nothing = new();
    private static readonly Turn NoAsk = new(); // an ordinary turn on which no ask may be made (an errand is open here)

    // v6 12.2: a row is "in play" with a townsperson while they sent it and it is not yet delivered, or while they are
    // the relative of a round trip whose answer is being carried back.
    private static bool Qualifies(ErrandRow r, Guid character, Guid npcId) =>
        r.Character == character &&
        (r.State == ErrandState.Accepted && r.Sender == npcId || r.State == ErrandState.Delivered && r.Receiver == npcId);

    // Called by TownsfolkTalk before the request is built. `yields`: the line is being handled as something else
    // (a story correction) - errands yield to everything else, and the row waits.
    public static async Task<Turn> BeforeTurnAsync(BaseCreature npc, Mobile from, string text, int openingRevision, bool yields)
    {
        if (!Completion || from is not PlayerMobile pm || pm.Deleted)
        {
            return Nothing;
        }

        var npcId = AiIdentity.GetOrCreate(npc);
        var character = AiIdentity.GetOrCreate(pm);
        if (npcId == Guid.Empty || character == Guid.Empty)
        {
            return Nothing;
        }

        // The ticket is used up by the player's next line to this townsperson, whatever the line is (3.6).
        var ticket = TakeTicket(npc, character);
        if (yields)
        {
            return Nothing;
        }

        Lapse(character);

        // 1. a return, 2. a delivery - the oldest row first
        if (CanComplete)
        {
            var row = Oldest(character, r => r.State == ErrandState.Delivered && r.Sender == npcId) ??
                      Oldest(character, r => r.State == ErrandState.Accepted && r.Receiver == npcId);
            if (row != null)
            {
                var done = await StepAsync(npc, pm, row, text, openingRevision);
                if (done != null)
                {
                    return done;
                }

                if (!TownsfolkTalk.CanDeliver(npc, from))
                {
                    return new Turn { Handled = true };
                }

                return Nothing; // the step did not happen: an ordinary turn, and the row waits
            }
        }

        // 3. an answer to an ask made on this townsperson's last line
        if (ticket != null)
        {
            return await AnswerAsync(npc, pm, ticket, text, openingRevision);
        }

        // Design v6 12.2: an errand already open with this townsperson (they sent it and it is not yet delivered, or
        // they are the relative whose answer is being carried back). The two question lists are looked at here and
        // nowhere else outside a pending ask; code answers, and the model is not called. Any other line is an ordinary
        // turn - with no detail of the errand, and no new ask from this townsperson.
        ErrandRow only = null;
        var qualifying = 0;
        var asSender = false;
        foreach (var r in _rows)
        {
            if (Qualifies(r, character, npcId))
            {
                qualifying++;
                only = r;
                asSender |= r.State == ErrandState.Accepted;
            }
        }

        if (qualifying > 0)
        {
            switch (ErrandMatch.Question(text, npc.Name, false))
            {
                case ErrandQuestion.Reward:
                    return Say(npc, RewardLine); // the same for every row: no selection, no brain call
                case ErrandQuestion.Remind when qualifying == 1:
                    return await RemindAsync(npc, pm, only, openingRevision);
                case ErrandQuestion.Remind:
                    {
                        // more than one errand concerns this townsperson: no guess (Codex RD3)
                        var neutral = Say(npc, Neutral[only.Id % Neutral.Length]);
                        Tell(pm, $"You carry more than one errand that concerns {npc.Name}. Say [Errands to see them.");
                        return neutral;
                    }
            }

            // Codex R2 note 6: with rows in both roles at one townsperson the wording is fixed by this rule - the
            // sender's if they sent any open errand, else the relative's - never by which row happens to come first.
            return Pointer ? new Turn { Ask = new { step = "pointer", role = asSender ? "sender" : "receiver" } } : NoAsk;
        }

        // 4. possibly a new ask (3.3 step 1)
        if (!CanOffer || PlaceOf(npc) == null || OpenCount(character) >= MaxOpen ||
            _askedAt.TryGetValue((npcId, character), out var asked) && OwnTime(pm) - asked < CooldownSeconds ||
            Dice(AskOneIn) != 0)
        {
            return Nothing;
        }

        var blocked = new List<string>();
        foreach (var (receiver, who) in _headStart)
        {
            if (who == character)
            {
                blocked.Add(Id(receiver));
            }
        }

        foreach (var r in _rows)
        {
            if (r.Character == character && r.Open && r.Kind == "introduce")
            {
                blocked.Add(Id(r.Receiver));
            }
        }

        var turn = new Turn { TicketId = Guid.NewGuid().ToString("N"), Gen = Generation, Request = $"q{++_requests}" };
        turn.Ask = new
        {
            step = "ask", gen = turn.Gen, request = turn.Request, ticket = turn.TicketId, @char = Id(character),
            speech_rev = openingRevision, stamp = Stamp(), intro_offered = _introOffered.Contains((npcId, character)),
            intro_blocked = blocked
        };
        return turn;
    }

    private static string Id(Guid g) => g.ToString("D");

    // After the ordinary reply was really SAID (3.3 steps 3 and 4). `plain`: no story told, no conduct flag, not shop or
    // decency, not a correction. The ask line is spoken in this same game-thread call, right after the reply.
    public static string AfterReply(BaseCreature npc, Mobile from, Turn turn, ErrandProof proof, bool plain, int openingRevision)
    {
        if (turn?.TicketId == null || proof == null || !plain || from is not PlayerMobile pm)
        {
            return null;
        }

        var npcId = AiIdentity.GetOrCreate(npc);
        var character = AiIdentity.GetOrCreate(pm);
        var receiver = Parse(proof.Receiver);
        if (!CanOffer || proof.Step != "ask" || proof.Ticket != turn.TicketId || proof.Request != turn.Request ||
            proof.Gen != turn.Gen || turn.Gen != Generation || proof.Char != Id(character) ||
            proof.SpeechRev != openingRevision || Revision(from) != openingRevision ||
            proof.Stamp == null || proof.Stamp != Stamp() || proof.DataGen == null || proof.DataGen != _leaseData ||
            Parse(proof.Sender) != npcId || !Shape(proof) || receiver == npcId ||
            ResidentAt(receiver, proof.ReceiverMap, proof.ReceiverTown) == null ||
            OpenCount(character) >= MaxOpen || HasOpenWith(character, npcId) ||
            !Sayable(proof.AskLine) || !Sayable(proof.YesLine) || !Sayable(proof.NoLine))
        {
            return null;
        }

        if (proof.Kind == "introduce" &&
            (_introOffered.Contains((npcId, character)) || _headStart.Contains((receiver, character)) || HasOpenIntro(character, receiver)))
        {
            return null;
        }

        npc.Say(proof.AskLine);

        // Only after the ask line was really said:
        _askedAt[(npcId, character)] = OwnTime(pm);
        if (proof.Kind == "introduce")
        {
            _introOffered.Add((npcId, character));
        }

        var ticket = new Ticket
        {
            Id = turn.TicketId, Proof = proof, Gen = Generation, Revision = Revision(from), At = Now(),
            Npc = npc.Serial, Player = pm.Serial, Char = character, Seats = SeatsRevision(), Stamp = proof.Stamp
        };
        if (_tickets.Remove((npc.Serial, character)))
        {
            _watched--;
        }

        _tickets[(npc.Serial, character)] = ticket;
        _watched++;
        return proof.AskLine;
    }

    private static bool Shape(ErrandProof p) =>
        p.Kind is "word" or "ask_after" or "introduce" && p.Route is "kin" or "cousin" or "standin" or "contact" &&
        p.Digest is { Length: 64 } && p.Band is "same" or "near" or "far" or "none" &&
        (p.Kind == "word") == (p.LineKey != null) && p.ReceiverMap != null && p.ReceiverTown != null &&
        p.SenderMap != null && p.SenderTown != null &&
        (p.Kind, p.Route) is ("word", "kin") or ("word", "cousin") or ("word", "standin") or ("ask_after", "kin") or
            ("ask_after", "cousin") or ("introduce", "contact");

    // A code-written line: whole, within the speech limit, no digit and no coin word (the shard's own second check).
    internal static bool Sayable(string line)
    {
        if (line is not { Length: >= 1 and <= SpeechMax })
        {
            return false;
        }

        foreach (var ch in line)
        {
            if (ch is >= '0' and <= '9' || ch < ' ' || ch is '{' or '}')
            {
                return false;
            }
        }

        var lower = " " + line.ToLowerInvariant() + " ";
        foreach (var word in CoinWords)
        {
            var at = lower.IndexOf(word, StringComparison.Ordinal);
            if (at > 0 && !char.IsLetter(lower[at - 1]) && !char.IsLetter(lower[at + word.Length]))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] CoinWords =
        ["gold", "coin", "coins", "silver", "copper", "pay", "paid", "payment", "wage", "wages", "reward", "rewards", "fee", "price", "purse", "gp"];

    private static bool HasOpenWith(Guid character, Guid sender)
    {
        foreach (var r in _rows)
        {
            if (r.Character == character && r.Open && r.Sender == sender)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOpenIntro(Guid character, Guid receiver)
    {
        foreach (var r in _rows)
        {
            if (r.Character == character && r.Open && r.Kind == "introduce" && r.Receiver == receiver)
            {
                return true;
            }
        }

        return false;
    }

    // ================================================================================================================
    // Tickets and saying yes (3.6)
    // ================================================================================================================

    private static Ticket TakeTicket(Mobile npc, Guid character)
    {
        if (!_tickets.Remove((npc.Serial, character), out var t))
        {
            return null;
        }

        _watched--;
        return t;
    }

    internal static void TicketTickForTests() => TicketTick();

    private static void TicketTick()
    {
        if (_tickets.Count == 0 && _verifying.Count == 0)
        {
            return;
        }

        var now = Now();
        // Codex Code R1 C2: a seat or quarantine change is caught by the monotonic Seats.Revision at the moment it is
        // tested. The town list's content stamp has no change event of its own (it is rebuilt from the world), so it
        // is also looked at here every second while a ticket lives, and ANY difference seen breaks the ticket for good
        // - a later return to the old value does not mend it.
        var stamp = Stamp();
        var seats = SeatsRevision();
        List<(Serial, Guid)> old = null;
        foreach (var (key, t) in _tickets)
        {
            if (stamp != t.Stamp || seats != t.Seats)
            {
                t.Broken = true;
            }

            if (now - t.At > TicketLife || t.Gen != Generation || t.Broken)
            {
                (old ??= []).Add(key);
            }
        }

        foreach (var t in _verifying)
        {
            if (stamp != t.Stamp || seats != t.Seats)
            {
                t.Broken = true;
            }
        }

        if (old != null)
        {
            foreach (var key in old)
            {
                _tickets.Remove(key);
                _watched--;
            }
        }
    }

    // The movement hooks (3.6, 11.3; Codex Code R1 C2): called FIRST in PlayerMobile's and BaseCreature's position and
    // map overrides, with the OLD position or map the engine hands them. A break is recorded by the change itself and is
    // never cleared - coming back does not restore the ticket. Both ends of the change are looked at: where the mobile
    // is NOW, and where it WAS. The engine runs region code before it calls these, and that code can move the mobile
    // again; a bounce out and back inside one outer move is then visible only as the old position of the inner call.
    public static void Moved(Mobile m, Point3D oldLocation)
    {
        if (_watched != 0)
        {
            Latch(m, oldLocation, m.Map);
        }
    }

    public static void MapChanged(Mobile m, Map oldMap)
    {
        if (_watched != 0)
        {
            Latch(m, m.Location, oldMap);
        }
    }

    private static void Latch(Mobile m, Point3D wasAt, Map wasOn)
    {
        foreach (var t in _tickets.Values)
        {
            Latch(t, m, wasAt, wasOn);
        }

        foreach (var t in _verifying)
        {
            Latch(t, m, wasAt, wasOn);
        }
    }

    private static void Latch(Ticket t, Mobile m, Point3D wasAt, Map wasOn)
    {
        if (t.Broken || t.Npc != m.Serial && t.Player != m.Serial)
        {
            return;
        }

        var npc = World.FindMobile(t.Npc) as BaseCreature;
        var player = World.FindMobile(t.Player);
        var other = t.Npc == m.Serial ? player : npc;
        if (npc == null || player == null || !TownsfolkTalk.CanDeliver(npc, player) || wasOn != other.Map ||
            Math.Abs(wasAt.X - other.X) > TownsfolkTalk.StaleRange || Math.Abs(wasAt.Y - other.Y) > TownsfolkTalk.StaleRange)
        {
            t.Broken = true;
        }
    }

    // Codex Code R1 C4: an exchange with the player's companion has started. Errands yield to it: any answer ticket
    // this player holds is void, whatever is said next.
    public static void ExchangeStarted(Mobile player)
    {
        if (_watched == 0 || player == null)
        {
            return;
        }

        foreach (var t in _tickets.Values)
        {
            if (t.Player == player.Serial)
            {
                t.Broken = true;
            }
        }

        foreach (var t in _verifying)
        {
            if (t.Player == player.Serial)
            {
                t.Broken = true;
            }
        }
    }

    // Logout, disconnection, death or deletion of either party.
    public static void Left(Mobile m)
    {
        if (_watched == 0 || m == null)
        {
            return;
        }

        foreach (var t in _tickets.Values)
        {
            if (t.Npc == m.Serial || t.Player == m.Serial)
            {
                t.Broken = true;
            }
        }

        foreach (var t in _verifying)
        {
            if (t.Npc == m.Serial || t.Player == m.Serial)
            {
                t.Broken = true;
            }
        }
    }

    private static readonly List<Ticket> _verifying = [];

    private static async Task<Turn> AnswerAsync(BaseCreature npc, PlayerMobile pm, Ticket ticket, string text, int openingRevision)
    {
        // Live: not broken, same activation generation, within its 120 seconds, the player's very next line, the two
        // still together, no seat or quarantine change since the ask (a counter that only goes up - Codex Code R1 C2),
        // and the town list is the one the ask was proved against.
        var live = !ticket.Broken && ticket.Gen == Generation && Now() - ticket.At <= TicketLife &&
                   openingRevision == ticket.Revision + 1 && TownsfolkTalk.CanDeliver(npc, pm) &&
                   ticket.Stamp != null && ticket.Stamp == Stamp() && ticket.Seats == SeatsRevision();
        var answer = live ? ErrandMatch.Classify(text, npc.Name) : ErrandAnswer.Other;

        if (answer == ErrandAnswer.No)
        {
            return Say(npc, ticket.Proof.NoLine); // nothing is created; no brain call
        }

        if (answer != ErrandAnswer.Yes)
        {
            // Design v6 12.2 (decided by the owner 2026-11-21; specified by Codex R1/R2): ONE listed question
            // on a LIVE ticket is answered by code and the offer continues. It is the SAME ticket - same id, same At (the
            // original 120 seconds are not extended), same proof, generation, seat revision, stamp and latch. It is put
            // back only after the reply was really spoken, in this same game-thread call: nothing is awaited between the
            // ticket being taken, found live, the sentence said and the ticket going back. Two fields change.
            if (live && !ticket.AskedOnce &&
                ErrandMatch.Question(text, npc.Name, true) is var question && question != ErrandQuestion.None)
            {
                // the ask is repeated as the offer that was just made, under the ticket's own live conditions - the
                // sentence already on the ticket, not a new proof
                var said = Say(npc, question == ErrandQuestion.Reward ? RewardLine : ticket.Proof.AskLine);
                if (said.Spoke && Now() - ticket.At <= TicketLife && !_tickets.ContainsKey((npc.Serial, ticket.Char)))
                {
                    ticket.Revision = openingRevision; // the player's next line is the one after this question
                    ticket.AskedOnce = true;
                    _tickets[(npc.Serial, ticket.Char)] = ticket;
                    _watched++;
                }

                return said; // not spoken: silence, and the ticket is gone
            }

            // Anything else: nothing is created, the ticket is gone, and the line goes to the brain as an ordinary turn
            // with one closing instruction written by code. Only when this really was the answer to a live ask.
            return live ? new Turn { Ask = new { step = "closed" } } : Nothing;
        }

        // Yes: one verification call to the brain, with no model (3.6). The latch stays alive through the call (11.3).
        var p = ticket.Proof;
        var request = $"q{++_requests}";
        var gen = Generation;
        var stamp = Stamp();
        ticket.Verifying = true;
        _verifying.Add(ticket);
        _watched++;
        ErrandProof proof;
        string dataGen;
        bool failed;
        try
        {
            (proof, dataGen, failed) = await Brain.ProofAsync(
                new
                {
                    step = "accept", gen, request, ticket = ticket.Id, @char = Id(ticket.Char), speech_rev = openingRevision,
                    stamp, kind = p.Kind, route = p.Route, sender = p.Sender, receiver = p.Receiver, about = p.About,
                    line_key = p.LineKey, seed = ticket.Id
                }
            );
        }
        finally
        {
            _verifying.Remove(ticket);
            _watched--;
        }

        Heard(failed, dataGen, proof != null && proof.Gen == gen && gen == Generation && proof.Request == request);
        var npcId = AiIdentity.GetOrCreate(npc);
        var receiver = Parse(p.Receiver);
        var ok = proof != null && !ticket.Broken && ticket.Seats == SeatsRevision() && ticket.Stamp == Stamp() &&
                 TownsfolkTalk.CanDeliver(npc, pm) && CanComplete &&
                 Accepted(proof, gen, request, stamp, openingRevision, pm) && proof.Ticket == ticket.Id &&
                 proof.Step == "accept" && proof.Digest == p.Digest && proof.Kind == p.Kind && proof.Route == p.Route &&
                 proof.Sender == p.Sender && proof.Receiver == p.Receiver && proof.LineKey == p.LineKey &&
                 OpenCount(ticket.Char) < MaxOpen && !HasOpenWith(ticket.Char, npcId) &&
                 ResidentAt(receiver, p.ReceiverMap, p.ReceiverTown) != null && Sayable(proof.YesLine) &&
                 (p.Kind != "introduce" || !_headStart.Contains((receiver, ticket.Char)) && !HasOpenIntro(ticket.Char, receiver));
        if (!ok)
        {
            return TownsfolkTalk.CanDeliver(npc, pm) ? Say(npc, Neutral[Math.Abs(ticket.Id.GetHashCode()) % Neutral.Length])
                : new Turn { Handled = true };
        }

        var now = TimeOf(ticket.Char);
        var row = new ErrandRow
        {
            Id = _nextId++, Version = 1, Kind = p.Kind, Route = p.Route, Character = ticket.Char, Sender = npcId,
            SenderMap = p.SenderMap, SenderTown = p.SenderTown, Receiver = receiver, ReceiverMap = p.ReceiverMap,
            ReceiverTown = p.ReceiverTown, About = p.About, LineKey = p.LineKey, LineHash = p.LineHash,
            ListsVersion = p.ListsVersion, Digest = p.Digest, Band = p.Band, State = ErrandState.Accepted,
            AcceptedAt = now, DueAt = now + DeadlineSeconds, SenderName = NameWithTrade(npc),
            ReceiverName = NameWithTrade(ResidentAt(receiver, p.ReceiverMap, p.ReceiverTown)),
            // v6 12.2: the ask exactly as it was said (the sentence on the ticket), and the value that chose its wording
            AskSeed = ticket.Id, AskHash = SpeechHash(p.AskLine)
        };
        _rows.Add(row);
        var turn = Say(npc, proof.YesLine);
        pm.SendMessage($"You have agreed to carry word for {row.SenderName}. Say [Errands to see what you are carrying.");
        return turn;
    }

    private static Turn Say(BaseCreature npc, string line)
    {
        if (!Sayable(line))
        {
            return new Turn { Handled = true };
        }

        npc.Say(line);
        return new Turn { Handled = true, Spoke = true, Said = line };
    }

    // What every errand reply tells us about the brain: a failed call ends the lease at once; a data generation that is
    // not the lease's moves the generation on.
    // Codex Code R1 note 4: the lease is RENEWED only by a reply that is the answer to what was just asked, in the
    // generation it was asked in (`ours`). A late reply from before an off-and-on, or to another request, renews nothing.
    private static void Heard(bool failed, string dataGen, bool ours)
    {
        if (failed)
        {
            DropLease("an errand call failed");
        }
        else if (dataGen != null && _leaseData != null && dataGen != _leaseData)
        {
            DropLease("the brain's data generation changed");
        }
        else if (ours && dataGen != null && _leaseData != null)
        {
            _leaseAt = Now();
        }
    }

    // The shard accepts a proof only if (3.1): the echo equals what it sent; the activation generation is still the
    // current one; the player has not spoken since; the town-list stamp is still current; the data generation equals
    // the one held by the brain lease.
    private static bool Accepted(ErrandProof proof, int gen, string request, string stamp, int openingRevision, Mobile from) =>
        Revision(from) == openingRevision && Current(proof, gen, request, stamp, openingRevision);

    // The same, without "the player has not spoken since": for [Errands show, which is a typed command and not a line
    // of speech (v6 12.8, Codex R2 note 3). The speech count is still echoed and compared, as an identity of the request.
    private static bool Current(ErrandProof proof, int gen, string request, string stamp, int sentRevision) =>
        proof.Gen == gen && gen == Generation && proof.Request == request && proof.SpeechRev == sentRevision &&
        stamp != null && proof.Stamp == stamp && Stamp() == stamp &&
        proof.DataGen != null && proof.DataGen == _leaseData && LeaseHeld;

    // ================================================================================================================
    // Design v6 12.2: "say it again" - the sentence that was spoken, exactly, or nothing of substance
    // ================================================================================================================

    // SHA-256, lower-case hex, of the UTF-8 bytes of the string exactly as given: nothing is trimmed, folded or
    // normalised (Codex R2 note 1). Null for no line - and null never equals a stored hash.
    internal static string SpeechHash(string line) =>
        line == null ? null :
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line))).ToLowerInvariant();

    private static object RemindBody(ErrandRow row, int gen, string request, string stamp, int revision)
    {
        var sender = row.State == ErrandState.Accepted;
        return new
        {
            step = "remind", role = sender ? "sender" : "receiver", gen, request, row = row.Id, version = row.Version,
            @char = Id(row.Character), speech_rev = revision, stamp, kind = row.Kind, route = row.Route,
            sender = Id(row.Sender), receiver = Id(row.Receiver), about = row.About, line_key = row.LineKey,
            // the STORED seed: the ticket id that chose the ask's wording; the row id chose the delivery sentence's
            seed = sender ? row.AskSeed : row.Id.ToString(),
            answer = sender ? null : new { key = row.AnswerKey, hash = row.AnswerHash, digest = row.AnswerDigest }
        };
    }

    // The hash this row's reminder must equal: the ask for a row not yet delivered, the relative's sentence for a round
    // trip on its way back. Null = it cannot be repeated (a row from before v6).
    private static string StoredHash(ErrandRow row) =>
        row.State == ErrandState.Accepted ? row.AskSeed == null ? null : row.AskHash :
        row.State == ErrandState.Delivered ? row.DeliveryHash : null;

    // Everything a reminder's proof must show, short of where the two people stand (Codex R2 note 2: the hash is ADDED
    // to the proof's checks, never in place of them). The echoed character id is compared too (Codex "Errands 15",
    // note 1): the row's owner is checked on its own by both callers, and this completes the echo beside it.
    private static bool RemindHolds(ErrandProof proof, ErrandRow row, int version, ErrandState state, string hash) =>
        proof != null && CanComplete && proof.Step == "remind" && proof.Row == row.Id && proof.Version == version &&
        proof.Char == Id(row.Character) &&
        row.Version == version && row.State == state && row.Open && _rows.Contains(row) && proof.Digest == row.Digest &&
        proof.Kind == row.Kind && proof.Route == row.Route && proof.Sender == Id(row.Sender) &&
        proof.Receiver == Id(row.Receiver) && TimeOf(row.Character) < row.DueAt && hash != null;

    // Spoken by the townsperson, to a player standing with them, for the ONE row in play between the two.
    private static async Task<Turn> RemindAsync(BaseCreature npc, PlayerMobile pm, ErrandRow row, int openingRevision)
    {
        var state = row.State;
        var hash = StoredHash(row);
        if (hash == null || !CanComplete)
        {
            return Unavailable(npc, pm, row, false); // a row from before v6, or no brain to ask: no call is made
        }

        var request = $"q{++_requests}";
        var gen = Generation;
        var stamp = Stamp();
        var version = row.Version;
        var (proof, dataGen, failed) = await Brain.ProofAsync(RemindBody(row, gen, request, stamp, openingRevision));
        Heard(failed, dataGen, proof != null && proof.Gen == gen && gen == Generation && proof.Request == request);
        if (!TownsfolkTalk.CanDeliver(npc, pm))
        {
            return new Turn { Handled = true }; // they have parted: nothing is said
        }

        var speaker = state == ErrandState.Accepted ? row.Sender : row.Receiver;
        var (map, town) = state == ErrandState.Accepted ? (row.SenderMap, row.SenderTown) : (row.ReceiverMap, row.ReceiverTown);
        var holds = RemindHolds(proof, row, version, state, hash) &&
                    Accepted(proof, gen, request, stamp, openingRevision, pm) &&
                    AiIdentity.GetOrCreate(pm) == row.Character && ResidentAt(speaker, map, town) == npc;
        if (holds && Sayable(proof.Line) && SpeechHash(proof.Line) == hash)
        {
            return Say(npc, proof.Line); // the same sentence, word for word
        }

        // Nothing of substance. The relative, whose errand is proved current but whose sentence would now come out
        // differently, says so in the one approved line; every other case is a neutral stock line.
        return Unavailable(npc, pm, row, holds && state == ErrandState.Delivered);
    }

    private static Turn Unavailable(BaseCreature npc, PlayerMobile pm, ErrandRow row, bool asHeard)
    {
        var turn = Say(npc, asHeard ? AsHeardLine : Neutral[row.Id % Neutral.Length]);
        Tell(pm, SeeLogMessage);
        return turn;
    }

    // [Errands show <number> (design v6 12.2, 12.8): the action that needs no phrasing. A REMOTE DISPLAY for one of the
    // character's own open rows - the log's own line, then the exact words as system text. It is defined apart from a
    // townsperson's speech: no nearness is asked for, and it never delivers, returns, pays or moves anything.
    // Bounded BEFORE any request is sent (Codex R2 must-fix): one read outstanding per character, a shard-wide cap,
    // fail fast with no queue, and a short pause after each read. The reservation belongs to this read only - it never
    // stands in the way of [Errands, drop, collect or talk.
    private static void Show(PlayerMobile pm, int id)
    {
        var character = AiIdentity.GetOrCreate(pm);
        Lapse(character);
        var row = _rows.Find(r => r.Id == id && r.Character == character && r.Open);
        if (row == null)
        {
            Tell(pm, "You are carrying no errand with that number.");
            return;
        }

        Tell(pm, Summary(row, character));
        if (_showOut.Contains(character))
        {
            return; // a read is already out for this character: nothing more is sent, and its answer still comes
        }

        if (StoredHash(row) == null || !CanComplete || _showOut.Count >= ShowMaxOutstanding ||
            _showNext.TryGetValue(character, out var next) && Now() < next)
        {
            Tell(pm, NotRecalledMessage);
            return;
        }

        _ = ShowAsync(pm, character, row);
    }

    internal static Task ShowForTests(PlayerMobile pm, int id)
    {
        var before = _lastShow;
        Show(pm, id);
        return ReferenceEquals(before, _lastShow) ? Task.CompletedTask : _lastShow;
    }

    private static Task _lastShow = Task.CompletedTask;

    private static Task ShowAsync(PlayerMobile pm, Guid character, ErrandRow row) => _lastShow = ShowReadAsync(pm, character, row);

    private static async Task ShowReadAsync(PlayerMobile pm, Guid character, ErrandRow row)
    {
        var state = row.State;
        var hash = StoredHash(row);
        var request = $"q{++_requests}";
        var gen = Generation;
        var stamp = Stamp();
        var version = row.Version;
        var revision = Revision(pm);
        var session = Session(pm);
        ErrandProof proof;
        string dataGen;
        bool failed;
        _showOut.Add(character); // the reservation, taken before the request is sent ...
        try
        {
            (proof, dataGen, failed) = await Brain.ProofAsync(RemindBody(row, gen, request, stamp, revision));
        }
        catch (Exception)
        {
            (proof, dataGen, failed) = (null, null, true); // nobody waits on this task: a throw must not be lost
        }
        finally
        {
            _showOut.Remove(character); // ... and given back on success, failure and timeout alike
            _showNext[character] = Now() + ShowCooldown;
        }

        Heard(failed, dataGen, proof != null && proof.Gen == gen && gen == Generation && proof.Request == request);

        // A late answer is dropped unless the same character, in the same session, still carries the same row unchanged.
        if (pm.Deleted || session == null || !ReferenceEquals(Session(pm), session) || AiIdentity.GetOrCreate(pm) != character ||
            !_rows.Contains(row) || row.Character != character || !row.Open || row.Version != version || row.State != state)
        {
            return;
        }

        var speaker = state == ErrandState.Accepted ? row.Sender : row.Receiver;
        var (map, town) = state == ErrandState.Accepted ? (row.SenderMap, row.SenderTown) : (row.ReceiverMap, row.ReceiverTown);
        var name = state == ErrandState.Accepted ? row.SenderName : row.ReceiverName;
        var ok = RemindHolds(proof, row, version, state, hash) && Current(proof, gen, request, stamp, revision) &&
                 ResidentAt(speaker, map, town) != null && Sayable(proof.Line) && SpeechHash(proof.Line) == hash &&
                 !string.IsNullOrEmpty(name);
        Tell(pm, ok ? $"{name} said: \"{proof.Line}\"" : NotRecalledMessage);
    }

    // ================================================================================================================
    // Delivery and return (3.2, 3.7): a model-free proof call, then - for a completion - record, mark, pay
    // ================================================================================================================

    private static async Task<Turn> StepAsync(BaseCreature npc, PlayerMobile pm, ErrandRow row, string text, int openingRevision)
    {
        var returning = row.State == ErrandState.Delivered;
        var request = $"q{++_requests}";
        var gen = Generation;
        var stamp = Stamp();
        var version = row.Version;
        var (proof, dataGen, failed) = await Brain.ProofAsync(
            new
            {
                step = returning ? "return" : "deliver", gen, request, row = row.Id, version, @char = Id(row.Character),
                speech_rev = openingRevision, stamp, kind = row.Kind, route = row.Route, sender = Id(row.Sender),
                receiver = Id(row.Receiver), about = row.About, line_key = row.LineKey, seed = row.Id.ToString(),
                answer = returning ? new { key = row.AnswerKey, hash = row.AnswerHash, digest = row.AnswerDigest } : null,
                // errands yield to the house rule: the brain gives no proof for a line it would rebuke, and the line
                // then goes on as an ordinary turn
                utterance = text
            }
        );
        Heard(failed, dataGen, proof != null && proof.Gen == gen && gen == Generation && proof.Request == request);

        var end = returning ? row.Sender : row.Receiver;
        var (map, town) = returning ? (row.SenderMap, row.SenderTown) : (row.ReceiverMap, row.ReceiverTown);
        if (proof == null || !TownsfolkTalk.CanDeliver(npc, pm) || !CanComplete ||
            !Accepted(proof, gen, request, stamp, openingRevision, pm) || proof.Row != row.Id || proof.Version != version ||
            row.Version != version || row.State != (returning ? ErrandState.Delivered : ErrandState.Accepted) ||
            proof.Digest != row.Digest || proof.Step != (returning ? "return" : "deliver") ||
            AiIdentity.GetOrCreate(pm) != row.Character || ResidentAt(end, map, town) != npc || !Sayable(proof.Line) ||
            TimeOf(row.Character) >= row.DueAt)
        {
            row.Waiting = "cannot be delivered just now";
            return null; // suspended for this turn: nothing is said or paid for it, and it still lapses at its deadline
        }

        row.Waiting = null;
        if (row.Kind == "ask_after" && !returning)
        {
            // A round trip's outward leg: the answer is fixed on the row. No event, no pay, no allowance place.
            if (proof.Answer == null)
            {
                row.Waiting = "cannot be delivered just now";
                return null;
            }

            row.State = ErrandState.Delivered;
            row.Version++;
            (row.AnswerKey, row.AnswerHash, row.AnswerDigest) = (proof.Answer.Key, proof.Answer.Hash, proof.Answer.Digest);
            row.DeliveryHash = SpeechHash(proof.Line); // v6 12.2: the relative's sentence exactly as it is said now
            return Say(npc, proof.Line);
        }

        return Complete(npc, pm, row, proof.Line) ? new Turn { Handled = true, Spoke = true, Said = proof.Line } : null;
    }

    public static int CoinFor(ErrandRow row)
    {
        var one = row.Band switch { "same" => CoinSame, "near" => CoinNear, "far" => CoinFar, _ => 0 };
        return row.Kind == "introduce" ? 0 : row.Kind == "ask_after" ? one * 3 / 2 : one; // a round trip: half as much again, rounded down
    }

    private static int Used(Guid character, string day) =>
        _allowance.TryGetValue(character, out var a) && a.Day == day ? a.Count : 0;

    // The completion operation (3.8): record, mark, pay - on the game thread, no wait and no save inside it. Nothing in
    // it is ever undone, and no claim is ever written off.
    internal static bool Complete(BaseCreature npc, PlayerMobile pm, ErrandRow row, string line)
    {
        // 1. re-checked by the caller; the Central-day key is read once here and used throughout
        var day = Server.Avallone.Named.NamedNpcs.Today(Core.Now);

        // 2. what is due
        var within = Used(row.Character, day) < DailyAllowance;
        var coin = within ? CoinFor(row) : 0;
        var karma = within ? KarmaRequest : 0;
        var headStart = row.Kind == "introduce" && !_headStart.Contains((row.Receiver, row.Character));

        // 3. record. If the recorder reports failure, stop: nothing has been changed.
        var facts = new
        {
            v = 1, errand = row.Id, kind = row.Kind, route = row.Route, sender_id = Id(row.Sender), sender_map = row.SenderMap,
            sender_town = row.SenderTown, receiver_id = Id(row.Receiver), receiver_map = row.ReceiverMap,
            receiver_town = row.ReceiverTown, played = Server.Avallone.Rumours.PlayedClock.Seconds,
            // design 11.2: the memory clock keeps fractions; the event carries it rounded DOWN to whole seconds
            memory = (long)Math.Max(0, Math.Floor(Server.Avallone.Rumours.PlayedClock.MemorySeconds)), day, head_start = headStart
        };
        if (!Recorder(row.Sender, pm, "errand_done", facts))
        {
            row.Waiting = "cannot be delivered just now";
            return false;
        }

        // 4. mark - plain assignments
        row.State = ErrandState.Done;
        row.Version++;
        row.CoinDue = coin;
        row.Coin = coin > 0 ? PayState.Owed : PayState.None;
        row.KarmaRequested = karma;
        row.HeadStart = headStart;
        if (row.Kind == "introduce")
        {
            _headStart.Add((row.Receiver, row.Character));
        }

        if (coin > 0)
        {
            _allowance[row.Character] = (day, Used(row.Character, day) + 1);
            row.AccrualDay = day;
        }

        // 5. pay - both parts attempted in this one operation, coin then karma, whatever became of the other
        var paid = coin > 0 && PayCoin(pm, row);
        if (karma > 0)
        {
            PayKarma(pm, row, day);
        }

        // 6. speak: the code-written line, then the pay message with what was actually given
        npc.Say(line);
        if (paid)
        {
            pm.SendMessage($"The errand for {row.SenderName} is done. You receive {row.CoinDue} gold.");
        }
        else if (row.Coin == PayState.Owed)
        {
            pm.SendMessage($"You have no room for the {row.CoinDue} gold you are owed. Make room, then say [Errands collect.");
        }
        else if (!within)
        {
            pm.SendMessage("You have done all the paid errands you can for today. This one earns thanks alone.");
        }

        return true;
    }

    // The coin part (3.8 step 5). True only when the stock call returned true. A throw anywhere sends the part to review
    // with the amount due KEPT; nothing is retried by itself.
    internal static bool PayCoin(PlayerMobile pm, ErrandRow row)
    {
        var pack = pm.Backpack;
        if (pack == null || pack.Deleted || pack.RootParent != pm)
        {
            return false; // the character's own carried pack, as it is at this moment - else no attempt; it stays owed
        }

        Item gold = null;
        var before = -1;
        try
        {
            before = pack.GetAmount(typeof(Gold));
            gold = new Gold(row.CoinDue);
            if (DropInPack(pack, pm, gold))
            {
                row.Coin = PayState.Paid;
                return true;
            }

            gold.Delete(); // false: the stock code changed nothing; the unplaced item is removed and the coin stays owed
            return false;
        }
        catch (Exception ex)
        {
            var after = -1;
            try
            {
                after = pack.GetAmount(typeof(Gold));
            }
            catch
            {
                // counted "if that can be done"
            }

            row.Coin = PayState.Review;
            row.Evidence = $"coin: {ex.GetType().Name}; pack gold {before} -> {after}; item " +
                           (gold == null ? "not made" : $"{gold.Serial} {(gold.Deleted ? "deleted" : "exists")}");
            try
            {
                if (gold is { Deleted: false, Parent: null })
                {
                    gold.Delete();
                }
            }
            catch
            {
                // nothing more can be done here
            }

            RaiseFault($"a pay part went to review (errand {row.Id})");
            return false;
        }
    }

    // The karma part: measured before and after WHETHER OR NOT the stock call threw - the stock routine changes karma
    // before its message and its lock check.
    // Dev play test 2026-11-22 (the owner): nothing was lost. The stock routine takes 1 off a
    // gain for every 100 karma already held, so 50 comes to 0 for a character above 5,000 - and it announces a change
    // of 0 as "You have lost a little karma". So the stock message is switched off here and the stock gain messages
    // are sent by this code, only for what was really gained.
    internal static void PayKarma(PlayerMobile pm, ErrandRow row, string day)
    {
        var before = pm.Karma;
        var threw = false;
        try
        {
            AwardKarma(pm, row.KarmaRequested, false);
        }
        catch (Exception ex)
        {
            threw = true;
            row.Evidence = (row.Evidence == null ? "" : row.Evidence + "; ") + $"karma: {ex.GetType().Name}";
        }

        row.KarmaGained = pm.Karma - before;
        row.Karma = threw ? PayState.Review : PayState.Paid;
        if (row.CoinDue == 0 && row.KarmaGained > 0)
        {
            _allowance[row.Character] = (day, Used(row.Character, day) + 1);
            row.AccrualDay = day;
        }

        if (threw)
        {
            RaiseFault($"a pay part went to review (errand {row.Id})");
        }

        // Codex "Errands 20" N1: the message comes LAST and is best-effort. Everything known about the payment - the
        // pay state, the allowance place, a fault - is already written; a message that cannot be sent changes none of it.
        if (row.KarmaGained > 0)
        {
            try
            {
                KarmaGainMessage(pm, row.KarmaGained);
            }
            catch (Exception ex)
            {
                row.Evidence = (row.Evidence == null ? "" : row.Evidence + "; ") + $"karma message: {ex.GetType().Name}";
            }
        }
    }

    // ================================================================================================================
    // Errand time, deadlines (3.9)
    // ================================================================================================================

    private static void TimeTick()
    {
        if (!CanComplete)
        {
            return; // a pause is never charged afterwards: time only ever goes up in fixed steps while this holds
        }

        foreach (var ns in Server.Network.NetState.Instances)
        {
            if (ns.Mobile is PlayerMobile pm && AiIdentity.TryGet(pm, out var character) && OpenCount(character) > 0)
            {
                _errandTime[character] = TimeOf(character) + TimeStep;
                Lapse(character);
            }
        }
    }

    internal static void AddTimeForTests(Guid character, long seconds) => _errandTime[character] = TimeOf(character) + seconds;

    // The row lapses when the counter is equal to or past the due value. No penalty.
    private static void Lapse(Guid character)
    {
        var now = TimeOf(character);
        foreach (var r in _rows)
        {
            if (r.Character == character && r.Open && now >= r.DueAt)
            {
                r.State = ErrandState.Lapsed;
                r.Version++;
                if (AiIdentity.TryGetPlayer(character, out var pm) && pm.NetState != null)
                {
                    pm.SendMessage($"Too long has passed. The errand for {r.SenderName} has lapsed.");
                }
            }
        }
    }

    // Rows with nothing owed and nothing under review are cleared once they are done or lapsed; a done row is KEPT for
    // as long as any part of it is owed or under review (3.8).
    private static void Prune()
    {
        _rows.RemoveAll(r => !r.Open && !r.Unsettled);
    }

    // ================================================================================================================
    // Commands
    // ================================================================================================================

    [Usage("Errands [show <n> | drop <n> | collect]  -  staff: status | settle <n> coin paid|owed | settle <n> karma closed | fault clear | completion on|off | offer on|off | pointer on|off | kill")]
    [Description("Avallone: the errands you are carrying for townsfolk.")]
    public static void Errands_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "";
        var staff = from.AccessLevel >= AccessLevel.Administrator;
        switch (verb)
        {
            case "" when from is PlayerMobile pm:
                List(pm);
                return;
            case "show" when from is PlayerMobile pm && e.Length > 1:
                Show(pm, e.GetInt32(1));
                return;
            case "drop" when from is PlayerMobile pm && e.Length > 1:
                Drop(pm, e.GetInt32(1));
                return;
            case "collect" when from is PlayerMobile pm:
                Collect(pm);
                return;
            case "status" when staff:
                from.SendMessage(Describe());
                foreach (var r in _rows)
                {
                    if (r.Unsettled || r.Waiting != null)
                    {
                        from.SendMessage($"  errand {r.Id}: {r.State}, coin {r.Coin} ({r.CoinDue}), karma {r.Karma} ({r.KarmaGained}) {r.Waiting} {r.Evidence}");
                    }
                }

                return;
            case "settle" when staff && e.Length == 4:
                from.SendMessage(Settle(e.GetInt32(1), e.GetString(2).ToLowerInvariant(), e.GetString(3).ToLowerInvariant(), from.Name));
                return;
            case "fault" when staff && e.Length == 2 && e.GetString(1).ToLowerInvariant() == "clear":
                from.SendMessage(ClearFault());
                return;
            case "completion" or "offer" or "pointer" when staff && e.Length == 2 && e.GetString(1).ToLowerInvariant() is "on" or "off":
                Switch(verb, e.GetString(1).ToLowerInvariant() == "on");
                from.SendMessage(Describe());
                return;
            case "kill" when staff:
                Kill();
                from.SendMessage(Describe());
                return;
            default:
                from.SendMessage("Say [Errands, [Errands show <number>, [Errands drop <number> or [Errands collect.");
                return;
        }
    }

    public static string Describe() =>
        $"errands: completion {(Completion ? "on" : "off")}, offer {(Offer ? "on" : "off")}; " +
        $"pointer line {(Pointer ? "on" : "off")}; " +
        $"{(Faulted ? "FAULT: " + _fault : "no fault")}; brain lease {(LeaseHeld ? "held" : "not held")}; " +
        $"ledger {(LedgerReady() ? "ready" : "not ready")}; generation {Generation}; {_rows.Count} row(s), {_tickets.Count} ticket(s)";

    internal static void Switch(string which, bool on)
    {
        if (which == "pointer")
        {
            // v6 12.3: only which fixed instruction an ordinary turn carries while an errand is open. No row, ticket or
            // proof depends on it, so the activation generation is left alone.
            Pointer = on;
            ServerConfiguration.SetSetting("avallone.errands.pointer", on ? "true" : "false");
            return;
        }

        if (on && ErrandMatch.Validate() is { } bad)
        {
            logger.Error("Avallone errands: the phrase lists are refused ({Why}); {Which} stays off.", bad, which);
            return;
        }

        if (which == "completion")
        {
            Completion = on;
            ServerConfiguration.SetSetting("avallone.errands.completion", on ? "true" : "false");
        }
        else
        {
            Offer = on;
            ServerConfiguration.SetSetting("avallone.errands.offer", on ? "true" : "false");
        }

        Bump($"{which} switched {(on ? "on" : "off")}");
        if (on && which == "completion" && !_probeOut)
        {
            _ = ProbeAsync(); // at once when the switch is turned on
        }
    }

    // Both off, every answer ticket cleared, the generation raised. Rows and debts are kept and wait.
    internal static void Kill()
    {
        Completion = Offer = false;
        ServerConfiguration.SetSetting("avallone.errands.completion", "false");
        ServerConfiguration.SetSetting("avallone.errands.offer", "false");
        _watched -= _tickets.Count;
        _tickets.Clear();
        foreach (var t in _verifying)
        {
            t.Broken = true;
        }

        Bump("kill");
    }

    // Clearing the fault lets errands resume for everyone even while a part is still under review; that part stays
    // kept and uncollectable until it is settled. It gives nothing and erases nothing.
    internal static string ClearFault()
    {
        if (_fault == null)
        {
            return "No fault is standing.";
        }

        var was = _fault;
        _fault = null;
        Bump("fault cleared");
        return $"Fault cleared (it was: {was}). Parts under review stay under review until settled.";
    }

    // An operator's typed command, after looking at the evidence. It gives nothing itself, takes no place in the
    // allowance and writes no ledger event.
    internal static string Settle(int id, string part, string to, string by)
    {
        var row = _rows.Find(r => r.Id == id);
        if (row == null)
        {
            return "No such errand.";
        }

        if (part == "coin" && row.Coin == PayState.Review && to is "paid" or "owed")
        {
            row.Coin = to == "paid" ? PayState.Paid : PayState.Owed;
        }
        else if (part == "karma" && row.Karma == PayState.Review && to == "closed")
        {
            row.Karma = PayState.Paid;
        }
        else
        {
            return "Nothing to settle there. Use: settle <n> coin paid|owed, or settle <n> karma closed.";
        }

        row.Evidence = (row.Evidence ?? "") + $"; settled {part} {to} by {by} {Core.Now:yyyy-MM-dd HH:mm}Z";
        return $"Errand {id}: {part} settled as {to}.";
    }

    private static void List(PlayerMobile pm)
    {
        var character = AiIdentity.GetOrCreate(pm);
        Lapse(character);
        Prune();
        var n = 0;
        var owed = 0;
        foreach (var r in _rows)
        {
            if (r.Character != character)
            {
                continue;
            }

            if (r.Coin == PayState.Owed)
            {
                owed += r.CoinDue;
            }

            if (!r.Open)
            {
                continue;
            }

            n++;
            pm.SendMessage(Summary(r, character));
        }

        if (n == 0)
        {
            pm.SendMessage("You are carrying no errands.");
        }

        if (owed > 0)
        {
            pm.SendMessage($"You are owed {owed} gold for errands. Say [Errands collect.");
        }
    }

    // The log's line for one open row: who, which town, what to do, the time left - from the row alone, no brain.
    private static string Summary(ErrandRow r, Guid character)
    {
        var left = Math.Max(0, r.DueAt - TimeOf(character));
        var time = left >= 5400 ? $"About {(left + 1800) / 3600} hours left." : left >= 3600 ? "About 1 hour left." : "Less than an hour left.";
        var what = r.Kind switch
        {
            "introduce" => $"Introduce yourself to {r.ReceiverName} in {r.ReceiverTown}, in the name of {r.SenderName} of {r.SenderTown}.",
            "ask_after" when r.State == ErrandState.Delivered =>
                $"Ask after {r.ReceiverName} in {r.ReceiverTown} for {r.SenderName} of {r.SenderTown} - answer in hand, return to {r.SenderName}.",
            "ask_after" => $"Ask after {r.ReceiverName} in {r.ReceiverTown} for {r.SenderName} of {r.SenderTown}, and bring the answer back.",
            _ => $"Carry word from {r.SenderName} of {r.SenderTown} to {r.ReceiverName} in {r.ReceiverTown}."
        };
        return $"{r.Id}. {what} {time}{(r.Waiting != null ? " That errand cannot be delivered just now." : "")}";
    }

    // Sets the row to lapsed. No penalty; the cooldown still applies.
    private static void Drop(PlayerMobile pm, int id)
    {
        var character = AiIdentity.GetOrCreate(pm);
        var row = _rows.Find(r => r.Id == id && r.Character == character && r.Open);
        if (row == null)
        {
            pm.SendMessage("You are carrying no errand with that number.");
            return;
        }

        row.State = ErrandState.Lapsed;
        row.Version++;
        pm.SendMessage($"You set aside the errand for {row.SenderName}.");
    }

    // The player's command and its gate (3.8): the caller's own rows, done and owed, oldest first; pay allowed; no
    // brain, no proof, no ledger event, no allowance place.
    internal static void Collect(PlayerMobile pm)
    {
        var character = AiIdentity.GetOrCreate(pm);
        var owed = new List<ErrandRow>();
        foreach (var r in _rows)
        {
            if (r.Character == character && r.State == ErrandState.Done && r.Coin == PayState.Owed)
            {
                owed.Add(r);
            }
        }

        if (owed.Count == 0)
        {
            pm.SendMessage("You are owed nothing for errands.");
            return;
        }

        owed.Sort((a, b) => a.Id.CompareTo(b.Id));
        var total = 0;
        foreach (var r in owed)
        {
            total += r.CoinDue;
        }

        if (!PayAllowed)
        {
            pm.SendMessage($"Errand pay is paused just now. The {total} gold you are owed is safe.");
            return;
        }

        var got = 0;
        var stopped = false;
        foreach (var r in owed)
        {
            if (!PayAllowed || !PayCoin(pm, r))
            {
                stopped = !PayAllowed || r.Coin == PayState.Review; // not "no room": pay itself stopped (note 3)
                break;
            }

            got += r.CoinDue;
        }

        if (got > 0)
        {
            pm.SendMessage($"You collect {got} gold owed for errands.");
        }

        if (stopped)
        {
            pm.SendMessage("Errand pay has been stopped for now. What you are still owed is safe.");
        }
        else if (got == 0)
        {
            pm.SendMessage($"You have no room for the {total} gold you are owed. Make room, then say [Errands collect.");
        }
    }

    // A deleted character: its rows, time, stamps and guards go with it.
    public static void Forget(Guid character)
    {
        _rows.RemoveAll(r => r.Character == character);
        _errandTime.Remove(character);
        _allowance.Remove(character);
        _introOffered.RemoveWhere(k => k.Char == character);
        _headStart.RemoveWhere(k => k.Char == character);
        _showNext.Remove(character);
        foreach (var key in new List<(Guid, Guid)>(_askedAt.Keys))
        {
            if (key.Item2 == character)
            {
                _askedAt.Remove(key);
            }
        }
    }

    // ================================================================================================================
    // The save record
    // ================================================================================================================

    public override void Serialize(IGenericWriter writer)
    {
        Prune();
        writer.WriteEncodedInt(_saveAs);
        writer.Write(_nextId);
        writer.Write(_fault);
        writer.WriteEncodedInt(_rows.Count);
        foreach (var r in _rows)
        {
            writer.Write(r.Id);
            writer.Write(r.Version);
            writer.Write(r.Kind);
            writer.Write(r.Route);
            writer.Write(r.Character);
            writer.Write(r.Sender);
            writer.Write(r.SenderMap);
            writer.Write(r.SenderTown);
            writer.Write(r.Receiver);
            writer.Write(r.ReceiverMap);
            writer.Write(r.ReceiverTown);
            writer.Write(r.About);
            writer.Write(r.LineKey);
            writer.Write(r.LineHash);
            writer.Write(r.ListsVersion);
            writer.Write(r.Digest);
            writer.Write(r.Band);
            writer.Write((byte)r.State);
            writer.Write(r.AcceptedAt);
            writer.Write(r.DueAt);
            writer.Write(r.AnswerKey);
            writer.Write(r.AnswerHash);
            writer.Write(r.AnswerDigest);
            writer.Write(r.CoinDue);
            writer.Write((byte)r.Coin);
            writer.Write(r.KarmaRequested);
            writer.Write((byte)r.Karma);
            writer.Write(r.KarmaGained);
            writer.Write(r.AccrualDay);
            writer.Write(r.Evidence);
            writer.Write(r.HeadStart);
            writer.Write(r.SenderName);
            writer.Write(r.ReceiverName);
            if (_saveAs >= 1)
            {
                writer.Write(r.AskSeed);
                writer.Write(r.AskHash);
                writer.Write(r.DeliveryHash);
            }
        }

        writer.WriteEncodedInt(_errandTime.Count);
        foreach (var (k, v) in _errandTime)
        {
            writer.Write(k);
            writer.Write(v);
        }

        writer.WriteEncodedInt(_askedAt.Count);
        foreach (var (k, v) in _askedAt)
        {
            writer.Write(k.Npc);
            writer.Write(k.Char);
            writer.Write(v);
        }

        writer.WriteEncodedInt(_allowance.Count);
        foreach (var (k, v) in _allowance)
        {
            writer.Write(k);
            writer.Write(v.Day);
            writer.Write(v.Count);
        }

        writer.WriteEncodedInt(_introOffered.Count);
        foreach (var (a, b) in _introOffered)
        {
            writer.Write(a);
            writer.Write(b);
        }

        writer.WriteEncodedInt(_headStart.Count);
        foreach (var (a, b) in _headStart)
        {
            writer.Write(a);
            writer.Write(b);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        ClearState();
        var saved = reader.ReadEncodedInt();
        _nextId = reader.ReadInt();
        _fault = reader.ReadString();
        var rows = reader.ReadEncodedInt();
        for (var i = 0; i < rows; i++)
        {
            var row =
                new ErrandRow
                {
                    Id = reader.ReadInt(), Version = reader.ReadInt(), Kind = reader.ReadString(), Route = reader.ReadString(),
                    Character = reader.ReadGuid(), Sender = reader.ReadGuid(), SenderMap = reader.ReadString(),
                    SenderTown = reader.ReadString(), Receiver = reader.ReadGuid(), ReceiverMap = reader.ReadString(),
                    ReceiverTown = reader.ReadString(), About = reader.ReadString(), LineKey = reader.ReadString(),
                    LineHash = reader.ReadString(), ListsVersion = reader.ReadInt(), Digest = reader.ReadString(),
                    Band = reader.ReadString(), State = (ErrandState)reader.ReadByte(), AcceptedAt = reader.ReadLong(),
                    DueAt = reader.ReadLong(), AnswerKey = reader.ReadString(), AnswerHash = reader.ReadString(),
                    AnswerDigest = reader.ReadString(), CoinDue = reader.ReadInt(), Coin = (PayState)reader.ReadByte(),
                    KarmaRequested = reader.ReadInt(), Karma = (PayState)reader.ReadByte(), KarmaGained = reader.ReadInt(),
                    AccrualDay = reader.ReadString(), Evidence = reader.ReadString(), HeadStart = reader.ReadBool(),
                    SenderName = reader.ReadString(), ReceiverName = reader.ReadString()
                };
            if (saved >= 1)
            {
                // a row saved before v6 has none of these, and is then never repeated word for word (12.2)
                (row.AskSeed, row.AskHash, row.DeliveryHash) = (reader.ReadString(), reader.ReadString(), reader.ReadString());
            }

            _rows.Add(row);
        }

        for (var n = reader.ReadEncodedInt(); n > 0; n--)
        {
            var k = reader.ReadGuid();
            _errandTime[k] = reader.ReadLong();
        }

        for (var n = reader.ReadEncodedInt(); n > 0; n--)
        {
            var a = reader.ReadGuid();
            var b = reader.ReadGuid();
            _askedAt[(a, b)] = reader.ReadLong();
        }

        for (var n = reader.ReadEncodedInt(); n > 0; n--)
        {
            var k = reader.ReadGuid();
            var day = reader.ReadString();
            _allowance[k] = (day, reader.ReadInt());
        }

        for (var n = reader.ReadEncodedInt(); n > 0; n--)
        {
            var a = reader.ReadGuid();
            _introOffered.Add((a, reader.ReadGuid()));
        }

        for (var n = reader.ReadEncodedInt(); n > 0; n--)
        {
            var a = reader.ReadGuid();
            _headStart.Add((a, reader.ReadGuid()));
        }
    }

    private static void ClearState()
    {
        _rows.Clear();
        _errandTime.Clear();
        _askedAt.Clear();
        _allowance.Clear();
        _introOffered.Clear();
        _headStart.Clear();
        _tickets.Clear();
        _verifying.Clear();
        _showOut.Clear();
        _showNext.Clear();
        _watched = 0;
        _nextId = 1;
        _fault = null;
    }

    // ================================================================================================================
    // Tests only
    // ================================================================================================================

    internal static IReadOnlyList<ErrandRow> RowsForTests => _rows;
    internal static int WatchedForTests => _watched;
    internal static Ticket TicketForTests(Mobile npc, Guid character) => _tickets.GetValueOrDefault((npc.Serial, character));
    internal static void AddRowForTests(ErrandRow row) => _rows.Add(row);
    internal static bool IntroOfferedForTests(Guid sender, Guid character) => _introOffered.Contains((sender, character));
    internal static bool HeadStartForTests(Guid receiver, Guid character) => _headStart.Contains((receiver, character));
    internal static int AllowanceForTests(Guid character, string day) => Used(character, day);

    internal static int ShowOutForTests => _showOut.Count;
    internal static void DropForTests(PlayerMobile pm, int id) => Drop(pm, id);
    internal static long? AskedAtForTests(Guid npc, Guid character) => _askedAt.TryGetValue((npc, character), out var at) ? at : null;
    internal static void SetShowMaxForTests(int max) => ShowMaxOutstanding = max;

    // A save record as it was written before v6 (save version 0), for the load test.
    internal static void RoundTripAsVersionZeroForTests()
    {
        var store = _instance ?? new Errands();
        var writer = new BufferWriter(true);
        _saveAs = 0;
        try
        {
            store.Serialize(writer);
        }
        finally
        {
            _saveAs = SaveVersion;
        }

        var bytes = writer.Buffer.AsSpan(0, (int)writer.Position).ToArray();
        store.Deserialize(new BufferReader(bytes));
    }

    private static int _saveAs = SaveVersion;

    internal static void ResetForTests(bool completion = true, bool offer = true)
    {
        ClearState();
        Pointer = false;
        ShowMaxOutstanding = 2;
        _lastShow = Task.CompletedTask;
        Completion = completion;
        Offer = offer;
        Generation = 1;
        _leaseAt = DateTime.MinValue;
        _leaseBoot = _leaseData = null;
        _probe = 0;
        _probeOut = false;
        _requests = 0;
    }

    internal static void RoundTripForTests()
    {
        var store = _instance ?? new Errands();
        var writer = new BufferWriter(true);
        store.Serialize(writer);
        var bytes = writer.Buffer.AsSpan(0, (int)writer.Position).ToArray();
        store.Deserialize(new BufferReader(bytes));
    }
}
