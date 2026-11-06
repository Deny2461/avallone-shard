namespace Server.Avallone.AI;

// Which speech keywords are real game commands (the owner, chat, 2026-10-07).
// The client tags ordinary words as keywords too - "what" 0x180, "news" 0x30, "join" 0x4, "pay" 0xF7, "time" 0x9E -
// and treating every tagged line as a command left the AI NPCs deaf to plain questions (verified in the shard log).
// Only the keywords below belong to the game outright; everything else is ordinary talk. A few more count only
// when the NPC that acts on them stands nearby - see TownsfolkTalk.IsGameCommand.
public static class GameCommands
{
    public static bool IsCommand(int keyword) =>
        keyword is >= 0x0 and <= 0x3     // withdraw, balance, bank, check
            or 0x7 or 0x8 or 0x9         // guards, stable, claim
            or 0x1D or 0x1E or 0x1F      // destination, i will take thee, disguise
            or >= 0x23 and <= 0x28       // house: lock down, release, secure, unsecure, strongbox, trash barrel
            or 0x33 or 0x34 or 0x38      // remove thyself, i ban thee, appraise
            or >= 0x3C and <= 0x41       // vendor buy, browse, collect, status, dismiss, cycle
            or 0x6C                      // train
            or 0x14D                     // vendor sell
            or >= 0x155 and <= 0x170     // pet orders (come, follow, kill, stay, all ...) and hire
            or >= 0x171 and <= 0x177;    // buy, browse, collect, status, dismiss, cycle, sell

    // *buy*, *sell*, *vendor buy*, *vendor sell*
    public static bool IsTrade(int keyword) => keyword is 0x171 or 0x177 or 0x3C or 0x14D;

    public static bool AnyTrade(int[] keywords)
    {
        if (keywords == null)
        {
            return false;
        }

        foreach (var k in keywords)
        {
            if (IsTrade(k))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.HashSet<string> WhWords = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "where", "what", "who", "how", "why", "when", "which", "whose", "whom", "anyone", "anybody", "somebody", "someone"
    };

    // Open a yes/no question only when they start the line, or come right after a name ("Mirabel, can I buy a room").
    private static readonly System.Collections.Generic.HashSet<string> AuxWords = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "can", "could", "would", "should", "do", "does", "did", "is", "are", "may", "will", "shall", "got", "have"
    };

    private static readonly System.Collections.Generic.HashSet<string> Pronouns = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "i", "i'd", "i'm", "we", "you", "they", "he", "she", "me"
    };

    // A line that mentions buying or selling but reads as a QUESTION, by its words alone (Codex 5c-1 code review finding 3;
    // the owner, chat, 2026-10-31: no rule - he may leave it off). Talk
    // "do you sell bread". Commands: "buy", "vendor buy", "Mirabel buy", "Mirabel I want to buy". Statements: see
    // OnlyTradeTalk (2026-11-01: a nameless "I have fish to sell" is talk now).
    private static readonly System.Collections.Generic.HashSet<string> Polite = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "please", "pray", "kindly", "prithee"
    };

    private static readonly System.Text.RegularExpressions.Regex Greeting = new(
        @"^\W*(hello|hullo|hi|hey|hail|greetings|ho|well met|good (morning|morrow|day|afternoon|evening|night))\b\W*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    // The line without one leading greeting ("Hello Mirabel can I..." -> "Mirabel can I..."; Codex 5c-1 round 2 finding 4).
    public static string WithoutGreeting(string text) => string.IsNullOrEmpty(text) ? text : Greeting.Replace(text, "", 1);

    // The line's words, without one leading greeting and without "please" and the like.
    private static string[] Words(string text) =>
        System.Array.FindAll(
            WithoutGreeting(text).Split(
                [' ', ',', '.', '!', '?', ';', ':', '"', '\t'],
                System.StringSplitOptions.RemoveEmptyEntries
            ),
            w => !Polite.Contains(w)   // "Mirabel please can I buy a room" -> "Mirabel can I buy a room"
        );

    public static bool IsTradeSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = Words(text);
        for (var i = 0; i < words.Length && i < 4; i++)
        {
            if (WhWords.Contains(words[i]))
            {
                return true;
            }
        }

        // "have" opens a question only at the very start ("have you any bread"), never after "I" ("I have fish to sell").
        return words.Length > 0 && AuxWords.Contains(words[0]) ||
               words.Length > 1 && AuxWords.Contains(words[1]) && !Pronouns.Contains(words[0]) &&
               !string.Equals(words[1], "have", System.StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(words[1], "got", System.StringComparison.OrdinalIgnoreCase);
    }

    // "sell me some wine", "sell it cheaper": the speaker asks the NPC to sell - never the "sell to the vendor" window
    // (dev play test 2, 2026-10-31: these were silent; named, "Mirabel, sell me wine" opened the wrong window).
    private static readonly System.Text.RegularExpressions.Regex SellToMe = new(
        @"\bsell (?:me|us|it|them)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    public static bool AsksToSell(string text) => !string.IsNullOrEmpty(text) && SellToMe.IsMatch(text);

    // Nothing but the trade words themselves: "buy", "sell", "buy please", "hello vendor sell".
    public static bool IsBareTrade(string text)
    {
        foreach (var w in Words(text ?? ""))
        {
            if (!w.Equals("buy", System.StringComparison.OrdinalIgnoreCase) &&
                !w.Equals("sell", System.StringComparison.OrdinalIgnoreCase) &&
                !w.Equals("vendor", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // Every command keyword in the line is a trade word, and the line is talk rather than a shop command:
    //   1. it reads as a question by its words ("where can I buy a sword") - as before;
    //   2. "vendor buy" / "vendor sell" otherwise stay the game's;
    //   3. it asks the NPC to sell ("sell me some cheaper wine", "Mirabel, sell it cheaper"), named or not;
    //   4. NOBODY is named and it is more than the bare words ("I want to buy some cheap wine", "I have fish to sell").
    // `named`: the line names someone nearby (exact or near spelling, or ambiguously). Named lines stay shop commands
    // ("Mirabel I want to buy" - Codex 5c-1 code review finding 3); a nameless one was silent before - no shop opens
    // without a name, and the AI stayed quiet (dev play test 2, 2026-10-31; the owner: fix as its own shard change).
    // Default true = the old rule for callers that cannot tell.
    public static bool OnlyTradeTalk(int[] keywords, string text, bool named = true)
    {
        if (keywords == null || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trade = false;
        var vendorWord = false;
        foreach (var k in keywords)
        {
            if (IsCommand(k) && !IsTrade(k))
            {
                return false;
            }

            trade |= IsTrade(k);
            vendorWord |= k is 0x3C or 0x14D;
        }

        if (!trade)
        {
            return false;
        }

        if (IsTradeSentence(text))
        {
            return true;
        }

        if (vendorWord)
        {
            return false;
        }

        return AsksToSell(text) || !named && !IsBareTrade(text);
    }

    public static bool AnyCommand(int[] keywords)
    {
        if (keywords == null)
        {
            return false;
        }

        foreach (var k in keywords)
        {
            if (IsCommand(k))
            {
                return true;
            }
        }

        return false;
    }
}
