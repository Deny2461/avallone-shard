using System;
using System.Linq;

namespace Server.Avallone.AI;

// In-memory counters for [ai status. Game thread only. Reset on restart.
public sealed class AiMetrics
{
    private const int LatencyWindow = 100;

    private readonly long[] _outcomes = new long[Enum.GetValues<AiOutcome>().Length];
    private readonly long[] _latencies = new long[LatencyWindow];
    private readonly long[] _hitches = new long[LatencyWindow]; // microseconds
    private int _hitchCount;
    private int _hitchNext;
    private int _latencyCount;
    private int _latencyNext;

    public DateTime Since { get; } = DateTime.UtcNow;

    // The headline safety metric: the longest time a single gateway call held the game thread.
    public TimeSpan MaxHitch { get; private set; }
    public TimeSpan LastHitch { get; private set; }
    public long Warmups { get; private set; }
    public long WarmupFailures { get; private set; }
    public int QueueHighWater { get; private set; }
    public string LastError { get; private set; }
    public DateTime LastErrorAt { get; private set; }

    public long this[AiOutcome outcome] => _outcomes[(int)outcome];

    public long Total => _outcomes.Sum();

    public void RecordOutcome(AiOutcome outcome, string error)
    {
        _outcomes[(int)outcome]++;

        if (error != null)
        {
            LastError = $"{outcome}: {error}";
            LastErrorAt = DateTime.UtcNow;
        }
    }

    public void RecordHitch(TimeSpan elapsed)
    {
        LastHitch = elapsed;
        if (elapsed > MaxHitch)
        {
            MaxHitch = elapsed;
        }

        _hitches[_hitchNext] = (long)(elapsed.TotalMilliseconds * 1000);
        _hitchNext = (_hitchNext + 1) % LatencyWindow;
        _hitchCount = Math.Min(_hitchCount + 1, LatencyWindow);
    }

    public void RecordWarmup(bool ok)
    {
        if (ok)
        {
            Warmups++;
        }
        else
        {
            WarmupFailures++;
        }
    }

    // Game-thread hold per request over the last 100 requests, in milliseconds; -1 when there are none.
    public double HitchPercentile(double p) =>
        _hitchCount == 0 ? -1 : Percentile(_hitches.AsSpan(0, _hitchCount).ToArray(), p) / 1000.0;

    public void RecordQueueDepth(int depth)
    {
        if (depth > QueueHighWater)
        {
            QueueHighWater = depth;
        }
    }

    public void RecordLatency(long ms)
    {
        _latencies[_latencyNext] = ms;
        _latencyNext = (_latencyNext + 1) % LatencyWindow;
        _latencyCount = Math.Min(_latencyCount + 1, LatencyWindow);
    }

    // Percentile over the last 100 successful calls; -1 when there are none.
    public long LatencyPercentile(double p)
    {
        if (_latencyCount == 0)
        {
            return -1;
        }

        return Percentile(_latencies.AsSpan(0, _latencyCount).ToArray(), p);
    }

    private static long Percentile(long[] values, double p)
    {
        Array.Sort(values);
        var index = (int)Math.Ceiling(p / 100.0 * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
