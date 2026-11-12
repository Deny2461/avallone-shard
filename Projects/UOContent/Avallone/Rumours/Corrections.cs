using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Server.Avallone.AI;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Rumours;

// Rumour step, Part A (design: Projects/Avallone-Rumour-Step-Design.md sections 3 and 12; the owner, chat, 2026-11-11
// - details only: how many, what, where; a murder or a death cannot be
// talked away).
//
// The shard decides WHETHER a correction counts and whether it is RECORDED; the brain only reads the player's line and
// offers a candidate.
//   1. TICKET. After a reply was really said, a ticket is kept only if that reply really told exactly ONE story about
//      the player it was said to, of a kind that shows a detail (or a murder / death, so the NPC can be told the tale
//      stands). One ticket per NPC + player.
//   2. USE. The player's NEXT line to that NPC uses the ticket up, whatever the line is - if it is the very next thing
//      the player said and within 120 s. The order number is fixed HERE, not when the reply finishes.
//   3. COMMIT. When the reply comes back with a candidate, everything is checked again on the game thread: the ticket,
//      the owner, the story, the details that kind shows, the closed lists, a newer accepted change, the hourly limit,
//      the brain's capability - and the record goes into the saved outbox. ONLY THEN is the accepting line spoken;
//      otherwise the ordinary (neutral) reply is, and nothing is recorded.
// Nothing of the player's sentence is ever put in the record: a number, a creature kind from the list, a place from the
// list.
public static class Corrections
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Corrections));

    public const string Kind = "rumour_corrected";
    public const int MaxCount = 999;
    public const int PerPlayedHour = 3;
    private static readonly TimeSpan TicketLife = TimeSpan.FromSeconds(120);

    public sealed record Ticket(
        Serial Player, Guid CharId, Serial Npc, int Revision, string Story, string Type, string[] Fields, DateTime At
    );

    // A ticket that one request has used up. Order = the shard's own counter at that moment.
    public sealed record Use(Ticket Ticket, long Order, int Revision);

    // Which details each kind of deed shows (the brain holds the same table in corrections.py). A kind that is not here
    // gets no ticket.
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["kill_notable"] = ["count", "thing", "place"],
        ["tame_notable"] = ["count", "thing", "place"],
        ["tame_failstreak"] = ["thing", "place"],
        ["pacified"] = ["thing", "place"],
        ["provoked"] = ["count", "thing", "place"],
        ["fished_boots"] = ["count"],
        ["pet_died"] = ["count", "thing"],
        ["drunk"] = ["place"]
    };

    // S4: a murder or a death takes no correction. A ticket is still kept (no fields), so the NPC is told the tale stands.
    private static readonly HashSet<string> Stands = ["murder", "died_notable", "died_pathetic"];

    // The closed list of places: the towns the rumours travel between and the dungeons, in their own spelling.
    public static readonly HashSet<string> Places = new(StringComparer.Ordinal)
    {
        "Britain", "Trinsic", "Yew", "Minoc", "Vesper", "Cove", "Moonglow", "Magincia", "Jhelom", "Skara Brae",
        "Serpent's Hold", "Nujel'm", "Haven", "Buccaneer's Den", "Papua", "Delucia", "Luna", "Umbra", "Zento", "Ocllo", "Wind",
        "Ancient Lair", "Ankh Dungeon", "Bedlam", "Blighted Grove", "Blood Dungeon", "Covetous", "Deceit", "Despise",
        "Destard", "Doom", "Exodus Dungeon", "Fan Dancer's Dojo", "Fire", "Hythloth", "Ice", "Khaldun", "Labyrinth",
        "Orc Cave", "Rock Dungeon", "Sanctuary", "Shame", "Sorcerer's Dungeon", "Spectre Dungeon", "Spider Cave",
        "Terathan Keep", "The Citadel", "The Painted Caves", "The Palace of Paroxysmus", "The Prism of Light",
        "Twisted Weald", "Wisp Dungeon", "Wrong", "Yomotsu Mines"
    };

    private static readonly Regex StoryId = new(@"^[1-9]\d{0,11}-[1-9]\d{0,11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<(Serial Npc, Serial Player), Ticket> _tickets = new();
    private static readonly Dictionary<string, long> _newest = new();          // story id -> order of its newest accepted change
    private static readonly Dictionary<Serial, List<long>> _taken = new();     // player -> played seconds of accepted lines
    private static long _order;
    internal static Func<DateTime> Now = () => Core.Now;   // tests move it

    public static bool Taking { get; private set; }
    public static int Issued { get; private set; }
    public static int Accepted { get; private set; }
    public static int Refused { get; private set; }
    public static string LastRefusal { get; private set; } = "-";

    // Taking corrections needs the switch, spoken rumours, and a brain that has SAID it accepts the new record kind.
    public static bool Ready => Taking && RumourSpeech.Enabled && AiLedger.CorrectionsIngest;

    public static void Configure()
    {
        Taking = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.corrections", false);
    }

    // Off = no new ticket, every ticket forgotten, and a candidate still on its way back is refused at Commit.
    public static void SetTaking(bool on)
    {
        Taking = on;
        ServerConfiguration.SetSetting("avallone.rumours.corrections", on ? "true" : "false");
        if (!on)
        {
            _tickets.Clear();
        }
    }

    // The closed list of things: creature kinds of this shard, named as a deed names them ("giant rat"). Hand-picked from
    // the shard's own creature types (KindsOfThisShard) - animals and monsters only, never a person or a personal name.
    // The brain holds the same list (brain/corrections.json); CorrectionsTests compares the two.
    public static readonly HashSet<string> Things = new(StringComparer.Ordinal)
    {
        "abysmal horror", "acid elemental", "agapite elemental", "air elemental", "alligator", "ancient lich",
        "ancient wyrm", "ant lion", "arcane daemon", "arctic ogre lord", "bake kitsune", "balron",
        "beetle", "bird", "black bear", "blood elemental", "boar", "bog thing",
        "bogle", "bogling", "bone demon", "bone knight", "bone magi", "brigand",
        "bronze elemental", "brown bear", "bull", "bull frog", "cat", "centaur",
        "changeling", "chaos daemon", "chicken", "copper elemental", "corpser", "corrosive slime",
        "cougar", "cow", "crane", "crystal elemental", "cu sidhe", "cyclops",
        "daemon", "deathwatch beetle", "deep sea serpent", "demon knight", "desert ostard", "devourer",
        "dire wolf", "dog", "dolphin", "dragon", "drake", "dread spider",
        "eagle", "earth elemental", "efreet", "elder gazer", "energy vortex", "ettin",
        "evil mage", "evil mage lord", "fan dancer", "ferret", "fire beetle", "fire elemental",
        "fire gargoyle", "fire steed", "flesh golem", "forest ostard", "frenzied ostard", "frost ooze",
        "frost spider", "frost troll", "gaman", "gargoyle", "gazer", "ghoul",
        "giant black widow", "giant rat", "giant serpent", "giant spider", "giant toad", "goat",
        "golem", "gorilla", "great hart", "greater dragon", "greater mongbat", "grey wolf",
        "grizzly bear", "harpy", "headless one", "hell cat", "hell hound", "hell steed",
        "hind", "hiryu", "horse", "hydra", "ice elemental", "ice fiend",
        "ice serpent", "ice snake", "imp", "jack rabbit", "juggernaut", "juka lord",
        "juka mage", "juka warrior", "kappa", "kirin", "kraken", "lava lizard",
        "lava serpent", "lava snake", "lesser hiryu", "leviathan", "lich", "lich lord",
        "lizardman", "llama", "minotaur", "mongbat", "mountain goat", "mummy",
        "nightmare", "ogre", "ogre lord", "oni", "ophidian archmage", "ophidian knight",
        "ophidian mage", "ophidian matriarch", "ophidian warrior", "orc", "orc bomber", "orc brute",
        "orc captain", "orcish lord", "orcish mage", "pack horse", "pack llama", "panther",
        "parrot", "phoenix", "pig", "pixie", "plague beast", "poison elemental",
        "polar bear", "rabbit", "raptor", "rat", "ratman", "ratman archer",
        "ratman mage", "reaper", "reptalon", "revenant", "ridgeback", "rotting corpse",
        "rune beetle", "satyr", "savage", "savage rider", "savage shaman", "scorpion",
        "sea horse", "sea serpent", "serpentine dragon", "sewer rat", "shade", "shadow wyrm",
        "sheep", "silver serpent", "skeletal dragon", "skeletal knight", "skeletal mage", "skeleton",
        "slime", "snake", "snow elemental", "snow leopard", "spectre", "squirrel",
        "stone gargoyle", "stone harpy", "succubus", "swamp dragon", "swamp tentacle", "terathan avenger",
        "terathan drone", "terathan matriarch", "terathan warrior", "timber wolf", "titan", "treefellow",
        "troglodyte", "troll", "tropical bird", "tsuki wolf", "unicorn", "valorite elemental",
        "vampire bat", "verite elemental", "vorpal bunny", "wailing banshee", "walrus", "water elemental",
        "whipping vine", "white wolf", "white wyrm", "wisp", "wraith", "wyvern",
        "yamandon", "zombie"
    };

    // Every creature type of this shard as a kind word. Tests only: each listed thing must be one of these.
    internal static HashSet<string> KindsOfThisShard()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeof(BaseCreature).Assembly.GetTypes())
        {
            if (!t.IsAbstract && typeof(BaseCreature).IsAssignableFrom(t))
            {
                kinds.Add(Regex.Replace(t.Name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant());
            }
        }

        return kinds;
    }

    // The details a story of this kind shows; empty for a murder or a death; null = no ticket for this kind.
    public static string[] FieldsFor(string type) =>
        type == null ? null : Stands.Contains(type) ? [] : Table.GetValueOrDefault(type);

    // 1. Right after a reply to `from` was really SAID. Any older ticket for this pair ends here, whatever was said.
    public static void AfterReply(Mobile npc, Mobile from, AiReply reply, int openingRevision)
    {
        if (npc == null || from is not PlayerMobile pm)
        {
            return;
        }

        _tickets.Remove((npc.Serial, pm.Serial));
        var story = reply.Correctable;
        if (!Ready || story == null || reply.Conduct != null || reply.Kind is "decency" or "shop" ||
            reply.Rumour?.Ids is not { Length: 1 } ids || ids[0] != story.Id || !StoryId.IsMatch(story.Id) ||
            Exchange.RevisionOf(pm) != openingRevision || pm.Deleted || !AiIdentity.TryGet(pm, out var charId))
        {
            return; // two stories, a teaser, someone else's or a made-up story, a stale reply: no ticket
        }

        var fields = FieldsFor(story.Type);
        if (fields == null)
        {
            return;
        }

        if (_tickets.Count > 2000)
        {
            Prune();
        }

        _tickets[(npc.Serial, pm.Serial)] = new Ticket(pm.Serial, charId, npc.Serial, openingRevision, story.Id, story.Type, fields, Now());
        Issued++;
        logger.Debug("Correction: ticket for {Player} with {Npc} on story {Story} ({Type})", pm.Name, npc.Name, story.Id, story.Type);
    }

    // 2. When the player's next line to this NPC begins. The ticket is used up whatever comes of it.
    public static Use Take(Mobile npc, Mobile from, int openingRevision)
    {
        if (npc == null || from is not PlayerMobile pm || !_tickets.Remove((npc.Serial, pm.Serial), out var ticket))
        {
            return null;
        }

        if (!Ready || Now() - ticket.At > TicketLife || openingRevision != ticket.Revision + 1 || pm.Deleted ||
            !AiIdentity.TryGet(pm, out var charId) || charId != ticket.CharId)
        {
            return null; // switched off meanwhile, too late, or the player said something else in between
        }

        return new Use(ticket, ++_order, openingRevision);
    }

    public static CorrectionAsk AskFor(Use use) =>
        use == null
            ? null
            : new CorrectionAsk(use.Ticket.Story, use.Ticket.Type, use.Ticket.Fields, PlayedClock.Seconds, RumourSpeech.CurrentRoster());

    // 3. With the reply in hand and a line about to be said. Returns the accepting line when - and only when - the
    // correction is now in the saved outbox; null = say the ordinary reply, nothing was recorded.
    public static string Commit(Mobile npc, Mobile from, Use use, AiReply reply)
    {
        var offer = reply.Correction;
        if (use == null || offer == null || from is not PlayerMobile pm)
        {
            return null;
        }

        var why = Check(pm, use, reply, offer, out var facts);
        if (why == null && !AiLedger.TryRecord(AiLedger.WorldId, pm, Kind, facts))
        {
            why = "the record could not be written (memory off or outbox full)";
        }

        if (why != null)
        {
            Refused++;
            LastRefusal = why;
            logger.Information("Correction: not taken from {Player} on story {Story} - {Why}", pm.Name, use.Ticket.Story, why);
            return null;
        }

        _newest[use.Ticket.Story] = use.Order;
        if (!_taken.TryGetValue(pm.Serial, out var times))
        {
            _taken[pm.Serial] = times = [];
        }

        times.Add(PlayedClock.Seconds);
        Accepted++;
        if (_newest.Count > 5000)
        {
            _newest.Clear(); // bound memory; in-flight requests that old are long dead
        }

        logger.Information(
            "Correction: {Player} corrected story {Story} ({Type}) with {Npc}: {Changes}",
            pm.Name, use.Ticket.Story, use.Ticket.Type, npc?.Name, Describe(offer)
        );
        return offer.Accept;
    }

    private static string Check(PlayerMobile pm, Use use, AiReply reply, CorrectionOffer offer, out Dictionary<string, object> facts)
    {
        facts = null;
        var ticket = use.Ticket;
        if (!Ready)
        {
            return !Taking ? "taking corrections is switched off" : "the brain has not said it accepts corrections";
        }

        if (reply.Conduct != null || reply.Kind is "decency" or "shop" || reply.Rumour != null)
        {
            return "the reply was not a plain correction reply";
        }

        if (Exchange.RevisionOf(pm) != use.Revision)
        {
            return "the player has spoken again since that line";
        }

        if (pm.Deleted || !AiIdentity.TryGet(pm, out var charId) || charId != ticket.CharId)
        {
            return "not the character the story was told to";
        }

        if (offer.Story != ticket.Story)
        {
            return "the candidate names another story";
        }

        var allowed = FieldsFor(ticket.Type);
        if (allowed == null || allowed.Length == 0)
        {
            return "this kind of story takes no correction";
        }

        bool Shows(string field) => Array.IndexOf(allowed, field) >= 0 && Array.IndexOf(ticket.Fields, field) >= 0;

        facts = new Dictionary<string, object> { ["story"] = ticket.Story, ["pt"] = PlayedClock.Seconds };
        if (offer.Count is { } count)
        {
            if (!Shows("count") || count < 1 || count > MaxCount)
            {
                return "the number is not one this story can take";
            }

            facts["count"] = count;
        }

        if (offer.Thing != null)
        {
            if (!Shows("thing") || !Things.Contains(offer.Thing))
            {
                return "the thing is not on the list for this story";
            }

            facts["thing"] = offer.Thing;
        }

        if (offer.Place != null)
        {
            if (!Shows("place") || !Places.Contains(offer.Place))
            {
                return "the place is not on the list for this story";
            }

            facts["place"] = offer.Place;
        }

        if (facts.Count <= 2)
        {
            return "no change";
        }

        if (!Speakable(offer.Accept))
        {
            return "the accepting line failed its check";
        }

        if (_newest.TryGetValue(ticket.Story, out var newest) && newest > use.Order)
        {
            return "a newer correction to this story was accepted first";
        }

        var now = PlayedClock.Seconds;
        if (_taken.TryGetValue(pm.Serial, out var times))
        {
            times.RemoveAll(t => now - t >= 3600 || t > now); // t > now: the played clock was restored to an earlier time
            if (times.Count >= PerPlayedHour)
            {
                return $"{PerPlayedHour} corrections already taken from this player this played hour";
            }
        }

        return null;
    }

    // The accepting line is written by the brain's code from the checked values; it is still checked here like any
    // line an NPC says: no name marker left in it, no real player's name, a sane length.
    private static bool Speakable(string line) =>
        line is { Length: >= 3 and <= 200 } && line.IndexOf('⟦') < 0 && line.IndexOf('⟧') < 0 &&
        RumourSpeech.NamedIn(line, out var ambiguous) == null && !ambiguous;

    private static string Describe(CorrectionOffer o) =>
        string.Join(
            ", ",
            new[] { o.Count is { } c ? $"count {c}" : null, o.Thing != null ? $"thing {o.Thing}" : null, o.Place != null ? $"place {o.Place}" : null }
                .Where(x => x != null)
        );

    private static void Prune()
    {
        var now = Now();
        var old = new List<(Serial, Serial)>();
        foreach (var (k, t) in _tickets)
        {
            if (now - t.At > TicketLife)
            {
                old.Add(k);
            }
        }

        foreach (var k in old)
        {
            _tickets.Remove(k);
        }
    }

    public static string DescribeState() =>
        $"corrections {(Taking ? "ON" : "OFF")} (brain accepts them: {(AiLedger.CorrectionsIngest ? "yes" : "not confirmed")}); " +
        $"{Issued} tickets, {Accepted} taken, {Refused} refused (last: {LastRefusal})";

    internal static void ResetForTests(bool taking)
    {
        Taking = taking;
        _tickets.Clear();
        _newest.Clear();
        _taken.Clear();
        _order = 0;
        Issued = Accepted = Refused = 0;
        LastRefusal = "-";
        Now = () => Core.Now;
    }

    internal static int TicketsForTests => _tickets.Count;
    internal static void SetTakingForTests(bool on) => Taking = on;   // the switch alone, without the saved setting
}
