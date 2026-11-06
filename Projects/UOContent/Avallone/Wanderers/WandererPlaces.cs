using System.Collections.Generic;

namespace Server.Mobiles;

// Where a wanderer can go (wanderer prototype, 2026-10-14). Britain on Trammel only for v1 (the owner, chat, 2026-10-14).
// Every point was read from the shard's own spawn data (Data/Spawns/shared/trammel/Vendors.json, Graveyards.json,
// Outdoors.json) - the vendors' spawner spots and the monster spawners' centres. Not yet walked in game.
public sealed class WandererPlace
{
    public WandererPlace(string key, string description, Point3D location, int range, (string Name, int Value)[] quarry = null)
    {
        Key = key;
        Description = description;
        Location = location;
        Goal = location; // boxed once: PathFollower keeps its route only while the goal is the same object
        Range = range;
        Quarry = quarry ?? [];
    }

    public string Key { get; }
    public string Description { get; }
    public Point3D Location { get; }
    public IPoint3D Goal { get; }
    public int Range { get; }

    // What lives here, for simulated hunts when no player is near: name and gold value of what it leaves.
    public (string Name, int Value)[] Quarry { get; }

    public bool IsHunt => Quarry.Length > 0;
}

public static class WandererPlaces
{
    public static readonly Map Map = Map.Trammel;

    public static readonly WandererPlace Inn = new("inn", "the inn in Britain", new Point3D(1493, 1616, 20), 3);
    public static readonly WandererPlace Healer = new("healer", "the healer in Britain", new Point3D(1471, 1611, 20), 3);
    public static readonly WandererPlace Provisioner = new("provisioner", "the provisioner in Britain", new Point3D(1469, 1668, 0), 3);
    public static readonly WandererPlace Bank = new("bank", "the bank in Britain", new Point3D(1425, 1690, 0), 3);

    public static readonly WandererPlace[] Hunts =
    [
        new(
            "hunt_graveyard",
            "the Britain graveyard, north-west of town: skeletons, zombies, wraiths and spectres. Close, steady pay, " +
            "the spectres hit hard",
            new Point3D(1369, 1475, 10),
            10,
            [("skeleton", 25), ("zombie", 30), ("wraith", 60), ("spectre", 80)]
        ),
        new(
            "hunt_north_woods",
            "the woods north of Britain: orcs, ratmen, lizardmen, harpies, the odd troll. Good pay, trolls are dangerous",
            new Point3D(1361, 1392, 0),
            12,
            [("orc", 50), ("ratman", 40), ("lizardman", 45), ("harpy", 50), ("troll", 140)]
        ),
        new(
            "hunt_south_fields",
            "the fields south of Britain: orcs, ratmen, mongbats, and ettins. A long walk; ettins pay best and hit hardest",
            new Point3D(1435, 1917, 0),
            12,
            [("mongbat", 15), ("orc", 50), ("ratman", 40), ("troll", 140), ("ettin", 180)]
        )
    ];

    public static WandererPlace FindHunt(string key)
    {
        foreach (var p in Hunts)
        {
            if (p.Key == key)
            {
                return p;
            }
        }

        return null;
    }

    public static IEnumerable<string> HuntKeys
    {
        get
        {
            foreach (var p in Hunts)
            {
                yield return p.Key;
            }
        }
    }
}
