using System;
using Server.Logging;
using Server.Multis;

namespace Server.Avallone;

// the owner, 2026-10-20. On production shards of this era a ship sailed into one of the serpent
// pillars came out at its twin in the other sea, which is how ships got between Britannia and the Lost Lands.
// Stock has the pillars only as walk-on teleporters (players only, and the pillar tiles cannot be walked or sailed
// onto), and keeps the two seas as separate wrap areas, so no ship could cross. With avallone.serpentPillarShips on,
// a ship under a plain move order ("forward", "left" ...) that sails into a pillar on Trammel or Felucca is moved,
// with everything aboard, to the far side of the twin pillar, keeping its facing and its order. Courses from a map
// never cross. Other facets are untouched.
public static class SerpentPillars
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(SerpentPillars));

    // The pillar pairs, the same four points the stock teleporters use (Distribution/Data/teleporters.json, Felucca
    // lines 23/688 and 343/1638, Trammel 2188/2873 and 2538/3753). Identical on both facets.
    private static readonly Point2D[][] Pairs =
    [
        [new Point2D(424, 3283), new Point2D(5267, 2757)],
        [new Point2D(2985, 2890), new Point2D(5974, 2697)]
    ];

    // Seconds after a crossing before the same ship can cross again, so it cannot bounce back and forth.
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    // How many tiles past the mirrored spot to look for open water if something is in the way.
    private const int MaxPush = 8;

    // Each pillar is 14 serpent-pillar tiles inside the 4 x 4 box from (x-2, y-2) to (x+1, y+1) of its point.
    private static Rectangle2D PillarBox(Point2D p) => new(p.X - 2, p.Y - 2, 4, 4);

    private static Rectangle2D Footprint(BaseBoat boat, int x, int y)
    {
        var mcl = boat.Components;
        return new Rectangle2D(x + mcl.Min.X, y + mcl.Min.Y, mcl.Width, mcl.Height);
    }

    // 0 = overlapping, 1 = touching, 2 = one tile of water between, and so on.
    private static int Gap(Rectangle2D a, Rectangle2D b)
    {
        var dx = Math.Max(0, Math.Max(b.X - (a.X + a.Width - 1), a.X - (b.X + b.Width - 1)));
        var dy = Math.Max(0, Math.Max(b.Y - (a.Y + a.Height - 1), a.Y - (b.Y + b.Height - 1)));
        return Math.Max(dx, dy);
    }

    // Called from BaseBoat.Move before the ship takes a step of (rx, ry). True means the ship crossed and this step
    // is used up.
    public static bool TryCross(BaseBoat boat, int rx, int ry)
    {
        var map = boat.Map;

        if (!AvalloneRules.SerpentPillarShips || map != Map.Trammel && map != Map.Felucca ||
            Core.Now < boat.NextPillarCrossing)
        {
            return false;
        }

        foreach (var pair in Pairs)
        {
            for (var i = 0; i < 2; i++)
            {
                var from = pair[i];
                var to = pair[1 - i];
                var box = PillarBox(from);

                var gapNow = Gap(Footprint(boat, boat.X, boat.Y), box);
                var gapNext = Gap(Footprint(boat, boat.X + rx, boat.Y + ry), box);

                // Sailing into it: the next step closes in to touching or closer, or is blocked while alongside.
                if (gapNext > 1 || gapNext >= gapNow &&
                    boat.CanFit(new Point3D(boat.X + rx, boat.Y + ry, boat.Z), map, boat.ItemID))
                {
                    continue;
                }

                if (!FindExit(boat, map, from, to, rx, ry, out var exit))
                {
                    return false;
                }

                var oldX = boat.X;
                var oldY = boat.Y;

                boat.NextPillarCrossing = Core.Now + Cooldown;
                boat.Teleport(exit.X - boat.X, exit.Y - boat.Y, 0);
                boat.TillerMan?.PublicOverheadMessage(MessageType.Regular, 0x3B2, false, "Ar, the serpent's pillars! Through we go, sir.");

                logger.Information(
                    "Serpent pillars: ship {Ship} ({Owner}) crossed on {Map} from {FromX},{FromY} to {ToX},{ToY}",
                    boat.ShipName ?? boat.GetType().Name,
                    boat.Owner?.Name ?? "no owner",
                    map,
                    oldX,
                    oldY,
                    boat.X,
                    boat.Y
                );

                return true;
            }
        }

        return false;
    }

    // The ship comes out on the far side of the twin pillar: its offset from the pillar it sailed into, mirrored,
    // so it is heading away from the twin. If that spot is taken, it tries further out along its heading, then
    // further out straight away from the pillar. It must be open water in the twin's sea, at least one clear tile
    // from the pillar.
    private static bool FindExit(BaseBoat boat, Map map, Point2D from, Point2D to, int rx, int ry, out Point3D exit)
    {
        var mx = to.X - (boat.X - from.X);
        var my = to.Y - (boat.Y - from.Y);
        var ax = -Math.Sign(boat.X - from.X);
        var ay = -Math.Sign(boat.Y - from.Y);
        var box = PillarBox(to);

        for (var k = 0; k <= MaxPush; k++)
        {
            if (TryExit(boat, map, box, mx + k * rx, my + k * ry, out exit) ||
                TryExit(boat, map, box, mx + k * ax, my + k * ay, out exit))
            {
                return true;
            }
        }

        exit = Point3D.Zero;
        return false;
    }

    private static bool TryExit(BaseBoat boat, Map map, Rectangle2D box, int x, int y, out Point3D exit)
    {
        exit = new Point3D(x, y, boat.Z);

        return BaseBoat.IsValidLocation(exit, map) && Gap(Footprint(boat, x, y), box) >= 2 &&
               boat.CanFit(exit, map, boat.ItemID);
    }
}
