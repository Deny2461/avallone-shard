using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Stage A1 (2026-11-09, Projects/Avallone-Stage-A-NPCs-Address-Companions.md): who travels with the player, for a townsperson
// or Mirabel. Only what anyone standing there could see: a companion's name and title, a pet's species and the name its owner
// gave it (the brain never shows that name to the model - it binds it in afterwards). Never trust, secrets, memory or gear.
// the owner (chat, 2026-11-09): - an estimate, not a locked rate - so the shard counts the
// replies it actually SAID while a companion or pet was there without mentioning them (Codex F3: counted on delivery, here),
// and after about seven asks the brain to remark on one of them. RAM only: a restart forgets the count (accepted).
public static class TravelParty
{
    public const int Range = 6;
    public const int MaxCompanions = 2;
    public const int MaxPets = 3;          // plus the mount being ridden
    public const int NudgeAfter = 7;       // misses in a row before the next reply is nudged

    // Id: Stage A3, sent only together with Known. Left out of the request entirely when null, so with the NPC-memory
    // switch off the wire format is byte for byte Stage A1.
    public sealed record PartyCompanion(
        string Name, string Title, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Id = null
    );
    public sealed record PartyPet(string Species, string Label, bool Ridden);
    // Roster: every player name the gossip roster knows (RumourSpeech.CurrentRoster, cached 60 s), so the brain's
    // name-clash rules hold on every request, not only when gossip came along (Codex A1-2).
    public sealed record PartyAsk(
        PartyCompanion[] Companions, PartyPet[] Pets, bool Nudge, string[] Roster = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KnownAsk Known = null,
        // Step 2 play test 2: true when the shard already knows no exchange can follow this reply, so the brain has the
        // NPC speak ABOUT the companion and not TO her (a greeting to her would hang unanswered). Left out when false,
        // so the request is unchanged whenever an exchange could start or exchanges are off.
        [property: JsonPropertyName("no_answer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NoAnswer = null
    );

    private static readonly Dictionary<Serial, int> _misses = new();
    private static readonly Regex Word = new(@"[a-z0-9']+", RegexOptions.Compiled);

    // The party the NPC can see beside this player, or null when there is none.
    public static PartyAsk For(Mobile npc, Mobile player)
    {
        if (npc?.Map == null || player == null || npc.Map == Map.Internal)
        {
            return null;
        }

        var companions = new List<PartyCompanion>();
        var pets = new List<PartyPet>();
        // Stage A3: with the NPC-memory switch on, the brain may look up whether this NPC has traded lines with the
        // companion before. Off = neither the read request nor a companion id is sent: the request is exactly A1's.
        var known = Exchange.KnownNow(null);

        foreach (var m in npc.GetMobilesInRange(Range))
        {
            if (m is not BaseCreature bc || bc.Deleted || !bc.Alive || bc.Hidden || bc.Map != npc.Map ||
                bc.ControlMaster != player || !bc.Controlled)
            {
                continue;
            }

            if (bc is BaseCompanion)
            {
                if (companions.Count < MaxCompanions && !string.IsNullOrWhiteSpace(bc.Name))
                {
                    var cid = known != null && bc is BaseCompanion { AiId: var id } && id != Guid.Empty ? id.ToString() : null;
                    companions.Add(new PartyCompanion(bc.Name.Trim(), bc.Title?.Trim() ?? "", cid));
                }
            }
            else if (!bc.Summoned && pets.Count < MaxPets && Species(bc) is { } sp)
            {
                pets.Add(new PartyPet(sp, Label(bc, sp), false));
            }
        }

        if (player.Mount is BaseCreature ridden && !ridden.Deleted && Species(ridden) is { } rsp)
        {
            pets.Add(new PartyPet(rsp, Label(ridden, rsp), true));
        }

        if (companions.Count == 0 && pets.Count == 0)
        {
            return null;
        }

        return new PartyAsk(
            companions.ToArray(), pets.ToArray(), NudgeDue(player), RosterNames(), companions.Count > 0 ? known : null,
            companions.Count > 0 && Exchange.CannotAnswerNow(npc, player) ? true : null
        );
    }

    private static string[] RosterNames()
    {
        var roster = Server.Avallone.Rumours.RumourSpeech.CurrentRoster();
        var names = new string[roster.Length];
        for (var i = 0; i < roster.Length; i++)
        {
            names[i] = roster[i].Name;
        }

        return names;
    }

    // Codex A1-1: what an NPC said goes back to the model as history on the next turn. A pet's owner-given name must not:
    // each pet name in the party is replaced by "the <species>" in the HISTORY copy only - the player still heard the name.
    // Done when the line is stored, with that turn's party, so a renamed or departed pet leaves no name behind.
    public static string ForHistory(string text, PartyAsk ask)
    {
        if (string.IsNullOrEmpty(text) || ask?.Pets == null)
        {
            return text;
        }

        foreach (var p in ask.Pets)
        {
            if (string.IsNullOrWhiteSpace(p.Label))
            {
                continue;
            }

            var species = p.Species ?? "animal";
            foreach (var art in new[] { "a ", "an ", "the " })
            {
                if (species.StartsWith(art, StringComparison.OrdinalIgnoreCase))
                {
                    species = species[art.Length..];
                    break;
                }
            }

            // Codex A1 fixes review: a possessive or short contraction ("Pip's", "PIP'S", curly ’s, "Pip'll") and a
            // quoted name ('Pip') must be caught too;
            // a longer name that only starts the same way ("Pippa") must not. An apostrophe counts as part of a word only
            // when a letter or digit follows it.
            text = Regex.Replace(
                text,
                @"(?<![A-Za-z0-9])(?<![A-Za-z0-9]['’])" + Regex.Escape(p.Label.Trim()) +
                @"(?=(?:['’][A-Za-z]{1,2})?(?![A-Za-z0-9])(?!['’][A-Za-z0-9]))",
                "the " + species,
                RegexOptions.IgnoreCase
            );
        }

        return text;
    }

    private static string Species(BaseCreature bc)
    {
        var s = bc.DefaultName?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // Only a name the owner actually gave it (RawName is null until renamed). The brain checks it again.
    private static string Label(BaseCreature bc, string species)
    {
        var raw = bc.RawName?.Trim();
        return string.IsNullOrEmpty(raw) || string.Equals(raw, species, StringComparison.OrdinalIgnoreCase) ? null : raw;
    }

    public static bool NudgeDue(Mobile player) =>
        player != null && _misses.TryGetValue(player.Serial, out var n) && n >= NudgeAfter;

    // After a reply was really said. `excluded`: a turn that neither counts nor clears (gossip, decency).
    public static void Note(Mobile player, PartyAsk ask, string spoken, bool excluded)
    {
        if (player == null || ask == null || excluded || spoken == null)
        {
            return;
        }

        if (Mentions(spoken, ask))
        {
            _misses.Remove(player.Serial);
            return;
        }

        _misses.TryGetValue(player.Serial, out var n);
        _misses[player.Serial] = Math.Min(n + 1, 1000);

        if (_misses.Count > 5000)
        {
            _misses.Clear(); // bound memory; losing counts only delays a nudge
        }
    }

    // First name, the title's last word ("healer"), a pet's name or its species' last word ("horse", "wyrm").
    public static bool Mentions(string spoken, PartyAsk ask)
    {
        var words = new HashSet<string>();
        foreach (Match m in Word.Matches(spoken.ToLowerInvariant()))
        {
            words.Add(m.Value);
        }

        foreach (var c in ask.Companions)
        {
            if (Last(First(c.Name)) is { } f && words.Contains(f) || Last(c.Title) is { } t && words.Contains(t))
            {
                return true;
            }
        }

        foreach (var p in ask.Pets)
        {
            if (Last(p.Species) is { } s && words.Contains(s) || p.Label != null && AllIn(p.Label, words))
            {
                return true;
            }
        }

        return false;
    }

    private static string First(string name) => name?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } a ? a[0] : null;

    private static string Last(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string last = null;
        foreach (Match m in Word.Matches(text.ToLowerInvariant()))
        {
            last = m.Value;
        }

        return last;
    }

    private static bool AllIn(string text, HashSet<string> words)
    {
        var any = false;
        foreach (Match m in Word.Matches(text.ToLowerInvariant()))
        {
            any = true;
            if (!words.Contains(m.Value))
            {
                return false;
            }
        }

        return any;
    }

    public static void ResetForTests() => _misses.Clear();
}
