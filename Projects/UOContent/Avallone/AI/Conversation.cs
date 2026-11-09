using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Server.Mobiles;

namespace Server.Avallone.AI;

// Step B (the owner, chat, 2026-11-08)
//
// One record per player of the AI character they are talking to - townsperson, Mirabel, wanderer or companion alike.
// Every speech handler asks it before taking a line that names nobody:
// - a line that names someone (exactly or close enough) still goes to them, and they become the partner;
// - a line that names nobody goes to the partner while the partner is alive, visible, on the same map, within
//   talking range (8 tiles) and the player has spoken within the last 5 minutes - nobody else takes it;
// - "goodbye" / "farewell" ends it: the partner keeps the token for CloseGrace (2 s) so no other handler answers the same
//   line (Codex Step B r1 finding 2: Mirabel nearby also took it), then it is gone; walking out of range or going quiet
//   ends it too. With no partner, the old 45 s "recently talked" windows no longer pick a listener (same finding): the
//   nearest townsperson, or your own companion beside you, does.
// The one exception: a plain order (follow / wait / go home) to your own companion standing within 3 tiles is always
// hers (see BaseCompanion.IsAddressedBy) - the game keeps that path.
// Game commands (vendor buy, bank, all follow ...) are untouched: each handler checks them before this.
// RAM only; a restart forgets every partner, which is harmless.
public static class Conversation
{
    public const int Range = 8;
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private static readonly Dictionary<Mobile, (Mobile Partner, DateTime Last, bool Closing)> _partners = new();

    private static readonly Regex Farewell = new(
        @"^\W*(?:(?:well|ok(?:ay)?|right|then)\W+)?(?:good\s*bye|bye(?:\s*bye)?|farewell|fare\s+(?:thee|you)\s+well|so\s+long|" +
        @"see\s+(?:you|ya)|take\s+care|until\s+(?:next\s+time|later|we\s+meet\s+again)|be\s+well|safe\s+travels)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public static bool IsFarewell(string text) => !string.IsNullOrEmpty(text) && Farewell.IsMatch(text);

    // The player is now talking to `partner` (it took this line). A farewell ends the conversation once answered.
    public static void Note(Mobile player, Mobile partner, string text = null)
    {
        if (player == null || partner == null)
        {
            return;
        }

        if (_partners.Count > 500)
        {
            Prune();
        }

        // A farewell still belongs to this partner for the rest of this speech event (CloseGrace), then ends.
        _partners[player] = (partner, Core.Now, IsFarewell(text));
    }

    public static void Release(Mobile player)
    {
        if (player != null)
        {
            _partners.Remove(player);
        }
    }

    // The partner a line naming nobody belongs to, or null (none, or the conversation has lapsed - then forgotten).
    public static Mobile Partner(Mobile player)
    {
        if (player == null || !_partners.TryGetValue(player, out var p))
        {
            return null;
        }

        if (Holds(player, p.Partner, p.Last) && !(p.Closing && Core.Now - p.Last >= CloseGrace))
        {
            return p.Partner;
        }

        _partners.Remove(player);
        return null;
    }

    // True when a line naming nobody is someone else's (another character holds the conversation).
    public static bool HeldByOther(Mobile player, Mobile me)
    {
        var p = Partner(player);
        return p != null && p != me;
    }

    public static bool HeldBy(Mobile player, Mobile me) => me != null && Partner(player) == me;

    private static bool Holds(Mobile player, Mobile partner, DateTime last) =>
        partner is { Deleted: false, Alive: true, Hidden: false } && !player.Deleted && player.Map != null &&
        player.Map != Map.Internal && partner.Map == player.Map && player.InRange(partner, Range) &&
        player.CanSee(partner) && Core.Now - last < Idle;

    private static void Prune()
    {
        var gone = new List<Mobile>();
        foreach (var (player, p) in _partners)
        {
            if (!Holds(player, p.Partner, p.Last))
            {
                gone.Add(player);
            }
        }

        foreach (var m in gone)
        {
            _partners.Remove(m);
        }
    }

    public static void ResetForTests() => _partners.Clear();

    // Tests: as if CloseGrace has passed since a farewell (Core.Now cannot be moved in the test host).
    public static void EndClosingForTests(Mobile player)
    {
        if (_partners.TryGetValue(player, out var p) && p.Closing)
        {
            _partners[player] = (p.Partner, p.Last - CloseGrace - TimeSpan.FromSeconds(1), true);
        }
    }
}
