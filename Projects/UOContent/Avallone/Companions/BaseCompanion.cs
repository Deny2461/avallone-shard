using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModernUO.Serialization;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Logging;

namespace Server.Mobiles;

// A permanent AI companion (AI plan v2, Phase 3). Deliberately NOT BaseHire: hirelings charge wages and
// delete themselves when unpaid. Built on BaseCreature's own pet machinery (follow, stay, owner healing,
// auto-stabling on logout) with every path that loses a pet closed off:
// - Commandable => false: no pet speech/context-menu commands ("all kill", release, transfer), and the hourly
//   loyalty drain skips her (BaseCreature.LoyaltyTimer only drains Controlled && Commandable creatures).
// - Never dies: OnBeforeDeath turns death into "badly wounded" for a while (no permanent death, the owner 2026-09-30).
// - Going home clears the control master without ever adding an Owners entry, so the 3-day abandoned-pet
//   delete timer (keyed on LastOwner) never starts.
// Talk goes through the AI gateway; the model may pick one of a few actions, which are re-validated here.
[SerializationGenerator(0, false)]
public abstract partial class BaseCompanion : BaseCreature
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(BaseCompanion));

    // the owner's rule (chat, 2026-09-30): 1-2 companions active at a time per character.
    public static int MaxActivePerCharacter { get; set; } = 2;

    private const int HearRange = 6;
    private const int StaleRange = 14;
    private const int NearOwnerRange = 15;
    private const int HistoryLines = 8;

    // In a fight she stays within this many tiles of her companion (the owner, chat, 2026-10-01
    // ). Bandages reach 2 tiles, so staying close is also what lets her heal.
    private const int SideRange = 2;

    private static readonly TimeSpan PerPlayerCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FallbackCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan WoundedFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FightOverAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HeadCountCooldown = TimeSpan.FromSeconds(45);
    private const int MaxEvents = 4;

    private static readonly HashSet<BaseCompanion> _all = [];

    public static IReadOnlyCollection<BaseCompanion> All => _all;

    [SerializableField(0)]
    private string _companionKey;

    [SerializableField(1)]
    private Guid _aiId;

    [SerializableField(2)]
    private Point3D _homeLocation;

    [SerializableField(3)]
    private Map _homeMap;

    // Trust per character (never per account). Read with GetTrust; changed only through AdjustTrust/SetTrust.
    [SerializableField(4, setter: "private")]
    private Dictionary<Mobile, int> _trust;

    // RAM only: lost on restart, by design for Phase 3 (durable memory is Phase 4).
    private readonly Dictionary<Mobile, List<AiLine>> _history = new();
    private readonly Dictionary<Mobile, DateTime> _lastTalk = new();
    private readonly Dictionary<Mobile, DateTime> _nextHeard = new();
    private readonly Dictionary<Mobile, DateTime> _nextFallback = new();
    private int _lastThinkingLine = -1;
    private readonly Dictionary<Mobile, DateTime> _nextTalkedRound = new();
    private readonly Dictionary<Mobile, List<(DateTime When, string Text)>> _events = new();
    private readonly Dictionary<Mobile, (DateTime Day, int Gained)> _timeTogetherToday = new();

    private long _nextThinkTick;
    private DateTime _woundedUntil;
    private DateTime _fightLastSeen;
    private bool _inFight;
    private DateTime _nextFightTrust;
    private DateTime _nextHeadCount;
    private TimeSpan _togetherAccrued;
    private DateTime _lastTogetherCheck;
    private Mobile _watchedOwner;
    private bool _ownerWasAlive;
    private bool _ownerCameBackUnseen;
    private bool _ownerWasCriminal;
    private int _ownerKills;
    private bool _goingHome;

    // What her companion last asked for: follow (true) or wait (false). Combat can swap ControlOrder to an attack
    // order for a while, so this - not ControlOrder - says whether she should be at their side.
    private bool _wantFollow = true;

    // Recall / Sacred Journey / bracelet of binding: she goes with the person she travels with, like a bonded pet (still
    // only when following them, within 3 tiles, on the same map - BaseCreature.TeleportPets).
    public override bool TravelsAsBonded => true;

    protected BaseCompanion(string companionKey) : base(AIType.AI_Melee, FightMode.Aggressor, 10, 1)
    {
        _companionKey = companionKey;
        _aiId = Guid.NewGuid();
        _trust = new Dictionary<Mobile, int>();
        ControlSlots = 1;
        Blessed = true; // at home and unattached
        _all.Add(this);
    }

    // Names a persona file in the brain sidecar (brain/personas/<key>.md).
    public abstract string PersonaKey { get; }

    public virtual string FirstName => Name?.Split(' ')[0] ?? "";

    // Said when she walks away over the owner's cruelty.
    public virtual string LeaveLine => "I'll not be part of this. Find someone else.";

    public virtual string[] FallbackEmotes => ["*is busy and does not answer*"];

    // Phase 4e: her own starting kit, exactly (type, layer, hue). Recorded once after load as Starter gear, which never
    // goes to a player. Anything she wears that is not on this list and was not given to her is left unrecorded.
    public virtual StarterGear[] StarterLoadout => [];

    // Said when a reply is taking a while, so the pause reads as her thinking rather than silence (the owner, 2026-10-06).
    // A line written as *...* is an emote, anything else is spoken (the owner, 2026-10-17). None may
    // react to what kind of line the player said (the owner, 2026-10-17).
    public virtual string[] ThinkingLines => CommonThinkingLines;

    // Shared by every companion: no pronouns, nothing tied to one person's trade, so any character can use them on top
    // of their own (the owner, 2026-10-17). the owner asked not to be shown these - keep them fresh.
    public static readonly string[] CommonThinkingLines =
    [
        "Let me see.",
        "One moment.",
        "Hmm, now.",
        "Ah.",
        "Right, then.",
        "Just a breath.",
        "Give it a moment.",
        "Mm-hm.",
        "Thinking.",
        "Bear with me.",
        "Hold that thought.",
        "Now, where was I.",
        "Let me think on that.",
        "Hm. Yes.",
        "A moment, if you would.",
        "*pauses*",
        "*thinks it over*",
        "*taps a foot*",
        "*looks up at the sky*",
        "*scratches an ear*",
        "*stares into the distance*",
        "*hums briefly*",
        "*shifts from foot to foot*",
        "*squints*",
        "*nods slowly*",
        "Patience, patience.",
        "Not so fast.",
        "There's a thought.",
        "In a moment.",
        "Steady now.",
        "*frowns in thought*",
        "Well, now.",
    ];

    public static bool IsEmoteLine(string line) => line.Length > 2 && line[0] == '*' && line[^1] == '*';

    public bool IsWounded => Core.Now < _woundedUntil;

    public override bool Commandable => false;
    protected override BaseAI ForcedAI => new CompanionAI(this);
    public override bool DeleteOnRelease => false;
    public override bool CanDrop => false;
    public override bool BardImmune => true;
    public override bool CanHeal => !IsWounded;
    public override bool CanHealOwner => !IsWounded;
    // Stock owner-healing is 78% / 6.5 s bandage / 30 s apart - too slow to save a fragile character
    // (the owner died twice and never saw a heal start, 2026-10-01). Reach stays the bandage rule (2 tiles).
    public override double HealOwnerTrigger => 0.85;
    public override double HealOwnerDelay => 3.0;
    public override double HealOwnerInterval => 6.0;
    public override TimeSpan BondingAbandonDelay => TimeSpan.FromDays(3650);
    public override bool CanBeRenamedBy(Mobile from) => false;

    // Not in Data/npc-speeds.json; a human pace.
    public override void GetSpeeds(out double activeSpeed, out double passiveSpeed)
    {
        activeSpeed = 0.2;
        passiveSpeed = 0.4;
    }

    [AfterDeserialization]
    private void AfterDeserialization()
    {
        if (_aiId == Guid.Empty)
        {
            _aiId = Guid.NewGuid();
        }

        _trust ??= new Dictionary<Mobile, int>();
        _all.Add(this);
    }

    public override void OnAfterDelete()
    {
        _all.Remove(this);
        base.OnAfterDelete();
    }

    public void SetHome(Point3D location, Map map)
    {
        HomeLocation = location;
        HomeMap = map;
        if (ControlMaster == null)
        {
            Home = location;
            RangeHome = 3;
        }
    }

    // ---- trust -----------------------------------------------------------------------------------------

    public int GetTrust(Mobile m) => m != null && _trust.TryGetValue(m, out var t) ? t : CompanionTrust.Start;

    public IEnumerable<KeyValuePair<Mobile, int>> TrustEntries => _trust;

    public void SetTrust(Mobile m, int value, string reason)
    {
        var old = GetTrust(m);
        _trust[m] = Math.Clamp(value, CompanionTrust.Min, CompanionTrust.Max);
        this.MarkDirty();

        var (oldBand, newBand) = (CompanionTrust.Band(old), CompanionTrust.Band(_trust[m]));
        if (oldBand != newBand)
        {
            AiLedger.Record(this, m, "trust_band_changed", new { from_band = oldBand, to_band = newBand, reason });
        }

        logger.Information(
            "Companion {Key}: trust {Player} {Old} -> {New} ({Reason})",
            _companionKey, m.Name, old, _trust[m], reason
        );
    }

    public void AdjustTrust(Mobile m, int delta, string reason)
    {
        if (delta == 0)
        {
            return;
        }

        SetTrust(m, CompanionTrust.Apply(GetTrust(m), delta), reason);

        if (ControlMaster == m && CompanionTrust.WillLeave(GetTrust(m)))
        {
            Say(LeaveLine);
            AddEvent(m, $"You walked away from {m.Name} over their cruelty.", "left_over_cruelty");
            GoHome();
        }
    }

    // Every event she notices also goes to the memory ledger (Phase 4a1), under a fixed kind.
    private void AddEvent(Mobile m, string text, string kind)
    {
        AiLedger.Record(this, m, kind, new { text });

        if (!_events.TryGetValue(m, out var list))
        {
            _events[m] = list = [];
        }

        list.Add((Core.Now, text));
        if (list.Count > MaxEvents)
        {
            list.RemoveRange(0, list.Count - MaxEvents);
        }
    }

    // ---- who is she travelling with -------------------------------------------------------------------

    public static int ActiveCompanionsOf(Mobile m)
    {
        var count = 0;
        foreach (var c in _all)
        {
            if (c.ControlMaster == m || c.IsStabled && c.StabledBy == m)
            {
                count++;
            }
        }

        return count;
    }

    private bool CanJoin(Mobile m, out string why)
    {
        if (ControlMaster != null || IsStabled)
        {
            why = "you are already travelling with someone";
            return false;
        }

        if (IsWounded)
        {
            why = "you are too badly wounded to travel";
            return false;
        }

        if (!CompanionTrust.WillJoin(GetTrust(m)))
        {
            why = "you parted ways over their cruelty and will not travel with them yet";
            return false;
        }

        if (ActiveCompanionsOf(m) >= MaxActivePerCharacter)
        {
            why = "they already have as many companions as they can lead";
            return false;
        }

        if (m.Followers + ControlSlots > m.FollowersMax)
        {
            why = "they already have too many animals and followers with them";
            return false;
        }

        why = null;
        return true;
    }

    public void GoHome()
    {
        EndSession();

        if (ControlMaster != null)
        {
            SetControlMaster(null);
        }

        StopDeleteTimer();
        Combatant = null;
        Warmode = false;
        Blessed = true;
        _goingHome = true;
        Emote("*sets off for home*");

        Timer.StartTimer(TimeSpan.FromSeconds(5), () =>
            {
                if (!Deleted && ControlMaster == null && !IsStabled)
                {
                    MoveHome();
                }

                _goingHome = false;
            }
        );
    }

    public void MoveHome()
    {
        if (_homeMap == null || _homeMap == Map.Internal)
        {
            return;
        }

        Home = _homeLocation;
        RangeHome = 3;
        MoveToWorld(_homeLocation, _homeMap);
    }

    public override void HealStart(Mobile patient)
    {
        base.HealStart(patient);

        if (patient != null && patient == ControlMaster)
        {
            _sessionHeals++;
            if (_inFight)
            {
                _fightHeals++;
            }
        }

        if (patient != this)
        {
            logger.Information(
                "Companion {Key}: bandaging {Patient} ({Hits}/{Max})",
                _companionKey, patient.Name, patient.Hits, patient.HitsMax
            );
        }
    }

    // ---- her own horse ---------------------------------------------------------------------------------
    // Mirrors her companion (the owner, chat, 2026-10-01): mounts when they are mounted and near, gets down
    // when they do. The horse exists only while ridden - never a pet, no follower slot, nothing to stable or lose.

    private void MirrorMount(Mobile owner, bool near)
    {
        var want = owner != null && near && owner.Alive && owner.Mount != null && !IsWounded;

        if (want && Mount == null)
        {
            new Horse().Rider = this;
        }
        else if (!want && Mount is BaseMount mine)
        {
            mine.Rider = null;
            mine.Delete();
        }
    }

    // ---- never dies --------------------------------------------------------------------------------------

    public override bool OnBeforeDeath()
    {
        if (Deleted)
        {
            return base.OnBeforeDeath();
        }

        BecomeWounded();
        return false;
    }

    private void BecomeWounded()
    {
        Hits = Math.Max(1, HitsMax / 10);
        CurePoison(this);
        Combatant = null;
        Warmode = false;
        Blessed = true;
        _woundedUntil = Core.Now + WoundedFor;
        Emote("*collapses, badly wounded*");

        if (ControlMaster != null)
        {
            AddEvent(ControlMaster, "You were badly wounded in a fight and had to be carried out of it.", "wounded");
        }
    }

    // ---- speech ----------------------------------------------------------------------------------------

    // Exactly or close enough, and not a better fit for someone else nearby (Phase 4e N, one resolver for everyone).
    private bool IsNamed(Mobile from, string text) => NameResolver.Resolve(from, text).Winner == this;

    // Named, or still mid-conversation with her, or - unnamed - she is the obvious listener.
    // A line naming someone else nearby is theirs, even mid-conversation with her (the owner, 2026-10-02).
    // A staff command is never hers (the owner, 2026-10-14).
    // Step B (the owner, chat, 2026-11-08): while the player is talking to someone (Conversation), a line naming nobody is
    // that partner's; a plain order to her from the one she travels with, within 3 tiles, is still hers.
    public bool IsAddressedBy(Mobile from, string text, bool hasKeywords = false) =>
        !TownsfolkTalk.IsStaffCommand(text) &&
        (IsNamed(from, text) ||
         !TownsfolkTalk.NamesSomeoneNearby(from, text, this) &&
         (Conversation.Partner(from) is { } partner
             ? partner == this || IsOwnOrder(from, text)
             : !hasKeywords && IsObviousListener(from)));   // Step B: no 45 s window (Codex r1 finding 2)

    private bool IsOwnOrder(Mobile from, string text) =>
        ControlMaster == from && from.Map == Map && from.InRange(this, UnnamedRange) && PlainOrder(text, Name) != null;

    // the owner, chat, 2026-10-02: answer without her name.
    // Built as decision 1(a): she is travelling with him, within 3 tiles, he is not mid-conversation with a
    // townsperson, and no townsperson or Mirabel stands nearer to him than she does. Real game commands
    // ("bank", "vendor buy", "all follow me" ...) never take this path - they belong to the game.
    private const int UnnamedRange = 3;

    private bool IsObviousListener(Mobile from)
    {
        if (ControlMaster != from || from.Map != Map || !from.InRange(this, UnnamedRange) ||
            TownsfolkTalk.InConversation(from))
        {
            return false;
        }

        var mine = from.GetDistanceToSqrt(this);
        foreach (var m in from.GetMobilesInRange(UnnamedRange))
        {
            if (m != this && (m is Mirabel || TownsfolkTalk.IsTownsperson(m)) && from.CanSee(m) &&
                from.GetDistanceToSqrt(m) < mine)
            {
                return false;
            }
        }

        return true;
    }

    // For other AI NPCs (Mirabel): leave this line to the companion it was meant for.
    public static bool IsTalkingToCompanion(Mobile from, string text)
    {
        foreach (var c in _all)
        {
            if (!c.Deleted && c.Map == from.Map && c.InRange(from, HearRange) && c.IsAddressedBy(from, text))
            {
                return true;
            }
        }

        return false;
    }

    public override bool HandlesOnSpeech(Mobile from) => from is PlayerMobile && from.InRange(this, HearRange);

    public override void OnSpeech(SpeechEventArgs e)
    {
        NameResolver.BeginSpeech(e); // one name computation per spoken line, shared by every handler (4e, Codex round 3)
        var from = e.Mobile;

        if (e.Handled || Deleted || from is not PlayerMobile || !from.Alive || from.Map != Map ||
            !from.InRange(this, HearRange))
        {
            return;
        }

        var text = e.Speech?.Trim();
        if (string.IsNullOrEmpty(text) || !IsAddressedBy(from, text, TownsfolkTalk.IsGameCommand(from, e.Keywords)))
        {
            return;
        }

        // Phase 4e: "give me back the sword" from someone who gave her things is handled by the server, not the model.
        if (TryGearRequest(from, text))
        {
            _lastTalk[from] = Core.Now;
            Conversation.Note(from, this, text);   // Step B: the conversation lock
            return;
        }

        // A plain order from the person she travels with is carried out here, whatever the model later picks
        // (the owner, chat, 2026-10-17: she ignored / when the model chose).
        var order = ControlMaster == from ? PlainOrder(text, Name) : null;
        if (order != null)
        {
            ApplyAction(from, order, "plain order");
        }

        var now = Core.Now;
        if (_nextHeard.TryGetValue(from, out var next) && now < next)
        {
            return;
        }

        _nextHeard[from] = now + PerPlayerCooldown;
        _lastTalk[from] = now;
        Conversation.Note(from, this, text);   // Step B: the conversation lock

        if (ControlMaster == null && !CompanionTrust.WillJoin(GetTrust(from)) &&
            (!_nextTalkedRound.TryGetValue(from, out var round) || now >= round))
        {
            _nextTalkedRound[from] = now + CompanionTrust.TalkedRoundCooldown;
            AdjustTrust(from, CompanionTrust.TalkedRound, "talked round");
        }

        if (order != null)
        {
            // The order is already done; the model only answers, and cannot pick a different action.
            _ = RespondAsync(from, text, ["none"], $"{from.Name} just told you to {OrderWords(order)}, and you are already doing it. ");
            return;
        }

        _ = RespondAsync(from, text, AllowedActions(from), null);
    }

    private static readonly Regex NotAnOrder = new(
        @"\b(don'?t|do not|never|stop|no longer|not|shouldn'?t|won'?t|can'?t)\b|\?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex HomeOrder = new(
        @"(^|[,.!;:]\s*)(please\s+|you can\s+|you may\s+)?(go|head|get|return)\s+(back\s+)?home\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex WaitOrder = new(
        @"\b(wait|stay)\s+(right\s+)?(here|there|put|behind|for me|a (moment|minute|bit|while))\b|\bhold (here|on here|this spot)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex FollowOrder = new(
        @"\bfol+ow(ing)?\b|\bcome (with|along with) me\b|\bcome along\b|\blet'?s go\b|\blet us go\b|\bhead out\b|\bstay (close|with me|near me|by me)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex BareWait = new(
        @"^\W*(please\W+)?(wait|stay)(\W+please)?\W*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // follow / wait / go_home when the line plainly says exactly one of them; null leaves it to the model.
    // A negative or a question ("don't follow me", "will you wait?") is never treated as an order.
    public static string PlainOrder(string text, string name = null)
    {
        if (string.IsNullOrWhiteSpace(text) || NotAnOrder.IsMatch(text))
        {
            return null;
        }

        // the line without her name and filler, so "Elspeth, go home" starts with the order
        var filler = string.IsNullOrEmpty(name) ? "" : Regex.Escape(name) + "|";
        var bare = Regex.Replace(text, $@"\b({filler}okay|ok|alright|now|then)\b", "", RegexOptions.IgnoreCase)
            .Trim(' ', ',');
        var home = HomeOrder.IsMatch(bare);   // only at the start of a sentence: not "I want to go home someday"
        var follow = FollowOrder.IsMatch(text); // includes "stay close"
        var wait = WaitOrder.IsMatch(text) || BareWait.IsMatch(bare);

        var count = (home ? 1 : 0) + (follow ? 1 : 0) + (wait ? 1 : 0);
        if (count != 1)
        {
            return null;
        }

        return home ? "go_home" : follow ? "follow" : "wait";
    }

    private static string OrderWords(string order) => order switch
    {
        "follow" => "follow them",
        "wait"   => "wait here",
        _        => "go home"
    };

    private string[] AllowedActions(Mobile from)
    {
        if (ControlMaster == from)
        {
            return ["none", "follow", "wait", "go_home"];
        }

        return CanJoin(from, out _) ? ["none", "join"] : ["none"];
    }

    private string DescribeSituation(Mobile from, string extra)
    {
        var sb = new StringBuilder();
        var name = from.Name;
        sb.Append($"The person speaking to you is {name}. ");
        sb.Append(CompanionTrust.Describe(GetTrust(from), name)).Append(' ');

        var region = Region?.Name;
        if (!string.IsNullOrEmpty(region))
        {
            sb.Append($"You are in {region}. ");
        }

        if (ControlMaster == from)
        {
            sb.Append(
                ControlOrder == OrderType.Stay
                    ? $"You are travelling with {name}, waiting where they asked you to. "
                    : $"You are travelling with {name}, following them. "
            );
            sb.Append($"When {name} tells you to follow, to wait, or to go home, you do it. ");
        }
        else if (ControlMaster != null)
        {
            sb.Append($"You are travelling with {ControlMaster.Name} and will not leave them for {name}. ");
        }
        else if (CanJoin(from, out var why))
        {
            sb.Append(
                CompanionTrust.Band(GetTrust(from)) is "trusting" or "devoted"
                    ? $"You are not travelling with anyone. If {name} asks you to travel with them, you agree gladly. "
                    : $"You are not travelling with anyone. If {name} asks you to travel with them, you agree, warily, telling them what you expect of them. "
            );
        }
        else
        {
            sb.Append($"You are not travelling with anyone, and you will not travel with {name} now: {why}. ");
        }

        if (IsWounded)
        {
            sb.Append("You are badly wounded and can barely stand. ");
        }

        if (_events.TryGetValue(from, out var events))
        {
            foreach (var (when, text) in events)
            {
                var ago = Core.Now - when;
                var age = ago.TotalMinutes < 2 ? "just now" :
                    ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} minutes ago" : "earlier today";
                sb.Append($"({age}) {text} ");
            }
        }

        if (!string.IsNullOrEmpty(extra))
        {
            sb.Append(extra);
        }

        // Phase 4c1: who she has become with this person (bounded traits from turning points).
        var grown = AiPersonality.Describe(this, from);
        if (grown.Length > 0)
        {
            sb.Append(' ').Append(grown);
        }

        return sb.ToString().Trim();
    }

    private async Task RespondAsync(Mobile from, string text, string[] actions, string extra)
    {
        if (!_history.TryGetValue(from, out var history))
        {
            _history[from] = history = [];
        }

        // An approved reveal that didn't get told (brain/model failure) is told the next time he speaks to her.
        var pending = ControlMaster == from ? AiSecrets.PendingFor(this, from) : null;
        if (pending != null)
        {
            await RevealAsync(from, pending);
            return;
        }

        var request = new AiRequest(_aiId, AiIdentity.GetOrCreate(from), null, text, AiPriority.Companion)
        {
            PersonaKey = PersonaKey,
            History = history.ToArray(),
            Situation = DescribeSituation(from, extra),
            Actions = actions,
            Secrets = AiSecrets.Offers(this, from, GetTrust(from), DateTime.UtcNow),
            Falling = RealWeather.Word(this) // rumour step C3
        };

        Direction = GetDirectionTo(from);

        // A slow reply: after a few seconds she says a short stalling line, and the real answer follows when it
        // arrives. Cancelled the moment the gateway answers, whatever the outcome.
        TimerExecutionToken thinking = default;
        if (AiGateway.ThinkingAfter > TimeSpan.Zero)
        {
            Timer.StartTimer(AiGateway.ThinkingAfter, () => SayThinkingLine(from), out thinking);
        }

        var reply = await AiGateway.RequestAsync(request);
        thinking.Cancel();

        if (Deleted || from.Deleted || from.NetState == null || from.Map != Map || !from.InRange(this, StaleRange))
        {
            return;
        }

        if (reply.Ok)
        {
            Direction = GetDirectionTo(from);
            Say(reply.Text);
            logger.Information(
                "Companion {Key}: {Player} said \"{Said}\" -> she said \"{Reply}\" (model action {Action})",
                _companionKey, from.Name, text, reply.Text, string.IsNullOrEmpty(reply.Action) ? "none" : reply.Action
            );
            Remember(history, "player", text);
            Remember(history, "npc", reply.Text);
            AiLedger.Record(this, from, "conversation", new { player_said = text, she_said = reply.Text });
            _lastTalk[from] = Core.Now;
            if (reply.Action == "reveal_secret")
            {
                StartReveal(from);
            }
            else
            {
                ApplyAction(from, reply.Action);
            }

            return;
        }

        if (reply.Outcome is AiOutcome.Busy or AiOutcome.Disabled)
        {
            return;
        }

        var now = Core.Now;
        if (_nextFallback.TryGetValue(from, out var next) && now < next)
        {
            return;
        }

        _nextFallback[from] = now + FallbackCooldown;
        var emotes = FallbackEmotes;
        Emote(emotes[Utility.Random(emotes.Length)]);
    }

    private void SayThinkingLine(Mobile from)
    {
        var lines = ThinkingLines;
        if (lines.Length == 0 || Deleted || from.Deleted || from.NetState == null || from.Map != Map ||
            !from.InRange(this, StaleRange))
        {
            return;
        }

        // Never the same line twice running.
        var i = Utility.Random(lines.Length);
        if (i == _lastThinkingLine)
        {
            i = (i + 1) % lines.Length;
        }

        _lastThinkingLine = i;
        Direction = GetDirectionTo(from);
        if (IsEmoteLine(lines[i]))
        {
            Emote(lines[i]);
        }
        else
        {
            Say(lines[i]);
        }
    }

    // ---- secrets (Phase 4b) ------------------------------------------------------------------------------
    // Stage 1 was her lead-in ("there's something I've never told anyone"). The shard re-checks and marks it
    // pending (saved), then stage 2 asks the brain to tell that one secret.
    private void StartReveal(Mobile from)
    {
        if (!AiSecrets.TryApprove(this, from, GetTrust(from), DateTime.UtcNow, out var gate))
        {
            logger.Information("Companion {Key}: reveal_secret refused (nothing open for {Player})", _companionKey, from.Name);
            return;
        }

        logger.Information("Companion {Key}: secret tier {Tier} approved for {Player}", _companionKey, gate.Tier, from.Name);
        Timer.StartTimer(TimeSpan.FromSeconds(2), () => _ = RevealAsync(from, gate));
    }

    private async Task RevealAsync(Mobile from, AiSecrets.Gate gate)
    {
        if (!_history.TryGetValue(from, out var history))
        {
            _history[from] = history = [];
        }

        var cue = $"*{from.Name} waits for you to go on*";
        var request = new AiRequest(_aiId, AiIdentity.GetOrCreate(from), null, cue, AiPriority.Companion)
        {
            PersonaKey = PersonaKey,
            History = history.ToArray(),
            Situation = DescribeSituation(from, null),
            RevealSecretId = gate.Id,
            Timeout = TimeSpan.FromSeconds(12)
        };

        var reply = await AiGateway.RequestAsync(request);

        if (!reply.Ok || Deleted || from.Deleted || from.NetState == null || from.Map != Map || !from.InRange(this, StaleRange))
        {
            // Stays pending; told the next time he speaks to her.
            logger.Information("Companion {Key}: secret tier {Tier} still pending ({Outcome})", _companionKey, gate.Tier, reply.Outcome);
            return;
        }

        Direction = GetDirectionTo(from);
        foreach (var part in SplitForSpeech(reply.Text))
        {
            Say(part);
        }

        Remember(history, "player", cue);
        Remember(history, "npc", reply.Text);
        _lastTalk[from] = Core.Now;
        AiSecrets.MarkRevealed(this, from, gate, DateTime.UtcNow);
        AiLedger.Record(this, from, "conversation", new { player_said = cue, she_said = reply.Text });
        AiLedger.Record(this, from, "secret_revealed", new { secret_id = gate.Id, tier = gate.Tier });
    }

    // Overhead speech is short; a few sentences go out one at a time.
    private static List<string> SplitForSpeech(string text)
    {
        var parts = new List<string>();
        var current = "";
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+"))
        {
            if (current.Length > 0 && current.Length + sentence.Length > 160)
            {
                parts.Add(current);
                current = "";
            }

            current = current.Length == 0 ? sentence : current + " " + sentence;
        }

        if (current.Length > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    // The model's pick is a request; this decides.
    private void ApplyAction(Mobile from, string action, string source = "model")
    {
        if (string.IsNullOrEmpty(action) || action == "none")
        {
            return;
        }

        var ok = false;
        switch (action)
        {
            case "join":
                if (from is PlayerMobile && CanJoin(from, out _) && SetControlMaster(from))
                {
                    ControlTarget = from;
                    ControlOrder = OrderType.Follow;
                    _wantFollow = true;
                    Blessed = IsWounded;
                    AddEvent(from, $"You agreed to travel with {from.Name}.", "joined");
                    ok = true;
                }

                break;
            case "follow":
                if (ControlMaster == from)
                {
                    ControlTarget = from;
                    ControlOrder = OrderType.Follow;
                    _wantFollow = true;
                    ok = true;
                }

                break;
            case "wait":
                if (ControlMaster == from)
                {
                    ControlTarget = null;
                    ControlOrder = OrderType.Stay;
                    _wantFollow = false;
                    ok = true;
                }

                break;
            case "go_home":
                if (ControlMaster == from)
                {
                    AddEvent(from, $"{from.Name} sent you home.", "sent_home");
                    GoHome();
                    ok = true;
                }

                break;
        }

        logger.Information(
            "Companion {Key}: action {Action} for {Player} {Result} ({Source})",
            _companionKey, action, from.Name, ok ? "done" : "refused (not valid now)", source
        );
    }

    private static void Remember(List<AiLine> history, string speaker, string text)
    {
        history.Add(new AiLine(speaker, text));
        if (history.Count > HistoryLines)
        {
            history.RemoveRange(0, history.Count - HistoryLines);
        }
    }

    // ---- what she notices -----------------------------------------------------------------------------

    public override void OnThink()
    {
        base.OnThink();

        var tick = Core.TickCount;
        if (tick < _nextThinkTick || Deleted)
        {
            return;
        }

        _nextThinkTick = tick + 1000;
        var now = Core.Now;

        // Wounded: back on her feet after a while. Blessed is saved, so this also repairs it after a restart.
        if (!IsWounded && _woundedUntil != DateTime.MinValue)
        {
            _woundedUntil = DateTime.MinValue;
            Hits = Math.Max(Hits, HitsMax / 2);
            Emote("*gets back to her feet*");
        }

        var owner = ControlMaster;
        var wantBlessed = owner == null || IsWounded;
        if (Blessed != wantBlessed)
        {
            Blessed = wantBlessed;
        }

        if (owner == null)
        {
            MirrorMount(null, false);
            _watchedOwner = null;
            _inFight = false;
            EndSession();

            // Unattached and away from home (e.g. after a restart or a GM move): walk back to her spot.
            if (!IsStabled && !_goingHome && _homeMap != null && _homeMap != Map.Internal &&
                (Map != _homeMap || !InRange(_homeLocation, 20)))
            {
                MoveHome();
            }

            return;
        }

        if (_watchedOwner != owner)
        {
            _watchedOwner = owner;
            _ownerWasAlive = owner.Alive;
            _ownerWasCriminal = owner.Criminal;
            _ownerKills = owner.Kills;
            _lastTogetherCheck = now;
        }

        var near = owner.Map == Map && owner.InRange(this, NearOwnerRange) && owner.NetState != null;
        WatchSession(owner, near, now);
        WatchRegion(owner, near);

        MirrorMount(owner, near);
        KeepToOwnersSide(owner, near);
        WatchOwnerLifeAndDeeds(owner, near);
        WatchFights(owner, near, now);
        AccrueTimeTogether(owner, near, now);
    }

    // A healer, not a skirmisher: if a fight has pulled her away from her companion, she breaks off and goes back.
    // She still defends herself when something reaches her at their side.
    private void KeepToOwnersSide(Mobile owner, bool near)
    {
        if (!_wantFollow || !near || !owner.Alive || IsWounded || InRange(owner, SideRange))
        {
            return;
        }

        if (Combatant == null && ControlOrder == OrderType.Follow)
        {
            return;
        }

        Combatant = null;
        FocusMob = null;
        Warmode = false;
        ControlTarget = owner;
        ControlOrder = OrderType.Follow;
    }

    // The farm plots are dozens of numbered regions ("A Wheatfield in Britain 3"): to her they are one place.
    private static readonly Regex FieldRegion = new(@"^An? .*field in (.+?)( \d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string PlaceName(string region)
    {
        if (string.IsNullOrEmpty(region))
        {
            return region;
        }

        var m = FieldRegion.Match(region);
        return m.Success ? $"the farmland near {m.Groups[1].Value}" : region;
    }

    // Memory of places: one event per named region entered with her companion, at most once a day each.
    private string _lastRegion;
    private readonly Dictionary<string, DateTime> _regionSeen = new();

    private void WatchRegion(Mobile owner, bool near)
    {
        var region = Region;
        while (region != null && string.IsNullOrEmpty(region.Name))
        {
            region = region.Parent;
        }

        var name = PlaceName(region?.Name);
        if (!near || string.IsNullOrEmpty(name) || name == _lastRegion)
        {
            return;
        }

        _lastRegion = name;
        if (_sessionPlaces.Count < MaxSessionPlaces && !_sessionPlaces.Contains(name))
        {
            _sessionPlaces.Add(name);
        }

        if (_regionSeen.TryGetValue(name, out var seen) && Core.Now - seen < TimeSpan.FromHours(20))
        {
            return;
        }

        _regionSeen[name] = Core.Now;
        AiLedger.Record(this, owner, "region_entered", new { region = name });
    }

    // Character deleted: record it once (the brain tombstones that character's memories) and drop the trust entry.
    public static void SweepDeletedCharacters()
    {
        // Phase 4e G4: every gear record must be where it says it is (a deleted giver's gifts go to the holding box).
        Server.Avallone.Companions.CompanionGear.Reconcile("5-minute check");

        foreach (var c in _all)
        {
            List<Mobile> gone = null;
            foreach (var m in c._trust.Keys)
            {
                if (m == null || m.Deleted)
                {
                    (gone ??= []).Add(m);
                }
            }

            if (gone == null)
            {
                continue;
            }

            foreach (var m in gone)
            {
                if (m != null)
                {
                    AiLedger.Record(c, m, "character_deleted");
                    c._history.Remove(m);
                    c._lastTalk.Remove(m);
                    c._events.Remove(m);
                }

                c._trust.Remove(m);
            }

            c.MarkDirty();
        }
    }

    private void WatchOwnerLifeAndDeeds(Mobile owner, bool near)
    {
        if (_ownerWasAlive && !owner.Alive)
        {
            if (near)
            {
                Emote($"*drops to her knees beside {owner.Name}*");
            }

            AddEvent(owner, $"{owner.Name} was killed in front of you.", "owner_died");
            _sessionOwnerDeaths++;
        }
        else if (!_ownerWasAlive && owner.Alive)
        {
            // She is not always at their side when they come back (a healer elsewhere, a GM): she notices when she
            // next sees them alive. Before 2026-10-03 a return she did not witness was never recorded at all.
            _ownerCameBackUnseen = true;
        }

        if (!owner.Alive)
        {
            _ownerCameBackUnseen = false;
        }
        else if (_ownerCameBackUnseen && near)
        {
            _ownerCameBackUnseen = false;
            AiLedger.Record(this, owner, "owner_resurrected", new { text = $"{owner.Name} came back from death." });
            _ = RespondAsync(
                owner,
                $"*{owner.Name} gasps and stirs, alive again*",
                ["none"],
                $"{owner.Name} has just been brought back from death. You are angry first and relieved second."
            );
        }

        _ownerWasAlive = owner.Alive;

        if (near && owner.Criminal && !_ownerWasCriminal)
        {
            Emote($"*glares at {owner.Name}*");
            AddEvent(owner, $"{owner.Name} did something criminal in front of you.", "criminal_act");
            AdjustTrust(owner, CompanionTrust.CriminalAct, "criminal act");
        }

        _ownerWasCriminal = owner.Criminal;

        if (owner.Kills > _ownerKills && near)
        {
            AddEvent(owner, $"{owner.Name} murdered someone innocent in front of you.", "murder");
            AdjustTrust(owner, CompanionTrust.Murder, "murder");
        }

        _ownerKills = owner.Kills;
    }

    // Who they fought, for the memory ("against Karthurg (orc), a mongbat").
    private readonly List<string> _fightFoes = [];

    private void NoteFoe(Mobile m)
    {
        if (m == null || m == this || m is PlayerMobile || _fightFoes.Count >= 5)
        {
            return;
        }

        var name = m.Name ?? "something";
        var foe = name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({m.GetType().Name.ToLowerInvariant()})";
        if (!_fightFoes.Contains(foe))
        {
            _fightFoes.Add(foe);
        }
    }

    private void WatchFights(Mobile owner, bool near, DateTime now)
    {
        if (Combatant != null || near && owner.Combatant != null)
        {
            if (!_inFight)
            {
                _fightFoes.Clear();
                _fightOwnerLowPct = 100;
                _fightHerLowPct = 100;
                _fightHeals = 0;
                _fightStarter = null;
                _fightLastFoe = null;
            }

            _inFight = true;
            _fightLastSeen = now;
            NoteFoe(Combatant);
            NoteFoe(near ? owner.Combatant : null);
            NoteFightState(owner, near);
            return;
        }

        if (!_inFight || now - _fightLastSeen < FightOverAfter)
        {
            return;
        }

        _inFight = false;
        _sessionFights++;
        if (!owner.Alive || _fightOwnerLowPct < CloseCallPct)
        {
            _sessionCloseCalls++;
        }

        if (!owner.Alive || !near || IsWounded)
        {
            return;
        }

        // Richer facts for her memory (the owner, 2026-10-06): how close it was, what she did, who began it, how it ended.
        // All measured here - nothing a player typed.
        AiLedger.Record(
            this, owner, "fight_survived",
            new
            {
                foes = string.Join(", ", _fightFoes),
                owner_hits_pct = owner.Hits * 100 / Math.Max(1, owner.HitsMax),
                owner_low_pct = _fightOwnerLowPct,
                her_low_pct = _fightHerLowPct,
                times_healed = _fightHeals,
                started = _fightStarter ?? "",
                broke_off = _fightLastFoe is { Deleted: false, Alive: true }
            }
        );

        if (now >= _nextHeadCount)
        {
            _nextHeadCount = now + HeadCountCooldown;
            Say(HeadCount(owner));
        }

        if (now >= _nextFightTrust)
        {
            _nextFightTrust = now + CompanionTrust.FightCooldown;
            AdjustTrust(owner, CompanionTrust.FightSurvived, "fight survived together");
        }
    }

    private const int CloseCallPct = 35;
    private int _fightOwnerLowPct = 100;
    private int _fightHerLowPct = 100;
    private int _fightHeals;
    private string _fightStarter; // "owner", "foe", or null when the shard cannot tell
    private Mobile _fightLastFoe;

    // Once a second while a fight lasts: the lowest either of them got, and who struck first.
    private void NoteFightState(Mobile owner, bool near)
    {
        _fightHerLowPct = Math.Min(_fightHerLowPct, Hits * 100 / Math.Max(1, HitsMax));

        if (!near)
        {
            return;
        }

        _fightOwnerLowPct = Math.Min(_fightOwnerLowPct, owner.Alive ? owner.Hits * 100 / Math.Max(1, owner.HitsMax) : 0);

        var foe = owner.Combatant ?? Combatant;
        if (foe == null || foe == this || foe == owner || foe is PlayerMobile)
        {
            return;
        }

        _fightLastFoe = foe;
        _fightStarter ??= Began(owner, foe) ? "owner" : Began(foe, owner) ? "foe" : null;
    }

    // The engine keeps one record per pair, held by whoever attacked first.
    private static bool Began(Mobile attacker, Mobile defender)
    {
        var list = attacker.Aggressed;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Defender == defender)
            {
                return true;
            }
        }

        return false;
    }

    // ---- session summary (richer memory facts, the owner, 2026-10-06) -------------------------------------------
    // One fact when they part: how long, how many fights, how many close calls, how often she bandaged them,
    // where they went. RAM only - a shard restart mid-session starts the count again.
    private const int MaxSessionPlaces = 8;
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(10);
    private Mobile _sessionOwner;
    private DateTime _sessionStart;
    private DateTime _sessionLastSeen;
    private TimeSpan _sessionTogether;
    private int _sessionFights;
    private int _sessionCloseCalls;
    private int _sessionHeals;
    private int _sessionOwnerDeaths;
    private readonly List<string> _sessionPlaces = [];

    private void WatchSession(Mobile owner, bool near, DateTime now)
    {
        // Logged out, a different companion, or she was not thinking for a while (stabled): that session is over.
        if (_sessionOwner != null && (_sessionOwner != owner || owner.NetState == null || now - _sessionLastSeen > SessionGap))
        {
            EndSession();
        }

        if (owner.NetState == null)
        {
            return;
        }

        if (_sessionOwner == null)
        {
            _sessionOwner = owner;
            _sessionStart = now;
            _sessionLastSeen = now;
            _sessionTogether = TimeSpan.Zero;
            _sessionFights = _sessionCloseCalls = _sessionHeals = _sessionOwnerDeaths = 0;
            _sessionPlaces.Clear();
            if (!string.IsNullOrEmpty(_lastRegion))
            {
                _sessionPlaces.Add(_lastRegion);
            }
        }

        if (near)
        {
            _sessionTogether += now - _sessionLastSeen;
        }

        _sessionLastSeen = now;
    }

    private void EndSession()
    {
        var owner = _sessionOwner;
        _sessionOwner = null;

        var minutes = (int)_sessionTogether.TotalMinutes;
        if (owner == null || owner.Deleted || minutes < 2 && _sessionFights == 0)
        {
            return;
        }

        AiLedger.Record(
            this, owner, "session_summary",
            new
            {
                minutes_together = minutes,
                fights = _sessionFights,
                close_calls = _sessionCloseCalls,
                times_healed = _sessionHeals,
                owner_deaths = _sessionOwnerDeaths,
                places = string.Join(", ", _sessionPlaces),
                time_of_day = AiGateway.TimeOfDay(_sessionStart)
            }
        );
    }

    // ---- fight diagnostics (decision 3a, the owner, chat, 2026-10-02) -------------------------------------------
    // the owner. Cause unknown, so log who strikes first - her or the monster -
    // once per opponent, with the owner's state. Drop to Debug once the cause is settled.
    private readonly HashSet<Serial> _firstBlowLogged = [];

    private void LogFirstBlow(Mobile other, bool sheStruck)
    {
        if (ControlMaster == null || other is PlayerMobile || !_firstBlowLogged.Add(other.Serial))
        {
            return;
        }

        if (_firstBlowLogged.Count > 64)
        {
            _firstBlowLogged.Clear();
            _firstBlowLogged.Add(other.Serial);
        }

        var owner = ControlMaster;
        var young = owner is PlayerMobile { Young: true };
        logger.Information(
            "Companion {Key}: first blow {Who} {Other} [{Type}] - owner {Owner} young={Young} ownerFighting={OwnerFight} " +
            "ownerCombatant={OwnerCombatant} her distance to owner {Dist:0.0}, order {Order}",
            _companionKey,
            sheStruck ? "BY HER on" : "ON HER by",
            other.Name,
            other.GetType().Name,
            owner.Name,
            young,
            owner.Combatant != null,
            owner.Combatant?.Name ?? "none",
            owner.Map == Map ? owner.GetDistanceToSqrt(this) : -1,
            ControlOrder
        );
    }

    private bool HasAttacked(Mobile other, bool sheIsAttacker)
    {
        foreach (var info in sheIsAttacker ? Aggressed : Aggressors)
        {
            if ((sheIsAttacker ? info.Defender : info.Attacker) == other)
            {
                return true;
            }
        }

        return false;
    }

    public override void DoHarmful(Mobile target, bool indirect = false)
    {
        // She strikes target; did target strike her before this?
        if (target != null && !Deleted && !HasAttacked(target, false))
        {
            LogFirstBlow(target, true);
        }

        base.DoHarmful(target, indirect);
    }

    public override void AggressiveAction(Mobile aggressor, bool criminal)
    {
        // aggressor strikes her; had she struck it first?
        if (aggressor != null && aggressor != this && !HasAttacked(aggressor, true))
        {
            LogFirstBlow(aggressor, false);
        }

        base.AggressiveAction(aggressor, criminal);
    }

    private static readonly string[] Numbers = ["One", "two", "three", "four", "five", "six", "seven", "eight"];

    // Counts heads out loud after every fight (the owner, chat, 2026-10-01).
    private string HeadCount(Mobile owner)
    {
        var count = 2; // the owner and herself
        if (owner is PlayerMobile pm && pm.AllFollowers != null)
        {
            foreach (var f in pm.AllFollowers)
            {
                if (f != this && f.Alive && f.Map == Map && f.InRange(this, NearOwnerRange))
                {
                    count++;
                }
            }
        }

        count = Math.Min(count, Numbers.Length);
        return $"{string.Join(", ", Numbers[..count])}. Good.";
    }

    private void AccrueTimeTogether(Mobile owner, bool near, DateTime now)
    {
        var elapsed = now - _lastTogetherCheck;
        _lastTogetherCheck = now;

        if (!near || elapsed > TimeSpan.FromMinutes(1))
        {
            return;
        }

        _togetherAccrued += elapsed;
        if (_togetherAccrued < CompanionTrust.TimeTogetherStep)
        {
            return;
        }

        _togetherAccrued = TimeSpan.Zero;

        var today = now.Date;
        var (day, gained) = _timeTogetherToday.TryGetValue(owner, out var t) ? t : (today, 0);
        if (day != today)
        {
            gained = 0;
        }

        if (gained >= CompanionTrust.TimeTogetherDailyCap)
        {
            return;
        }

        _timeTogetherToday[owner] = (today, gained + CompanionTrust.TimeTogether);
        AdjustTrust(owner, CompanionTrust.TimeTogether, "time together");
    }
}
