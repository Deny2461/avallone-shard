using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Server.Avallone.AI;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Rumours;

// Phase 5b (design v2 after Codex, 2026-10-29): townsfolk and Mirabel speak the live rumours.
// The shard decides WHEN gossip may come up and fills in WHO; the brain picks WHAT from the town's live rumours.
// - A rumour block is asked for only on a news question, a follow-up to gossip just told, or (unprompted, the owner decision 2)
//   a greeting / first line - at most once per NPC per real hour, and never a rumour this player heard in the last 2 hours.
//   CHANGED 2026-11-02 (the owner, chat: ->): unprompted
//   gossip only on a real greeting or idle small talk (never any first line, never with a question or request), only
//   about 1 in 4 of those (avallone.rumours.greetPercent), and never from an NPC that does not know them yet (brain:
//   dials familiarity, holds_gossip). Asked-for news is unchanged.
// - The town comes from the NPC's own named region (Mirabel is pinned to Britain); the brain only accepts real town names.
// - The reply carries ONE random sentinel for ONE person; it is replaced here with that character's CURRENT name, or the
//   deed's descriptor if the character is gone. Anything else in sentinel brackets -> the line is not spoken.
// - History never keeps gossip text: the NPC's line is stored as Marker, and the rumour id is kept for a follow-up.
public static class RumourSpeech
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(RumourSpeech));

    public const string Marker = "[shared a piece of town gossip]";
    private const char Open = '⟦';
    private const char Close = '⟧';

    // Levers (settings; a shard restart applies them): how often one NPC may volunteer gossip, and how long a story
    // counts as "heard" by a player (never volunteered again, and damped for "what news", in that window).
    private static TimeSpan NpcUnpromptedGap = TimeSpan.FromHours(1);
    private static TimeSpan PlayerHeardGap = TimeSpan.FromHours(2);
    private static TimeSpan RecentBlock = TimeSpan.FromMinutes(10);   // just heard: never retold on "what news"
    private static readonly TimeSpan RosterAge = TimeSpan.FromSeconds(60);
    private const int RosterMax = 500;

    private static readonly Regex News = new(
        @"\b(news|tidings|gossip|rumou?rs?|what'?s new|anything new|(hear|heard)\s+(any|aught|tell|anything))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    // A line that carries on the gossip just told: "tell me more", "really?", "who was it?", "what happened?"... Anything
    // else is an ordinary turn, the thread ends, and the NPC's real reply stays in its history (dev play test 3).
    private static readonly Regex FollowUp = new(
        // 2026-11-02 (greeting teases): "no", "no, what happened?", "tell me", "what about them?" take the bait.
        @"^\W*((no|nope|nah|not yet|i have not|i haven'?t|i did not|i didn'?t)\b\W*)?" +
        @"(no|nope|nah|not yet|i have not|i haven'?t|tell me|what about (them|it|that one)|what of (them|it)|what'?s that|" +
        @"what talk|what tale|what are they saying|tell me more|more|go on|do tell|and( then)?|then what|what (happened|else|then|next|did (they|that one) do)|" +
        @"who( was it| did it| was that| is that| told you)?|how( so| did (they|that one)| come)?|why|when|where|" +
        @"really|truly|seriously|is (that|it) true|true|no way|wow|oh|huh|indeed|surely|you don'?t say|is that so)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    private static readonly Regex Greeting = new(
        @"^\s*(hail|hello|hi|hey|greetings|well met|good\s+(morning|day|evening|afternoon|morrow)|ho)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    // Idle small talk that may open gossip like a greeting (2026-11-02).
    private static readonly Regex Idle = new(
        @"^\W*(how are you|how fare you|how goes it|how do you do|how('?s| is) (it going|business|trade|life|the day)|" +
        @"(nice|fine|lovely|good) (day|weather|morning|evening)|good to see you)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    // A question or request anywhere in the line: gossip is never tacked onto it (2026-11-02).
    private static readonly Regex Request = new(
        @"\b(buy|sell|train|teach|bank|balance|withdraw|deposit|vendor|stable|claim|repair|heal|cure|resurrect|price|cost|" +
        @"like|want|need|have you|do you|can you|could you|would you|will you|where|show me|give me|help|" +
        // Codex 2b code review (optional): "Hello, why is the sky blue?" is a question, not small talk
        @"why|what|who|when|which|how (much|many|far|long|old|do|does|did|can|could|would|should))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    public static int GreetPercent { get; private set; } = 25;
    // 5c-2 2c (design M7.6): true = the brain makes the one greeting draw (by mood); false = the shard rolls as before
    // (keep false only when running with a pre-2c brain - it does not draw, so every greeting would tease).
    public static bool GreetInBrain { get; private set; } = true;
    internal static Func<int> GreetRoll = () => Utility.Random(100);

    private static readonly Dictionary<Serial, DateTime> _npcUnprompted = new();
    private static readonly Dictionary<(Serial Player, string Id), DateTime> _heard = new();
    private static RumourRosterEntry[] _roster = [];
    private static DateTime _rosterAt;

    public static bool Enabled { get; private set; }

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.speak", true);
        NpcUnpromptedGap = TimeSpan.FromMinutes(
            Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.rumours.unpromptedGapMinutes", 60))
        );
        PlayerHeardGap = TimeSpan.FromMinutes(
            Math.Max(1, ServerConfiguration.GetOrUpdateSetting("avallone.rumours.heardGapMinutes", 120))
        );
        RecentBlock = TimeSpan.FromMinutes(
            Math.Max(0, ServerConfiguration.GetOrUpdateSetting("avallone.rumours.recentBlockMinutes", 10))
        );
        GreetPercent = Math.Clamp(ServerConfiguration.GetOrUpdateSetting("avallone.rumours.greetPercent", 25), 0, 100);
        GreetInBrain = ServerConfiguration.GetOrUpdateSetting("avallone.rumours.greetInBrain", true);
    }

    internal static void SetGreetInBrainForTests(bool on) => GreetInBrain = on;

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        ServerConfiguration.SetSetting("avallone.rumours.speak", on ? "true" : "false");
    }

    // Tests only: switch without touching the saved settings, and forget all caps.
    public static void ResetForTests(bool enabled)
    {
        Enabled = enabled;
        GreetRoll = () => 0;   // tests: the 1-in-4 roll always passes unless a test says otherwise
        GreetInBrain = false;  // tests: the shard-side roll, unless a test says otherwise
        _npcUnprompted.Clear();
        _heard.Clear();
        _rosterAt = default;
    }

    public static bool IsNews(string text) => !string.IsNullOrEmpty(text) && News.IsMatch(text);

    public static bool IsGreeting(string text) => !string.IsNullOrEmpty(text) && Greeting.IsMatch(text);

    // A line that may open unprompted gossip: a greeting or idle small talk, with no question or request in it.
    public static bool IsSmallTalkOpening(string text) =>
        !string.IsNullOrWhiteSpace(text) && (Greeting.IsMatch(text) || Idle.IsMatch(text)) && !Request.IsMatch(text);

    // Short and follow-up shaped (at most 8 words).
    public static bool IsFollowUp(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length <= 8 &&
        FollowUp.IsMatch(text) && !Decline.IsMatch(text);

    // Codex 2b code review (optional): "No thanks, keep it to yourself" declines a tease - it must not reveal the tale.
    private static readonly Regex Decline = new(
        @"\b(no thanks?|thanks,? but|keep it|not interested|never ?mind|don'?t (care|want|tell)|do not (care|want|tell)|" +
        @"rather not|spare me|i'?ll pass|no need|not now|another time)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    // The NPC's own named region ("Britain", "Ocllo" ...), as its persona already uses. The brain accepts only real towns.
    public static string TownOf(Mobile npc)
    {
        var region = npc?.Region;
        while (region != null && string.IsNullOrEmpty(region.Name))
        {
            region = region.Parent;
        }

        return region?.Name;
    }

    // Null = no rumour block for this line: the prompt is exactly today's.
    public static RumourAsk Ask(Mobile npc, Mobile from, string text, bool firstLine, string followId, string town)
    {
        if (!Enabled || npc == null || from is not PlayerMobile || string.IsNullOrEmpty(town))
        {
            return null;
        }

        string mode = null;
        var named = NamedIn(text, out var ambiguous);
        if (named != null || ambiguous)
        {
            mode = "about";   // "Do you know Brynn?" (the owner, chat, 2026-10-30): gossip about that one person
        }
        else if (IsNews(text))
        {
            mode = "news";
        }
        else if (!string.IsNullOrEmpty(followId) && IsFollowUp(text))
        {
            mode = "follow";
        }
        else if (IsSmallTalkOpening(text) && UnpromptedAllowed(npc) && (GreetInBrain || GreetRoll() < GreetPercent))
        {   // firstLine no longer opens gossip by itself (2026-11-02); the parameter stays for the callers
            mode = "greet";   // 2c: with GreetInBrain the brain makes the one draw (by mood) - see RumourAsk.GreetUnrolled
        }

        if (mode == null)
        {
            return null;
        }

        return new RumourAsk(
            town, PlayedClock.Seconds, mode, followId ?? "", HeardRecently(from), Roster(), named?.Id ?? "",
            HeardWithin(from, RecentBlock), ambiguous, mode == "greet" && GreetInBrain
        );
    }

    public static RumourRosterEntry NamedIn(string text) => NamedIn(text, out _);

    // The ONE real player a line names, or null (Codex 5c-1 code review findings 2, round 2 finding 2: never decided by
    // roster order, and judged by WHERE each name occurs). Every whole-word occurrence of a full name, and of the first
    // name (4+ letters) of a two-part name, is a span; a span inside a longer full-name span is the same mention and is
    // dropped. One character left -> that one; two or more (two people named, or a first name several share) -> null and
    // `ambiguous`: she asks which one.
    public static RumourRosterEntry NamedIn(string text, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var exact = new List<(RumourRosterEntry P, int Start, int End)>();
        var alias = new List<(RumourRosterEntry P, int Start, int End)>();
        foreach (var p in Roster())
        {
            var name = p.Name?.Trim();
            if (name == null || name.Length < 3)
            {
                continue;
            }

            foreach (var at in WordSpans(text, name))
            {
                exact.Add((p, at, at + name.Length));
            }

            var space = name.IndexOf(' ');
            if (space >= 4)
            {
                foreach (var at in WordSpans(text, name[..space]))
                {
                    alias.Add((p, at, at + space));
                }
            }
        }

        bool InsideLongerExact(int start, int end) =>
            exact.Exists(e => e.End - e.Start > end - start && e.Start <= start && end <= e.End);

        var people = new HashSet<RumourRosterEntry>();
        foreach (var e in exact)
        {
            if (!InsideLongerExact(e.Start, e.End))
            {
                people.Add(e.P);
            }
        }

        foreach (var a in alias)
        {
            // the first name inside a full-name mention is that mention, never a second person
            if (!InsideLongerExact(a.Start, a.End) && !exact.Exists(e => e.Start <= a.Start && a.End <= e.End))
            {
                people.Add(a.P);
            }
        }

        if (people.Count == 1)
        {
            foreach (var only in people)
            {
                return only;
            }
        }

        ambiguous = people.Count > 1;
        return null;
    }

    private static List<int> WordSpans(string text, string word)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + word.Length;
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                found.Add(i);
            }
        }

        return found;
    }

    // The line to speak, or null if it must not be spoken. `info` null = no rumour was offered. `asked` = what this
    // request asked the brain for: a reply carrying gossip for another mode, or gossip nobody asked for, is not spoken
    // (Codex code review 2026-10-30, optional 5: bind the reply to request-side state).
    public static string Fill(Mobile npc, Mobile from, AiReply reply, string town, RumourAsk asked = null, bool bind = false)
    {
        var text = reply.Text ?? "";
        var info = reply.Rumour;
        if (info == null)
        {
            return text.IndexOf(Open) >= 0 || text.IndexOf(Close) >= 0 ? null : text;
        }

        if (bind && (asked == null || info.Mode != asked.Mode))
        {
            logger.Warning("Rumour: reply from {Npc} carried gossip ({Mode}) that was not asked for - not spoken", npc.Name, info.Mode);
            return null;
        }

        if (!string.IsNullOrEmpty(info.Sentinel))
        {
            var rest = text.Replace(info.Sentinel, "");
            if (rest.IndexOf(Open) >= 0 || rest.IndexOf(Close) >= 0)
            {
                logger.Warning("Rumour: reply from {Npc} carried a sentinel it was not given - not spoken", npc.Name);
                return null;
            }

            text = text.Replace(info.Sentinel, CurrentName(info));
        }
        else if (text.IndexOf(Open) >= 0 || text.IndexOf(Close) >= 0)
        {
            return null;
        }

        // Codex finding 2: the brain now flags EVERY reply that had live gossip in its prompt (history keeps only the
        // marker), with Ids = only the rumours the reply really told. Caps and the heard timer follow Ids.
        var now = Core.Now;
        var told = info.Ids is { Length: > 0 };
        if (!told)
        {
            logger.Debug("Rumour: {Npc} ({Town}) had gossip in mind for {Player} ({Mode}) but did not tell it", npc.Name, town, from.Name, info.Mode);
            return text;
        }

        if (info.Mode == "greet")
        {
            _npcUnprompted[npc.Serial] = now;
        }

        foreach (var id in info.Ids)
        {
            _heard[(from.Serial, id)] = now;
        }

        Prune(now);
        logger.Information(
            "Rumour: {Npc} ({Town}) answered {Player} with gossip {Ids} ({Mode})",
            npc.Name,
            town,
            from.Name,
            string.Join(",", info.Ids ?? []),
            info.Mode
        );
        return text;
    }

    // What a reply that used gossip leaves in the NPC's history, and the id kept for a follow-up.
    public static string HistoryText(AiReply reply, string spoken) => reply.Rumour != null ? Marker : spoken;

    public static string FollowId(AiReply reply) => reply.Rumour?.Ids is { Length: > 0 } ids ? ids[0] : null;

    // With speaking off, gossip markers leave the history too: the prompt is then today's prompt.
    public static AiLine[] History(IReadOnlyList<AiLine> history)
    {
        if (Enabled)
        {
            return [..history];
        }

        var list = new List<AiLine>(history.Count);
        foreach (var line in history)
        {
            if (line.Text != Marker)
            {
                list.Add(line);
            }
        }

        return list.ToArray();
    }

    private static string CurrentName(RumourInfo info) =>
        Guid.TryParse(info.CharId, out var id) && AiIdentity.TryGetPlayer(id, out var pm)
            ? pm.Name
            : string.IsNullOrWhiteSpace(info.Descriptor) ? "someone" : info.Descriptor;

    private static bool UnpromptedAllowed(Mobile npc) =>
        !_npcUnprompted.TryGetValue(npc.Serial, out var last) || Core.Now - last >= NpcUnpromptedGap;

    private static string[] HeardRecently(Mobile from) => HeardWithin(from, PlayerHeardGap);

    private static string[] HeardWithin(Mobile from, TimeSpan window)
    {
        var now = Core.Now;
        var ids = new List<string>();
        foreach (var ((player, id), at) in _heard)
        {
            if (player == from.Serial && now - at < window)
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    // Every current player character's name (bounded), for the brain's reply check only - never put in a prompt.
    // Phase 5c: the same bounded roster, for scrubbing player names out of visit notes.
    public static RumourRosterEntry[] CurrentRoster() => Roster();

    private static RumourRosterEntry[] Roster()
    {
        var now = Core.Now;
        if (now - _rosterAt < RosterAge)
        {
            return _roster;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();   // Codex optional 1: measure the game-thread walk
        var list = new List<RumourRosterEntry>();
        foreach (var m in World.Mobiles.Values)
        {
            if (m is PlayerMobile { Deleted: false } pm && !string.IsNullOrWhiteSpace(pm.Name))
            {
                list.Add(new RumourRosterEntry(AiIdentity.TryGet(pm, out var id) ? id.ToString() : null, pm.Name));
                if (list.Count >= RosterMax)
                {
                    break;
                }
            }
        }

        _roster = list.ToArray();
        _rosterAt = now;
        watch.Stop();
        if (watch.ElapsedMilliseconds >= 20)
        {
            logger.Warning("Rumour: roster walk took {Ms} ms ({Count} players)", watch.ElapsedMilliseconds, _roster.Length);
        }
        else
        {
            logger.Debug("Rumour: roster walk {Ms} ms ({Count} players)", watch.Elapsed.TotalMilliseconds, _roster.Length);
        }

        return _roster;
    }

    private static void Prune(DateTime now)
    {
        if (_heard.Count > 2000)
        {
            var old = new List<(Serial, string)>();
            foreach (var (k, at) in _heard)
            {
                if (now - at >= PlayerHeardGap || World.FindMobile(k.Player) is not { Deleted: false })
                {
                    old.Add(k);
                }
            }

            foreach (var k in old)
            {
                _heard.Remove(k);
            }
        }

        if (_npcUnprompted.Count > 2000)
        {
            var old = new List<Serial>();
            foreach (var (k, at) in _npcUnprompted)
            {
                if (now - at >= NpcUnpromptedGap)
                {
                    old.Add(k);
                }
            }

            foreach (var k in old)
            {
                _npcUnprompted.Remove(k);
            }
        }
    }

    public static string Describe() =>
        $"speaking {(Enabled ? "ON" : "OFF")} (unprompted gap {NpcUnpromptedGap.TotalMinutes:0} min, heard window {PlayerHeardGap.TotalMinutes:0} min); {_heard.Count} heard, {_npcUnprompted.Count} NPC cooldowns, roster {_roster.Length}";
}
