using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server.Engines.Spawners;
using Server.Logging;

namespace Server.Avallone;

// Extra permanent townsfolk for this shard, as data (same idea as Companions/CompanionPlacement: Claude places,
// the owner adjusts in game). Data/Avallone/spawners.json lists them; at startup each one that does not exist yet gets
// an ordinary Spawner (so the NPC comes back if it is ever lost). An existing one is left exactly where it is -
// moving the spawner in game with [move sticks across restarts. Found again by its name, "Avallone: <key>".
// An entry with "items" instead of "type" scatters single pick-up items over the wilderness the way the stock
// reagent spawners do (arrows and bolts, the owner 2026-10-04): it sits on the exact tile given and fills homeRange.
// For those, the amounts in the file are the amounts in the world: an existing one whose amounts differ is
// brought into line at startup (its place and range are still left alone).
public static class AvalloneSpawns
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AvalloneSpawns));

    private const string FilePath = "Data/Avallone/spawners.json";

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed class Entry
    {
        public string Key { get; set; }
        public string Type { get; set; }
        public string Map { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int WalkingRange { get; set; } = 2;
        public Dictionary<string, int> Items { get; set; } // item type -> most of it on the ground at once
        public int HomeRange { get; set; }
        public int MinMinutes { get; set; } = 5;
        public int MaxMinutes { get; set; } = 10;
        public string Note { get; set; }
    }

    // After the world has loaded.
    public static void Initialize()
    {
        var path = Path.Combine(Core.BaseDirectory, FilePath);
        if (!File.Exists(path))
        {
            return;
        }

        List<Entry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), _json) ?? [];
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Avallone spawners: could not read {Path}", path);
            return;
        }

        var existing = new Dictionary<string, Spawner>();
        foreach (var item in World.Items.Values)
        {
            if (item is Spawner s && item.Name?.StartsWith("Avallone: ", StringComparison.Ordinal) == true)
            {
                existing[item.Name] = s;
            }
        }

        var adjusted = 0;

        foreach (var e in entries)
        {
            var name = $"Avallone: {e.Key}";
            if (string.IsNullOrWhiteSpace(e.Key))
            {
                continue;
            }

            if (existing.TryGetValue(name, out var have))
            {
                if (e.Items?.Count > 0 && AdjustScatter(have, e))
                {
                    adjusted++;
                }

                continue;
            }

            if (e.Items?.Count > 0)
            {
                PlaceScatter(e, name);
                continue;
            }

            var map = Server.Map.Parse(e.Map);
            if (map == null || map == Server.Map.Internal || AssemblyHandler.FindTypeByName(e.Type) == null)
            {
                logger.Warning("Avallone spawners: {Key} skipped - unknown map {Map} or type {Type}", e.Key, e.Map, e.Type);
                continue;
            }

            var spot = FindSpot(map, new Point3D(e.X, e.Y, e.Z));
            var spawner = new Spawner(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, e.Type)
            {
                Name = name,
                WalkingRange = e.WalkingRange
            };
            spawner.MoveToWorld(spot, map);
            spawner.HomeRange = 0;
            spawner.Respawn();
            logger.Information(
                "Avallone spawners: placed {Key} ({Type}) at {Spot} {Map}{Moved}",
                e.Key,
                e.Type,
                spot,
                map,
                spot == new Point3D(e.X, e.Y, e.Z) ? "" : " (nearest free tile to the one asked for)"
            );
        }

        if (adjusted > 0)
        {
            logger.Information("Avallone spawners: {Count} scatter spawner(s) brought into line with the file's amounts", adjusted);
        }
    }

    // True if the spawner's amounts had to be changed to match the file. Item types are not added or removed here.
    private static bool AdjustScatter(Spawner spawner, Entry e)
    {
        var total = 0;
        var changed = false;
        foreach (var (type, max) in e.Items)
        {
            if (max < 1)
            {
                return false;
            }

            total += max;
            foreach (var entry in spawner.Entries)
            {
                if (string.Equals(entry.SpawnedName, type, StringComparison.OrdinalIgnoreCase) && entry.SpawnedMaxCount != max)
                {
                    entry.SpawnedMaxCount = max;
                    changed = true;
                }
            }
        }

        if (spawner.Count != total)
        {
            spawner.Count = total;
            changed = true;
        }

        if (changed)
        {
            spawner.Respawn();
        }

        return changed;
    }

    private static void PlaceScatter(Entry e, string name)
    {
        var map = Server.Map.Parse(e.Map);
        if (map == null || map == Server.Map.Internal)
        {
            logger.Warning("Avallone spawners: {Key} skipped - unknown map {Map}", e.Key, e.Map);
            return;
        }

        var total = 0;
        foreach (var (type, max) in e.Items)
        {
            if (max < 1 || AssemblyHandler.FindTypeByName(type) == null)
            {
                logger.Warning("Avallone spawners: {Key} skipped - unknown type {Type} or bad count", e.Key, type);
                return;
            }

            total += max;
        }

        var spawner = new Spawner(total, TimeSpan.FromMinutes(e.MinMinutes), TimeSpan.FromMinutes(e.MaxMinutes))
        {
            Name = name
        };
        foreach (var (type, max) in e.Items)
        {
            spawner.AddEntry(type, 100, max, false);
        }

        spawner.MoveToWorld(new Point3D(e.X, e.Y, e.Z), map);
        spawner.HomeRange = e.HomeRange;
        spawner.Respawn();
        logger.Information(
            "Avallone spawners: placed {Key} ({Total} items, range {Range}) at {Spot} {Map}",
            e.Key,
            total,
            e.HomeRange,
            spawner.Location,
            map
        );
    }

    // The tile asked for if a mobile can stand there, otherwise the nearest one within three tiles that it can.
    private static Point3D FindSpot(Map map, Point3D want)
    {
        for (var range = 0; range <= 3; range++)
        {
            for (var dx = -range; dx <= range; dx++)
            {
                for (var dy = -range; dy <= range; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != range)
                    {
                        continue;
                    }

                    var p = new Point3D(want.X + dx, want.Y + dy, want.Z);
                    if (map.CanSpawnMobile(p))
                    {
                        return p;
                    }
                }
            }
        }

        return want;
    }
}
