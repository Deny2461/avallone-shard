using System;
using System.Collections.Generic;
using Server.Accounting;
using Server.Logging;
using Server.Mobiles;
using Server.Network;

namespace Server.Avallone.Rumours;

// Phase 5a - the played-time world clock (the owner, chat, 2026-10-01).
// Counts seconds while at least one non-staff player is logged in. Saved with the world, so restoring an older
// save rewinds the clock with it - rumours spread on this clock, so a restore also "un-spreads" them.
// Also keeps each character's last-seen PLAYED second (for the "return after a long absence" deed): saved with the
// world like the clock, so a restore or a crash rewinds both together (Codex code review 2026-10-28 finding 5).
// Saved as its own file in world saves ("AvalloneClock"). Never delete this type once it is in saves.
//
// 5c-2 step 2a (design v1.2 D2b + Codex round 2 F2/F3, the owner's): a second clock, the MEMORY
// clock, ticks only when this one does, at rate r(x) = (1 + k) / (x + k), k = avallone.memory.clockK (default 1), where
// x = the effective number of players over the last 7 UTC days = (sum h)^2 / (sum h^2) of each non-staff account's
// played hours h (equals the headcount when everyone plays equally; a 10-minute visitor barely counts), minimum 1.
// Named NPCs fade memories on it. It has its OWN per-account activity record (not Account.LastLogin), saved with the
// world with the clocks, so a restore rewinds all three together. Fractional memory-seconds are kept (a double): at 20
// players a 10-s tick adds under a second and must never truncate to zero. Rate changes apply to future ticks only.
public class PlayedClock : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(PlayedClock));
    private const int Version = 2; // 1: last-seen in played seconds (0 held wall-clock logouts; discarded); 2: memory clock
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private static PlayedClock _instance;
    private static readonly Dictionary<Serial, long> _lastSeen = new();

    // Played seconds since the clock started.
    public static long Seconds { get; private set; }
    public static double Hours => Seconds / 3600.0;

    // 5c-2 2a: the memory clock (memory-seconds) and what sets its rate.
    public static double MemorySeconds { get; private set; }
    public static double Rate { get; private set; } = 1.0;
    public static double EffectivePlayers { get; private set; } = 1.0;
    public static double ClockK { get; private set; } = 1.0;
    private const int WindowDays = 7;
    // account username -> UTC day number -> played seconds that day (only the last WindowDays days are kept)
    private static readonly Dictionary<string, Dictionary<int, long>> _activity = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _tickAccounts = new(StringComparer.OrdinalIgnoreCase);

    public static int Day(DateTime utc) => (int)(utc.Date - DateTime.UnixEpoch.Date).TotalDays;

    // r(x) = (1 + k) / (x + k); k >= 0 and finite, else 1; x >= 1.
    public static double RateFor(double x, double k)
    {
        if (!double.IsFinite(k) || k < 0)
        {
            k = 1;
        }

        if (!double.IsFinite(x) || x < 1)
        {
            x = 1;
        }

        return (1 + k) / (x + k);
    }

    // The effective number of players from per-account played hours: (sum h)^2 / (sum h^2), at least 1.
    public static double EffectiveFrom(IEnumerable<double> hours)
    {
        double sum = 0, sq = 0;
        foreach (var h in hours)
        {
            if (h > 0 && double.IsFinite(h))
            {
                sum += h;
                sq += h * h;
            }
        }

        return sq <= 0 ? 1 : Math.Max(1, sum * sum / sq);
    }

    // Recomputed hourly from the activity record (and after a load); the account names never leave the shard.
    public static void RefreshRate(DateTime utc)
    {
        var today = Day(utc);
        var hours = new List<double>();
        List<string> empty = null;
        foreach (var (account, days) in _activity)
        {
            long secs = 0;
            List<int> old = null;
            foreach (var (day, s) in days)
            {
                if (day > today - WindowDays && day <= today)
                {
                    secs += s;
                }
                else if (day <= today - WindowDays)
                {
                    (old ??= []).Add(day);
                }
            }

            old?.ForEach(d => days.Remove(d));
            if (days.Count == 0)
            {
                (empty ??= []).Add(account);
            }

            hours.Add(secs / 3600.0);
        }

        empty?.ForEach(a => _activity.Remove(a));
        EffectivePlayers = EffectiveFrom(hours);
        Rate = RateFor(EffectivePlayers, ClockK);
    }

    // One tick of play: who was online (non-staff accounts, each counted once however many characters it has on).
    internal static void CountTick(IEnumerable<string> accounts, DateTime utc, long seconds)
    {
        var day = Day(utc);
        foreach (var a in accounts)
        {
            if (!_activity.TryGetValue(a, out var days))
            {
                _activity[a] = days = new Dictionary<int, long>();
            }

            days[day] = (days.TryGetValue(day, out var s) ? s : 0) + seconds;
        }
    }

    internal static void SetSecondsForTests(long seconds) => Seconds = seconds;   // rumour step, Part A tests

    internal static void ResetMemoryForTests()
    {
        _activity.Clear();
        MemorySeconds = Seconds; // the played clock itself is shared with other tests: left alone
        Rate = EffectivePlayers = 1;
    }

    internal static void TickForTests(IEnumerable<string> accounts, DateTime utc)
    {
        CountTick(accounts, utc, (long)Step.TotalSeconds);
        Seconds += (long)Step.TotalSeconds;
        MemorySeconds += Step.TotalSeconds * Rate;
    }

    public PlayedClock() : base("AvalloneClock", 10)
    {
    }

    public static void Configure()
    {
        _instance = new PlayedClock();
        EventSink.Logout += m =>
        {
            if (m is PlayerMobile { AccessLevel: AccessLevel.Player })
            {
                _lastSeen[m.Serial] = Seconds;
            }
        };
    }

    public static void Initialize()
    {
        ClockK = ServerConfiguration.GetOrUpdateSetting("avallone.memory.clockK", 1.0);
        if (!double.IsFinite(ClockK) || ClockK < 0)
        {
            logger.Warning("avallone.memory.clockK {K} is not a finite number >= 0; using 1", ClockK);
            ClockK = 1;
        }

        RefreshRate(Core.Now);
        logger.Information("Avallone rumours: played clock at {Hours:F2} h; memory clock {Mem:F2} h at rate {Rate:F3} " +
                           "(effective players {X:F2}, k {K})", Hours, MemorySeconds / 3600, Rate, EffectivePlayers, ClockK);
        Timer.StartTimer(Step, Step, Tick);
    }

    public static bool AnyPlayerOnline()
    {
        foreach (var ns in NetState.Instances)
        {
            if (ns.Mobile is PlayerMobile { AccessLevel: AccessLevel.Player })
            {
                return true;
            }
        }

        return false;
    }

    private static void Tick()
    {
        var any = false;
        _tickAccounts.Clear();
        foreach (var ns in NetState.Instances)
        {
            if (ns.Mobile is PlayerMobile { AccessLevel: AccessLevel.Player } pm)
            {
                if (!any)
                {
                    Seconds += (long)Step.TotalSeconds; // once per tick however many are online
                    MemorySeconds += Step.TotalSeconds * Rate; // 5c-2 2a: the memory clock, at the current rate
                    any = true;
                }

                _lastSeen[pm.Serial] = Seconds; // a crash loses at most one save interval of this, like everything else
                if (pm.Account is Account { AccessLevel: AccessLevel.Player } acct)
                {
                    _tickAccounts.Add(acct.Username); // staff accounts never count, even on a player-level character
                }
            }
        }

        if (any)
        {
            CountTick(_tickAccounts, Core.Now, (long)Step.TotalSeconds);
            if (Seconds % 3600 < (long)Step.TotalSeconds)
            {
                RefreshRate(Core.Now); // hourly, in played time
            }
        }
    }

    // Played seconds since the character was last seen in the world (false = never seen since 5a).
    public static bool TryGetPlayedAbsence(Mobile m, out long seconds)
    {
        seconds = 0;
        if (!_lastSeen.TryGetValue(m.Serial, out var seen))
        {
            return false;
        }

        seconds = Math.Max(0, Seconds - seen);
        return true;
    }

    public static void Forget(Serial s) => _lastSeen.Remove(s);

    // GM test hook only ([Rumour clock add <hours>): moves the clock forward so spreading can be watched without hours of play.
    public static bool Advance(double hours)
    {
        if (!double.IsFinite(hours) || hours <= 0 || hours > 1000)
        {
            return false; // forward only: a rewind would not reconcile what already spread
        }

        Seconds += (long)(hours * 3600);
        MemorySeconds += hours * 3600 * Rate; // 5c-2 2a: the test hook moves both clocks by the same rule
        return true;
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.Write(Seconds);
        writer.WriteEncodedInt(_lastSeen.Count);
        foreach (var (serial, seen) in _lastSeen)
        {
            writer.Write(serial);
            writer.Write(seen);
        }

        writer.Write(MemorySeconds); // v2
        writer.WriteEncodedInt(_activity.Count);
        foreach (var (account, days) in _activity)
        {
            writer.Write(account);
            writer.WriteEncodedInt(days.Count);
            foreach (var (day, secs) in days)
            {
                writer.WriteEncodedInt(day);
                writer.Write(secs);
            }
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        var version = reader.ReadEncodedInt();
        Seconds = reader.ReadLong();
        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var serial = reader.ReadSerial();
            if (version >= 1)
            {
                _lastSeen[serial] = reader.ReadLong();
            }
            else
            {
                reader.ReadDateTime(); // dev-only v0 wall-clock logouts: not meaningful in played time
            }
        }

        _activity.Clear();
        if (version >= 2)
        {
            MemorySeconds = reader.ReadDouble();
            var accounts = reader.ReadEncodedInt();
            for (var i = 0; i < accounts; i++)
            {
                var name = reader.ReadString();
                var days = new Dictionary<int, long>();
                var n = reader.ReadEncodedInt();
                for (var j = 0; j < n; j++)
                {
                    var day = reader.ReadEncodedInt();
                    days[day] = reader.ReadLong();
                }

                if (!string.IsNullOrEmpty(name))
                {
                    _activity[name] = days;
                }
            }
        }
        else
        {
            MemorySeconds = Seconds; // a save from before 5c-2: as if the memory clock had always run solo
        }

        if (!double.IsFinite(MemorySeconds) || MemorySeconds < 0 || MemorySeconds > Seconds)
        {
            logger.Warning("Memory clock {Mem} not valid against played clock {Pt}; reset to the played clock", MemorySeconds, Seconds);
            MemorySeconds = Seconds;
        }
    }
}
