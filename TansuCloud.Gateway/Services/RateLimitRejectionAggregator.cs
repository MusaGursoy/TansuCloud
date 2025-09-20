// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using TansuCloud.Observability;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Aggregates rate limit rejections and emits a summary once per window. Keeps memory bounded.
/// </summary>
internal sealed class RateLimitRejectionAggregator
{
    private readonly ILogger<RateLimitRejectionAggregator> _logger;
    private readonly IDynamicLogLevelOverride _overrides;
    private readonly TimeSpan _window;
    private readonly Timer _timer;

    // key: routeBase|tenant -> count
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private long _total;
    private volatile RateLimitSummarySnapshot? _lastSnapshot;

    public RateLimitRejectionAggregator(
        ILogger<RateLimitRejectionAggregator> logger,
        IDynamicLogLevelOverride overrides,
        int windowSeconds
    )
    {
        _logger = logger;
        _overrides = overrides;
        _window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds));
        _timer = new Timer(Flush, null, _window, _window);
    } // End of Constructor RateLimitRejectionAggregator

    public void Report(string routeBase, string tenant, string partitionKey)
    {
        Interlocked.Increment(ref _total);
        var key = string.Concat(routeBase, "|", tenant);
        _counts.AddOrUpdate(key, 1, static (_, n) => n + 1);

        // Optional per-rejection debug when override enabled for this category
        var category = typeof(RateLimitRejectionAggregator).FullName!;
        var level =
            _overrides.Get(category)
            ?? _overrides.Get("TansuCloud.Gateway")
            ?? _overrides.Get("Microsoft.AspNetCore.RateLimiting")
            ?? _overrides.Get("RateLimits")
            ?? _overrides.Get("*");
        if (level.HasValue && level.Value <= LogLevel.Debug)
        {
            _logger.LogDebug(
                LogEvents.RateLimitRejectedDebug,
                "RateLimit rejected route='{Route}' tenant='{Tenant}' part='{Partition}'",
                routeBase,
                tenant,
                partitionKey
            );
        }
    } // End of Method Report

    private void Flush(object? _)
    {
        try
        {
            var total = Interlocked.Exchange(ref _total, 0);
            if (total == 0)
            {
                // Nothing to report
                return;
            }

            // Snapshot and clear counts
            var snapshot = _counts.ToArray();
            _counts.Clear();

            // Top 3 keys by count
            var top = snapshot.OrderByDescending(kv => kv.Value).Take(3).ToArray();
            var topString = string.Join(", ", top.Select(kv => $"{kv.Key}:{kv.Value}"));
            var topList = top.Select(kv => new RateLimitPartitionCount(kv.Key, kv.Value)).ToArray();
            _lastSnapshot = new RateLimitSummarySnapshot(
                WindowSeconds: (int)_window.TotalSeconds,
                Total: (int)total,
                EmittedAtUtc: DateTimeOffset.UtcNow,
                TopPartitions: topList
            );
            _logger.LogInformation(
                LogEvents.RateLimitRejectedSummary,
                "RateLimit summary total={Total} top=[{Top}] windowSec={Window}",
                total,
                topString,
                (int)_window.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RateLimit summary flush failed");
        }
    } // End of Method Flush

    public RateLimitSummarySnapshot? GetLastSnapshot() => _lastSnapshot;
} // End of Class RateLimitRejectionAggregator

public sealed record RateLimitPartitionCount(string Partition, int Count);
public sealed record RateLimitSummarySnapshot(int WindowSeconds, int Total, DateTimeOffset EmittedAtUtc, IReadOnlyList<RateLimitPartitionCount> TopPartitions);
