using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.AI;

public enum NameMatch
{
    None,
    Exact,
    Near
}

public readonly record struct NameResult(Mobile Winner, NameMatch Match, bool Ambiguous);

// Phase 4e "N": one answer to "which AI character did this line name?" for companions, wanderers, townsfolk and Mirabel
// (the owner, chat, 2026-10-20: - random stock names like Ailiynn; the owner's eyesight and typing:
// "close enough"). Design: wiki Admin/Review-Request-2026-10-21-...-Round-2.md section N, Codex round 2 GO, plus the owner's
// 2026-10-21 decision that sound-alike matching is LOG-ONLY for now.
//
//   exact name word anywhere in the line  >  near miss (letters off)  >  nothing.
//   Near miss: the typed word is not an English word or chat slang (Data/Avallone/words.txt), doubled letters are
//   collapsed first, and it is at most 1 letter off for names of 4+ letters, 2 off for names of 7+ letters
//   (swapped neighbours count as one). Near misses only within 4 tiles (the owner, 2026-10-02).
//   Best score wins; equal score -> the name said first wins. One word fitting two DIFFERENT names equally -> nobody
//   answers (logged). Two NPCs with the SAME name -> the nearer one.
//   Sound-alike (same first letter and consonants, in the first two words): logged only, never answered.
// Stock NPCs that are not AI characters (town criers, guards ...) are not handled here - TownsfolkTalk.OpensWithName.
public static class NameResolver
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(NameResolver));

    private const int MaxRange = 8;
    private const int NearMissRange = 4;
    private const int MaxChars = 200;
    private const int MaxTokens = 40;
    private const int MaxTokenLength = 20;

    // Every handler asking about the same spoken line gets the same answer from ONE computation, and a line is logged
    // at most once (Codex round 3: the old key used Core.TickCount, a clock, not the speech event). Each AI speech
    // handler calls BeginSpeech with the engine's single SpeechEventArgs for the line; results are shared until the
    // next line (or 1 s, so a stale line is never reused by later non-speech callers).
    private static SpeechEventArgs _speech;
    private static long _speechTick;
    private static readonly Dictionary<(Serial From, string Text), NameResult> _speechCache = [];

    private static readonly HashSet<string> Greetings =
        ["hail", "hi", "hello", "hey", "greetings", "oi", "yo", "ho", "hiya", "howdy", "ahoy", "evening", "morning"];

    // How many times a line was actually worked out (tests and instrumentation).
    public static long ComputeCount { get; private set; }

    public static void BeginSpeech(SpeechEventArgs e)
    {
        if (e != null && !ReferenceEquals(e, _speech))
        {
            _speech = e;
            _speechTick = Core.TickCount;
            _speechCache.Clear();
        }
    }

    public static bool IsAiCharacter(Mobile m) => m is Mirabel or BaseWanderer or BaseCompanion || TownsfolkTalk.IsTownsperson(m);

    // How far away each kind hears its name (unchanged from before 4e).
    private static int HearRange(Mobile m) => m switch
    {
        BaseCompanion => 6,
        Mirabel       => 4,
        _             => 8
    };

    public static NameResult Resolve(Mobile from, string text)
    {
        if (from == null || string.IsNullOrWhiteSpace(text) || from.Map == null)
        {
            return default;
        }

        var norm = text.Trim();
        var inSpeech = _speech != null && _speech.Mobile == from && Core.TickCount - _speechTick <= 1000 &&
                       string.Equals(_speech.Speech?.Trim(), norm, StringComparison.Ordinal);
        if (inSpeech && _speechCache.TryGetValue((from.Serial, norm), out var cached))
        {
            return cached;
        }

        var result = Compute(from, norm);
        ComputeCount++;
        if (inSpeech)
        {
            _speechCache[(from.Serial, norm)] = result;
        }

        return result;
    }

    private static NameResult Compute(Mobile from, string text)
    {
        var tokens = Tokens(text);
        if (tokens.Count == 0)
        {
            return default;
        }

        var words = TownsfolkTalk.WordList;
        var scored = new List<(Mobile M, string Name, int Score, int Pos, double Dist)>();
        var soundAlike = new List<Mobile>();

        foreach (var m in from.GetMobilesInRange(MaxRange))
        {
            if (m == from || m.Deleted || m.Hidden || !from.CanSee(m) || !IsAiCharacter(m) || !from.InRange(m, HearRange(m)))
            {
                continue;
            }

            var name = FirstName(m);
            if (name.Length == 0)
            {
                continue;
            }

            var dist = from.GetDistanceToSqrt(m);
            var exact = tokens.FindIndex(t => t == name || t.EndsWith("'s", StringComparison.Ordinal) && t[..^2] == name);
            if (exact >= 0)
            {
                scored.Add((m, name, 0, exact, dist));
                continue;
            }

            if (name.Length < 4 || words.Count == 0)
            {
                continue;
            }

            if (dist <= NearMissRange)
            {
                var allowed = name.Length >= 7 ? 2 : 1;
                var cn = CollapseDoubles(name);
                int best = int.MaxValue, bestPos = -1;
                for (var i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t.Length < 3 || words.Contains(t))
                    {
                        continue;
                    }

                    var d = Osa(CollapseDoubles(t), cn);
                    if (d <= allowed && d < best)
                    {
                        best = d;
                        bestPos = i;
                    }
                }

                if (bestPos >= 0)
                {
                    // +1: any spelling that is not the name itself ranks below an exact name (even "ailiyn" for Ailiynn)
                    scored.Add((m, name, best + 1, bestPos, dist));
                    continue;
                }
            }

            // The first word, or the second word right after a greeting ("hail Elspit") - round 2 rule.
            for (var i = 0; i < Math.Min(2, tokens.Count); i++)
            {
                if (i == 1 && !Greetings.Contains(tokens[0]))
                {
                    break;
                }

                if (!words.Contains(tokens[i]) && SoundsLike(tokens[i], name))
                {
                    soundAlike.Add(m);
                    break;
                }
            }
        }

        if (scored.Count == 0)
        {
            if (soundAlike.Count > 0)
            {
                logger.Information(
                    "Names: {Player} \"{Text}\" sounds like {Names} - not answered (sound-alike is log-only)",
                    from.Name,
                    Clip(text),
                    string.Join(", ", soundAlike.ConvertAll(m => m.Name))
                );
            }

            return default;
        }

        scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) :
            a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Dist.CompareTo(b.Dist));
        var top = scored[0];
        for (var i = 1; i < scored.Count; i++)
        {
            var other = scored[i];
            if (other.Score != top.Score || other.Pos != top.Pos)
            {
                break;
            }

            if (other.Name != top.Name)
            {
                logger.Information(
                    "Names: {Player} \"{Text}\" fits {A} and {B} equally - nobody answers",
                    from.Name,
                    Clip(text),
                    top.M.Name,
                    other.M.Name
                );
                return new NameResult(null, NameMatch.None, true);
            }
        }

        if (top.Score > 0)
        {
            // Round 2: log every near-miss answer with its score, so the canary can count false positives.
            logger.Information(
                "Names: {Player} \"{Text}\" -> {Npc} by close spelling ({Off} letter(s) off, {Dist:F1} tiles)",
                from.Name,
                Clip(text),
                top.M.Name,
                top.Score - 1,
                top.Dist
            );
        }

        return new NameResult(top.M, top.Score == 0 ? NameMatch.Exact : NameMatch.Near, false);
    }

    public static string FirstName(Mobile m) => (m.Name ?? "").Trim().Split(' ')[0].ToLowerInvariant();

    internal static List<string> Tokens(string text)
    {
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
        }

        var list = new List<string>();
        foreach (Match w in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}']+"))
        {
            var t = w.Value.Trim('\'');
            if (t.Length == 0)
            {
                continue;
            }

            list.Add(t.Length > MaxTokenLength ? t[..MaxTokenLength] : t);
            if (list.Count == MaxTokens)
            {
                break;
            }
        }

        return list;
    }

    internal static string CollapseDoubles(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (sb.Length == 0 || sb[^1] != ch)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    // Optimal string alignment distance: insert, delete, substitute, or swap two neighbours, each costs 1.
    internal static int Osa(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    // Same first letter, and the same consonants in order once vowels, y, h and doubled letters are dropped.
    internal static bool SoundsLike(string token, string name)
    {
        if (token.Length < 3 || name.Length < 4 || token[0] != name[0])
        {
            return false;
        }

        var a = Skeleton(token);
        return a.Length >= 2 && a == Skeleton(name);
    }

    internal static string Skeleton(string s)
    {
        var sb = new StringBuilder();
        sb.Append(s[0]);
        for (var i = 1; i < s.Length; i++)
        {
            var ch = s[i];
            if ("aeiouyh'".IndexOf(ch) < 0)
            {
                sb.Append(ch);
            }
        }

        return CollapseDoubles(sb.ToString());
    }

    private static string Clip(string s) => s.Length > 80 ? s[..80] + "..." : s;

    internal static void ResetCacheForTests()
    {
        _speech = null;
        _speechCache.Clear();
    }
}
