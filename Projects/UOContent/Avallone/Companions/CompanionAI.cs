namespace Server.Mobiles;

// A companion's AI: melee, except she trails her companion at 2 tiles instead of the stock pet's 1
// (the owner, chat, 2026-10-01: /).
// 2 is also her bandage reach (HealStartRange), so she can heal without moving in.
public class CompanionAI : MeleeAI
{
    private const int TrailRange = 2;

    public CompanionAI(BaseCreature m) : base(m)
    {
    }

    public override bool DoOrderFollow()
    {
        var target = Mobile.ControlTarget;

        if (target?.Deleted != false || target == Mobile)
        {
            return base.DoOrderFollow();
        }

        var distance = (int)Mobile.GetDistanceToSqrt(target);

        if (distance > Mobile.RangePerception)
        {
            return base.DoOrderFollow();
        }

        // Walk in only when further than TrailRange; never back away, so she does not jitter as he moves.
        if (distance > TrailRange)
        {
            WalkMobileRange(target, 1, 1, TrailRange);
        }

        return true;
    }
}
