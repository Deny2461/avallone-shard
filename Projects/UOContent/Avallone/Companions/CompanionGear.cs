using System;
using System.Collections.Generic;
using Server.Avallone.AI;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Companions;

// Phase 4e custody of companion gear (the owner, chat, 2026-10-20/21; Codex design reviews 2026-10-20 and 2026-10-21 in the
// wiki mailbox). Every item a player gives a companion - and every item of a companion's own starting kit - has one
// record here, keyed by the item's serial. The rule this file exists for: a player's item is never lost and never
// copied. Records are only ever about the ORIGINAL item serial; nothing here creates, copies or recreates an item.
//
// Kept in its own save file ("AvalloneCompanionGear") rather than as a field on BaseCompanion, so the companions'
// own save format does not change: a problem in this file can never stop a companion (or the world) from loading.
// Never delete this type once it is in saves - retire it in place.
public enum GearOrigin : byte
{
    Given = 1,   // a player gave it; it can only ever go back to that same character (GiverId)
    Starter = 2  // her own starting kit; never goes to a player
}

public enum GearState : byte
{
    OnCompanion = 1,  // equipped on, or in the backpack of, Companion
    InHoldingBox = 2, // moved to the GM holding box (owner deleted, companion removed, or quarantine); Note says why
    Tombstone = 3     // the item was deleted by something outside this system - kept as evidence, never recreated
}

public sealed class GearRecord
{
    public Serial ItemSerial { get; init; }
    public Item Item { get; set; }
    public string ItemType { get; init; }
    public string ItemName { get; init; }
    public GearOrigin Origin { get; init; }
    public GearState State { get; set; }
    public BaseCompanion Companion { get; set; }
    public Serial CompanionSerial { get; set; }
    public string CompanionName { get; set; }
    public Mobile Giver { get; set; }          // null for Starter; null after the giver is deleted and the world reloads
    public Guid GiverId { get; init; }         // AiIdentity of the giver; Guid.Empty for Starter
    public string GiverName { get; init; }     // snapshot at the time of giving
    public DateTime AcceptedUtc { get; init; }
    public Layer Layer { get; set; }
    public DateTime ChangedUtc { get; set; }
    public string Note { get; set; }

    public override string ToString() =>
        $"{ItemType} {ItemSerial} ({Origin}, {State}) on {CompanionName} {CompanionSerial}" +
        (Origin == GearOrigin.Given ? $", given by {GiverName} {GiverId}" : "") +
        (string.IsNullOrEmpty(Note) ? "" : $" - {Note}");
}

// One expected piece of a companion's starting kit. Only an exact match (type, layer, hue) is recorded as Starter.
public readonly record struct StarterGear(Type Type, Layer Layer, int Hue);

public class CompanionGear : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(CompanionGear));

    // Version 1: first format. Fields in Serialize/Deserialize below are the whole wire contract.
    private const int Version = 1;

    private static CompanionGear _instance;
    private static readonly Dictionary<Serial, GearRecord> _records = [];
    private static readonly HashSet<BaseCompanion> _starterAlerted = [];

    // The delete guard is installed the first time this type is touched (which is always before any gift is recorded),
    // as well as in Configure - so no path, test harness included, can have gifts without the guard.
    static CompanionGear() => Item.DeleteVeto = VetoDelete;

    public CompanionGear() : base("AvalloneCompanionGear", 10)
    {
    }

    public static int Count => _records.Count;

    public static void Configure()
    {
        _instance = new CompanionGear();
        EventSink.ServerStarted += AfterWorldLoad;
        Item.DeleteVeto = VetoDelete;
    }

    // ---- nothing deletes a player's gift (Codex round 3, P0) -------------------------------------------------------

    // Disposal unlocks exactly ONE item, by reference (Codex round 4): nothing deleted during that call - another gift, a
    // re-entrant delete - gets past the guard. _allowAllForTests is set only by the test cleanup helper.
    private static Item _disposing;
    private static bool _allowAllForTests;

    // Called by Item.Delete for EVERY item: a live record of a player's gift refuses the delete, whatever asked for it
    // ([remove, [wipe, a container or mobile being deleted, engine code). The only way to destroy one is the explicit,
    // Administrator-only [companion gearbox dispose, which closes the record only after the delete is proven.
    private static bool VetoDelete(Item item)
    {
        if (_allowAllForTests || ReferenceEquals(item, _disposing) || !_records.TryGetValue(item.Serial, out var r) || r.Item != item ||
            r.Origin != GearOrigin.Given || r.State == GearState.Tombstone)
        {
            return false;
        }

        logger.Error(
            "Companion gear: REFUSED to delete a player's gift - {Record}. Use [companion gearbox (return / dispose) instead.",
            r
        );
        return true;
    }

    // ---- queries ----------------------------------------------------------------------------------------------

    public static GearRecord Get(Item item) =>
        item != null && _records.TryGetValue(item.Serial, out var r) && r.Item == item ? r : null;

    public static List<GearRecord> For(BaseCompanion c)
    {
        var list = new List<GearRecord>();
        foreach (var r in _records.Values)
        {
            if (r.State == GearState.OnCompanion && r.Companion == c)
            {
                list.Add(r);
            }
        }

        return list;
    }

    public static IEnumerable<GearRecord> All => _records.Values;

    // Used by every durability path: a recorded item rooted on a companion does not wear (the owner's).
    public static bool IsProtected(Item item) =>
        item != null && _records.TryGetValue(item.Serial, out var r) && r.Item == item &&
        r.State == GearState.OnCompanion && item.RootParent is BaseCompanion;

    // ---- changes (callers own the physical move; these only record what has ALREADY happened) ------------------

    internal static GearRecord RecordGiven(BaseCompanion c, Item item, Mobile giver, Guid giverId)
    {
        var r = new GearRecord
        {
            ItemSerial = item.Serial,
            Item = item,
            ItemType = item.GetType().Name,
            ItemName = item.Name,
            Origin = GearOrigin.Given,
            State = GearState.OnCompanion,
            Companion = c,
            CompanionSerial = c.Serial,
            CompanionName = c.Name,
            Giver = giver,
            GiverId = giverId,
            GiverName = giver?.Name,
            AcceptedUtc = Core.Now,
            Layer = item.Layer,
            ChangedUtc = Core.Now
        };
        _records[item.Serial] = r;
        return r;
    }

    internal static GearRecord RecordStarter(BaseCompanion c, Item item)
    {
        var r = new GearRecord
        {
            ItemSerial = item.Serial,
            Item = item,
            ItemType = item.GetType().Name,
            ItemName = item.Name,
            Origin = GearOrigin.Starter,
            State = GearState.OnCompanion,
            Companion = c,
            CompanionSerial = c.Serial,
            CompanionName = c.Name,
            AcceptedUtc = Core.Now,
            Layer = item.Layer,
            ChangedUtc = Core.Now
        };
        _records[item.Serial] = r;
        return r;
    }

    // Only after the item has verifiably reached its new owner (G3) - the record is then finished.
    internal static bool Forget(GearRecord r) => _records.Remove(r.ItemSerial);

    // ---- starter kit ------------------------------------------------------------------------------------------

    // Records a companion's starting kit, once. Only items that match the companion's canonical list exactly; a
    // missing or doubled piece is logged and left unrecorded (Codex round 2: never guess what is "hers").
    public static int SeedStarters(BaseCompanion c)
    {
        var loadout = c.StarterLoadout;
        if (loadout.Length == 0 || c.Deleted)
        {
            return 0;
        }

        foreach (var r in _records.Values)
        {
            if (r.Origin == GearOrigin.Starter && r.CompanionSerial == c.Serial)
            {
                return 0; // already seeded
            }
        }

        var added = 0;
        var problems = new List<string>();
        foreach (var want in loadout)
        {
            Item match = null;
            var count = 0;
            foreach (var item in c.Items)
            {
                if (item.GetType() == want.Type && item.Layer == want.Layer && item.Hue == want.Hue && Get(item) == null)
                {
                    match = item;
                    count++;
                }
            }

            if (count == 1)
            {
                RecordStarter(c, match);
                added++;
            }
            else
            {
                problems.Add($"{want.Type.Name}/{want.Layer}/0x{want.Hue:X}: {count} found");
            }
        }

        if (problems.Count > 0 && _starterAlerted.Add(c))
        {
            logger.Warning(
                "Companion gear: {Companion} {Serial} starting kit not as expected ({Problems}) - those pieces left unrecorded",
                c.Name,
                c.Serial,
                string.Join("; ", problems)
            );
        }

        return added;
    }

    // ---- after load ---------------------------------------------------------------------------------------------

    private static void AfterWorldLoad()
    {
        CompanionGearBox.FindAfterLoad();
        Timer.DelayCall(() =>
        {
            var n = Reconcile("after world load");
            logger.Information("Companion gear: after-load check done, {Count} record(s) changed", n);
        });

        var tombstoned = TombstoneMissing("after world load");
        var seeded = 0;
        foreach (var c in BaseCompanion.All)
        {
            seeded += SeedStarters(c);
        }

        logger.Information(
            "Companion gear: {Count} record(s) loaded, {Seeded} starting-kit piece(s) recorded, {Tombstoned} missing item(s)",
            _records.Count,
            seeded,
            tombstoned
        );
    }

    // A record whose item no longer exists becomes a tombstone with a loud log line. Nothing is ever recreated.
    public static int TombstoneMissing(string when)
    {
        var n = 0;
        foreach (var r in _records.Values)
        {
            if (r.State != GearState.Tombstone && (r.Item == null || r.Item.Deleted))
            {
                r.State = GearState.Tombstone;
                r.Item = null;
                r.ChangedUtc = Core.Now;
                r.Note = $"item missing ({when})";
                n++;
                logger.Error("Companion gear: ITEM MISSING - {Record}", r);
            }
        }

        return n;
    }

    // ---- save file ----------------------------------------------------------------------------------------------

    public override void Serialize(IGenericWriter writer)
    {
        TombstoneMissing("at save");

        writer.WriteEncodedInt(Version);
        writer.WriteEncodedInt(_records.Count);
        foreach (var r in _records.Values)
        {
            Write(writer, r);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        var version = reader.ReadEncodedInt();
        CheckVersion(version);

        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var r = Read(reader, version);
            _records[r.ItemSerial] = r;
        }
    }

    // Fail closed (Codex round 3): never guess at a layout this build does not know.
    internal static void CheckVersion(int version)
    {
        if (version != Version)
        {
            throw new InvalidOperationException(
                $"AvalloneCompanionGear save is version {version}; this build reads only version {Version}. Refusing to load."
            );
        }
    }

    internal static void Write(IGenericWriter writer, GearRecord r)
    {
        writer.Write(r.ItemSerial);
        writer.Write(r.Item?.Deleted == false ? r.Item.Serial : Serial.MinusOne);
        writer.Write(r.ItemType ?? "");
        writer.Write(r.ItemName ?? "");
        writer.Write((byte)r.Origin);
        writer.Write((byte)r.State);
        writer.Write(r.CompanionSerial);
        writer.Write(r.CompanionName ?? "");
        writer.Write(r.Giver?.Deleted == false ? r.Giver.Serial : Serial.MinusOne);
        writer.Write(r.GiverId);
        writer.Write(r.GiverName ?? "");
        writer.Write(r.AcceptedUtc);
        writer.Write((byte)r.Layer);
        writer.Write(r.ChangedUtc);
        writer.Write(r.Note ?? "");
    }

    internal static GearRecord Read(IGenericReader reader, int version)
    {
        // Same order as Write. version is 1 for every record so far.
        var itemSerial = reader.ReadSerial();
        var liveSerial = reader.ReadSerial();
        var itemType = reader.ReadString();
        var itemName = reader.ReadString();
        var origin = (GearOrigin)reader.ReadByte();
        var state = (GearState)reader.ReadByte();
        var companionSerial = reader.ReadSerial();
        var companionName = reader.ReadString();
        var giverSerial = reader.ReadSerial();
        var giverId = reader.ReadGuid();
        var giverName = reader.ReadString();
        var accepted = reader.ReadDateTime();
        var layer = (Layer)reader.ReadByte();
        var changed = reader.ReadDateTime();
        var note = reader.ReadString();

        // An item is only re-attached if it is the very same serial; anything else stays null and becomes a tombstone.
        var item = liveSerial == itemSerial ? World.FindItem(itemSerial) : null;

        return new GearRecord
        {
            ItemSerial = itemSerial,
            Item = item,
            ItemType = itemType,
            ItemName = NullIfEmpty(itemName),
            Origin = origin,
            State = state,
            CompanionSerial = companionSerial,
            Companion = World.FindMobile(companionSerial) as BaseCompanion,
            CompanionName = companionName,
            Giver = giverSerial == Serial.MinusOne ? null : World.FindMobile(giverSerial),
            GiverId = giverId,
            GiverName = NullIfEmpty(giverName),
            AcceptedUtc = accepted,
            Layer = layer,
            ChangedUtc = changed,
            Note = NullIfEmpty(note)
        };
    }

    private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    // ---- the holding box (G4) -----------------------------------------------------------------------------------

    private static bool _gmMoving;

    // Moves a player's gift out of harm's way. Into the holding box if possible; if the box cannot be had, onto the
    // internal map on its own (still alive, still recorded, loudly logged). Never deletes, never copies.
    internal static bool MoveToBox(GearRecord r, string reason)
    {
        var item = r.Item;
        if (item == null || item.Deleted || r.Origin != GearOrigin.Given)
        {
            return false;
        }

        var box = CompanionGearBox.Obtain();
        if (box != null)
        {
            box.DropItem(item);
        }

        if (box == null || item.Parent != box)
        {
            item.Internalize();
            reason += " - NOT IN THE BOX (box unavailable), kept on the internal map";
            logger.Error("Companion gear: holding box unavailable - {Item} {Serial} kept on the internal map", r.ItemType, r.ItemSerial);
        }

        r.State = GearState.InHoldingBox;
        r.Note = reason;
        r.ChangedUtc = Core.Now;
        logger.Warning("Companion gear: moved to the holding box - {Record}", r);
        return true;
    }

    // Called by the box when anything leaves it. The record is KEPT (Codex round 3): the next check either finishes it
    // (the item reached its giver) or puts the item back in the box.
    internal static void OnLeftBox(Item item)
    {
        if (_gmMoving)
        {
            return;
        }

        var r = Get(item);
        if (r?.State == GearState.InHoldingBox)
        {
            logger.Warning("Companion gear: taken out of the holding box by other means - record kept, next check decides: {Record}", r);
        }
    }

    // A companion is being deleted (before the engine deletes what she carries): her gifts go to the box, her own kit
    // is forgotten (it dies with her - it was never a player's). Any gift still on her afterwards is shouted about; the
    // delete veto keeps it alive and the next check moves it to the box.
    internal static void RescueBeforeDelete(BaseCompanion c)
    {
        foreach (var r in For(c))
        {
            if (r.Origin == GearOrigin.Starter)
            {
                Forget(r);
            }
            else if (r.Item?.RootParent == c)
            {
                MoveToBox(r, $"{c.Name} was deleted");
            }
        }

        foreach (var r in For(c))
        {
            if (r.Origin == GearOrigin.Given && r.Item?.RootParent == c)
            {
                logger.Error("Companion gear: RESCUE FAILED while deleting {Companion} - {Record}", c.Name, r);
            }
        }
    }

    // Swappable only so tests (which have no network connections) can say "online".
    internal static Func<Mobile, bool> IsOnline = m => m.NetState != null;

    // The giver's own character, if it still exists and is still the same identity. Null otherwise.
    private static Mobile LiveGiver(GearRecord r) =>
        r.Giver is { Deleted: false } g && AiIdentity.TryGet(g, out var id) && id == r.GiverId ? g : null;

    // Every record must be where it says it is. Runs after world load and every 5 minutes. Nothing is ever recreated.
    public static int Reconcile(string when)
    {
        if (World.Saving)
        {
            return 0;
        }

        var changed = TombstoneMissing(when);
        var box = CompanionGearBox.Current;

        foreach (var r in new List<GearRecord>(_records.Values))
        {
            if (r.State == GearState.Tombstone)
            {
                continue;
            }

            var item = r.Item;

            if (r.State == GearState.InHoldingBox)
            {
                // In any holding box counts - with two boxes (Broken) nothing is closed until a GM sorts it out.
                if (item.Parent is CompanionGearBox)
                {
                    if (CompanionGearBox.Broken && item.Parent != box)
                    {
                        logger.Warning("Companion gear: record kept for an item in a second holding box {Box} - {Record}", ((Item)item.Parent).Serial, r);
                    }

                    continue;
                }

                if (item.Parent == null && item.Map == Map.Internal)
                {
                    // was kept outside the box earlier; try the box again
                    if (CompanionGearBox.Obtain() is { } b)
                    {
                        b.DropItem(item);
                        if (item.Parent == b)
                        {
                            r.Note = $"{r.Note} - moved into the box ({when})";
                            r.ChangedUtc = Core.Now;
                            changed++;
                        }
                    }

                    continue;
                }

                if (LiveGiver(r) is { } owner && item.RootParent == owner)
                {
                    logger.Information("Companion gear: holding-box item reached its giver ({When}) - record closed: {Record}", when, r);
                    Forget(r);
                }
                else
                {
                    // Out of the box but not with its owner: back into custody, record kept.
                    logger.Error("Companion gear: holding-box item found elsewhere ({When}) - moved back: {Record}", when, r);
                    MoveToBox(r, $"{r.Note} - found outside the box ({when})");
                }

                changed++;
                continue;
            }

            var c = r.Companion;

            if (r.Origin == GearOrigin.Starter)
            {
                if (c == null || c.Deleted || item.RootParent != c)
                {
                    logger.Warning("Companion gear: starting-kit piece not with its companion ({When}) - record closed: {Record}", when, r);
                    Forget(r);
                    changed++;
                }

                continue;
            }

            if (r.Giver is { Deleted: false } giver && item.RootParent == giver)
            {
                // A return that reached the player but was not closed: finish it.
                logger.Information("Companion gear: return finished ({When}) - {Record}", when, r);
                Forget(r);
                changed++;
            }
            else if (c == null || c.Deleted)
            {
                changed += MoveToBox(r, $"{r.CompanionName} no longer exists ({when})") ? 1 : 0;
            }
            else if (item.RootParent != c)
            {
                changed += MoveToBox(r, $"found away from {r.CompanionName} ({when})") ? 1 : 0;
            }
            else if (r.Giver == null || r.Giver.Deleted)
            {
                changed += MoveToBox(r, $"the giver's character was deleted ({when})") ? 1 : 0;
            }
        }

        // G5: weapon damage follows what is actually in her hand (also restores it after a world load).
        foreach (var c in BaseCompanion.All)
        {
            c.RecomputeDamageMode();
        }

        return changed;
    }

    // [companion gearbox                    - list what is in the box
    // [companion gearbox return <n>         - send item n straight back to its giver (online, same identity); the record
    //                                         closes only once it is verified in their backpack (Codex round 3, P0)
    // [companion gearbox dispose <n> confirm - Administrator only: destroy item n for good (giver gone for ever, etc.)
    public static void GearboxCommand(Mobile from, string[] args)
    {
        var inBox = new List<GearRecord>();
        foreach (var r in _records.Values)
        {
            if (r.State == GearState.InHoldingBox)
            {
                inBox.Add(r);
            }
        }

        inBox.Sort((a, b) => a.ChangedUtc.CompareTo(b.ChangedUtc));

        if (CompanionGearBox.Broken)
        {
            from.SendMessage(0x22, "Warning: more than one holding box exists - see the shard log.");
        }

        var verb = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        if (verb is "return" or "dispose" or "take")
        {
            if (verb == "take")
            {
                from.SendMessage("'take' is gone: use [companion gearbox return <number> to send it straight to its owner.");
                return;
            }

            if (args.Length < 3 || !int.TryParse(args[2], out var n) || n < 1 || n > inBox.Count)
            {
                from.SendMessage($"Pick a number from 1 to {inBox.Count}.");
                return;
            }

            var rec = inBox[n - 1];
            if (rec.Item == null || rec.Item.Deleted)
            {
                from.SendMessage("That item is missing; nothing moved.");
                return;
            }

            if (verb == "return")
            {
                ReturnFromBox(from, rec);
            }
            else
            {
                DisposeFromBox(from, rec, args.Length >= 4 && args[3].Equals("confirm", StringComparison.OrdinalIgnoreCase));
            }

            return;
        }

        logger.Information("Companion gear: {Gm} listed the holding box ({Count} item(s))", from.Name, inBox.Count);
        if (inBox.Count == 0)
        {
            from.SendMessage("The companion gear holding box is empty.");
            return;
        }

        for (var i = 0; i < inBox.Count; i++)
        {
            var r = inBox[i];
            var owner = LiveGiver(r);
            var status = owner == null ? "giver gone" : owner.NetState == null ? "giver offline" : "giver online";
            from.SendMessage(
                $"{i + 1}. {r.ItemType} {r.ItemSerial} - given by {r.GiverName} to {r.CompanionName} ({status}) - {r.Note} ({r.ChangedUtc:yyyy-MM-dd HH:mm} UTC)"
            );
        }

        from.SendMessage("[companion gearbox return <number> sends one straight to its owner (they must be online).");
    }

    internal static bool ReturnFromBox(Mobile gm, GearRecord r)
    {
        var item = r.Item;
        var owner = LiveGiver(r);
        if (owner == null)
        {
            gm.SendMessage($"{r.GiverName}'s character no longer exists; the {r.ItemType} stays in the box.");
            return false;
        }

        if (!IsOnline(owner))
        {
            gm.SendMessage($"{owner.Name} is not online; try again when they are. The {r.ItemType} stays in the box.");
            return false;
        }

        var pack = owner.Backpack;
        if (pack == null || !pack.CheckHold(owner, item, false))
        {
            gm.SendMessage($"{owner.Name}'s backpack can't hold it; it stays in the box.");
            return false;
        }

        _gmMoving = true;
        try
        {
            pack.DropItem(item);
        }
        finally
        {
            _gmMoving = false;
        }

        if (item.Parent != pack || item.RootParent != owner)
        {
            // Not where it should be: back into custody, record kept.
            MoveToBox(r, $"{r.Note} - return by {gm.Name} did not land");
            logger.Error("Companion gear: {Gm}'s return of {Record} did not land in {Owner}'s backpack - kept in custody", gm.Name, r, owner.Name);
            gm.SendMessage(0x22, "It did not reach their backpack; it stays in the box, still recorded.");
            return false;
        }

        Forget(r);
        logger.Warning("Companion gear: {Gm} returned {Record} from the holding box to {Owner}", gm.Name, r, owner.Name);
        gm.SendMessage($"The {r.ItemType} is in {owner.Name}'s backpack.");
        owner.SendMessage($"A GM returned your {CompanionGearRules.Describe(item)} (held for you by {r.CompanionName}) to your backpack.");
        return true;
    }

    internal static bool DisposeFromBox(Mobile gm, GearRecord r, bool confirmed)
    {
        if (gm.AccessLevel < AccessLevel.Administrator)
        {
            gm.SendMessage("Only an Administrator can dispose of a player's gift.");
            return false;
        }

        if (!confirmed)
        {
            gm.SendMessage($"This destroys {r.GiverName}'s {r.ItemType} for good. Repeat with 'confirm' at the end to do it.");
            return false;
        }

        // Fail closed (Codex round 4): the record stays until the item is really gone. If the delete throws or does not
        // complete, custody is kept and the GM is told.
        var item = r.Item;
        if (item == null || _disposing != null)
        {
            gm.SendMessage("That gift cannot be disposed of right now. Nothing was changed.");
            return false;
        }

        _disposing = item;
        try
        {
            item.Delete();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Companion gear: dispose by {Gm} FAILED (delete threw) - record kept: {Record}", gm.Name, r);
        }
        finally
        {
            _disposing = null;
        }

        if (!item.Deleted)
        {
            logger.Error("Companion gear: dispose by {Gm} FAILED (item not deleted) - record kept: {Record}", gm.Name, r);
            gm.SendMessage($"The {r.ItemType} could not be destroyed. The record is kept; nothing was lost.");
            return false;
        }

        Forget(r);
        logger.Warning("Companion gear: {Gm} DISPOSED of {Record}", gm.Name, r);
        gm.SendMessage($"Disposed of the {r.ItemType}.");
        return true;
    }

    // Tests only: an engine-side delete that gets past the guard (to prove the tombstone path still works).
    internal static void DeleteBypassingGuardForTests(Item item)
    {
        _disposing = item;
        try
        {
            item.Delete();
        }
        finally
        {
            _disposing = null;
        }
    }

    // Tests only: start from an empty table.
    internal static void ClearForTests()
    {
        _records.Clear();
        _starterAlerted.Clear();
        _disposing = null;
        _allowAllForTests = true;
        try
        {
            foreach (var item in new List<Item>(World.Items.Values))
            {
                if (item is CompanionGearBox b && !b.Deleted)
                {
                    foreach (var child in new List<Item>(b.Items))
                    {
                        child.Delete();
                    }

                    b.Delete();
                }
            }
        }
        finally
        {
            _allowAllForTests = false;
        }

        CompanionGearBox.ResetForTests();
    }
}
