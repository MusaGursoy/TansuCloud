// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Hybrid;

namespace TansuCloud.Observability.Caching;

/// <summary>
/// Extension methods that wrap HybridCache operations with shared instrumentation.
/// </summary>
public static class HybridCacheInstrumentationExtensions
{
    /// <summary>
    /// Wraps the standard HybridCache get-or-create pattern with metrics for hits, misses, and latency.
    /// </summary>
    /// <typeparam name="T">The entry type stored in the cache.</typeparam>
    /// <param name="cache">The HybridCache instance.</param>
    /// <param name="key">Cache key to resolve.</param>
    /// <param name="factory">Factory invoked on cache misses.</param>
    /// <param name="service">Stable service identifier (e.g., "storage").</param>
    /// <param name="operation">Stable operation identifier (e.g., "objects.list").</param>
    /// <param name="options">Optional cache entry options.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The cached or newly created value.</returns>
    public static async ValueTask<T?> GetOrCreateWithMetricsAsync<T>(
        this HybridCache cache,
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
    string service,
    string operation,
    HybridCacheEntryOptions? options = null,
    CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(service);
        ArgumentException.ThrowIfNullOrEmpty(operation);

        var startTimestamp = Stopwatch.GetTimestamp();
        var wasHit = true;

        async ValueTask<T?> WrappedFactory(CancellationToken token)
        {
            wasHit = false;
            var created = await factory(token).ConfigureAwait(false);
            HybridCacheMetrics.RecordSet(service, operation);
            return created;
        }

        try
        {
            var result = await cache.GetOrCreateAsync(
                    key,
                    WrappedFactory,
                    options,
                    tags: null,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (wasHit)
            {
                HybridCacheMetrics.RecordHit(service, operation);
                HybridCacheMetrics.RecordLatency(service, operation, "hit", elapsedMs);
            }
            else
            {
                HybridCacheMetrics.RecordMiss(service, operation);
                HybridCacheMetrics.RecordLatency(service, operation, "miss", elapsedMs);
            }
            return result;
        }
        catch
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            HybridCacheMetrics.RecordLatency(service, operation, "error", elapsedMs);
            throw;
        }
    }
} // End of Class HybridCacheInstrumentationExtensions
