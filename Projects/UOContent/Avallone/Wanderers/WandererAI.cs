using System;
using System.Collections.Generic;
using Server.Logging;

namespace Server.Mobiles;

// A wanderer's AI (wanderer prototype, 2026-10-14): stock melee combat, plus walking to the place its goal names.
// While travelling it only fights what attacks it (BaseWanderer.IsEnemy decides); while hunting it fights monsters
// and roams the hunting ground (Home/RangeHome); otherwise it idles near home like any NPC.
//
// Long walks: the stock pathfinder only plans inside a 38-tile box (BitmapAStarAlgorithm.AreaSize), so a goal 150
// tiles away never gets a route and the NPC never moves (seen in the first dev test, 2026-10-14). He walks in hops
// instead: stepping stones along a route searched at the start of the trip (WandererRoutes); if that search finds
// nothing, a stone up to HopLength tiles toward the goal, nudged sideways until the pathfinder can reach it.
public class WandererAI : MeleeAI
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WandererAI));

    private const int HopLength = 24;
    private const int DirectRange = 30;
    private static readonly int[] Sideways = [0, 6, -6, 12, -12, 18, -18];
    private static readonly int[] Lengths = [HopLength, 16, 10];

    private WandererPlace _for;
    private IPoint3D _hop;
    private Point3D _hopLocation;
    private List<Point3D> _stones; // this trip's route (WandererRoutes), null if the search found none
    private int _stoneAt;
    private long _nextReplan;

    public WandererAI(BaseCreature m) : base(m)
    {
    }

    public override bool DoActionWander()
    {
        if (Mobile is not BaseWanderer w || w.TravelTarget is not { } target)
        {
            _for = null;
            return Mobile is BaseWanderer { IsRecovering: true } || base.DoActionWander();
        }

        // Something attacked him on the road: stock combat handles it, then he walks on.
        if (AcquireFocusMob(Mobile.RangePerception, Mobile.FightMode, false, false, true))
        {
            Mobile.Combatant = Mobile.FocusMob;
            Action = ActionType.Combat;
            return true;
        }

        Mobile.Warmode = false;

        if (Mobile.InRange(target.Location, target.Range))
        {
            _for = null;
            w.Arrive();
            return true;
        }

        if (!CanMoveNow(out _))
        {
            return true;
        }

        if (_for != target)
        {
            _for = target;
            Replan(target);
        }

        if (_hop == null || Mobile.InRange(_hopLocation, 1))
        {
            PickHop(target);
        }

        if (!MoveToPoint(_hop, _hop == target.Goal ? target.Range : 1) && !Mobile.InRange(_hopLocation, 1))
        {
            _hop = null; // no progress toward this stone: choose another next tick (the stuck watchdog backs this up)
            if (Core.TickCount >= _nextReplan)
            {
                Replan(target); // pushed off the route (a fight, a crowd): search again from here
            }
        }

        return true;
    }

    private void Replan(WandererPlace target)
    {
        _nextReplan = Core.TickCount + 20000;
        var started = Core.TickCount;
        _stones = WandererRoutes.FindStones(Mobile, Mobile.Location, target.Location);
        logger.Information("Wanderer {Name}: route from {From} to {Target}: {Stones} ({Ms} ms)", Mobile.Name,
            Mobile.Location, target.Key, _stones == null ? "none found" : $"{_stones.Count} stones", Core.TickCount - started);
        _stoneAt = 0;
        _hop = null;
    }

    private void PickHop(WandererPlace target)
    {
        var from = Mobile.Location;
        var to = target.Location;

        if (Mobile.InRange(to, DirectRange) && new MovementPath(Mobile, to).Success)
        {
            SetHop(target.Goal, to);
            return;
        }

        // This trip's searched route (the owner, chat, 2026-10-15): the next stone not yet reached.
        if (_stones != null)
        {
            while (_stoneAt < _stones.Count - 1 && Mobile.InRange(_stones[_stoneAt], 1))
            {
                _stoneAt++;
            }

            var stone = _stones[_stoneAt];
            if (stone == to)
            {
                SetHop(target.Goal, to);
            }
            else
            {
                SetHop(stone, stone);
            }

            return;
        }

        if (Mobile.InRange(to, DirectRange))
        {
            SetHop(target.Goal, to);
            return;
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var ux = dx / len;
        var uy = dy / len;
        var map = Mobile.Map;

        foreach (var length in Lengths)
        {
            foreach (var side in Sideways)
            {
                // Forward along the line, then sideways (perpendicular) by `side` tiles.
                var x = from.X + (int)Math.Round(ux * length - uy * side);
                var y = from.Y + (int)Math.Round(uy * length + ux * side);
                var z = map.GetAverageZ(x, y);
                var p = new Point3D(x, y, z);

                if (map.CanSpawnMobile(x, y, z) && new MovementPath(Mobile, p).Success)
                {
                    SetHop(p, p);
                    return;
                }
            }
        }

        // Nothing reachable: aim straight at the goal and let the stuck watchdog decide.
        SetHop(target.Goal, to);
    }

    private void SetHop(IPoint3D goal, Point3D location)
    {
        _hop = goal;
        _hopLocation = location;
    }
}
