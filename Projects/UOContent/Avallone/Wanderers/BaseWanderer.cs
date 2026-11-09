using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModernUO.Serialization;
using Server.Avallone.AI;
using Server.Logging;

namespace Server.Mobiles;

// A wanderer: an NPC who lives on his own (wanderer prototype, 2026-10-14; the owner, ).
// Design: Projects/Avallone-AI-NPC-Plan.md "Wanderer prototype". the owner's decisions: Trammel, a private purse (his gold is
// a number on him, never coins in the economy), no permanent death.
//
// The server decides, the model suggests. The loop is plain code: rest at the inn -> pick a hunting ground -> walk
// there -> hunt -> walk back -> sell at the provisioner -> bank -> rest. The model is asked once per loop, which
// hunting ground (or rest longer), and writes one journal line. Anything else - the model host down, the kill switch off,
// a pick that is not on the list - and the server picks for him. He never stalls waiting on the model.
//
// He fights monsters only: never a player, a pet, a vendor or another AI NPC. With no player near the hunting
// ground the hunt is simulated (kills and wounds rolled on a timer) so he never beats on monsters whose AI is asleep.
// Runtime state (goal, route) is RAM only: after a restart he walks back to the inn and starts again.
[SerializationGenerator(0, false)]
public abstract partial class BaseWanderer : BaseCreature
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(BaseWanderer));

    private static readonly HashSet<BaseWanderer> _all = [];

    public static IReadOnlyCollection<BaseWanderer> All => _all;

    public static bool Enabled { get; private set; }
    public static int FarmPrice { get; private set; } = 20000;
    public static TimeSpan RecoverFor { get; private set; } = TimeSpan.FromMinutes(30);

    private const int JournalLines = 12;
    private const int HearRange = 8;  // by name - the same reach as townsfolk (the owner, 2026-10-14)
    private const int NextToRange = 2; // without his name
    private const int StaleRange = 10;
    private const int HistoryLines = 8;
    private const double RetreatAt = 0.35;

    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(90);
    // 2026-10-15: Bram set out for the south fields at 06:13 UTC and logged nothing for 7+ hours - no arrival,
    // no stuck warning. Cause unknown; a trip that runs this long is cut short and its position logged.
    private static readonly TimeSpan TripLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RoadLogEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerPlayerCooldown = TimeSpan.FromSeconds(3);

    [SerializableField(0)]
    private Guid _aiId;

    // Carried, not banked: lost on death. Loot is what he took (as gold value); the purse is coin from selling it.
    [SerializableField(1)]
    private int _loot;

    [SerializableField(2)]
    private int _purse;

    [SerializableField(3)]
    private int _banked;

    // Newest last, newline-separated. Server facts start with "* "; the rest are his own words.
    [SerializableField(4)]
    private string _journal;

    private enum Goal { Resting, Traveling, Hunting, Recovering }

    private enum Stop { Hunt, Sell, Bank, Rest }

    private Goal _goal = Goal.Traveling;
    private readonly Queue<(WandererPlace Place, Stop Stop)> _route = new();
    private WandererPlace _target;
    private Stop _targetStop = Stop.Rest;
    private WandererPlace _ground;
    private DateTime _until;
    private bool _planning;
    private bool _simulating;
    private DateTime _nextSim;
    private Point3D _lastLocation;
    private DateTime _lastProgress;
    private Mobile _lastCombatant;
    private long _nextTick;
    private DateTime _nextTrace;
    private DateTime _tripStarted;
    private DateTime _nextRoadLog;

    // Dev rig only (AVALLONE_WANDERER_SELFTEST=1): a position line every 30 s while travelling.
    private static readonly bool Verbose = Environment.GetEnvironmentVariable("AVALLONE_WANDERER_SELFTEST") == "1";

    private int _tripKills;
    private int _tripLoot;
    private string _lastTrip;

    private readonly Dictionary<Mobile, List<AiLine>> _history = new();
    private readonly Dictionary<Mobile, DateTime> _nextHeard = new();

    protected BaseWanderer() : base(AIType.AI_Melee, FightMode.Closest, 12, 1)
    {
        _aiId = Guid.NewGuid();
        _journal = "";
        _all.Add(this);
        StartRoute((WandererPlaces.Inn, Stop.Rest));
    }

    // Names a persona file in the brain sidecar (brain/personas/<key>.md).
    public abstract string PersonaKey { get; }

    public virtual string FirstName => Name?.Split(' ')[0] ?? "";

    public WandererPlace TravelTarget => _goal == Goal.Traveling ? _target : null;
    public bool IsRecovering => _goal == Goal.Recovering;

    protected override BaseAI ForcedAI => new WandererAI(this);
    public override bool PlayerRangeSensitive => false; // he keeps living with nobody near
    public override bool CanBeRenamedBy(Mobile from) => false;
    public override bool BardImmune => true;
    public override bool AlwaysAttackable => false;
    public override bool ClickTitle => true;

    public override void GetSpeeds(out double activeSpeed, out double passiveSpeed)
    {
        activeSpeed = 0.2;
        passiveSpeed = 0.4;
    }

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.wanderers.enabled", false);
        FarmPrice = ServerConfiguration.GetOrUpdateSetting("avallone.wanderers.farmPrice", 20000);
        RecoverFor = ServerConfiguration.GetOrUpdateSetting("avallone.wanderers.recoverFor", TimeSpan.FromMinutes(30));
    }

    // Off: wanderers finish nothing, fight nothing, and wait at the inn. On: they pick up from wherever they are.
    public static void SetEnabled(bool enabled, bool persist)
    {
        Enabled = enabled;
        if (persist)
        {
            ServerConfiguration.SetSetting("avallone.wanderers.enabled", enabled ? "true" : "false");
        }

        foreach (var w in _all)
        {
            if (!enabled && !w.IsRecovering)
            {
                w.Combatant = null;
                w.StartRoute((WandererPlaces.Inn, Stop.Rest));
            }
        }

        logger.Information("Avallone wanderers {State}{Persist}", enabled ? "ENABLED" : "DISABLED", persist ? "" : " (until restart)");
    }

    [AfterDeserialization]
    private void AfterDeserialization()
    {
        if (_aiId == Guid.Empty)
        {
            _aiId = Guid.NewGuid();
        }

        _journal ??= "";
        _all.Add(this);

        // A restart mid-recovery must not leave him invisible for good.
        Hidden = false;
        Frozen = false;
        Blessed = false;
        StartRoute((WandererPlaces.Inn, Stop.Rest));
    }

    public override void OnAfterDelete()
    {
        _all.Remove(this);
        base.OnAfterDelete();
    }

    // ---- who he fights -------------------------------------------------------------------------------------

    public override bool IsEnemy(Mobile m)
    {
        if (!Enabled || m is not BaseCreature bc || bc.Deleted || !bc.Alive || bc.GetMaster() != null || bc.Summoned ||
            bc is BaseVendor or BaseCompanion or BaseWanderer || bc.Body.IsHuman || !(bc.AlwaysMurderer || bc.Karma < 0))
        {
            return false;
        }

        // Hunting for real: any monster. Otherwise only what is already attacking him.
        return _goal == Goal.Hunting && !_simulating || bc.Combatant == this;
    }

    // ---- the loop ------------------------------------------------------------------------------------------

    public override void OnThink()
    {
        base.OnThink();

        var tick = Core.TickCount;
        if (tick < _nextTick || Deleted || Map == Map.Internal)
        {
            return;
        }

        _nextTick = tick + 1000;

        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Wanderer {Name}: tick failed in {Goal}", Name, _goal);
        }
    }

    private void Tick()
    {
        var now = Core.Now;

        if (Combatant is PlayerMobile || Combatant is BaseCreature { Controlled: true })
        {
            Combatant = null;
            Warmode = false;
        }

        CountKill();

        switch (_goal)
        {
            case Goal.Recovering:
                {
                    if (now >= _until)
                    {
                        Hidden = false;
                        Frozen = false;
                        Blessed = false;
                        Hits = HitsMax;
                        Emote("*limps out of the healer's, bandaged and grumbling*");
                        StartRest(TimeSpan.FromMinutes(5));
                    }

                    break;
                }
            case Goal.Resting:
                {
                    if (Enabled && !_planning && now >= _until)
                    {
                        _ = PlanAsync();
                    }

                    break;
                }
            case Goal.Traveling:
                {
                    if (Verbose && now >= _nextTrace)
                    {
                        _nextTrace = now + TimeSpan.FromSeconds(30);
                        logger.Information("Wanderer {Name}: at {Location}, heading for {Target}", Name, Location, _target.Key);
                    }

                    if (_tripStarted == default)
                    {
                        _tripStarted = now; // loaded from a save mid-trip
                    }

                    if (now >= _nextRoadLog)
                    {
                        _nextRoadLog = now + RoadLogEvery;
                        logger.Information("Wanderer {Name}: on the road at {Location} to {Target}, {Minutes:0} min out",
                            Name, Location, _target.Key, (now - _tripStarted).TotalMinutes);
                    }

                    if (now - _tripStarted > TripLimit)
                    {
                        logger.Warning("Wanderer {Name}: trip to {Target} took over {Limit} min, at {Location}; skipping ahead",
                            Name, _target.Key, TripLimit.TotalMinutes, Location);
                        MoveToWorld(_target.Location, WandererPlaces.Map);
                        Arrive();
                    }
                    else if (Location != _lastLocation || Combatant != null)
                    {
                        _lastLocation = Location;
                        _lastProgress = now;
                    }
                    else if (now - _lastProgress > StuckAfter)
                    {
                        // A route the pathfinder cannot walk. Logged so the place list can be fixed; he does not stall.
                        logger.Warning(
                            "Wanderer {Name}: stuck at {Location} on the way to {Target}; skipping ahead",
                            Name, Location, _target.Key
                        );
                        MoveToWorld(_target.Location, WandererPlaces.Map);
                        Arrive();
                    }

                    break;
                }
            case Goal.Hunting:
                {
                    Hunt(now);
                    break;
                }
        }
    }

    private void Hunt(DateTime now)
    {
        // Real fights only where a player could see them (the sector is awake); otherwise roll it.
        var wasSimulating = _simulating;
        _simulating = Map?.GetSector(Location).Active != true;
        if (_simulating && !wasSimulating)
        {
            Combatant = null;
            Warmode = false;
        }

        if (_simulating && now >= _nextSim)
        {
            _nextSim = now + TimeSpan.FromSeconds(Utility.RandomMinMax(45, 90));
            if (Utility.RandomDouble() < 0.6)
            {
                var (name, value) = _ground.Quarry[Utility.Random(_ground.Quarry.Length)];
                AddKill(name, value);
                Hits = Math.Max(1, Hits - Utility.RandomMinMax(0, value / 3));
            }
        }

        var hurt = Hits < HitsMax * RetreatAt;
        if ((hurt || now >= _until) && Combatant == null)
        {
            _lastTrip = $"Hunted {_ground.Description.Split(':')[0]}: {_tripKills} kills, {_tripLoot} gold of loot" +
                        (hurt ? ", came back hurt." : ".");
            AddJournal("* " + _lastTrip);
            StartRoute((WandererPlaces.Provisioner, Stop.Sell), (WandererPlaces.Bank, Stop.Bank), (WandererPlaces.Inn, Stop.Rest));
        }
    }

    private void CountKill()
    {
        if (_lastCombatant is BaseCreature v && v != Combatant && (v.Deleted || !v.Alive) && IsEnemyKind(v))
        {
            AddKill(v.Name, Math.Max(5, v.Fame / 20) + Utility.Random(10));
        }

        _lastCombatant = Combatant;
    }

    private static bool IsEnemyKind(BaseCreature v) => v.GetMaster() == null && (v.AlwaysMurderer || v.Karma < 0);

    private void AddKill(string name, int value)
    {
        _tripKills++;
        _tripLoot += value;
        _loot += value;
        logger.Information("Wanderer {Name}: killed {Victim} (+{Value}){Sim}", Name, name, value, _simulating ? " [simulated]" : "");
    }

    // Called by the AI when he reaches the current target (or by the stuck watchdog).
    public void Arrive()
    {
        if (_goal != Goal.Traveling || _target == null)
        {
            return;
        }

        logger.Information("Wanderer {Name}: arrived at {Target} ({Location})", Name, _target.Key, Location);

        switch (_targetStop)
        {
            case Stop.Hunt:
                {
                    _ground = _target;
                    _tripKills = 0;
                    _tripLoot = 0;
                    Home = _ground.Location;
                    RangeHome = _ground.Range;
                    _goal = Goal.Hunting;
                    _until = Core.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(10, 20));
                    _nextSim = Core.Now + TimeSpan.FromSeconds(30);
                    break;
                }
            case Stop.Sell:
                {
                    _purse += _loot;
                    _loot = 0;
                    NextLeg();
                    break;
                }
            case Stop.Bank:
                {
                    _banked += _purse;
                    _purse = 0;
                    NextLeg();
                    break;
                }
            default:
                {
                    StartRest(TimeSpan.FromMinutes(Utility.RandomMinMax(5, 10)));
                    break;
                }
        }
    }

    // [Wanderer send <hunt> and the dev rig: walk to that hunt now and work it, then the usual sell/bank/rest.
    public void SendTo(WandererPlace hunt)
    {
        _route.Clear();
        _route.Enqueue((hunt, Stop.Hunt));
        NextLeg();
        AddJournal($"* Set out for {hunt.Description.Split(':')[0]}.");
    }

    private void StartRoute(params (WandererPlace Place, Stop Stop)[] legs)
    {
        _route.Clear();
        foreach (var leg in legs)
        {
            _route.Enqueue(leg);
        }

        NextLeg();
    }

    private void NextLeg()
    {
        if (!_route.TryDequeue(out var leg))
        {
            StartRest(TimeSpan.FromMinutes(5));
            return;
        }

        (_target, _targetStop) = leg;
        _goal = Goal.Traveling;
        Home = _target.Location;
        RangeHome = _target.Range;
        _lastLocation = Location;
        _lastProgress = Core.Now;
        _tripStarted = Core.Now;
        _nextRoadLog = Core.Now + RoadLogEvery;
    }

    private void StartRest(TimeSpan forHowLong)
    {
        _goal = Goal.Resting;
        _target = null;
        Home = WandererPlaces.Inn.Location;
        RangeHome = WandererPlaces.Inn.Range;
        _until = Core.Now + forHowLong;
    }

    // ---- choosing the next goal ------------------------------------------------------------------------------

    private async Task PlanAsync()
    {
        _planning = true;
        try
        {
            var options = WandererPlaces.HuntKeys.Append("rest").ToArray();
            string pick = null;
            string line = null;

            if (AiGateway.Enabled)
            {
                var request = new AiRequest(_aiId, _aiId, null, "(deciding what to do next)", AiPriority.Background)
                {
                    PersonaKey = PersonaKey,
                    Mode = "plan",
                    Timeout = TimeSpan.FromSeconds(8), // background: worth waiting for (the brain caps budgets at 8 s)
                    Situation = PlanSituation(),
                    Actions = options
                };

                var reply = await AiGateway.RequestAsync(request);
                if (Deleted || _goal != Goal.Resting)
                {
                    return;
                }

                if (reply.Ok && options.Contains(reply.Action))
                {
                    pick = reply.Action;
                    line = reply.Text == "-" ? null : reply.Text;
                }
                else
                {
                    logger.Information("Wanderer {Name}: model gave no usable plan ({Outcome}); choosing myself", Name, reply.Outcome);
                }
            }

            pick ??= FallbackPick();

            if (line != null)
            {
                AddJournal(line);
            }

            if (pick == "rest")
            {
                AddJournal("* Rested longer at the inn.");
                StartRest(TimeSpan.FromMinutes(Utility.RandomMinMax(5, 10)));
                return;
            }

            var ground = WandererPlaces.FindHunt(pick);
            AddJournal($"* Set out for {ground.Description.Split(':')[0]}.");
            StartRoute((ground, Stop.Hunt));
        }
        finally
        {
            _planning = false;
        }
    }

    // Without the model: rest if hurt, otherwise any ground but the last one if that trip came back hurt.
    private string FallbackPick()
    {
        if (Hits < HitsMax * 0.8)
        {
            return "rest";
        }

        var grounds = WandererPlaces.Hunts.Where(h => h != _ground || _lastTrip?.EndsWith("hurt.") != true).ToArray();
        return grounds[Utility.Random(grounds.Length)].Key;
    }

    private string PlanSituation()
    {
        var sb = new StringBuilder();
        sb.Append($"It is {AiGateway.TimeOfDay(Core.Now)}. You are at the inn in Britain. ");
        sb.Append($"Health: {Hits * 100 / Math.Max(1, HitsMax)}%. ");
        sb.Append($"Banked toward the farm: {_banked} of {FarmPrice} gold.\n");
        if (_lastTrip != null)
        {
            sb.Append($"Last trip: {_lastTrip}\n");
        }

        var recent = RecentJournal(3);
        if (recent.Length > 0)
        {
            sb.Append("Your recent journal:\n");
            foreach (var l in recent)
            {
                sb.Append("- ").Append(l.TrimStart('*', ' ')).Append('\n');
            }
        }

        sb.Append("Options:\n");
        foreach (var h in WandererPlaces.Hunts)
        {
            sb.Append($"- \"{h.Key}\": go hunting at {h.Description}\n");
        }

        sb.Append("- \"rest\": stay at the inn a while longer\n");
        return sb.ToString();
    }

    // ---- journal ---------------------------------------------------------------------------------------------

    private void AddJournal(string line)
    {
        var lines = (_journal ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.Add($"{Core.Now:yyyy-MM-dd HH:mm}Z {line.Replace('\n', ' ')}");
        if (lines.Count > JournalLines)
        {
            lines.RemoveRange(0, lines.Count - JournalLines);
        }

        _journal = string.Join('\n', lines);
        this.MarkDirty();
        logger.Information("Wanderer {Name} journal: {Line}", Name, line);
    }

    // Newest last, without the timestamp.
    public string[] RecentJournal(int count)
    {
        var lines = (_journal ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Skip(Math.Max(0, lines.Length - count)).Select(l => l.Length > 18 ? l[18..] : l).ToArray();
    }

    public string[] FullJournal() => (_journal ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- never dies ----------------------------------------------------------------------------------------

    public override bool OnBeforeDeath()
    {
        if (Deleted)
        {
            return base.OnBeforeDeath();
        }

        var lost = _loot + _purse;
        var where = _ground?.Description.Split(':')[0] ?? "the road";
        _loot = 0;
        _purse = 0;
        Combatant = null;
        Warmode = false;
        Hits = Math.Max(1, HitsMax / 10);
        Emote("*falls*");

        _route.Clear();
        _target = null;
        _goal = Goal.Recovering;
        _until = Core.Now + RecoverFor;
        Hidden = true;
        Blessed = true;
        Frozen = true;
        MoveToWorld(WandererPlaces.Healer.Location, WandererPlaces.Map);

        _lastTrip = $"Was killed at {where} and lost {lost} gold.";
        AddJournal("* " + _lastTrip);
        return false;
    }

    // ---- speech ----------------------------------------------------------------------------------------------

    // Exactly or close enough, and not a better fit for someone else nearby (Phase 4e N, one resolver for everyone).
    private bool IsNamed(Mobile from, string text) => NameResolver.Resolve(from, text).Winner == this;

    // For the townsfolk, Mirabel and companions: a line naming a wanderer within his hearing is his.
    public static bool IsTalkingToWanderer(Mobile from, string text)
    {
        foreach (var w in _all)
        {
            if (!w.Deleted && !w.Hidden && w.Map == from.Map && w.InRange(from, HearRange) && w.IsNamed(from, text))
            {
                return true;
            }
        }

        return false;
    }

    public override bool HandlesOnSpeech(Mobile from) =>
        from is PlayerMobile && from.InRange(this, HearRange) || base.HandlesOnSpeech(from);

    public override void OnSpeech(SpeechEventArgs e)
    {
        NameResolver.BeginSpeech(e); // one name computation per spoken line, shared by every handler (4e, Codex round 3)
        base.OnSpeech(e);

        var from = e.Mobile;
        var text = e.Speech?.Trim();
        if (e.Handled || Deleted || Hidden || from is not PlayerMobile || !from.Alive || from.Map != Map ||
            !from.InRange(this, HearRange) || string.IsNullOrEmpty(text) || TownsfolkTalk.IsStaffCommand(text) ||
            TownsfolkTalk.IsGameCommand(from, e.Keywords))
        {
            return;
        }

        // Named; or, unnamed, the player is right next to him and the line isn't meant for someone else
        // (the owner, chat, 2026-10-14: don't).
        // Step B: while the player is talking to someone (Conversation), a line naming nobody is that partner's - his
        // from anywhere in hearing if he is the partner, nobody's here if someone else is.
        var named = IsNamed(from, text);
        var partner = Conversation.Partner(from);
        if (!named && (TownsfolkTalk.NamesSomeoneNearby(from, text, this) || BaseCompanion.IsTalkingToCompanion(from, text) ||
                       (partner != null ? partner != this : !from.InRange(this, NextToRange))))
        {
            return;
        }

        var now = Core.Now;
        if (_nextHeard.TryGetValue(from, out var next) && now < next)
        {
            return;
        }

        _nextHeard[from] = now + PerPlayerCooldown;
        Conversation.Note(from, this, text);   // Step B: the conversation lock
        _ = RespondAsync(from, text);
    }

    private async Task RespondAsync(Mobile from, string text)
    {
        if (!_history.TryGetValue(from, out var history))
        {
            if (_history.Count > 32)
            {
                _history.Clear();
                _nextHeard.Clear();
            }

            _history[from] = history = [];
        }

        var request = new AiRequest(_aiId, AiIdentity.GetOrCreate(from), null, text, AiPriority.Conversation)
        {
            PersonaKey = PersonaKey,
            History = history.ToArray(),
            Situation = TalkSituation(),
            Standing = Relations.Standing(this, from) // 5c-2 2b
        };

        Direction = GetDirectionTo(from);
        var reply = await AiGateway.RequestAsync(request);

        if (Deleted || from.Deleted || from.NetState == null || !from.Alive || from.Map != Map || !from.InRange(this, StaleRange))
        {
            return;
        }

        if (reply.Ok)
        {
            Direction = GetDirectionTo(from);
            Say(reply.Text);
            Relations.Visited(this, from); // 5c-2 2b
            Relations.Conduct(this, from, reply);
            Remember(history, "player", text);
            Remember(history, "npc", reply.Text);
        }
        else if (reply.Outcome is not (AiOutcome.Busy or AiOutcome.Disabled))
        {
            Emote("*nods, too busy to talk just now*");
        }
    }

    private string TalkSituation()
    {
        var doing = _goal switch
        {
            Goal.Hunting   => $"hunting at {_ground.Description.Split(':')[0]}",
            Goal.Traveling => $"on your way to {_target.Description.Split(':')[0]}",
            _              => "resting in Britain between jobs"
        };

        var sb = new StringBuilder($"Right now you are {doing}. Banked toward the farm: {_banked} of {FarmPrice} gold.");
        var recent = RecentJournal(4);
        if (recent.Length > 0)
        {
            sb.Append(" Lately: ").Append(string.Join(" ", recent.Select(l => l.TrimStart('*', ' '))));
        }

        return sb.ToString();
    }

    private static void Remember(List<AiLine> history, string speaker, string text)
    {
        history.Add(new AiLine(speaker, text));
        if (history.Count > HistoryLines)
        {
            history.RemoveRange(0, history.Count - HistoryLines);
        }
    }

    // ---- admin -----------------------------------------------------------------------------------------------

    public string Describe()
    {
        var where = _goal switch
        {
            Goal.Hunting    => $"hunting {_ground.Key}{(_simulating ? " (simulated, no player near)" : "")}, {_tripKills} kills so far",
            Goal.Traveling  => $"walking to {_target.Key} ({_targetStop})",
            Goal.Recovering => $"recovering at the healer until {_until:HH:mm}Z",
            _               => $"resting until {_until:HH:mm}Z{(_planning ? ", deciding" : "")}"
        };

        return $"{Name} at {Location}: {where}. HP {Hits}/{HitsMax}. Loot {_loot}, purse {_purse}, banked {_banked}/{FarmPrice}.";
    }

    // Admin: end the current rest or hunt now.
    public void Hurry()
    {
        if (_goal is Goal.Resting or Goal.Hunting)
        {
            _until = Core.Now;
        }
    }
}
