using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Server.Avallone.AI;
using Server.Items;
using Server.Logging;
using Server.Mobiles;
using Server.Multis;
using Server.Regions;

namespace Server.Avallone.Rumours;

// Phase 5a - the deed feed (design: Projects/Avallone-AI-NPC-Plan.md, "Phase 5", deed list v2).
// The server notices a fixed list of PUBLIC deeds and records each as a "deed" event in the memory ledger under the
// world id, so the existing outbox / ack / rollback reconciliation covers them. The brain turns deeds into rumours.
// Never recorded: conversation, secrets, houses, banks. Every hook in stock code calls one method here, and every
// method here swallows its own errors - a rumour bug must never break a kill, a tame or a death.
public static class Deeds
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Deeds));

    public static bool Enabled { get; private set; }
    public static int NotableFame { get; private set; }
    public static int NotableTameFame { get; private set; }
    public static int TameFailStreak { get; private set; }
    public static int BootsInSession { get; private set; }
    public static int DrunkBac { get; private set; }
    public static int AbsencePlayedHours { get; private set; }

    // Killers so feeble that dying to one is news.
    private static readonly HashSet<string> Pathetic = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rat", "SewerRat", "GiantRat", "Chicken", "Mongbat", "Slime", "Rabbit", "JackRabbit", "Sheep", "Goat",
        "Cat", "Dog", "Bird", "Eagle", "Squirrel", "Ferret", "Pig", "Frog", "BullFrog", "Toad", "Bat", "Snake"
    };

    // One event per (character, type, subject) per window - farming makes one growing rumour in the brain, not a flood.
    private static readonly Dictionary<(Serial, string, string), DateTime> _next = new();
    private static readonly Dictionary<(Serial, Serial), int> _tameFails = new();
    private static readonly Dictionary<Serial, int> _boots = new();

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.deeds", true);
        NotableFame = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.notableFame", 8000);
        NotableTameFame = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.notableTameFame", 10000);
        TameFailStreak = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.tameFailStreak", 10);
        BootsInSession = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.bootsInSession", 3);
        DrunkBac = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.drunkBac", 30);
        AbsencePlayedHours = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.absencePlayedHours", 24);
        EventSink.Connected += m => Guard("login", () => OnLogin(m));
        EventSink.Logout += m =>
        {
            _boots.Remove(m.Serial);
            _next.Keys.Where(k => k.Item1 == m.Serial).ToList().ForEach(k => _next.Remove(k));
            _tameFails.Keys.Where(k => k.Item1 == m.Serial).ToList().ForEach(k => _tameFails.Remove(k));
        };
    }

    public static void Initialize() =>
        logger.Information(
            "Avallone rumours: deeds {State}; notable kill fame {Fame}, notable tame fame {Tame}, tame-fail streak {Streak}, " +
            "boots {Boots}, drunk at BAC {Bac}, absence {Hours} played hours",
            Enabled ? "on" : "OFF", NotableFame, NotableTameFame, TameFailStreak, BootsInSession, DrunkBac, AbsencePlayedHours
        );

    // Set by the rumour call (RumourCommands): false until a brain that understands deeds has answered. A brain that is
    // merely DOWN leaves this alone - events then wait in the saved outbox as usual; only a brain that answers "no
    // such thing" turns it off. Deeds made meanwhile are not recorded: gossip only, companion memory is unaffected.
    public static bool BrainReady { get; private set; }
    private static bool _brainAnswered;

    public static void SetBrainReady(bool ready, string why)
    {
        if (ready != BrainReady || !_brainAnswered)
        {
            _brainAnswered = true;
            BrainReady = ready;
            logger.Information("Avallone rumours: deed recording {State} ({Why})", ready ? "ON" : "PAUSED", why);
        }
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        ServerConfiguration.SetSetting("avallone.rumours.deeds", on ? "true" : "false");
    }

    // Dev only: prove a failing hook never breaks the stock code around it (Codex 2026-10-28 finding 4).
    private static readonly bool _failForTest = Environment.GetEnvironmentVariable("AVALLONE_RUMOUR_FAILTEST") == "1";

    private static void Guard(string what, Action a)
    {
        try
        {
            if (Enabled)
            {
                if (_failForTest)
                {
                    throw new InvalidOperationException("injected test failure");
                }

                a();
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Avallone rumours: deed hook {What} failed (ignored)", what);
        }
    }

    // ---- hooks called from stock code (one line each) ----

    // BaseCreature.OnDeath, first line.
    public static void OnCreatureDeath(BaseCreature bc) => Guard("creature death", () =>
    {
        if (bc.Controlled && bc.IsBonded && bc.ControlMaster is PlayerMobile owner)
        {
            Report(owner, "pet_died", Kind(bc), at: bc); // the kind only, never the name its owner gave it
            return;
        }

        if (bc.Controlled || bc.Summoned || bc.NoKillAwards || bc.Fame < NotableFame)
        {
            return;
        }

        // Credit goes to the top LOOTING-RIGHTS claimant: the stock game's own answer to "whose kill was this" - damage
        // summed per player with their pets' and summons' damage counted as theirs, plus the stock 25% first-hit bonus
        // (Codex code review 2026-10-28 finding 3: the rule is named for what it is, not "raw top damager").
        // Players only; deleted mobiles skipped. Ties: the first in that list.
        PlayerMobile pm = null;
        var best = 0;
        foreach (var ds in BaseCreature.GetLootingRights(bc.DamageEntries, bc.HitsMax))
        {
            if (ds.m_Mobile is PlayerMobile { Deleted: false } p && ds.m_Damage > best)
            {
                pm = p;
                best = ds.m_Damage;
            }
        }

        if (pm != null)
        {
            var byPet = bc.LastKiller is BaseCreature kc && kc.GetMaster() == pm;
            Report(pm, "kill_notable", Kind(bc), extra: new Dictionary<string, object> { ["by_pet"] = byPet }, at: bc);
        }
    });

    // PlayerMobile.OnDeath, after base.OnDeath.
    public static void OnPlayerDeath(PlayerMobile victim) => Guard("player death", () =>
    {
        var killer = victim.LastKiller;
        if (killer is BaseCreature { Controlled: false, Summoned: false } bc)
        {
            if (Pathetic.Contains(bc.GetType().Name))
            {
                Report(victim, "died_pathetic", Kind(bc), at: victim);
            }
            else if (bc.Fame >= NotableFame)
            {
                Report(victim, "died_notable", Kind(bc), at: victim);
            }

            return;
        }
    });

    // PlayerMurderSystem.ReportMurder, after the murder counts: the game's own decision that this was a murder (duels,
    // guild war and fights the victim started never get here). Codex code review 2026-10-28 finding 3.
    public static void OnMurderReported(PlayerMobile victim, PlayerMobile murderer) => Guard("murder", () =>
    {
        if (murderer != victim)
        {
            Report(murderer, "murder", "", at: victim); // no victim name: another player's business
        }
    });

    // AnimalTaming, after a successful tame.
    public static void OnTamed(Mobile tamer, BaseCreature bc, bool alreadyOwned) => Guard("tame", () =>
    {
        if (!alreadyOwned && tamer is PlayerMobile pm && bc.Fame >= NotableTameFame)
        {
            Report(pm, "tame_notable", Kind(bc), at: bc);
        }
    });

    // AnimalTaming, after "You fail to tame the creature."
    public static void OnTameFailed(Mobile tamer, BaseCreature bc) => Guard("tame fail", () =>
    {
        if (tamer is not PlayerMobile pm)
        {
            return;
        }

        var key = (pm.Serial, bc.Serial);
        _tameFails.TryGetValue(key, out var n);
        _tameFails[key] = ++n;
        if (n == TameFailStreak)
        {
            Report(pm, "tame_failstreak", Kind(bc), count: n, at: bc);
        }
    });

    // Fishing.SendSuccessTo, first line.
    public static void OnFished(Mobile from, Item item) => Guard("fishing", () =>
    {
        if (from is not PlayerMobile pm)
        {
            return;
        }

        if (item is MessageInABottle)
        {
            Report(pm, "fished_bottle", "a message in a bottle", at: pm);
        }
        else if (item is BaseShoes)
        {
            _boots.TryGetValue(pm.Serial, out var n);
            _boots[pm.Serial] = ++n;
            if (n >= BootsInSession)
            {
                Report(pm, "fished_boots", "old boots", count: n, at: pm);
            }
        }
    });

    // Beverage, after the drink raised BAC.
    public static void OnDrink(Mobile from) => Guard("drink", () =>
    {
        if (from is PlayerMobile pm && pm.BAC >= DrunkBac)
        {
            Report(pm, "drunk", "", at: pm);
        }
    });

    // Provocation, after Provoke().
    public static void OnProvoked(Mobile bard, BaseCreature a, Mobile b) => Guard("provoke", () =>
    {
        if (bard is PlayerMobile pm && b is BaseCreature { Controlled: false } && (a.Fame >= NotableFame || b.Fame >= NotableFame))
        {
            Report(pm, "provoked", Kind(a), extra: new Dictionary<string, object> { ["other"] = Kind(b) }, at: a);
        }
    });

    // Peacemaking (targeted), after Pacify().
    public static void OnPacified(Mobile bard, BaseCreature bc) => Guard("peace", () =>
    {
        if (bard is PlayerMobile pm && bc.Fame >= NotableFame)
        {
            Report(pm, "pacified", Kind(bc), at: bc);
        }
    });

    private static void OnLogin(Mobile m)
    {
        // Played-time gap (design: "Return of the hero"; the owner, chat, 2026-10-01):
        // hours the world was played - on other characters - while this one was away. Count = played hours.
        if (m is PlayerMobile { AccessLevel: AccessLevel.Player } pm && PlayedClock.TryGetPlayedAbsence(pm, out var away))
        {
            var hours = away / 3600;
            if (hours >= AbsencePlayedHours)
            {
                Report(pm, "returned", "", count: (int)hours, at: pm);
            }
        }
    }

    // ---- recording ----

    // Phase 5b: a dropped deed shows in the normal log (it was Debug-only, so 2026-10-29's quiet evening could not be read),
    // at most once per character + reason every 10 minutes.
    private static readonly Dictionary<(Serial, string), DateTime> _droppedLogged = new();

    private static void Dropped(PlayerMobile pm, string type, string why)
    {
        var key = (pm.Serial, why);
        var now = Core.Now;
        if (_droppedLogged.TryGetValue(key, out var next) && now < next)
        {
            return;
        }

        if (_droppedLogged.Count > 500)
        {
            _droppedLogged.Clear();
        }

        _droppedLogged[key] = now + TimeSpan.FromMinutes(10);
        logger.Information("Avallone rumours: deed {Type} by {Who} not recorded ({Why})", type, pm.Name, why);
    }

    // GM command [Rumour fire / plant, and the hooks above.
    public static bool Report(PlayerMobile pm, string type, string subject, int count = 1,
        Dictionary<string, object> extra = null, IEntity at = null, string text = null, bool bypassCooldown = false)
    {
        if (pm == null || pm.Deleted)
        {
            return false;
        }

        if (pm.AccessLevel > AccessLevel.Player && !bypassCooldown)
        {
            Dropped(pm, type, "staff character"); // staff do not make rumours by accident
            return false;
        }

        var key = (pm.Serial, type, subject ?? "");
        if (!bypassCooldown && _next.TryGetValue(key, out var until) && Core.Now < until)
        {
            return false;
        }

        if (_next.Count > 2000)
        {
            _next.Keys.Where(k => _next[k] <= Core.Now).ToList().ForEach(k => _next.Remove(k));
        }

        _next[key] = Core.Now + TimeSpan.FromSeconds(60);

        var where = at ?? pm;
        var map = where.Map ?? pm.Map;
        if (!Admit(type, where.Location, map, out var why))
        {
            Dropped(pm, type, $"not admitted: {why}");
            return false;
        }

        if (!IsWord(subject ?? ""))
        {
            logger.Warning("Avallone rumours: deed {Type} subject refused (not a plain word)", type);
            return false;
        }

        if (!BrainReady)
        {
            Dropped(pm, type, "brain has no rumour support");
            return false;
        }

        var origin = Origin(where.Location, map, out var place);
        if (!IsWord(place))
        {
            place = "";
        }

        var facts = new Dictionary<string, object>
        {
            ["type"] = type,
            ["subject"] = subject ?? "",
            ["count"] = count,
            ["who"] = pm.Name ?? "",
            ["descriptor"] = Descriptor(pm),
            ["place"] = place,
            ["map"] = map?.Name ?? "",
            ["x"] = origin.X,
            ["y"] = origin.Y,
            ["pt"] = PlayedClock.Seconds
        };
        if (text != null && type == "gm_planted")
        {
            facts["text"] = text;
        }

        if (extra != null)
        {
            foreach (var (k, v) in extra)
            {
                if (!ExtraKeys.Contains(k) || v is string sv && !IsWord(sv))
                {
                    logger.Warning("Avallone rumours: deed {Type} extra {Key} refused", type, k);
                    return false;
                }

                facts[k] = v;
            }
        }

        AiLedger.RecordWorld(pm, "deed", facts);
        logger.Information(
            "Deed: {Who} {Type} {Subject} x{Count} at {Place} ({Map} {X},{Y}), played {Hours:F2} h",
            pm.Name, type, subject, count, place, map?.Name, origin.X, origin.Y, PlayedClock.Hours
        );
        return true;
    }

    // Extra facts a deed may carry: a yes/no and a creature kind. Anything else is refused (Codex code review finding 4).
    private static readonly HashSet<string> ExtraKeys = new() { "by_pet", "other", "town", "unease" };

    // Maps a rumour can start on. Ilshenar has no towns: its deeds start in the brain's fallback town (Britain - the
    // moongate travellers carry them), and the brain never "drifts" such a deed's place to a town (finding 4).
    private static readonly HashSet<string> RumourMaps = new() { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno" };

    // Creature kinds, region names, town names: letters, spaces, apostrophes and hyphens only, 40 at most - no
    // braces, quotes, digits or control characters can reach the ledger or, later, a prompt.
    private static readonly Regex Word = new(@"^[A-Za-z][A-Za-z' \-]{0,39}$", RegexOptions.Compiled);
    public static bool IsWord(string s) => s.Length == 0 || Word.IsMatch(s);

    // The one gate every deed passes (Codex 2026-10-28 finding 5): default deny.
    // Public deed types only; never inside a house (a player's home is private - this also keeps house locations out
    // of the ledger); never from the internal map (logged-out characters). Facts are an allow-list built in Report:
    // type, subject (a creature kind), count, who, descriptor, place (region name), map, x, y, pt, plus by_pet / other
    // (creature kind) / town (GM plants) / text (GM plants only). No pet names, no item names, no victim names.
    // Witnesses are NOT required: the owner is nearly the only player, so town gossip is knowingly about public
    // places (policy, recorded in the plan; the owner can switch any type off in rumours.json).
    private static readonly HashSet<string> PublicTypes = new()
    {
        "kill_notable", "tame_notable", "died_notable", "murder", "died_pathetic", "tame_failstreak", "fished_boots",
        "fished_bottle", "drunk", "provoked", "pacified", "pet_died", "returned", "gm_planted"
    };

    // What [Rumour fire] accepts (gm_planted has its own command, [Rumour plant).
    public static string[] FireableTypes => PublicTypes.Where(t => t != "gm_planted").OrderBy(t => t).ToArray();

    public static bool Admit(string type, Point3D loc, Map map, out string why)
    {
        if (!PublicTypes.Contains(type))
        {
            why = "not a public deed type";
            return false;
        }

        if (map == null || map == Map.Internal)
        {
            why = "not in the world";
            return false;
        }

        if (!RumourMaps.Contains(map.Name))
        {
            why = "no rumours on this map";
            return false;
        }

        if (type != "gm_planted" && BaseHouse.FindHouseAt(loc, map, 16) != null)
        {
            why = "inside a house";
            return false;
        }

        why = null;
        return true;
    }

    // Where a rumour starts: a dungeon's entrance (dungeons sit in a far-off block of the map), otherwise the spot itself.
    public static Point3D Origin(Point3D loc, Map map, out string place)
    {
        var region = map == null ? null : Region.Find(loc, map);
        var dungeon = region?.GetRegion<DungeonRegion>();
        if (dungeon != null)
        {
            place = dungeon.Name ?? "";
            return dungeon.Entrance != Point3D.Zero ? dungeon.Entrance : loc;
        }

        var named = region;
        while (named != null && string.IsNullOrEmpty(named.Name))
        {
            named = named.Parent;
        }

        place = named?.Name ?? "";
        return loc;
    }

    // A creature's kind, e.g. "dragon" - names like "a dragon" lose the article; unique names stay. Anything a player
    // could have renamed (a pet, or a wild creature that was ever tamed) uses its TYPE instead ("Nightmare" ->
    // "nightmare", "GiantRat" -> "giant rat"): a player-chosen name never becomes a deed fact (finding 4).
    public static string Kind(Mobile m)
    {
        if (m is BaseCreature bc && (bc.Controlled || bc.Owners?.Count > 0) || string.IsNullOrWhiteSpace(m.Name))
        {
            return Regex.Replace(m.GetType().Name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        }

        var n = m.Name.Trim();
        foreach (var article in new[] { "a ", "an ", "the " })
        {
            if (n.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return n[article.Length..];
            }
        }

        return n;
    }

    // How a stranger would describe the character, from the highest of a few telling skills.
    public static string Descriptor(Mobile m)
    {
        (SkillName skill, string word)[] telling =
        [
            (SkillName.AnimalTaming, "a tamer"), (SkillName.Musicianship, "a bard"), (SkillName.Magery, "a mage"),
            (SkillName.Swords, "a swordsman"), (SkillName.Archery, "an archer"), (SkillName.Fencing, "a fencer"),
            (SkillName.Macing, "a brawler"), (SkillName.Healing, "a healer"), (SkillName.Fishing, "a fisher"),
            (SkillName.Blacksmith, "a smith"), (SkillName.Necromancy, "a necromancer")
        ];
        var best = "an adventurer";
        var bestValue = 30.0;
        foreach (var (skill, word) in telling)
        {
            var v = m.Skills[skill].Base;
            if (v > bestValue)
            {
                bestValue = v;
                best = word;
            }
        }

        return best;
    }
}
