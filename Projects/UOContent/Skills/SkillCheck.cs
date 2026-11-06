using System;
using System.Runtime.CompilerServices;
using Server.Factions;
using Server.Logging;
using Server.Mobiles;

namespace Server.Misc;

public static class SkillCheck
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(SkillCheck));

    private static int _statMax;
    private static double _statGainChanceMultiplier;
    private static double _skillGainChanceMultiplier;
    private static bool _logGains;

    // Avallone: exposed so tests can set the multiplier; the server sets it from config in Configure().
    internal static double SkillGainChanceMultiplier
    {
        get => _skillGainChanceMultiplier;
        set => _skillGainChanceMultiplier = value;
    }

    internal static TimeSpan StatGainDelay => _statGainDelay;
    internal static TimeSpan PetStatGainDelay => _petStatGainDelay;
    private static bool _usePub45StatGain;
    private static double _primaryStatGainChance;
    private static TimeSpan _statGainDelay;
    private static TimeSpan _petStatGainDelay;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RollStatIncreaseChance(double statGain) =>
        statGain / 33.3 * _statGainChanceMultiplier > Utility.RandomDouble();

    public static void Configure()
    {
        _statMax = ServerConfiguration.GetOrUpdateSetting("stats.statMax", Core.LBR ? 125 : 100);
        _statGainChanceMultiplier = ServerConfiguration.GetOrUpdateSetting("stats.gainChanceMultiplier", 1.0);
        // Avallone: shard-wide skill gain chance multiplier (the owner, 2026-10-22: x1.5)
        _skillGainChanceMultiplier = ServerConfiguration.GetOrUpdateSetting("skills.gainChanceMultiplier", 1.0);
        _primaryStatGainChance = ServerConfiguration.GetSetting("stats.primaryStatGainChance", 0.75);
        _statGainDelay = ServerConfiguration.GetSetting("stats.gainDelay", TimeSpan.FromMinutes(Core.ML ? 0.05 : 10));
        _petStatGainDelay = ServerConfiguration.GetSetting("stats.petGainDelay", TimeSpan.FromMinutes(5.0));

        // Publish 45 - Preparation for UOKR
        _usePub45StatGain = ServerConfiguration.GetSetting("stats.usePub45StatGain", Core.ML);

        // Avallone: one log line per player/pet skill or stat gain, so the gain rate can be checked from the journal
        _logGains = ServerConfiguration.GetOrUpdateSetting("avallone.logGains", true);
        logger.Information(
            "Skill gain settings: skill chance x{Mult}, stat gain delay {Delay}, pet stat gain delay {PetDelay}, gain log {Log}; stat rules {Rules}, stat chance x{StatMult}, stat max {StatMax}",
            _skillGainChanceMultiplier, _statGainDelay, _petStatGainDelay, _logGains ? "on" : "off",
            _usePub45StatGain ? "Publish 45 (only on a skill gain)" : "legacy (any successful use)", _statGainChanceMultiplier, _statMax
        );
    }

    private static bool ShouldLog(Mobile from) => _logGains && (from.Player || from is BaseCreature { Controlled: true });

    public static void Initialize()
    {
        Mobile.SkillCheckLocationHandler = Mobile_SkillCheckLocation;
        Mobile.SkillCheckDirectLocationHandler = Mobile_SkillCheckDirectLocation;

        Mobile.SkillCheckTargetHandler = Mobile_SkillCheckTarget;
        Mobile.SkillCheckDirectTargetHandler = Mobile_SkillCheckDirectTarget;
    }

    public static bool Mobile_SkillCheckLocation(Mobile from, SkillName skillName, double minSkill, double maxSkill)
    {
        var skill = from.Skills[skillName];

        if (skill == null)
        {
            return false;
        }

        var success = CheckLocation(from, skill, minSkill, maxSkill);
        SkillEvents.InvokeSkillUsed(from, skill, success);
        return success;
    }

    private static bool CheckLocation(Mobile from, Skill skill, double minSkill, double maxSkill)
    {
        var value = skill.Value;

        if (value < minSkill)
        {
            return false; // Too difficult
        }

        if (value >= maxSkill || minSkill >= maxSkill)
        {
            return true; // No challenge
        }

        var chance = (value - minSkill) / (maxSkill - minSkill);

        var size = AntiMacroSystem.Settings.LocationSize;
        var loc = new Point2D(from.Location.X / size, from.Location.Y / size);
        return CheckSkill(from, skill, loc, chance);
    }

    public static bool Mobile_SkillCheckDirectLocation(Mobile from, SkillName skillName, double chance)
    {
        var skill = from.Skills[skillName];

        if (skill == null)
        {
            return false;
        }

        var success = CheckDirectLocation(from, skill, chance);
        SkillEvents.InvokeSkillUsed(from, skill, success);
        return success;
    }

    private static bool CheckDirectLocation(Mobile from, Skill skill, double chance)
    {
        if (chance < 0.0)
        {
            return false; // Too difficult
        }

        if (chance >= 1.0)
        {
            return true; // No challenge
        }

        var size = AntiMacroSystem.Settings.LocationSize;
        var loc = new Point2D(from.Location.X / size, from.Location.Y / size);
        return CheckSkill(from, skill, loc, chance);
    }

    public static bool CheckSkill(Mobile from, Skill skill, object amObj, double chance)
    {
        if (from.Skills.Cap == 0)
        {
            return false;
        }

        var success = chance >= Utility.RandomDouble();

        var region = from.Region;
        if (from.Alive && region.AllowGain(from, skill, amObj))
        {
            if (skill.Base < 10.0) // Gain regardless of the AllowGain check
            {
                var before = skill.BaseFixedPoint;
                Gain(from, skill);
                LogSkillGain(from, skill, before, -1);
            }
            else if (AllowGain(from, skill, amObj))
            {
                var gc = ComputeSkillGainChance(from, skill, chance, success, _skillGainChanceMultiplier);

                if (gc >= Utility.RandomDouble())
                {
                    var before = skill.BaseFixedPoint;
                    Gain(from, skill);
                    LogSkillGain(from, skill, before, gc);
                }
            }

            // Gain one stat per 10 minutes, regardless of whether you gain in the skill
            if (!_usePub45StatGain && success)
            {
                LegacyGain(from, skill.Info);
            }
        }

        return success;
    }

    public static bool Mobile_SkillCheckTarget(
        Mobile from, SkillName skillName, object target, double minSkill,
        double maxSkill
    )
    {
        var skill = from.Skills[skillName];

        if (skill == null)
        {
            return false;
        }

        var success = CheckTarget(from, skill, target, minSkill, maxSkill);
        SkillEvents.InvokeSkillUsed(from, skill, success);
        return success;
    }

    private static bool CheckTarget(Mobile from, Skill skill, object target, double minSkill, double maxSkill)
    {
        var value = skill.Value;

        if (value < minSkill)
        {
            return false; // Too difficult
        }

        if (value >= maxSkill || minSkill >= maxSkill)
        {
            return true; // No challenge
        }

        var chance = (value - minSkill) / (maxSkill - minSkill);

        return CheckSkill(from, skill, target, chance);
    }

    public static bool Mobile_SkillCheckDirectTarget(Mobile from, SkillName skillName, object target, double chance)
    {
        var skill = from.Skills[skillName];

        if (skill == null)
        {
            return false;
        }

        var success = CheckDirectTarget(from, skill, target, chance);
        SkillEvents.InvokeSkillUsed(from, skill, success);
        return success;
    }

    private static bool CheckDirectTarget(Mobile from, Skill skill, object target, double chance)
    {
        if (chance < 0.0)
        {
            return false; // Too difficult
        }

        if (chance >= 1.0)
        {
            return true; // No challenge
        }

        return CheckSkill(from, skill, target, chance);
    }

    // The chance that one use of a skill raises it. Same math as stock ModernUO, plus the Avallone multiplier
    // (applied after the 1% floor, before the pet x2). A result of 1.0 or more means every use gains.
    internal static double ComputeSkillGainChance(Mobile from, Skill skill, double chance, bool success, double multiplier)
    {
        var gc = (double)(from.Skills.Cap - from.Skills.Total) / from.Skills.Cap;
        gc += (skill.Cap - skill.Base) / skill.Cap;
        gc /= 2;

        gc += (1.0 - chance) * (success ? 0.5 : Core.AOS ? 0.0 : 0.2);
        gc /= 2;

        gc *= skill.Info.GainFactor;

        if (gc < 0.01)
        {
            gc = 0.01;
        }

        gc *= multiplier;

        if (from is BaseCreature { Controlled: true })
        {
            gc *= 2;
        }

        return gc;
    }

    // gc < 0 = the under-10.0 "always gains" path
    private static void LogSkillGain(Mobile from, Skill skill, int beforeFixed, double gc)
    {
        if (skill.BaseFixedPoint == beforeFixed || !ShouldLog(from))
        {
            return;
        }

        logger.Information(
            "Skill gain: {Name} {Skill} {Before:F1} -> {After:F1} (chance {Chance}, x{Mult})",
            from.Name, skill.SkillName, beforeFixed / 10.0, skill.Base,
            gc < 0 ? "always, under 10" : gc.ToString("F4"), _skillGainChanceMultiplier
        );
    }

    private static bool AllowGain(Mobile from, Skill skill, object obj)
    {
        if (Core.AOS && Faction.InSkillLoss(from)) // Changed some time between the introduction of AoS and SE.
        {
            return false;
        }

        return from is not PlayerMobile mobile || AntiMacroSystem.AntiMacroCheck(mobile, skill, obj);
    }

    public static void Gain(Mobile from, Skill skill)
    {
        if (from is BaseCreature { IsDeadPet: true })
        {
            return;
        }

        if (skill.SkillName == SkillName.Focus && from is BaseCreature)
        {
            return;
        }

        if (skill.Base < skill.Cap && skill.Lock == SkillLock.Up)
        {
            var toGain = 1;

            if (skill.Base <= 10.0)
            {
                toGain = Utility.Random(4) + 1;
            }

            var skills = from.Skills;

            if (from.Player && skills.Total / (double)skills.Cap >= Utility.RandomDouble())
            {
                for (var i = 0; i < skills.Length; ++i)
                {
                    var toLower = skills[i];

                    if (toLower != skill && toLower.Lock == SkillLock.Down && toLower.BaseFixedPoint >= toGain)
                    {
                        toLower.BaseFixedPoint = Math.Max(toLower.BaseFixedPoint - toGain, 0);
                        break;
                    }
                }
            }

            if (from is PlayerMobile pm && skill.SkillName == pm.AcceleratedSkill && pm.AcceleratedStart > Core.Now)
            {
                toGain *= Utility.RandomMinMax(2, 5);
            }

            if (!from.Player || skills.Total < skills.Cap)
            {
                skill.BaseFixedPoint = Math.Min(skill.BaseFixedPoint + toGain, skill.CapFixedPoint);
            }
        }

        if (_usePub45StatGain && skill.Lock == SkillLock.Up)
        {
            var info = skill.Info;

            var primaryStat = info.PrimaryStat;
            var secondaryStat = info.SecondaryStat;

            var primaryStatLock = primaryStat.ToLock(from);
            var secondaryStatLock = secondaryStat.ToLock(from);

            if (primaryStatLock != StatLockType.Up && secondaryStatLock != StatLockType.Up)
            {
                return;
            }

            // Flat 1 in 20 chance to gain anything
            if (0.05 * _statGainChanceMultiplier > Utility.RandomDouble())
            {
                // 75% for primary, 25% for secondary - Unless primary is not set to gain.
                var statToGain = primaryStatLock is StatLockType.Up && _primaryStatGainChance > Utility.RandomDouble()
                    ? primaryStat
                    : secondaryStat;

                GainStat(from, statToGain);
            }
        }
    }

    // Avallone diagnostic: how many successful uses have been checked for a stat roll, per character (logged every 25).
    private static readonly System.Collections.Generic.Dictionary<Serial, int> _statChecks = new();

    public static void LegacyGain(Mobile from, SkillInfo info)
    {
        if (ShouldLog(from))
        {
            _statChecks.TryGetValue(from.Serial, out var n);
            _statChecks[from.Serial] = ++n;

            if (n % 25 == 0)
            {
                logger.Information(
                    "Stat check: {Name} {Count} successful uses checked for stat rolls so far (locks Str {SL} Dex {DL} Int {IL}; total {Total} of cap {Cap})",
                    from.Name, n, from.StrLock, from.DexLock, from.IntLock, from.RawStatTotal, from.StatCap
                );
            }
        }

        if (info.StrGain > 0 && from.StrLock == StatLockType.Up && RollStatIncreaseChance(info.StrGain))
        {
            GainStat(from, Stat.Str);
        }

        if (info.DexGain > 0 && from.DexLock == StatLockType.Up && RollStatIncreaseChance(info.DexGain))
        {
            GainStat(from, Stat.Dex);
        }

        if (info.IntGain > 0 && from.IntLock == StatLockType.Up && RollStatIncreaseChance(info.IntGain))
        {
            GainStat(from, Stat.Int);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatLockType ToLock(this Stat stat, Mobile from) =>
        stat switch
        {
            Stat.Str => from.StrLock,
            Stat.Dex => from.DexLock,
            Stat.Int => from.IntLock,
            _ => StatLockType.Up
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanLower(Mobile from, Stat stat)
    {
        return stat switch
        {
            Stat.Str => from.StrLock == StatLockType.Down && from.RawStr > 10,
            Stat.Dex => from.DexLock == StatLockType.Down && from.RawDex > 10,
            Stat.Int => from.IntLock == StatLockType.Down && from.RawInt > 10,
            _        => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanRaise(Mobile from, Stat stat)
    {
        if (!(from is BaseCreature creature && creature.Controlled))
        {
            if (from.RawStatTotal >= from.StatCap)
            {
                return false;
            }
        }

        return stat switch
        {
            Stat.Str => from.StrLock == StatLockType.Up && from.RawStr < _statMax,
            Stat.Dex => from.DexLock == StatLockType.Up && from.RawDex < _statMax,
            Stat.Int => from.IntLock == StatLockType.Up && from.RawInt < _statMax,
            _        => false
        };
    }

    public static void IncreaseStat(Mobile from, Stat stat, bool atrophy)
    {
        atrophy = atrophy || from.RawStatTotal >= from.StatCap;

        switch (stat)
        {
            case Stat.Str:
                {
                    if (atrophy)
                    {
                        if (CanLower(from, Stat.Dex) && (from.RawDex < from.RawInt || !CanLower(from, Stat.Int)))
                        {
                            from.RawDex -= 1;
                        }
                        else if (CanLower(from, Stat.Int))
                        {
                            from.RawInt -= 1;
                        }
                    }

                    if (CanRaise(from, Stat.Str))
                    {
                        from.RawStr = Math.Min(from.RawStr + 1, _statMax);
                    }

                    break;
                }
            case Stat.Dex:
                {
                    if (atrophy)
                    {
                        if (CanLower(from, Stat.Str) && (from.RawStr < from.RawInt || !CanLower(from, Stat.Int)))
                        {
                            from.RawStr -= 1;
                        }
                        else if (CanLower(from, Stat.Int))
                        {
                            from.RawInt -= 1;
                        }
                    }

                    if (CanRaise(from, Stat.Dex))
                    {
                        from.RawDex = Math.Min(from.RawDex + 1, _statMax);
                    }

                    break;
                }
            case Stat.Int:
                {
                    if (atrophy)
                    {
                        if (CanLower(from, Stat.Str) && (from.RawStr < from.RawDex || !CanLower(from, Stat.Dex)))
                        {
                            from.RawStr -= 1;
                        }
                        else if (CanLower(from, Stat.Dex))
                        {
                            from.RawDex -= 1;
                        }
                    }

                    if (CanRaise(from, Stat.Int))
                    {
                        from.RawInt = Math.Min(from.RawInt + 1, _statMax);
                    }

                    break;
                }
        }
    }

    public static void GainStat(Mobile from, Stat stat)
    {
        // Same rules as stock (one gain per stat per delay; pets use the pet delay), written once instead of three times
        // so the outcome of every stat roll can be logged (the owner, 2026-10-27: no stat gains seen since about 2026-10-19).
        var delay = from is BaseCreature { Controlled: true } ? _petStatGainDelay : _statGainDelay;
        var previousGain = stat switch
        {
            Stat.Str => from.LastStrGain,
            Stat.Dex => from.LastDexGain,
            Stat.Int => from.LastIntGain,
            _        => DateTime.MinValue
        };

        if (previousGain + delay >= Core.Now)
        {
            if (ShouldLog(from))
            {
                logger.Information(
                    "Stat roll: {Name} {Stat} passed but blocked by the timer ({Since:F1} min since last {Stat2} gain; delay {Delay})",
                    from.Name, stat, (Core.Now - previousGain).TotalMinutes, stat, delay
                );
            }

            return;
        }

        switch (stat)
        {
            case Stat.Str:
                {
                    from.LastStrGain = Core.Now;
                    break;
                }
            case Stat.Dex:
                {
                    from.LastDexGain = Core.Now;
                    break;
                }
            case Stat.Int:
                {
                    from.LastIntGain = Core.Now;
                    break;
                }
        }

        var atrophy = from.RawStatTotal / (double)from.StatCap >= Utility.RandomDouble();

        var before = GetRawStat(from, stat);
        IncreaseStat(from, stat, atrophy);
        var after = GetRawStat(from, stat);

        if (!ShouldLog(from))
        {
            return;
        }

        if (after != before)
        {
            var sincePrevious = previousGain == DateTime.MinValue ? "first since start" : $"{(Core.Now - previousGain).TotalMinutes:F1} min since last {stat} gain";
            logger.Information(
                "Stat gain: {Name} {Stat} {Before} -> {After} ({Since}; delay {Delay})",
                from.Name, stat, before, after, sincePrevious, delay
            );
        }
        else
        {
            logger.Information(
                "Stat roll: {Name} {Stat} passed but not raised (lock {Lock}, {Stat2} {Value} of max {Max}, total {Total} of cap {Cap})",
                from.Name, stat, stat.ToLock(from), stat, before, _statMax, from.RawStatTotal, from.StatCap
            );
        }
    }

    private static int GetRawStat(Mobile from, Stat stat) =>
        stat switch
        {
            Stat.Str => from.RawStr,
            Stat.Dex => from.RawDex,
            _        => from.RawInt
        };
}
