// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace TansuCloud.Observability.Caching;

/// <summary>
/// Shared metrics for HybridCache usage across services.
/// </summary>
public static class HybridCacheMetrics
{
    public static readonly Meter Meter = new(
        "TansuCloud.HybridCache",
        typeof(HybridCacheMetrics).Assembly.GetName().Version?.ToString()
    );

    public static readonly Counter<long> Hits = Meter.CreateCounter<long>(
        name: "tansu_hybridcache_hits_total",
        unit: "hits",
        description: "Number of HybridCache hits"
    );

    public static readonly Counter<long> Misses = Meter.CreateCounter<long>(
        name: "tansu_hybridcache_misses_total",
        unit: "misses",
        description: "Number of HybridCache misses triggering factory execution"
    );

    public static readonly Counter<long> Sets = Meter.CreateCounter<long>(
        name: "tansu_hybridcache_sets_total",
        unit: "sets",
        description: "Number of cache sets performed after misses"
    );

    public static readonly Counter<long> Evictions = Meter.CreateCounter<long>(
        name: "tansu_hybridcache_evictions_total",
        unit: "evictions",
        description: "Number of cache evictions or version invalidations"
    );

    public static readonly Histogram<double> LatencyMs = Meter.CreateHistogram<double>(
        name: "tansu_hybridcache_latency_ms",
        unit: "ms",
        description: "HybridCache lookup latency in milliseconds"
    );

    /// <summary>
    /// Records a cache hit for the specified service and operation.
    /// </summary>
    /// <param name="service">Stable service identifier.</param>
    /// <param name="operation">Stable operation identifier.</param>
    public static void RecordHit(string service, string operation)
    {
        Hits.Add(1, CreateTags(service, operation));
    }

    /// <summary>
    /// Records a cache miss for the specified service and operation.
    /// </summary>
    /// <param name="service">Stable service identifier.</param>
    /// <param name="operation">Stable operation identifier.</param>
    public static void RecordMiss(string service, string operation)
    {
        Misses.Add(1, CreateTags(service, operation));
    }

    /// <summary>
    /// Records a cache set operation following a miss.
    /// </summary>
    /// <param name="service">Stable service identifier.</param>
    /// <param name="operation">Stable operation identifier.</param>
    public static void RecordSet(string service, string operation)
    {
        Sets.Add(1, CreateTags(service, operation));
    }

    /// <summary>
    /// Records an eviction or invalidation for the specified service and operation.
    /// </summary>
    /// <param name="service">Stable service identifier.</param>
    /// <param name="operation">Stable operation identifier.</param>
    /// <param name="reason">Reason for the eviction (e.g., version increment).</param>
    public static void RecordEviction(string service, string operation, string reason)
    {
        Evictions.Add(1, CreateTags(service, operation, "reason", reason));
    }

    /// <summary>
    /// Records the latency in milliseconds for a cache lookup.
    /// </summary>
    /// <param name="service">Stable service identifier.</param>
    /// <param name="operation">Stable operation identifier.</param>
    /// <param name="outcome">Outcome of the lookup (hit/miss/error).</param>
    /// <param name="latencyMs">Latency in milliseconds.</param>
    public static void RecordLatency(string service, string operation, string outcome, double latencyMs)
    {
        LatencyMs.Record(latencyMs, CreateTags(service, operation, "outcome", outcome));
    }

    private static KeyValuePair<string, object?>[] CreateTags(string service, string operation)
        => new[]
        {
            new KeyValuePair<string, object?>("service", service),
            new KeyValuePair<string, object?>("operation", operation)
        };

    private static KeyValuePair<string, object?>[] CreateTags(
        string service,
        string operation,
        string extraKey,
        object? extraValue
    )
        => new[]
        {
            new KeyValuePair<string, object?>("service", service),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>(extraKey, extraValue)
        };
} // End of Class HybridCacheMetrics
