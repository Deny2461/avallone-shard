using System;
using System.Collections.Generic;

namespace Server.Avallone.Errands;

// Errands build 1 (design v5 3.6): did the player say yes? The match is the SHARD's, on the WHOLE utterance, exact,
// against a closed list. The model is never asked and its words are never read for consent.
//
// Order (Codex R2, clarification): 1 the RAW text is checked before anything is changed; 2 lower-case, trim, one space,
// up to three closing '.' or '!' removed; 3 the townsperson's first name, as a whole word, removed once from the very
// start or once from the very end - never when the name is itself a word of the lists - then one adjoining comma and
// step 2 again (Codex R3, note 3); 4 exact comparison. Anything else is "other": the ticket is gone and nothing is agreed.
public enum ErrandAnswer
{
    Other,
    Yes,
    No
}

// Design v6 12.1 (Codex "Errands-Redesign" R1 and R2): two more closed lists - asking what the errand gives, and asking
// to hear it again. They have THEIR OWN path (ErrandMatch.Question); the yes/no path above is not touched by them, so
// a line with a '?' in it is still never consent.
public enum ErrandQuestion
{
    None,
    Reward,
    Remind
}

public static class ErrandMatch
{
    // MaxLength is two things: the longest yes/no line a player may say, and the longest ENTRY in any list - the
    // question lists too. That is on purpose (Codex "Errands 15", note 2): a question may be 60 characters as spoken,
    // and holding its entries to 40 keeps 20 for the townsperson's name, a comma and the closing marks.
    public const int MaxLength = 40;
    public const int MaxQuestionLength = 60; // the longest entry, a name and a '?' come to 44

    // The starting lists (design v5 3.6). "sure", "ok" and "okay" are left off on purpose (Codex R2: common
    // acknowledgements); add them only if a consent measurement supports it.
    public static readonly string[] DefaultYes =
    [
        "yes", "aye", "yes i will", "aye i will", "i will", "i will do it", "i'll do it", "gladly", "i accept",
        "of course", "certainly", "very well", "consider it done",
        // the same two phrases as a player naturally types them (build finding, for the code review):
        "yes, i will", "aye, i will"
    ];

    public static readonly string[] DefaultNo =
        ["no", "nay", "no thanks", "no thank you", "not now", "not today", "i cannot", "i can't"];

    // Words a name is never stripped as, beyond the words of the two lists: a townsperson called "Never" or "Maybe"
    // must not turn "never" or "maybe" into an empty line next to a yes.
    private static readonly string[] Guard =
        ["not", "never", "maybe", "later", "if", "but", "don't", "dont", "won't", "wont", "and", "or", "sure", "ok", "okay"];

    public static ErrandAnswer Classify(string raw, string npcName) => Classify(raw, npcName, DefaultYes, DefaultNo);

    public static ErrandAnswer Classify(string raw, string npcName, IReadOnlyList<string> yes, IReadOnlyList<string> no)
    {
        // 1. on the raw text, before anything is changed
        if (raw == null || raw.Length < 1 || raw.Length > MaxLength)
        {
            return ErrandAnswer.Other;
        }

        foreach (var ch in raw)
        {
            if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or ' ' or '\'' or ',' or '.' or '!'))
            {
                return ErrandAnswer.Other; // a line break, a control or invisible character, any non-ASCII character
            }
        }

        // 2.
        var text = Tidy(raw.ToLowerInvariant());

        // 3.
        var name = FirstName(npcName);
        if (name.Length > 0 && !IsListWord(name, yes, no))
        {
            text = StripName(text, name);
        }

        // 4.
        if (Contains(yes, text))
        {
            return ErrandAnswer.Yes;
        }

        return Contains(no, text) ? ErrandAnswer.No : ErrandAnswer.Other;
    }

    private static bool Contains(IReadOnlyList<string> list, string text)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // trim ASCII spaces; runs of spaces to one; up to three closing '.' or '!' removed
    internal static string Tidy(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (ch == ' ')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        var end = sb.Length;
        for (var i = 0; i < 3 && end > 0 && sb[end - 1] is '.' or '!'; i++)
        {
            end--;
        }

        return sb.ToString(0, end).TrimEnd(' ');
    }

    internal static string FirstName(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return "";
        }

        var first = npcName.Trim().Split(' ')[0].ToLowerInvariant();
        foreach (var ch in first)
        {
            if (!(ch is >= 'a' and <= 'z' or '\''))
            {
                return ""; // a name we could not have matched in the raw text anyway
            }
        }

        return first;
    }

    private static bool IsListWord(string name, IReadOnlyList<string> yes, IReadOnlyList<string> no)
    {
        if (Array.IndexOf(Guard, name) >= 0)
        {
            return true;
        }

        foreach (var list in new[] { yes, no })
        {
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var word in list[i].Split(' ', ','))
                {
                    if (word == name)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Once, from the very start OR the very end, as a whole word; then one adjoining comma and the spaces round it.
    internal static string StripName(string text, string name)
    {
        if (text.Length > name.Length && text.StartsWith(name, StringComparison.Ordinal) && text[name.Length] is ' ' or ',')
        {
            var rest = text[name.Length..].TrimStart(' ');
            if (rest.StartsWith(','))
            {
                rest = rest[1..];
            }

            return Tidy(rest);
        }

        if (text.Length > name.Length && text.EndsWith(name, StringComparison.Ordinal) &&
            text[text.Length - name.Length - 1] is ' ' or ',')
        {
            var rest = text[..^name.Length].TrimEnd(' ');
            if (rest.EndsWith(','))
            {
                rest = rest[..^1];
            }

            return Tidy(rest);
        }

        return text;
    }

    // ================================================================================================================
    // Design v6 12.1: the two question lists. Closed and exact; when in doubt, no match. No intent model. (2026-11-22:
    // the "say it again" question alone also has a small word rule - LooseRemind below.)
    // "Any": apply while an ask is pending AND while an errand is open with this townsperson. "Pending": only on the
    // player's next line after an ask - a short question can then only be about the ask (Codex R1's narrowing).
    // ================================================================================================================

    public static readonly string[] RewardAny =
    [
        "what will you give me", "what will you give me for it", "what would i get for it", "what do i get for it",
        "what is in it for me", "what's in it for me", "what does it pay", "what will you pay", "what will you pay me",
        "will you pay me", "is there a reward", "what is the reward", "what's the reward", "what reward",
        // the owner on live, 2026-11-22: closed an offer - only the forms were listed.
        "how much does it pay", "how much will you pay", "how much will you pay me", "how much is the reward",
        "how much do i get for it", "what is the pay", "what's the pay", "what do you pay", "does it pay",
        "do i get paid", "is there pay", "is there any reward"
    ];

    public static readonly string[] RewardPending = ["what would i get", "what do i get"];

    public static readonly string[] RemindAny =
    [
        "what was it you wanted", "what was it you wanted me to do", "what was it you wanted me to do again",
        "what did you want me to do", "what did you ask of me", "what was the errand", "what was the favour",
        "what was the favor", "what was the message", "remind me of the errand", "what shall i tell them",
        "what should i tell them", "what do i tell them", "what shall i tell him", "what shall i tell her",
        "what should i tell him", "what should i tell her"
    ];

    public static readonly string[] RemindPending =
        ["what did you want", "what did you ask", "remind me", "tell me again", "say it again", "what shall i say", "what should i say"];

    // 1 the RAW text: length 1 to 60, the yes/no path's characters plus '?'; 2 lower-case, trim, one space, up to three
    // closing '.', '!' or '?' removed; 3 the townsperson's first name removed once, as a whole word, from the very start
    // or the very end - not at all if that name is a word of ANY of the lists or of the guard words - then one
    // adjoining comma and step 2 again, as the yes/no path does; 4 exact comparison.
    public static ErrandQuestion Question(string raw, string npcName, bool pending)
    {
        if (raw == null || raw.Length < 1 || raw.Length > MaxQuestionLength)
        {
            return ErrandQuestion.None;
        }

        foreach (var ch in raw)
        {
            if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or ' ' or '\'' or ',' or '.' or '!' or '?'))
            {
                return ErrandQuestion.None;
            }
        }

        var text = TidyQuestion(raw.ToLowerInvariant(), 3, out var marks);
        var name = FirstName(npcName);
        if (name.Length > 0 && !IsAnyListWord(name))
        {
            // Step 2 again only if the name was really removed, and only for what is left of the three closing marks:
            // never more than three in all, wherever the name stood. (StripName tidies with the yes/no path's own step,
            // which knows '.' and '!'; a mark it takes is counted by the length it leaves.)
            var stripped = StripNameOnly(text, name);
            if (stripped != null)
            {
                text = TidyQuestion(stripped, 3 - marks, out _);
            }
        }

        if (Contains(RewardAny, text) || pending && (Contains(RewardPending, text) || LooseReward(text)))
        {
            return ErrandQuestion.Reward;
        }

        return Contains(RemindAny, text) || pending && Contains(RemindPending, text) || LooseRemind(text)
            ? ErrandQuestion.Remind
            : ErrandQuestion.None;
    }

    // Decided by the owner after the dev play test of 2026-11-22 (his
    // "What is it you wanted?", "what was I doing again?" and "who did you want me to take a message to?" all missed).
    // Beyond the exact lists, a line is a "say it again" question when its FIRST word is one of the openers and any
    // LATER word is one of the errand words. It is asked only where the exact lists are: right after an ask, or while
    // an errand is open with this townsperson. A wrong match costs one thing: the townsperson says the errand's own
    // sentence again when the player meant something else. It gives no authority - the sentence is the stored one.
    // The reward question stays exact, and so does consent: this is never a yes.
    public static readonly string[] LooseOpeners = ["what", "what's", "who", "whom", "where", "which", "remind", "repeat"];

    public static readonly string[] LooseWords =
        ["wanted", "want", "errand", "favour", "favor", "message", "doing", "supposed", "deliver", "carry", "take"];

    // the owner on live, 2026-11-22: ONLY on the player's next
    // line after an ask, a line that holds a pay word is the reward question - the fixed line is said and the offer
    // stays open, once, exactly as for a listed question. Not while an errand is merely open: a shopkeeper asked about
    // gold days later is asked about their wares. Never a yes; the caller looks at consent first.
    public static readonly string[] LoosePayWords = ["pay", "pays", "paid", "payment", "reward", "gold", "coin", "coins", "worth"];

    internal static bool LooseReward(string text)
    {
        foreach (var word in text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(LoosePayWords, word.Trim('.', '!', '?')) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool LooseRemind(string text)
    {
        var words = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || Array.IndexOf(LooseOpeners, words[0]) < 0)
        {
            return false;
        }

        for (var i = 1; i < words.Length; i++)
        {
            if (Array.IndexOf(LooseWords, words[i].Trim('.', '!', '?')) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // The question path's own tidy: trim ASCII spaces; runs of spaces to one; up to three closing '.', '!' or '?'
    // removed. Tidy (the yes/no path's) is left exactly as it was and is not used here.
    internal static string TidyQuestion(string s) => TidyQuestion(s, 3, out _);

    private static string TidyQuestion(string s, int most, out int removed)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (ch == ' ')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        var end = sb.Length;
        removed = 0;
        while (removed < most && end > 0 && sb[end - 1] is '.' or '!' or '?')
        {
            end--;
            removed++;
        }

        return sb.ToString(0, end).TrimEnd(' ');
    }

    // The name, as a whole word, once from the very start or the very end, with one adjoining comma - and nothing else
    // touched. Null = the name was not there. (The question path's own; StripName, the yes/no path's, is left as it was.)
    private static string StripNameOnly(string text, string name)
    {
        if (text.Length > name.Length && text.StartsWith(name, StringComparison.Ordinal) && text[name.Length] is ' ' or ',')
        {
            var rest = text[name.Length..].TrimStart(' ');
            return rest.StartsWith(',') ? rest[1..] : rest;
        }

        if (text.Length > name.Length && text.EndsWith(name, StringComparison.Ordinal) &&
            text[text.Length - name.Length - 1] is ' ' or ',')
        {
            var rest = text[..^name.Length].TrimEnd(' ');
            return rest.EndsWith(',') ? rest[..^1] : rest;
        }

        return null;
    }

    private static readonly string[][] QuestionLists = [RewardAny, RewardPending, RemindAny, RemindPending, LooseOpeners, LooseWords, LoosePayWords];

    private static bool IsAnyListWord(string name)
    {
        if (IsListWord(name, DefaultYes, DefaultNo))
        {
            return true;
        }

        foreach (var list in QuestionLists)
        {
            foreach (var entry in list)
            {
                foreach (var word in entry.Split(' ', ','))
                {
                    if (word == name)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // The load check (12.1): every entry is in its own normal form and within its limit; no entry is in two lists; none
    // is a guard word. Null = sound; otherwise the first fault found, and the errand module stays switched off.
    public static string Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (list, question) in new[]
                 {
                     (DefaultYes, false), (DefaultNo, false), (RewardAny, true), (RewardPending, true), (RemindAny, true),
                     (RemindPending, true)
                 })
        {
            foreach (var entry in list)
            {
                if (string.IsNullOrEmpty(entry) || entry.Length > MaxLength || // 40 for question entries too: see MaxLength
                    entry != (question ? TidyQuestion(entry.ToLowerInvariant()) : Tidy(entry.ToLowerInvariant())))
                {
                    return $"'{entry}' is not in its normal form";
                }

                foreach (var ch in entry)
                {
                    if (!(ch is >= 'a' and <= 'z' or ' ' or '\'' or ','))
                    {
                        return $"'{entry}' holds a character no player line could match";
                    }
                }

                if (!seen.Add(entry))
                {
                    return $"'{entry}' is in more than one list";
                }

                if (Array.IndexOf(Guard, entry) >= 0)
                {
                    return $"'{entry}' is a guard word";
                }
            }
        }

        // a question entry must never be read as consent, nor a consent entry as a question
        foreach (var list in QuestionLists)
        {
            foreach (var entry in list)
            {
                if (Classify(entry, null) != ErrandAnswer.Other || Classify(entry + "?", null) != ErrandAnswer.Other)
                {
                    return $"'{entry}' matches the yes/no path";
                }
            }
        }

        foreach (var entry in DefaultYes)
        {
            if (Question(entry, null, true) != ErrandQuestion.None || Question(entry + "?", null, true) != ErrandQuestion.None)
            {
                return $"'{entry}' matches the question path";
            }
        }

        foreach (var entry in DefaultNo)
        {
            if (Question(entry, null, true) != ErrandQuestion.None || Question(entry + "?", null, true) != ErrandQuestion.None)
            {
                return $"'{entry}' matches the question path";
            }
        }

        return null;
    }
}
