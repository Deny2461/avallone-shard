using System;
using System.Collections.Generic;
using Server.Engines.CannedEvil;
using Server.Logging;

namespace Server.Avallone;

// Champion spawns on Trammel as well as Felucca (the owner, 2026-10-05: /).
// The stock world-builder ([GenChamps) only knows Felucca, and running it again deletes every champion spawn
// first, so the Trammel ones are made here instead: at startup, every Felucca champion spawn that has no twin of
// the same kind on the same tile in Trammel gets one. Felucca is never touched, and a twin that exists is left
// alone, so this is safe to run at every start. To take them out again: turn avallone.championsOnTrammel off
// (stops the scrolls and stops new twins) and delete the Trammel spawns in game.
public static class AvalloneChampions
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AvalloneChampions));

    // After the world has loaded.
    public static void Initialize()
    {
        if (!AvalloneRules.ChampionsOnTrammel)
        {
            logger.Information("Avallone champions: Trammel spawns are off (avallone.championsOnTrammel)");
            return;
        }

        var felucca = new List<ChampionSpawn>();
        var trammel = new HashSet<(Type, Point3D)>();

        foreach (var item in World.Items.Values)
        {
            if (item is not ChampionSpawn spawn || spawn.Deleted)
            {
                continue;
            }

            if (spawn.Map == Map.Felucca)
            {
                felucca.Add(spawn);
            }
            else if (spawn.Map == Map.Trammel)
            {
                trammel.Add((spawn.GetType(), spawn.Location));
            }
        }

        var made = 0;

        foreach (var source in felucca)
        {
            if (trammel.Contains((source.GetType(), source.Location)))
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(source.GetType()) is not ChampionSpawn twin)
                {
                    continue;
                }

                twin.RandomizeType = source.RandomizeType;
                twin.Type = source.Type;
                twin.MoveToWorld(source.Location, Map.Trammel);

                if (source.EjectLocation != Point3D.Zero)
                {
                    twin.EjectLocation = source.EjectLocation;
                    twin.EjectMap = Map.Trammel;
                }

                if (twin.AlwaysActive)
                {
                    twin.ReadyToActivate = true;
                }

                made++;
                logger.Information(
                    "Avallone champions: placed {Kind} ({Type}) at {Location} on Trammel",
                    twin.GetType().Name,
                    twin.RandomizeType ? "rotating" : twin.Type.ToString(),
                    twin.Location
                );
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Avallone champions: could not place a twin of the spawn at {Location}", source.Location);
            }
        }

        logger.Information(
            "Avallone champions: {Felucca} on Felucca, {Trammel} on Trammel ({Made} placed now)",
            felucca.Count,
            trammel.Count + made,
            made
        );
    }
}
