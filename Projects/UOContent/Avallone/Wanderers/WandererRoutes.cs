using System;
using System.Collections.Generic;
using System.Text;
using Server.Logging;
using MovementRules = Server.Movement.Movement;

namespace Server.Mobiles;

// Stepping stones for long walks (the owner, chat, 2026-10-15). The trip limit caught Bram
// shuffling for 15 minutes near (1470, 1771) on the way to the south fields and near (1437, 1548) on the way to the
// north woods: the straight-line hops in WandererAI found no way round water. Hand-placed stones only served the
// trips they were placed for (a dev Bram starting at the graveyard stuck at once), so each trip now searches the
// map for a walkable route and lays stones along it.
public static class WandererRoutes
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WandererRoutes));

    // Search box around start and goal, tiles; the wider one only when the first finds nothing. 60 alone missed
    // south fields -> provisioner (dev rig 2026-10-16): the way round the water runs further west than either end.
    private static readonly int[] Margins = [60, 150];
    private const int StoneEvery = 12; // steps between stones - well inside the pathfinder's 38-tile box

    // A walkable route from `from` to `to` as stepping stones, or null if the search finds none. Breadth-first over
    // the game's own movement rules for this mobile (doors count as open). Measured on the dev rig 2026-10-16: the
    // five Britain routes (63-316 steps) together took under a second.
    public static List<Point3D> FindStones(Mobile m, Point3D from, Point3D to)
    {
        List<Point3D> path = null;
        foreach (var margin in Margins)
        {
            path = Search(m, from, to, margin, out _);
            if (path != null)
            {
                break;
            }
        }

        if (path == null)
        {
            return null;
        }

        var stones = new List<Point3D>();
        for (var i = StoneEvery; i < path.Count; i += StoneEvery)
        {
            stones.Add(path[i]);
        }

        stones.Add(to);
        return stones;
    }

    // ---- dev-rig probe -----------------------------------------------------------------------------------------
    // AVALLONE_ROUTE_PROBE=1: breadth-first search over walkable tiles from the inn to every hunt, using the
    // game's own movement rules for this mobile, and log a stone every `every` steps.

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("AVALLONE_ROUTE_PROBE") != "1")
        {
            return;
        }

        Timer.StartTimer(
            TimeSpan.FromSeconds(5),
            () =>
            {
                Mobile walker = null;
                foreach (var w in BaseWanderer.All)
                {
                    walker = w;
                    break;
                }

                if (walker == null)
                {
                    logger.Warning("Route probe: no wanderer in the world to walk as");
                    return;
                }

                foreach (var hunt in WandererPlaces.Hunts)
                {
                    logger.Information("Route probe inn -> {Key}: {Path}", hunt.Key,
                        Probe(walker, WandererPlaces.Inn.Location, hunt.Location, 18));
                    var started = Core.TickCount;
                    var stones = FindStones(walker, new Point3D(hunt.Location.X + 7, hunt.Location.Y + 2, hunt.Location.Z),
                        WandererPlaces.Provisioner.Location);
                    logger.Information("Route probe {Key} -> provisioner: {Stones} ({Ms} ms)", hunt.Key,
                        stones == null ? "NONE" : $"{stones.Count} stones", Core.TickCount - started);
                }

                logger.Information("Route probe inn -> provisioner: {Path}",
                    Probe(walker, WandererPlaces.Inn.Location, WandererPlaces.Provisioner.Location, 18));
                logger.Information("Route probe provisioner -> bank: {Path}",
                    Probe(walker, WandererPlaces.Provisioner.Location, WandererPlaces.Bank.Location, 18));
            }
        );
    }

    private static bool DoorAt(Map map, int x, int y, int z, out int doorZ)
    {
        foreach (var item in map.GetItemsAt(x, y))
        {
            if (item is Items.BaseDoor && Math.Abs(item.Z - z) <= 6)
            {
                doorZ = item.Z;
                return true;
            }
        }

        doorZ = z;
        return false;
    }

    public static string Probe(Mobile m, Point3D from, Point3D to, int every)
    {
        var path = Search(m, from, to, Margins[^1], out var searched);
        if (path == null)
        {
            return $"NO PATH ({searched} tiles searched)";
        }

        var sb = new StringBuilder($"{path.Count} steps:");
        for (var i = every; i < path.Count; i += every)
        {
            sb.Append($" ({path[i].X},{path[i].Y},{path[i].Z})");
        }

        return sb.ToString();
    }

    private static List<Point3D> Search(Mobile m, Point3D from, Point3D to, int margin, out int searched)
    {
        var map = WandererPlaces.Map;
        int minX = Math.Min(from.X, to.X) - margin, maxX = Math.Max(from.X, to.X) + margin;
        int minY = Math.Min(from.Y, to.Y) - margin, maxY = Math.Max(from.Y, to.Y) + margin;

        var prev = new Dictionary<(int, int), (int X, int Y, int Z)>();
        var queue = new Queue<Point3D>();
        queue.Enqueue(from);
        prev[(from.X, from.Y)] = (from.X, from.Y, from.Z);

        Point3D? found = null;
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (Math.Abs(p.X - to.X) <= 2 && Math.Abs(p.Y - to.Y) <= 2)
            {
                found = p;
                break;
            }

            for (var d = 0; d < 8; d++)
            {
                int x = p.X, y = p.Y;
                MovementRules.Offset((Direction)d, ref x, ref y);
                if (x < minX || x > maxX || y < minY || y > maxY || prev.ContainsKey((x, y)))
                {
                    continue;
                }

                // A closed door blocks the movement check, but he opens doors as he walks.
                if (MovementRules.CheckMovement(m, map, p, (Direction)d, out var z) || DoorAt(map, x, y, p.Z, out z))
                {
                    prev[(x, y)] = (p.X, p.Y, p.Z);
                    queue.Enqueue(new Point3D(x, y, z));
                }
            }
        }

        searched = prev.Count;
        if (found == null)
        {
            return null;
        }

        var path = new List<Point3D>();
        var cur = found.Value;
        while (true)
        {
            path.Add(cur);
            var back = prev[(cur.X, cur.Y)];
            if (back.X == cur.X && back.Y == cur.Y)
            {
                break;
            }

            cur = new Point3D(back.X, back.Y, back.Z);
        }

        path.Reverse();
        return path;
    }
}
