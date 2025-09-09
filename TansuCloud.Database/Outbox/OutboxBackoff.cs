// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;

namespace TansuCloud.Database.Outbox;

public static class OutboxBackoff
{
    // Exponential backoff with jitter. Attempts start at 1. Caps at maxSeconds and maxPow.
    public static TimeSpan Compute(int attempts, int maxSeconds = 300, int maxPow = 8, int jitterMsMax = 1000, Random? rng = null)
    {
        var a = Math.Max(1, attempts);
        var baseSeconds = Math.Pow(2, Math.Min(maxPow, a));
        var delay = TimeSpan.FromSeconds(Math.Min(maxSeconds, baseSeconds));
        var r = (rng ?? Random.Shared).Next(0, jitterMsMax + 1);
        return delay + TimeSpan.FromMilliseconds(r);
    } // End of Method Compute
} // End of Class OutboxBackoff
