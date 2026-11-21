using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server.Avallone.AI;
using Server.Engines.Spawners;
using Server.Items;
using Server.Logging;
using Server.Mobiles;
using Server.Regions;
using CalcMoves = Server.Movement.Movement;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 (Projects/Avallone-Townsfolk-Step-Design.md §5.2; Codex design R4 GO, 2026-11-15).
//
// A READ-ONLY census of the resident townsfolk, written once after the world has loaded when the setting
// avallone.townsfolk.censusFile names a file. It changes nothing in the world. It answers what was "not read yet" in the
// design: how many townsfolk each town holds, where the NPCs of one spawn point stand relative to that point and to
// each other, and how large the area is that they can reach by the rules a vendor actually moves by.
//
// One JSON object per line: a "spawner" line for every spawner holding at least one resident, a "placed" line for every
// resident with no spawner, and a final "summary" line.
public static class TownsfolkCensus
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TownsfolkCensus));

    private const int DomainCap = 600; // tiles; a domain this large is reported as capped, never searched further

    // North, Right (NE), East, Down (SE), South, Left (SW), West, Up (NW) - the eight Direction values, in order.
    private static readonly (int X, int Y)[] _offsets =
        [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    public static string CensusFile { get; private set; }

    public static void Configure()
    {
        CensusFile = ServerConfiguration.GetSetting("avallone.townsfolk.censusFile", "");

        if (!string.IsNullOrWhiteSpace(CensusFile))
        {
            EventSink.ServerStarted += Run;
        }
    }

    // A resident: an ordinary townsperson who is not a traveller (design §3.1). Escortables come and go.
    public static bool IsResident(Mobile m) => TownsfolkTalk.IsTownsperson(m) && m is not BaseEscortable;

    // The town key of design §4.2: the name of the nearest enclosing TownRegion THAT HAS A NAME, or null.
    // Inns and taverns are unnamed TownRegions inside their town (census of 2026-11-15: 162 residents stood in one), so
    // the first TownRegion found is not always the town; an unnamed one is climbed past.
    public static string TownAt(Map map, Point3D p)
    {
        if (map == null || map == Map.Internal)
        {
            return null;
        }

        for (var r = Region.Find(p, map); r != null; r = r.Parent)
        {
            if (r is TownRegion && !string.IsNullOrEmpty(r.Name))
            {
                return r.Name;
            }
        }

        return null;
    }

    // The stock domain (design §5.2): every tile a vendor can stand on under WalkRandomWithHome - it takes a random
    // step only from a tile whose whole-number distance to home is within the range, so it can end one step past it.
    // Doors are treated as closed (the game's own step check), which makes this the smaller, certain part of the area.
    public static Dictionary<Point2D, int> StockDomain(Mobile walker, Map map, Point3D home, int range, out bool capped)
    {
        capped = false;
        var seen = new Dictionary<Point2D, int> { [new Point2D(home.X, home.Y)] = home.Z };
        var queue = new Queue<Point3D>();
        queue.Enqueue(home);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            var dx = p.X - home.X;
            var dy = p.Y - home.Y;

            if ((int)Math.Sqrt(dx * dx + dy * dy) > range)
            {
                continue; // from here the AI only steps back towards home
            }

            for (var d = 0; d < 8; d++)
            {
                if (!CalcMoves.CheckMovement(walker, map, p, (Direction)d, out var newZ))
                {
                    continue;
                }

                var q = new Point2D(p.X + _offsets[d].X, p.Y + _offsets[d].Y);

                if (seen.ContainsKey(q))
                {
                    continue;
                }

                if (seen.Count >= DomainCap)
                {
                    capped = true;
                    return seen;
                }

                seen[q] = newZ;
                queue.Enqueue(new Point3D(q.X, q.Y, newZ));
            }
        }

        return seen;
    }

    private static void Run()
    {
        try
        {
            var lines = new List<string>();
            var bySpawner = new Dictionary<BaseSpawner, List<BaseCreature>>();
            var placed = new List<BaseCreature>();
            var residents = 0;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not BaseCreature bc || !IsResident(bc))
                {
                    continue;
                }

                residents++;

                if (bc.Spawner is BaseSpawner sp && !sp.Deleted)
                {
                    if (!bySpawner.TryGetValue(sp, out var list))
                    {
                        bySpawner[sp] = list = [];
                    }

                    list.Add(bc);
                }
                else
                {
                    placed.Add(bc);
                }
            }

            foreach (var (sp, list) in bySpawner)
            {
                list.Sort((a, b) => a.Serial.CompareTo(b.Serial));
                var walker = list[0];
                var home = walker.Home == Point3D.Zero ? sp.Location : walker.Home;
                var map = sp.Map;
                var domain = StockDomain(walker, map, home, Math.Max(walker.RangeHome, 0), out var capped);

                var doors = 0;
                foreach (var item in map.GetItemsInRange(sp.Location, Math.Max(walker.RangeHome, 0) + 2))
                {
                    if (item is BaseDoor)
                    {
                        doors++;
                    }
                }

                var entries = new List<string>();
                foreach (var e in sp.Entries)
                {
                    entries.Add(e.SpawnedName);
                }

                var people = new List<object>();
                foreach (var bc in list)
                {
                    people.Add(Person(bc, sp.Location, domain));
                }

                lines.Add(
                    JsonSerializer.Serialize(
                        new
                        {
                            kind = "spawner",
                            guid = sp.Guid.ToString(),
                            type = sp.GetType().Name,
                            map = map?.Name,
                            x = sp.Location.X,
                            y = sp.Location.Y,
                            z = sp.Location.Z,
                            town = TownAt(map, sp.Location),
                            count = sp.Count,
                            homeRange = sp.HomeRange,
                            walkingRange = sp.WalkingRange,
                            returnOnDeactivate = sp.ReturnOnDeactivate,
                            spawnLocationIsHome = sp.SpawnLocationIsHome,
                            homeRangeStyle = sp.IsHomeRangeStyle,
                            entries,
                            domainTiles = domain.Count,
                            domainCapped = capped,
                            doorsNear = doors,
                            people
                        }
                    )
                );
            }

            foreach (var bc in placed)
            {
                lines.Add(
                    JsonSerializer.Serialize(
                        new { kind = "placed", town = TownAt(bc.Map, bc.Location), person = Person(bc, bc.Home, null) }
                    )
                );
            }

            lines.Add(
                JsonSerializer.Serialize(
                    new { kind = "summary", residents, spawners = bySpawner.Count, placed = placed.Count, aiIds = AiIdentity.Count }
                )
            );

            File.WriteAllLines(CensusFile, lines);
            logger.Information(
                "Townsfolk census: {Residents} residents on {Spawners} spawners, {Placed} placed by hand - written to {File}",
                residents, bySpawner.Count, placed.Count, CensusFile
            );
        }
        catch (Exception e)
        {
            logger.Error(e, "Townsfolk census failed - nothing in the world was changed");
        }
    }

    // The first named region at a point, climbing outwards (what TownsfolkTalk.TownOf sends today), with its type.
    public static (string Name, string Type) NamedRegionAt(Map map, Point3D p)
    {
        if (map == null || map == Map.Internal)
        {
            return (null, null);
        }

        var region = Region.Find(p, map);

        while (region != null && string.IsNullOrEmpty(region.Name))
        {
            region = region.Parent;
        }

        return (region?.Name, region?.GetType().Name);
    }

    private static object Person(BaseCreature bc, Point3D spawnPoint, Dictionary<Point2D, int> domain) => new
    {
        region = NamedRegionAt(bc.Map, bc.Home == Point3D.Zero ? bc.Location : bc.Home).Name,
        regionType = NamedRegionAt(bc.Map, bc.Home == Point3D.Zero ? bc.Location : bc.Home).Type,
        guarded = bc.Map == null || bc.Map == Map.Internal
            ? null
            : Region.Find(bc.Home == Point3D.Zero ? bc.Location : bc.Home, bc.Map)?.GetRegion<GuardedRegion>()?.Name,
        serial = bc.Serial.Value,
        type = bc.GetType().Name,
        name = bc.Name,
        title = bc.Title,
        female = bc.Female,
        map = bc.Map?.Name,
        x = bc.Location.X,
        y = bc.Location.Y,
        z = bc.Location.Z,
        homeX = bc.Home.X,
        homeY = bc.Home.Y,
        rangeHome = bc.RangeHome,
        fromSpawnPoint = Math.Round(bc.GetDistanceToSqrt(spawnPoint), 1),
        inDomain = domain?.ContainsKey(new Point2D(bc.Location.X, bc.Location.Y)),
        hasAiId = AiIdentity.TryGet(bc, out _),
        town = TownAt(bc.Map, bc.Home == Point3D.Zero ? bc.Location : bc.Home)
    };
}
