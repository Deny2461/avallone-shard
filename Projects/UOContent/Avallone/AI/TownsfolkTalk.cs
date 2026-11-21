using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Server.Commands;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Gives every ordinary shopkeeper and townsperson a voice (the owner, chat, 2026-10-01).
// One listener on the global speech event, so no vendor class is edited: when a player talks to a nearby
// townsperson, that NPC answers through the AI gateway with a shared template persona (brain/personas/townsfolk.md)
// filled with its name, trade and town - and gets that town's lore. Buying, selling, banking, escorts and every
// other keyword are left to the normal game code. Short memory per NPC + player, in RAM only.
public static class TownsfolkTalk
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TownsfolkTalk));

    private const string PersonaKey = "townsfolk";
    // Named: normal talking distance. Unnamed: within 4 tiles - 3 missed a healer at 3.6 (the owner, chat, 2026-10-02).
    private const int HearRange = 8;
    private const int NearestRange = 4;
    private const int StaleRange = 10;
    private const int HistoryLines = 6;

    private static readonly TimeSpan PerPlayerCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConversationWindow = TimeSpan.FromSeconds(45); // 30 lapsed while the owner typed (chat, 2026-10-07)
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);

    private static readonly Dictionary<(Serial Npc, Serial Player), Talk> _talks = new();
    private static readonly Dictionary<Mobile, DateTime> _nextHeard = new();

    public static bool Enabled { get; private set; }

    private sealed class Talk
    {
        public readonly List<AiLine> History = [];
        public DateTime Last;
        public string FollowRumour; // Phase 5b: the rumour just told, for a follow-up; never its text
    }

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.ai.townsfolk", true);
        GameCommands.BankWordsNeedBanker = ServerConfiguration.GetOrUpdateSetting("avallone.ai.bankWordsNeedBanker", true);
        NamedNewsBeatsCrier = ServerConfiguration.GetOrUpdateSetting("avallone.ai.namedNewsBeatsCrier", true);
        TownCrier.NewsRange = Math.Clamp(ServerConfiguration.GetOrUpdateSetting("avallone.crierNewsRange", 5), 1, 12);
        EventSink.Speech += OnSpeech;
        CommandSystem.Register("Townsfolk", AccessLevel.Administrator, Townsfolk_OnCommand);
    }

    [Usage("Townsfolk [on|off|status]")]
    [Description("Avallone: shopkeepers and townsfolk answer players through the AI gateway. on/off is saved.")]
    public static void Townsfolk_OnCommand(CommandEventArgs e)
    {
        var verb = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "status";
        if (verb is "on" or "off")
        {
            Enabled = verb == "on";
            ServerConfiguration.SetSetting("avallone.ai.townsfolk", Enabled ? "true" : "false");
        }

        e.Mobile.SendMessage($"Townsfolk talk is {(Enabled ? "ON" : "OFF")} (saved); {_talks.Count} conversations remembered.");
    }

    // Who counts: shopkeepers (incl. healers, bankers), escortable townsfolk and the other town characters.
    // Not Mirabel or companions (they have their own personas), not guards, monsters, pets or player vendors.
    // Townsfolk step (design 3.4a, Quarantine): THE shared eligibility decision. An NPC whose seat ownership is
    // unresolved is an ordinary stock vendor - no AI talk, no exchange, no AI id - until an operator settles it.
    public static bool IsTownsperson(Mobile m) =>
        IsTownspersonType(m) && !Server.Avallone.Townsfolk.Seats.IsQuarantined(m);

    // The type test alone (who COULD be an AI townsperson). Seats asks this, never IsTownsperson.
    public static bool IsTownspersonType(Mobile m) =>
        m is BaseCreature { Deleted: false, Alive: true, Controlled: false, Summoned: false } bc &&
        m is not Mirabel && m is not Server.Avallone.Named.INamedNpc && m is not BaseCompanion && bc.Body.IsHuman &&
        m is BaseVendor or BaseEscortable or Actor or Artist or Gypsy or HarborMaster or Sculptor or BrideGroom;

    private static void OnSpeech(SpeechEventArgs e)
    {
        NameResolver.BeginSpeech(e); // one name computation per spoken line, shared by every handler (4e, Codex round 3)
        var from = e.Mobile;

        if (!Enabled || !AiGateway.Enabled || e.Handled || e.Blocked || from is not PlayerMobile || !from.Alive ||
            from.Map == null || from.Map == Map.Internal || e.Type != MessageType.Regular)
        {
            return;
        }

        var text = e.Speech?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (IsStaffCommand(text))
        {
            Skip(from, text, "staff command");
            return;
        }

        // "Zahra buy", "zabra sell": a shop command naming a townsperson vendor (exact or near spelling) opens that
        // vendor's shop, or the vendor says to step closer (dev play test 6, 2026-10-30).
        var shop = ShopTarget(from, text, e.Keywords);
        if (shop != null)
        {
            e.Handled = true;
            if (!from.InRange(shop, Core.AOS ? 1 : 4))
            {
                shop.SayTo(from, "Step up to the counter if you want to trade, traveller.");
            }
            else if (Array.IndexOf(e.Keywords, 0x177) >= 0)
            {
                shop.VendorSell(from);
            }
            else
            {
                shop.VendorBuy(from);
            }

            shop.FocusMob = from;
            Conversation.Note(from, shop, text);   // Step B: the conversation lock
            return;
        }

        // A real game command ("vendor buy", "bank", "train", "destination", "guards" ...) belongs to the game.
        // Other tagged words ("what", "pay", "time" ...) are ordinary speech - see GameCommands.
        if (IsGameCommand(from, e.Keywords, text))
        {
            Skip(from, text, $"game keyword(s) 0x{string.Join(",0x", Array.ConvertAll(e.Keywords, k => k.ToString("X")))}");
            return;
        }

        // Mirabel answers everything near her; a line meant for a companion is the companion's.
        if (BaseCompanion.IsTalkingToCompanion(from, text))
        {
            Skip(from, text, "meant for a companion");
            return;
        }

        if (BaseWanderer.IsTalkingToWanderer(from, text))
        {
            Skip(from, text, "meant for a wanderer");
            return;
        }

        if (MirabelNearby(from) && !IsTownsperson(NameResolver.Resolve(from, text).Winner) &&
            !IsTownsperson(Conversation.Partner(from)))   // Step B: talking to a townsperson by her, it is theirs
        {
            Skip(from, text, "Mirabel is within 4 tiles");
            return;
        }

        var now = Core.Now;
        if (_nextHeard.TryGetValue(from, out var next) && now < next)
        {
            Skip(from, text, "per-player 3 s cooldown");
            return;
        }

        var npc = PickListener(from, text, now);
        if (npc == null)
        {
            Skip(from, text, "no eligible townsperson: " + DescribeNearby(from));
            return;
        }

        _nextHeard[from] = now + PerPlayerCooldown;
        Conversation.Note(from, npc, text);   // Step B: the conversation lock
        if (GameCommands.AnyTrade(e.Keywords))
        {
            e.Handled = true;   // a buying question answered by the AI: a named shopkeeper does not also open its window
        }

        // Step 2e E1 (Codex code R2): ONLY here - every admission check passed and this townsperson is answering - is a
        // named-news line claimed, so the stock crier/barkeeper stay silent for THIS speech event and no other. Off,
        // gateway down, a yell, the cooldown, nobody eligible: never claimed, and local news is answered as before.
        SpeechEventArgs claimed = null;
        if (NamedNewsBeatsCrier && Array.IndexOf(e.Keywords, 0x30) >= 0 && OpensWithTownsperson(from, text) &&
            NameResolver.Resolve(from, text).Winner == npc)
        {
            _claimedNews = claimed = e;
        }

        _ = RespondAsync(npc, from, text, claimed);
    }

    // Real commands, plus three kinds that count only when the NPC acting on them is close by: the greeting and
    // "hire" words (hirelings - verified 2026-10-02: every "hello" to a townsperson was being skipped), "news"
    // (town criers, player barkeepers) and "join"/"resign" (guildmasters, who need to be within 2 tiles).
    // `text` given (townsfolk only): a sentence that only mentions buying/selling is talk, not a command.
    // The townsperson vendor a short "<name> buy" / "<name> sell" command is for, or null. Mirabel handles her own.
    public static BaseVendor ShopTarget(Mobile from, string text, int[] keywords)
    {
        if (from is not PlayerMobile || keywords == null || Array.IndexOf(keywords, 0x171) < 0 && Array.IndexOf(keywords, 0x177) < 0 ||
            Array.IndexOf(keywords, 0x3C) >= 0 || Array.IndexOf(keywords, 0x14D) >= 0 || GameCommands.OnlyTradeTalk(keywords, text))
        {
            return null;
        }

        var r = NameResolver.Resolve(from, text);
        return !r.Ambiguous && r.Match != NameMatch.None && r.Winner is BaseVendor v && IsTownsperson(v) &&
               v.Map == from.Map && from.InRange(v, 8)
            ? v
            : null;
    }

    public static bool IsGameCommand(Mobile from, int[] keywords, string text = null)
    {
        if (keywords is not { Length: > 0 })
        {
            return false;
        }

        // 2026-11-01: a buying/selling line that names nobody nearby is talk too ("I want to buy some bread") - no shop
        // opens without a name, so it used to be silent. See GameCommands.OnlyTradeTalk.
        if (GameCommands.AnyCommand(keywords) &&
            !(text != null && GameCommands.OnlyTradeTalk(keywords, text, NamesSomeoneNearby(from, text))))
        {
            return true;
        }

        foreach (var k in keywords)
        {
            var mine = k switch
            {
                0x3B or 0xC => Near<BaseHire>(from, 6),
                // Step 2e (design §8.8 E1; the owner, chat, 2026-11-12): is the crier's - unless the line opens with
                // the name of one townsperson, unambiguously (the owner's demo: went unanswered).
                // the owner 2026-11-13: the crier's range is a setting (TownCrier.NewsRange, default 5); a player barkeeper only
                // answers news within 3 tiles, so at 4-12 it used to own the word and nobody answered.
                0x30 => (Near<TownCrier>(from, TownCrier.NewsRange) || Near<PlayerBarkeeper>(from, 3)) &&
                        !(NamedNewsBeatsCrier && OpensWithTownsperson(from, text)),
                _ when GameCommands.IsBankWord(k) && GameCommands.BankWordsNeedBanker => Near<Banker>(from, 12),
                0x4 or 0x5  => Near<BaseGuildmaster>(from, 2),
                _           => false
            };

            if (mine)
            {
                return true;
            }
        }

        return false;
    }

    // Step 2e E1 switch (avallone.ai.namedNewsBeatsCrier).
    public static bool NamedNewsBeatsCrier { get; set; } = true;

    // The one speech event whose named news a townsperson took (game thread only; the same SpeechEventArgs object goes to
    // the EventSink handlers first and then to each nearby listener's OnSpeech - Mobile.DoSpeech).
    private static SpeechEventArgs _claimedNews;

    public static bool ClaimedNamedNews(SpeechEventArgs e) => e != null && ReferenceEquals(e, _claimedNews);

    // Codex code R4: the event whose news the FALLBACK already answered. An immediately refused request can fall back while
    // the engine is still handing this same event to nearby listeners; they must not answer it a second time.
    private static SpeechEventArgs _servicedNews;

    // What the stock crier/barkeeper ask: is this event's news someone else's (the AI took it) or already answered?
    public static bool NewsTakenOrServiced(SpeechEventArgs e) =>
        e != null && (ReferenceEquals(e, _claimedNews) || ReferenceEquals(e, _servicedNews));

    internal static void OnSpeechForTests(SpeechEventArgs e) => OnSpeech(e);
    internal static void ClaimForTests(SpeechEventArgs e) => _claimedNews = e;

    private static readonly Regex LeadingWord = new(
        @"^\s*(?:(?:hail|hello|hi|hey|greetings|ho)[\s,]+)?([\p{L}']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    // Step 2e E1 (Codex design R1 Q7): the line's LEADING name (after at most one greeting) resolves, unambiguously and
    // through the one name resolver with its spelling and common-word safeguards, to an eligible townsperson. Merely
    // containing a nearby name is not enough.
    public static bool OpensWithTownsperson(Mobile from, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var named = NameResolver.Resolve(from, text);
        if (named.Ambiguous || named.Winner == null || !IsTownsperson(named.Winner))
        {
            return false;
        }

        var m = LeadingWord.Match(text);
        if (!m.Success)
        {
            return false;
        }

        var lead = m.Groups[1].Value;
        var first = named.Winner.Name?.Split(' ')[0] ?? "";
        if (string.Equals(lead, first, StringComparison.OrdinalIgnoreCase))
        {
            return OpensWithName(text, first, IsCommonWord(first));
        }

        // a near spelling ("Maddy" for Mady): the leading word alone must resolve to the same townsperson
        return named.Match == NameMatch.Near && NameResolver.Resolve(from, lead).Winner == named.Winner;
    }

    private static bool Near<T>(Mobile from, int range) where T : Mobile
    {
        foreach (var m in from.GetMobilesInRange<T>(range))
        {
            if (!m.Deleted)
            {
                return true;
            }
        }

        return false;
    }

    // A staff command ("[Wanderer spawn") is never conversation, even if the command system didn't take it.
    // the owner, chat, 2026-10-14: NPCs must.
    public static bool IsStaffCommand(string text) => text.StartsWith(CommandSystem.Prefix, StringComparison.Ordinal);

    // True if the line names (exactly or one letter off) a townsperson, Mirabel, a wanderer or another companion
    // within talking range - so a companion mid-conversation doesn't take a line meant for them (the owner, 2026-10-02:
    // "hello nell" went to Elspeth; 2026-10-14: "Bram" went to Elspeth). `except` is the NPC asking.
    //
    // Everyone else in range - town criers (TownCrier is a plain Mobile, not a BaseCreature), guards, pets, other
    // players - counts only when the line OPENS with their exact first name: "Ayana, what news" / "hail Ayana". the owner,
    // 2026-10-16: "Ayana, what news" was answered by Ayana AND Elspeth. Codex review 2026-10-16: exact leading name only
    // for these, no near-miss spelling, so "will you follow me" next to a guard named Will is still Elspeth's.
    public static bool NamesSomeoneNearby(Mobile from, string text, Mobile except = null)
    {
        // AI characters (companions, wanderers, townsfolk, Mirabel): the one name resolver (Phase 4e N). A line whose
        // name fits two different characters equally is nobody's - so it is "someone else's" for everyone.
        var named = NameResolver.Resolve(from, text);
        if (named.Ambiguous || named.Winner != null && named.Winner != except)
        {
            return true;
        }

        foreach (var m in from.GetMobilesInRange(HearRange))
        {
            if (m == except || m == from || m.Deleted || !from.CanSee(m))
            {
                continue;
            }

            var first = m.Name?.Split(' ')[0];
            if (string.IsNullOrEmpty(first))
            {
                continue;
            }

            if (NameResolver.IsAiCharacter(m))
            {
                continue; // decided above
            }

            if (m.Alive && OpensWithName(text, first, IsCommonWord(first)))
            {
                return true;
            }
        }

        return false;
    }

    // "Ayana, ...", "Ayana!", "Ayana", "hail Ayana", "hello Ayana, ..." - the name first (after at most one greeting).
    // A name that is also an ordinary word (Will, May, Mark) must be followed by punctuation or the end of the line, so
    // "Will you ..." / "May I ..." / "Mark the map" do not count. Any other name counts without the comma too: the owner,
    // 2026-10-17 - "Ayana what news?" was answered by Elspeth ("you've forgotten my name again").
    public static bool OpensWithName(string text, string first, bool nameIsWord = true) =>
        Regex.IsMatch(
            text,
            $@"^\s*(?:(?:hail|hello|hi|hey|greetings|ho)[\s,]+)?{Regex.Escape(first)}" +
            (nameIsWord ? @"\s*(?:[,!?.:;]|$)" : @"(?![\p{L}'])"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

    // In the near-miss word list (Data/Avallone/words.txt). No list loaded: treat every name as a word (the strict rule).
    public static bool IsCommonWord(string name)
    {
        var dictionary = Words;
        return dictionary.Count == 0 || dictionary.Contains(name.ToLowerInvariant());
    }

    // Diagnostic (2026-10-02, the owner): one line per unanswered player line.
    private static void Skip(Mobile from, string text, string reason) =>
        logger.Information("Townsfolk: no reply to {Player} \"{Text}\" - {Reason}", from.Name, text, reason);

    private static string DescribeNearby(Mobile from)
    {
        var parts = new List<string>();
        foreach (var m in from.GetMobilesInRange(HearRange))
        {
            if (m == from || m is PlayerMobile)
            {
                continue;
            }

            var why = IsTownsperson(m) ? (from.CanSee(m) ? "ok" : "not visible") : "not townsfolk";
            parts.Add($"{m.Name} [{m.GetType().Name}] {from.GetDistanceToSqrt(m):0.0} tiles {why}");
            if (parts.Count >= 6)
            {
                break;
            }
        }

        return parts.Count == 0 ? "nobody within 8 tiles" : string.Join("; ", parts);
    }

    private static bool MirabelNearby(Mobile from)
    {
        foreach (var m in from.GetMobilesInRange<Mirabel>(4))
        {
            if (!m.Deleted)
            {
                return true;
            }
        }

        return false;
    }

    // Named (exact) > named (close spelling) > already talking with this player > the nearest one within 4 tiles.
    // Only ever one answers.
    public static BaseCreature PickListener(Mobile from, string text, DateTime now)   // public for tests
    {
        BaseCreature nearest = null;
        var nearestDist = double.MaxValue;

        // Named (exactly or close enough) by the one resolver: that townsperson, or - if the name is someone else's or
        // fits two people equally - no townsperson at all.
        var resolved = NameResolver.Resolve(from, text);
        if (resolved.Ambiguous)
        {
            return null;
        }

        if (resolved.Winner != null)
        {
            return IsTownsperson(resolved.Winner) ? (BaseCreature)resolved.Winner : null;
        }

        // Step B (the owner, chat, 2026-11-08): the conversation lock. A line naming nobody is the partner's - this
        // townsperson's, or (Mirabel, a companion, a wanderer) no townsperson's at all.
        var partner = Conversation.Partner(from);
        if (partner != null)
        {
            return IsTownsperson(partner) ? (BaseCreature)partner : null;
        }

        foreach (var m in from.GetMobilesInRange(HearRange))
        {
            // No line-of-sight test: it fails across bank and shop counters (verified 2026-10-02, diagnostic log).
            if (!IsTownsperson(m) || !from.CanSee(m))
            {
                continue;
            }

            var bc = (BaseCreature)m;

            var d = from.GetDistanceToSqrt(bc);
            if (d <= NearestRange && d < nearestDist)
            {
                nearest = bc;
                nearestDist = d;
            }
        }

        return nearest;   // Step B: no partner -> the nearest (the old 45 s window is the token's job now)
    }

    // Close spellings count as the name (the owner, chat, 2026-10-02): same first 3 letters, or 1-2 letters off.
    // Guarded so ordinary words don't trigger it: names under 4 letters need an exact match, the word must be
    // Near misses only count within 4 tiles (the owner, chat, 2026-10-02). Tightened the same day after testing showed
    // ordinary words hitting names (there/Thera, horse/Horace, help/Helga): a near miss is now ONE letter off, names
    // of 5+ letters only, and the word must NOT be an English word or common chat slang (Data/Avallone/words.txt,
    // lowercase entries of /usr/share/dict/american-english + slang). If the list is missing, near misses are off.
    private static HashSet<string> _words;

    internal static HashSet<string> WordList => Words;

    private static HashSet<string> Words
    {
        get
        {
            if (_words != null)
            {
                return _words;
            }

            var path = System.IO.Path.Combine(Core.BaseDirectory, "Data", "Avallone", "words.txt");
            try
            {
                _words = new HashSet<string>(System.IO.File.ReadAllLines(path), StringComparer.Ordinal);
                logger.Information("Townsfolk: loaded {Count} words for near-miss names", _words.Count);
            }
            catch (Exception ex)
            {
                _words = [];
                logger.Warning("Townsfolk: no word list at {Path} ({Error}) - near-miss names disabled", path, ex.Message);
            }

            return _words;
        }
    }

    public static bool SpelledLike(MatchCollection words, string name)
    {
        name = name.ToLowerInvariant();
        var dictionary = Words;
        if (name.Length < 5 || dictionary.Count == 0)
        {
            return false;
        }

        foreach (Match w in words)
        {
            var word = w.Value.Trim('\'').ToLowerInvariant();
            if (Math.Abs(word.Length - name.Length) > 1 || dictionary.Contains(word))
            {
                continue;
            }

            if (EditDistance(word, name) == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    // Tests: as if this townsperson had just answered this player (the old 45 s window's record).
    public static void NoteTalkForTests(BaseCreature npc, Mobile player) =>
        _talks[(npc.Serial, player.Serial)] = new Talk { Last = Core.Now };

    // For companions: is this player mid-conversation with a townsperson? Then an unnamed line is theirs.
    // Step B (Codex r1 finding 2): the conversation token decides, not the old 45 s window (which outlived a farewell).
    public static bool InConversation(Mobile player) => IsTownsperson(Conversation.Partner(player));

    // The persona vars a townsperson's request carries (also [Named why], so its numbers match the conversation's).
    internal static Dictionary<string, string> PersonaVarsFor(BaseCreature npc) => new()
    {
        ["name"] = npc.Name ?? "",
        ["role"] = RoleOf(npc),
        ["town"] = TownOf(npc),
        ["trade"] = TradeOf(npc),
        // Townsfolk step, Stage 1 (design 3.7): two plain facts the brain was never told. An older brain's template
        // ignores keys it does not use.
        ["sex"] = npc.Female ? "woman" : "man",
        ["kind"] = npc is BaseEscortable ? "traveller"
            : Server.Avallone.Townsfolk.Seats.TryGetSeat(npc, out _) ? "resident" : "placed"
    };

    internal static string RoleOf(BaseCreature npc)
    {
        var title = npc.Title?.Trim();
        if (!string.IsNullOrEmpty(title))
        {
            return title.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? title[4..] : title;
        }

        return Regex.Replace(npc.GetType().Name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
    }

    private static string TradeOf(BaseCreature npc) =>
        npc switch
        {
            Banker     => "banker",
            BaseHealer => "healer",
            BaseVendor => "vendor",
            _          => ""
        };

    private static string TownOf(Mobile npc)
    {
        var region = npc.Region;
        while (region != null && string.IsNullOrEmpty(region.Name))
        {
            region = region.Parent;
        }

        return region?.Name ?? "the countryside";
    }

    // Step 2e (Codex code R3): if the townsperson took a named-news line but never SAID its reply (any return or error
    // below), the stock crier/barkeeper news is given now, once, under their own rules - local news is never lost.
    private static async Task RespondAsync(BaseCreature npc, Mobile from, string text, SpeechEventArgs claimed = null)
    {
        var spoke = new System.Runtime.CompilerServices.StrongBox<bool>();
        try
        {
            await ReplyAsync(npc, from, text, spoke);
        }
        finally
        {
            if (claimed != null && !spoke.Value)   // set the moment the line is said, so a later error never re-answers
            {
                NewsFallback(claimed);
            }
        }
    }

    // Tests only: whether a player counts as connected (a test player has no client).
    internal static Func<Mobile, bool> Online { get; set; } = m => m.NetState != null;

    // May the reply that has just come back still be SAID and recorded? Checked after the await. Townsfolk step (Codex
    // code R1 F4): the NPC must still be an eligible townsperson NOW - a quarantine that began while the request was
    // out stops the line, its correction and every record that would follow it.
    internal static bool CanDeliver(BaseCreature npc, Mobile from) =>
        !(npc.Deleted || !npc.Alive || from.Deleted || !Online(from) || from.Map != npc.Map ||
          !from.InRange(npc, StaleRange)) && IsTownsperson(npc);

    internal static Task RespondForTests(BaseCreature npc, Mobile from, string text, SpeechEventArgs claimed) =>
        RespondAsync(npc, from, text, claimed);

    internal static int NewsFallbacks { get; private set; }

    internal static void NewsFallback(SpeechEventArgs e)
    {
        if (ReferenceEquals(_claimedNews, e))
        {
            _claimedNews = null;
        }

        _servicedNews = e;   // answered here, once: the engine's own dispatch of this event (if still to come) skips it
        var from = e?.Mobile;
        if (from == null || from.Deleted || from.Map == null || from.Map == Map.Internal)
        {
            return;
        }

        NewsFallbacks++;
        foreach (var m in from.GetMobilesInRange(12))
        {
            switch (m)
            {
                case TownCrier crier:
                    crier.TryAnswerNews(from);
                    break;
                case PlayerBarkeeper keeper:
                    keeper.TryAnswerNews(from);
                    break;
            }
        }
    }

    // Stage 2 repair, decision 9 (the owner, chat, 2026-11-17): the speaking player's own name and every player name the gossip
    // roster knows (RumourSpeech.CurrentRoster: cached 60 s, at most 500) - for the brain's name checks only, never a prompt.
    internal static NameCheckAsk NameCheckFor(Mobile from)
    {
        var roster = Server.Avallone.Rumours.RumourSpeech.CurrentRoster();
        var names = new List<string>(roster.Length);
        foreach (var entry in roster)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                names.Add(entry.Name);
            }
        }

        return new NameCheckAsk(from?.Name ?? "", names.ToArray());
    }

    // The reply itself; true only once the townsperson has SAID it.
    private static async Task<bool> ReplyAsync(BaseCreature npc, Mobile from, string text,
        System.Runtime.CompilerServices.StrongBox<bool> spoke)
    {
        var key = (npc.Serial, from.Serial);
        var firstLine = !_talks.TryGetValue(key, out var talk) || Core.Now - talk.Last > ConversationWindow;
        if (talk == null)
        {
            Prune();
            _talks[key] = talk = new Talk();
        }

        talk.Last = Core.Now;
        var town = TownOf(npc);
        var follow = talk.History.Count > 0 && talk.History[^1].Text == Server.Avallone.Rumours.RumourSpeech.Marker ? talk.FollowRumour : null;

        // Codex A2-R1: the player's speech count at THIS turn. If they say anything else before the reply is delivered,
        // that reply may still be said but can no longer start an exchange.
        var openingRevision = Exchange.RevisionOf(from);
        // Rumour step, Part A: this line uses up the correction ticket, if the last reply left one (Corrections.cs).
        var fix = Server.Avallone.Rumours.Corrections.Take(npc, from, openingRevision);

        var request = new AiRequest(AiIdentity.GetOrCreate(npc), AiIdentity.GetOrCreate(from), null, text, AiPriority.Conversation)
        {
            PersonaKey = PersonaKey,
            PersonaVars = PersonaVarsFor(npc),
            History = Server.Avallone.Rumours.RumourSpeech.History(talk.History),
            Rumours = Server.Avallone.Rumours.RumourSpeech.Ask(npc, from, text, firstLine, follow, Server.Avallone.Rumours.RumourSpeech.TownOf(npc)),
            Standing = Relations.Standing(npc, from), // 5c-2 2b
            Party = TravelParty.For(npc, from), // Stage A1: companions + pets the NPC can see
            Falling = RealWeather.Word(npc), // rumour step C3
            Correction = Server.Avallone.Rumours.Corrections.AskFor(fix),
            NameCheck = NameCheckFor(from) // Stage 2 repair, decision 9
        };

        npc.Direction = npc.GetDirectionTo(from);

        var reply = await AiGateway.RequestAsync(request);

        if (!CanDeliver(npc, from))
        {
            return false;
        }

        // Townsfolk fail silently: an unanswered remark is normal in a busy town.
        if (!reply.Ok)
        {
            return false;
        }

        var spoken = Server.Avallone.Rumours.RumourSpeech.Fill(npc, from, reply, town, request.Rumours, bind: true);
        if (spoken == null)
        {
            return false; // a gossip line that failed its name check is not said (townsfolk fail silently)
        }

        // Part A: the accepting line is said only if the correction is now in the saved outbox; else the ordinary reply.
        var filled = spoken;
        spoken = Server.Avallone.Rumours.Corrections.Commit(npc, from, fix, reply) ?? spoken;
        npc.Direction = npc.GetDirectionTo(from);
        npc.Say(spoken);
        spoke.Value = true;
        if (ReferenceEquals(spoken, filled))
        {
            Server.Avallone.Rumours.RumourSpeech.OnSpoken(npc, from, reply); // Step 2e: a story really said, recorded now
        }
        TravelParty.Note(from, request.Party, spoken, reply.Rumour != null || reply.Conduct != null || reply.Kind == "decency"); // A1 floor
        // Stage A2: if this line spoke to the player's companion by name, she may answer (switch avallone.ai.exchanges).
        Exchange.AfterReply(
            npc, from, request.Party, spoken, reply.Rumour != null || reply.Conduct != null || reply.Kind is "decency" or "shop",
            openingRevision, Exchange.ReactSortOf(reply), // rumour step C1
            reply.Rumour                                  // Step 2e D1: a made-up story it fully told may be handed on
        );
        Relations.Visited(npc, from); // 5c-2 2b: familiarity + the day's warmth, only once a reply was really said
        Relations.Conduct(npc, from, reply);
        talk.Last = Core.Now;
        talk.FollowRumour = Server.Avallone.Rumours.RumourSpeech.FollowId(reply);
        Server.Avallone.Rumours.Corrections.AfterReply(npc, from, reply, openingRevision); // Part A: a ticket, or none
        Remember(talk.History, "player", text);
        Remember(talk.History, "npc", TravelParty.ForHistory(Server.Avallone.Rumours.RumourSpeech.HistoryText(reply, spoken), request.Party));
        return true;
    }

    private static void Remember(List<AiLine> history, string speaker, string text)
    {
        history.Add(new AiLine(speaker, text));
        if (history.Count > HistoryLines)
        {
            history.RemoveRange(0, history.Count - HistoryLines);
        }
    }

    private static void Prune()
    {
        if (_talks.Count < 200)
        {
            return;
        }

        var now = Core.Now;
        var old = new List<(Serial, Serial)>();
        foreach (var (k, t) in _talks)
        {
            if (now - t.Last > ForgetAfter)
            {
                old.Add(k);
            }
        }

        foreach (var k in old)
        {
            _talks.Remove(k);
        }

        logger.Debug("Townsfolk: forgot {Count} old conversations", old.Count);
    }
}
