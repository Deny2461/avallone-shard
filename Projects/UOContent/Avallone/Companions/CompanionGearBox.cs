using ModernUO.Serialization;
using Server.Items;
using Server.Logging;

namespace Server.Avallone.Companions;

// Phase 4e G4: the one place a player's gift goes when it can no longer stay with the companion (she was deleted, the
// giver's character was deleted, or the item turned up somewhere it should not be). Lives on the internal map, never
// moves, and refuses to be deleted while it holds anything. What is in it, and why, is in each item's CompanionGear
// record (state InHoldingBox) - GMs see that with [companion gearbox and send items straight back with [companion gearbox return N.
[SerializationGenerator(0, false)]
public partial class CompanionGearBox : Container
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(CompanionGearBox));

    private static CompanionGearBox _box;

    public CompanionGearBox() : base(0xE7C)
    {
        Movable = false;
        Name = "companion gear holding box";
    }

    // More than one box found after load: something is wrong, so nothing is moved into either until a GM looks.
    public static bool Broken { get; internal set; }

    public static CompanionGearBox Current => _box is { Deleted: false } ? _box : null;

    internal static void FindAfterLoad()
    {
        _box = null;
        Broken = false;
        var found = 0;
        foreach (var item in World.Items.Values)
        {
            if (item is CompanionGearBox b && !b.Deleted)
            {
                found++;
                _box ??= b;
            }
        }

        if (found > 1)
        {
            Broken = true;
            logger.Error(
                "Companion gear: {Count} holding boxes found - nothing will be moved into a box until a GM sorts it out",
                found
            );
        }
    }

    // The box, created on first need. Null only when Broken.
    internal static CompanionGearBox Obtain()
    {
        if (Broken)
        {
            return null;
        }

        if (Current != null)
        {
            return _box;
        }

        _box = new CompanionGearBox();
        _box.MoveToWorld(Point3D.Zero, Map.Internal);
        logger.Information("Companion gear: holding box created {Serial}", _box.Serial);
        return _box;
    }

    public override void Delete()
    {
        if (!Deleted && Items.Count > 0)
        {
            logger.Error(
                "Companion gear: refused to delete the holding box {Serial} - it still holds {Count} item(s)",
                Serial,
                Items.Count
            );
            return;
        }

        base.Delete();
    }

    public override void OnItemRemoved(Item item)
    {
        base.OnItemRemoved(item);
        CompanionGear.OnLeftBox(item);
    }

    // Tests only.
    internal static void ResetForTests()
    {
        _box = null;
        Broken = false;
    }
}
