using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Server.Avallone.AI;
using Server.Avallone.Companions;
using Server.Gumps;
using Server.Items;
using Server.Factions;
using Server.Network;
using Server.SkillHandlers;

namespace Server.Mobiles;

// Phase 4e, G2 + G3: giving a companion gear, and asking for it back (the owner, chat, 2026-10-20/21; Codex design reviews
// 2026-10-20 and 2026-10-21, wiki mailbox/). The one rule: a player's item is never lost and never copied. Every move
// is of the ORIGINAL item; a give is all-or-nothing (anything displaced is put back if it fails); a return finishes
// only once the item is verifiably in the giver's backpack. No dragging items off her in v1 - she gives them back when
// asked. v1 takes ordinary (non-magic) melee weapons, armour and clothing only (the owner, 2026-10-21).
public abstract partial class BaseCompanion
{
    private bool _gearBusy;

    // Per companion: what she will not take, whatever the model says. null = acceptable.
    public virtual GearRefusal RefuseGear(Item item) => null;

    // ---- giving -----------------------------------------------------------------------------------------------

    public override bool OnDragDrop(Mobile from, Item dropped)
    {
        if (!CompanionGearRules.IsWearableKind(dropped))
        {
            return base.OnDragDrop(from, dropped);
        }

        return TryAcceptGear(from, dropped);
    }

    // Returns true only when the item is on her and recorded. False = it stays the player's (the drop bounces back).
    internal bool TryAcceptGear(Mobile from, Item item)
    {
        if (_gearBusy)
        {
            return false;
        }

        var refusal = GearGate(from, true) ?? CompanionGearRules.Admission(this, item) ?? RefuseGear(item);
        if (refusal != null)
        {
            OnGearRefused(from, item, refusal);
            return false;
        }

        _gearBusy = true;
        try
        {
            return CommitGive(from, item);
        }
        finally
        {
            _gearBusy = false;
            AfterGearChange();
        }
    }

    // When gear may change hands at all. giving = true also requires the giver to be the one she travels with.
    private GearRefusal GearGate(Mobile from, bool giving)
    {
        if (from == null || from.Deleted || !from.Alive)
        {
            return GearRefusal.Gate("dead", "You cannot do that now.");
        }

        if (giving && ControlMaster != from)
        {
            return GearRefusal.Gate("not-owner", $"{FirstName} only takes gear from the one she travels with.");
        }

        if (Deleted || Map == null || Map == Map.Internal || IsStabled || _goingHome || from.Map != Map ||
            !from.InRange(this, 3))
        {
            return GearRefusal.Gate("not-here", $"{FirstName} is not close enough.");
        }

        if (IsWounded)
        {
            return GearRefusal.Gate("wounded", $"{FirstName} is too badly hurt to deal with gear right now.");
        }

        if (_inFight || Combatant != null || Warmode)
        {
            return GearRefusal.Gate("fighting", $"Not in the middle of a fight - try again when it's over.");
        }

        if (World.Saving)
        {
            return GearRefusal.Gate("saving", "Try again in a moment.");
        }

        return null;
    }

    private bool CommitGive(Mobile from, Item item)
    {
        var giverId = AiIdentity.GetOrCreate(from);
        if (giverId == Guid.Empty)
        {
            return false;
        }

        var pack = Backpack;
        if (pack == null)
        {
            pack = new Backpack { Movable = false };
            AddItem(pack);
            if (pack.Parent != this)
            {
                pack.Delete(); // brand new and empty - never a player's item
                logger.Error("Companion gear: {Companion} could not get a backpack - gift refused", Name);
                return false;
            }
        }

        // Whatever the engine itself says clashes with the new item (the same two-way test as Mobile.CheckEquip).
        var displaced = new List<(Item Item, Layer Layer)>();
        foreach (var worn in Items.ToArray())
        {
            if (worn == pack || worn.Layer == Layer.Mount)
            {
                continue;
            }

            if (worn.CheckConflictingLayer(this, item, item.Layer) || item.CheckConflictingLayer(this, worn, worn.Layer) ||
                IsChestArmour(item) && worn.Layer == Layer.OuterTorso)
            {
                displaced.Add((worn, worn.Layer));
            }
        }

        var ok = true;
        foreach (var (worn, _) in displaced)
        {
            pack.DropItem(worn);
            if (worn.Parent != pack)
            {
                ok = false;
                break;
            }
        }

        if (ok)
        {
            try
            {
                ok = EquipItem(item) && item.Parent == this;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Companion gear: equipping {Item} {Serial} on {Companion} threw", item.GetType().Name, item.Serial, Name);
                ok = false;
            }
        }

        if (ok)
        {
            try
            {
                CompanionGear.RecordGiven(this, item, from, giverId);
                ok = CompanionGear.Get(item) != null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Companion gear: recording {Item} {Serial} threw", item.GetType().Name, item.Serial);
                ok = false;
            }
        }

        if (!ok)
        {
            RollBackGive(item, displaced);
            from.SendMessage(0x22, $"{FirstName} could not take that. It is still yours.");
            return false;
        }

        logger.Information(
            "Companion gear: {Companion} took {Item} {Serial} from {Giver} {GiverId}; moved to her pack: {Displaced}",
            Name,
            item.GetType().Name,
            item.Serial,
            from.Name,
            giverId,
            displaced.Count == 0 ? "nothing" : string.Join(", ", displaced.ConvertAll(d => $"{d.Item.GetType().Name} {d.Item.Serial}"))
        );
        OnGearGiven(from, item);
        return true;
    }

    // The new item comes off her (the caller returns false, so the engine bounces it back to the giver) and every displaced
    // item goes back on its old layer. Nothing is created or deleted. Anything that cannot be put back stays in her pack,
    // still recorded, and is logged loudly.
    private void RollBackGive(Item item, List<(Item Item, Layer Layer)> displaced)
    {
        var record = CompanionGear.Get(item);
        if (record != null && record.Origin == GearOrigin.Given && record.State == GearState.OnCompanion)
        {
            CompanionGear.Forget(record);
        }

        if (item.Parent == this)
        {
            RemoveItem(item);
        }

        foreach (var (worn, layer) in displaced)
        {
            if (worn.Deleted)
            {
                continue;
            }

            if (worn.Parent != this && !(CheckEquip(worn) && EquipItem(worn)))
            {
                logger.Error(
                    "Companion gear: rollback could not put {Item} {Serial} back on {Companion} ({Layer}) - left in her pack, still recorded",
                    worn.GetType().Name,
                    worn.Serial,
                    Name,
                    layer
                );
            }
        }
    }

    // ---- asking for it back -------------------------------------------------------------------------------------
    //
    // Codex round 3: a loose "give ... back" anywhere in the line swallowed ordinary talk ("I want to go back to town",
    // "give me the history of Britain"). Now only a gear-return PHRASE counts, and the item words must sit inside it:
    //   give/hand/pass/return [me] [back] [my/the/...] <item words> [back]   - needs "me", "my", "back", or "return"
    //   (want/need/have/get) my <item words> back                             - "can I have my sword back"
    // Item words stop at the first clause word ("to", "so", "about", ...). Nothing named + "my"/"back" -> the list;
    // nothing named otherwise -> ordinary conversation. Kind words (armour, weapon, clothes) only count with "my"/"back".

    internal enum GearAskKind
    {
        None,
        All,
        Items
    }

    internal readonly record struct GearAsk(GearAskKind Kind, List<string> Words, bool Strong);

    private static readonly HashSet<string> GiveVerbs = ["give", "hand", "pass", "return"];
    private static readonly HashSet<string> HaveVerbs = ["want", "need", "have", "get"];
    private static readonly HashSet<string> NotMe = ["you", "her", "him", "yourself", "thee"];
    private static readonly HashSet<string> PhraseFiller =
    [
        "the", "a", "an", "that", "this", "those", "these", "your", "some", "of", "it", "them", "please", "all", "both",
        "one", "ones", "pair", "now", "again", "mine"
    ];

    private static readonly HashSet<string> ClauseBreak =
    [
        "to", "so", "because", "then", "while", "if", "when", "about", "for", "and", "or", "but", "here", "there", "at",
        "with", "from", "since", "until", "before", "after", "i", "we", "you"
    ];

    private static readonly HashSet<string> EverythingWords = ["all", "everything", "gear", "stuff", "things", "items", "kit", "equipment"];
    private static readonly HashSet<string> KindWords = ["armor", "armour", "weapon", "weapons", "clothes", "clothing"];

    internal static GearAsk ParseGearAsk(string text, string firstName)
    {
        var none = new GearAsk(GearAskKind.None, null, false);
        var first = (firstName ?? "").ToLowerInvariant();
        var tokens = new List<string>();
        foreach (var t in NameResolver.Tokens(text ?? ""))
        {
            if (!IsNameLike(t, first))
            {
                tokens.Add(t);
            }
        }

        // Form A: give/hand/pass/return ...
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!GiveVerbs.Contains(tokens[i]))
            {
                continue;
            }

            var j = i + 1;
            if (j < tokens.Count && NotMe.Contains(tokens[j]))
            {
                return none; // "give you my sword", "hand her the cloak": the player is giving, not asking
            }

            var hasMe = j < tokens.Count && tokens[j] == "me";
            if (hasMe)
            {
                j++;
            }

            var (words, strong) = Collect(tokens, j, -1);
            if (tokens[i] != "return" && !hasMe && !strong)
            {
                return none;
            }

            return Classify(words, strong);
        }

        // Form B: (want/need/have/get) my ... back
        for (var m = 1; m < tokens.Count; m++)
        {
            if (tokens[m] != "my" || !HaveVerbs.Contains(tokens[m - 1]))
            {
                continue;
            }

            var back = tokens.IndexOf("back", m + 1);
            if (back < 0)
            {
                return none;
            }

            var (words, _) = Collect(tokens, m + 1, back);
            return Classify(words, true);
        }

        return none;
    }

    private static (List<string> Words, bool Strong) Collect(List<string> tokens, int from, int stopAt)
    {
        var words = new List<string>();
        var strong = false;
        var end = stopAt < 0 ? tokens.Count : stopAt;
        for (var k = from; k < end; k++)
        {
            var t = tokens[k];
            if (t is "back" or "my" or "mine")
            {
                strong = true;
                continue;
            }

            if (ClauseBreak.Contains(t))
            {
                break; // "give me my sword so I can fight" - the item words end here
            }

            if (t is "me" || PhraseFiller.Contains(t) || t.Length < 2)
            {
                continue;
            }

            words.Add(t);
        }

        return (words, strong || stopAt >= 0);
    }

    private static GearAsk Classify(List<string> words, bool strong)
    {
        foreach (var w in words)
        {
            if (EverythingWords.Contains(w))
            {
                return new GearAsk(GearAskKind.All, words, true);
            }
        }

        if (words.Count == 0)
        {
            return strong ? new GearAsk(GearAskKind.All, words, true) : new GearAsk(GearAskKind.None, null, false);
        }

        return new GearAsk(GearAskKind.Items, words, strong);
    }

    // The companion's own name, exactly or as a near spelling (the owner types), is never an item word.
    private static bool IsNameLike(string token, string first)
    {
        if (first.Length == 0 || token.Length < 3)
        {
            return false;
        }

        if (token == first || token == first + "'s")
        {
            return true;
        }

        if (first.Length < 4)
        {
            return false;
        }

        var allowed = first.Length >= 7 ? 2 : 1;
        return NameResolver.Osa(NameResolver.CollapseDoubles(token), NameResolver.CollapseDoubles(first)) <= allowed;
    }

    // A gear-return phrase from someone who gave her things. true = handled here (the line does not go to the model).
    internal bool TryGearRequest(Mobile from, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var ask = ParseGearAsk(text, FirstName);
        if (ask.Kind == GearAskKind.None)
        {
            return false;
        }

        var mine = GearGivenBy(from);
        if (mine.Count == 0)
        {
            return false; // nothing of theirs here: let her answer normally
        }

        var picked = ask.Kind == GearAskKind.All ? mine : MatchWords(ask.Words, ask.Strong, mine);
        if (picked.Count == 0 && !ask.Strong)
        {
            return false; // "give me the history of Britain": not about gear after all
        }

        var gate = GearGate(from, false);
        if (gate != null)
        {
            from.SendMessage(0x22, gate.Says);
            return true;
        }

        if (picked.Count == 1 && ask.Kind == GearAskKind.Items)
        {
            ReturnGear(from, picked[0]);
        }
        else
        {
            GearReturnGump.DisplayTo(from, this, picked.Count > 0 ? picked : mine);
        }

        return true;
    }

    // Records of items on her that this character gave her (by durable identity, never by current owner).
    internal List<GearRecord> GearGivenBy(Mobile from)
    {
        var list = new List<GearRecord>();
        if (!AiIdentity.TryGet(from, out var id) || id == Guid.Empty)
        {
            return list; // never mint an identity while looking for gear to return
        }

        foreach (var r in CompanionGear.For(this))
        {
            if (r.Origin == GearOrigin.Given && r.GiverId == id && r.Item?.Deleted == false && r.Item.RootParent == this)
            {
                list.Add(r);
            }
        }

        return list;
    }

    // Kept for tests and callers that have a whole line: what that line would pick (all of them for "everything").
    internal List<GearRecord> MatchRequest(string text, List<GearRecord> mine)
    {
        var ask = ParseGearAsk(text, FirstName);
        return ask.Kind switch
        {
            GearAskKind.All   => mine,
            GearAskKind.Items => MatchWords(ask.Words, ask.Strong, mine),
            _                 => []
        };
    }

    // Whole words only (a plain plural is fine) - no substring guessing.
    internal static List<GearRecord> MatchWords(List<string> words, bool strong, List<GearRecord> mine)
    {
        var picked = new List<GearRecord>();
        foreach (var r in mine)
        {
            var names = CompanionGearRules.Words(r.Item);
            var hit = false;
            foreach (var w in words)
            {
                if (KindWords.Contains(w) && !strong)
                {
                    continue;
                }

                foreach (var n in names)
                {
                    // whole word, plain plural, or the end of a compound ("sword" for longsword, "hammer" for warhammer)
                    if (n == w || w == n + "s" || w == n + "es" || n == w + "s" || w.Length >= 4 && n.EndsWith(w, StringComparison.Ordinal))
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    break;
                }
            }

            if (hit)
            {
                picked.Add(r);
            }
        }

        return picked;
    }

    // Hands one recorded item back. The record is finished only once the item is in the giver's own backpack.
    internal bool ReturnGear(Mobile to, GearRecord r)
    {
        if (_gearBusy || r == null)
        {
            return false;
        }

        var item = r.Item;
        if (CompanionGear.Get(item) != r || r.Origin != GearOrigin.Given || r.State != GearState.OnCompanion ||
            r.Companion != this || item.RootParent != this ||
            !AiIdentity.TryGet(to, out var id) || id != r.GiverId)
        {
            return false;
        }

        var gate = GearGate(to, false);
        if (gate != null)
        {
            to.SendMessage(0x22, gate.Says);
            return false;
        }

        var pack = to.Backpack;
        if (pack == null || !pack.CheckHold(to, item, false))
        {
            to.SendMessage(0x22, $"Your pack is too full for the {CompanionGearRules.Describe(item)}. Make room and ask again.");
            return false;
        }

        _gearBusy = true;
        try
        {
            var wasLayer = item.Parent == this ? item.Layer : Layer.Invalid;
            pack.DropItem(item);

            if (item.Parent != pack || item.RootParent != to)
            {
                // Not where it should be: put it back with her, keep the record, shout.
                if (item.RootParent != this && !(wasLayer != Layer.Invalid && CheckEquip(item) && EquipItem(item)))
                {
                    Backpack?.DropItem(item);
                }

                logger.Error(
                    "Companion gear: return of {Item} {Serial} to {Player} did not land in their backpack - kept with {Companion}, record kept",
                    item.GetType().Name,
                    item.Serial,
                    to.Name,
                    Name
                );
                to.SendMessage(0x22, "Something went wrong; she still has it. Ask a GM.");
                return false;
            }

            CompanionGear.Forget(r);
            logger.Information(
                "Companion gear: {Companion} gave {Item} {Serial} back to {Player} {GiverId}",
                Name,
                item.GetType().Name,
                item.Serial,
                to.Name,
                id
            );

            if (wasLayer != Layer.Invalid)
            {
                ReequipStarter(wasLayer);
            }
        }
        finally
        {
            _gearBusy = false;
            AfterGearChange();
        }

        OnGearReturned(to, item);
        return true;
    }

    // Chest armour goes on the inner torso, which a dress or robe hides completely. So when she is given chest armour her
    // outer garment goes into her pack, and it comes back on when the armour is asked back (the owner, chat, 2026-10-22).
    private static bool IsChestArmour(Item item) => item is BaseArmor && item.Layer == Layer.InnerTorso;

    private bool WearsChestArmour()
    {
        foreach (var worn in Items)
        {
            if (IsChestArmour(worn))
            {
                return true;
            }
        }

        return false;
    }

    // After a gift comes off, her own kit goes back on from her pack wherever its slot is free again - including her dress
    // once no chest armour is left to hide.
    private void ReequipStarter(Layer freed)
    {
        var pack = Backpack;
        if (pack == null)
        {
            return;
        }

        foreach (var r in CompanionGear.For(this))
        {
            var it = r.Item;
            if (r.Origin != GearOrigin.Starter || it?.Parent != pack)
            {
                continue;
            }

            var slot = r.Layer;
            var wanted = slot == freed || it.Layer == freed ||
                         freed == Layer.InnerTorso && slot == Layer.OuterTorso && !WearsChestArmour();
            if (wanted && FindItemOnLayer(slot) == null && CheckEquip(it))
            {
                EquipItem(it);
            }
        }
    }

    // ---- what she says (the server decides; the model only words it) ------------------------------------------

    private void OnGearGiven(Mobile from, Item item)
    {
        var what = CompanionGearRules.Describe(item);
        Emote($"*takes the {what}*");
        GearLedger.Given(this, from, item);
        _ = RespondAsync(
            from,
            $"*hands you the {what}*",
            ["none"],
            $"{from.Name} just gave you the {what}. You took it and are now {(item is BaseWeapon ? "carrying" : "wearing")} it. " +
            "Say something short about it, in your own words. "
        );
    }

    private void OnGearRefused(Mobile from, Item item, GearRefusal why)
    {
        from.SendMessage(0x22, why.Says);
        logger.Information(
            "Companion gear: {Companion} refused {Item} {Serial} from {Player}: {Code}",
            Name,
            item.GetType().Name,
            item.Serial,
            from.Name,
            why.Code
        );

        if (why.IsGate || ControlMaster != from)
        {
            return; // only a real refusal of the item itself gets a spoken answer and a memory
        }

        var what = CompanionGearRules.Describe(item);
        Emote("*shakes her head*");
        GearLedger.Refused(this, from, item, why.Why);
        _ = RespondAsync(
            from,
            $"*offers you the {what}*",
            ["none"],
            $"{from.Name} offered you the {what} and you refused it, because {why.Why}. Say so briefly, in your own words. "
        );
    }

    private void OnGearReturned(Mobile to, Item item)
    {
        var what = CompanionGearRules.Describe(item);
        Emote($"*hands back the {what}*");
        GearLedger.Returned(this, to, item);
    }

    // G5 hook: weapon damage mode is recomputed after every committed gear change.
    // G4: before the engine deletes everything she carries, players' gifts go to the holding box.
    public override void OnDelete()
    {
        CompanionGear.RescueBeforeDelete(this);
        base.OnDelete();
    }

    private void AfterGearChange()
    {
        RecomputeDamageMode();
        RefreshLookForNearbyPlayers();
    }

    // G5. Her own fixed damage (Elspeth 4-8), used whenever she is not holding a weapon a player gave her. Null = this
    // companion's damage is not managed here.
    public virtual (int Min, int Max)? OwnDamage => null;

    // A creature's own DamageMin/Max (when >= 0) override any weapon (BaseWeapon.GetBaseDamageRange). So: a GIVEN weapon
    // in hand -> -1/-1 and the weapon's damage applies; otherwise her own numbers. Her own dagger keeps her old damage,
    // so nothing changes for her until a player gives her a weapon.
    internal void RecomputeDamageMode()
    {
        if (OwnDamage is not { } own || Deleted)
        {
            return;
        }

        var gift = Weapon is BaseWeapon w and not Fists && CompanionGear.Get(w) is { Origin: GearOrigin.Given };
        var (min, max) = gift ? (-1, -1) : own;
        if (DamageMin != min || DamageMax != max)
        {
            SetDamage(min, max);
        }
    }

    // the owner's play test 2026-10-21/22: gloves given, asked back, then given again were worn on the server (log + test
    // GiveBackThenGiveAgainIsWornOnTheSameLayer) but never drawn on his client; the first give of the same pair was.
    // Cause unknown. As a safety net, after every gear change each nearby player gets the full "this character and what
    // she wears" update - the same one their client gets when she walks into view, which the engine builds so one slot
    // never shows two items.
    private void RefreshLookForNearbyPlayers()
    {
        var map = Map;
        if (map == null || map == Map.Internal || Deleted)
        {
            return;
        }

        foreach (var state in map.GetClientsInRange(Location))
        {
            var beholder = state.Mobile;
            if (beholder != null && beholder != this && beholder.CanSee(this))
            {
                state.SendMobileIncoming(beholder, this);
            }
        }
    }
}

// Why gear was not taken. A gate (wrong time / wrong person) is only told to the player; a refusal of the item itself is
// also said by her and remembered.
public sealed class GearRefusal
{
    public string Code { get; init; }  // short, for logs and the memory ledger
    public string Says { get; init; }  // system message to the player
    public string Why { get; init; }   // reason given to the model to word, in her voice
    public bool IsGate { get; init; }

    public static GearRefusal Gate(string code, string says) => new() { Code = code, Says = says, IsGate = true };

    public static GearRefusal Item(string code, string says, string why) => new() { Code = code, Says = says, Why = why };
}

// Placeholder until G6 wires the memory ledger events gear_given / gear_returned / gear_refused.
// G6: what she remembers about gear. Sent only after the physical change has happened. The brain must know these kinds
// before the shard sends them (deploy the brain first), or it rejects them.
internal static class GearLedger
{
    private static string The(Item item) => $"the {CompanionGearRules.Describe(item)}";

    public static void Given(BaseCompanion c, Mobile from, Item item) =>
        AiLedger.Record(c, from, "gear_given", new { item = The(item), worn = item is BaseWeapon ? "carrying" : "wearing" });

    public static void Refused(BaseCompanion c, Mobile from, Item item, string why) =>
        AiLedger.Record(c, from, "gear_refused", new { item = The(item), reason = why });

    public static void Returned(BaseCompanion c, Mobile to, Item item) =>
        AiLedger.Record(c, to, "gear_returned", new { item = The(item) });
}

// Asking for gear back when more than one item could be meant (or nothing was named): a numbered list, one button each.
public class GearReturnGump : DynamicGump
{
    private readonly BaseCompanion _companion;
    private readonly List<Serial> _serials;

    public override bool Singleton => true;

    private GearReturnGump(BaseCompanion companion, List<Serial> serials) : base(50, 50)
    {
        _companion = companion;
        _serials = serials;
    }

    public static void DisplayTo(Mobile to, BaseCompanion companion, List<GearRecord> records)
    {
        if (to?.NetState == null || companion == null || records.Count == 0)
        {
            return;
        }

        var serials = new List<Serial>();
        foreach (var r in records)
        {
            serials.Add(r.ItemSerial);
        }

        to.SendGump(new GearReturnGump(companion, serials));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        var h = 70 + _serials.Count * 25;
        builder.AddPage();
        builder.AddBackground(0, 0, 340, h, 5054);
        builder.AddImageTiled(10, 10, 320, h - 20, 2624);
        builder.AddAlphaRegion(10, 10, 320, h - 20);
        builder.AddLabel(20, 15, 1152, $"Which should {_companion.FirstName} give back?");

        for (var i = 0; i < _serials.Count; i++)
        {
            var item = World.FindItem(_serials[i]);
            var y = 45 + i * 25;
            builder.AddButton(20, y, 4005, 4007, i + 1);
            builder.AddLabel(55, y, 1152, $"{i + 1}. {(item == null ? "(gone)" : CompanionGearRules.Describe(item))}");
        }
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var i = info.ButtonID - 1;
        if (i < 0 || i >= _serials.Count)
        {
            return;
        }

        var item = World.FindItem(_serials[i]);
        var record = CompanionGear.Get(item);
        if (record != null)
        {
            _companion.ReturnGear(sender.Mobile, record);
        }
    }
}

// What a companion may be given at all (v1), and how items are named in speech.
public static class CompanionGearRules
{
    public static bool IsWearableKind(Item item) => item is BaseWeapon or BaseArmor or BaseClothing or BaseJewel;

    public static GearRefusal Admission(BaseCompanion c, Item item)
    {
        if (item is BaseJewel)
        {
            return GearRefusal.Item("jewellery", $"{c.FirstName} won't take jewellery yet.", "you don't wear jewellery");
        }

        if (item is Container || item.Amount != 1)
        {
            return GearRefusal.Item("not-wearable", $"{c.FirstName} can't wear that.", "that isn't something to wear");
        }

        if (item.QuestItem || item.Nontransferable)
        {
            return GearRefusal.Gate("quest", "That is a quest item; keep it.");
        }

        if (StolenItem.IsStolen(item))
        {
            return GearRefusal.Item("stolen", $"{c.FirstName} won't take stolen goods.", "it is stolen");
        }

        if (item.Insured || item.BlessedFor != null || item.LootType != LootType.Regular)
        {
            return GearRefusal.Gate("blessed", "Blessed, insured or cursed items can't be given to a companion yet.");
        }

        if (item is IFactionItem { FactionItemState: not null })
        {
            return GearRefusal.Gate("faction", "Faction items can't be given to a companion.");
        }

        if (item is BaseRanged or BaseThrown)
        {
            return GearRefusal.Item("ranged", $"{c.FirstName} only fights up close.", "you only fight up close, never with bows or thrown weapons");
        }

        if (item.Layer is <= Layer.Invalid or > Layer.LastUserValid or Layer.Backpack or Layer.Hair or Layer.FacialHair)
        {
            return GearRefusal.Item("not-wearable", $"{c.FirstName} can't wear that.", "that isn't something to wear");
        }

        if (IsMagic(item))
        {
            return GearRefusal.Gate("magic", "Magic items can't be given to a companion yet - ordinary gear only for now.");
        }

        var strReq = item switch
        {
            BaseWeapon w => AOS.Scale(w.StrRequirement, 100 - w.GetLowerStatReq()),
            BaseArmor a  => a.ComputeStatReq(StatType.Str),
            _            => 0
        };
        if (strReq > c.Str)
        {
            return GearRefusal.Item("too-heavy", $"{c.FirstName} isn't strong enough for that ({strReq} strength needed).", "it is too heavy for you to use");
        }

        return null;
    }

    // v1 takes only ordinary items: no magic property of any kind (Codex round 2: default-deny until tested). Codex
    // round 3: that includes the OLD-style magic too (force/vanquishing, accurate, indestructible, poison, cursed,
    // consecrated, invulnerable/fortified armour, hand-set stat bonuses). Allowed as ordinary (the owner, chat, 2026-10-24):
    // exceptional quality and coloured metal/leather/wood - crafted, not magic.
    public static bool IsMagic(Item item) => item switch
    {
        BaseWeapon w => !w.Attributes.IsEmpty || !w.WeaponAttributes.IsEmpty || !w.SkillBonuses.IsEmpty ||
                        !w.AosElementDamages.IsEmpty || w.Slayer != SlayerName.None || w.Slayer2 != SlayerName.None ||
                        w.DamageLevel != WeaponDamageLevel.Regular || w.AccuracyLevel != WeaponAccuracyLevel.Regular ||
                        w.DurabilityLevel != WeaponDurabilityLevel.Regular || w.Poison != null || w.PoisonCharges > 0 ||
                        w.Cursed || w.Consecrated,
        BaseArmor a => !a.Attributes.IsEmpty || !a.ArmorAttributes.IsEmpty || !a.SkillBonuses.IsEmpty ||
                       a.ProtectionLevel != ArmorProtectionLevel.Regular || a.Durability != ArmorDurabilityLevel.Regular ||
                       a.StrBonus != (Core.AOS ? a.AosStrBonus : a.OldStrBonus) ||
                       a.DexBonus != (Core.AOS ? a.AosDexBonus : a.OldDexBonus) ||
                       a.IntBonus != (Core.AOS ? a.AosIntBonus : a.OldIntBonus),
        BaseClothing c => !c.Attributes.IsEmpty || !c.ClothingAttributes.IsEmpty || !c.SkillBonuses.IsEmpty ||
                          !c.Resistances.IsEmpty,
        _ => true
    };

    // "Longsword" -> "longsword", "PlateChest" -> "plate chest". A custom name wins.
    public static string Describe(Item item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Name))
        {
            return item.Name.Trim();
        }

        var type = item?.GetType().Name ?? "thing";
        var sb = new StringBuilder();
        for (var i = 0; i < type.Length; i++)
        {
            if (i > 0 && char.IsUpper(type[i]) && !char.IsUpper(type[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(char.ToLowerInvariant(type[i]));
        }

        return sb.ToString();
    }

    // Words a player might use for this item: its name, plus what kind of thing it is.
    public static List<string> Words(Item item)
    {
        var words = new List<string>();
        if (item == null)
        {
            return words;
        }

        foreach (Match m in Regex.Matches(Describe(item).ToLowerInvariant(), @"[\p{L}']+"))
        {
            words.Add(m.Value);
        }

        // What players call the pieces (the owner's play test: for a leather chest).
        foreach (var w in new List<string>(words))
        {
            switch (w)
            {
                case "chest":
                    words.AddRange(["tunic", "breastplate", "shirt"]);
                    break;
                case "legs":
                    words.AddRange(["leggings", "pants", "legging"]);
                    break;
                case "arms":
                    words.AddRange(["sleeves"]);
                    break;
                case "gloves":
                    words.AddRange(["gauntlets"]);
                    break;
                case "gorget":
                    words.AddRange(["collar"]);
                    break;
                case "helm":
                    words.AddRange(["helmet"]);
                    break;
            }
        }

        switch (item)
        {
            case BaseShield:
                words.Add("shield");
                break;
            case BaseArmor:
                words.Add("armor");
                words.Add("armour");
                break;
            case BaseWeapon:
                words.Add("weapon");
                break;
            case BaseClothing:
                words.Add("clothes");
                words.Add("clothing");
                break;
        }

        return words;
    }
}
