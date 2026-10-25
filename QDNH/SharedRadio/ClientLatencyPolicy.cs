using System;
namespace QDNH.SharedRadio;

// One instance per session. Never writes the hardware/global TX playback latency.
public sealed class ClientLatencyPolicy
{
    public int TargetMs { get; private set; } = 100;
    private long previousUnderruns;
    private int stableReports;
    private bool initialized;
    public int Observe(int requestedMs, int queueMs, long underruns)
    {
        requestedMs = Math.Clamp(requestedMs, 40, 500);
        underruns = Math.Max(0, underruns);
        if (!initialized) { initialized = true; TargetMs = requestedMs; previousUnderruns = underruns; return TargetMs; }
        bool interruption = underruns > previousUnderruns;
        previousUnderruns = underruns;
        if (interruption || requestedMs > TargetMs)
        {
            TargetMs = Math.Min(500, Math.Min(TargetMs + 20, Math.Max(requestedMs, TargetMs + (interruption ? 20 : 0))));
            stableReports = 0;
        }
        else if (++stableReports >= 6 && requestedMs < TargetMs && queueMs >= 0)
        { TargetMs = Math.Max(requestedMs, TargetMs - 10); stableReports = 0; }
        return TargetMs;
    }
}
