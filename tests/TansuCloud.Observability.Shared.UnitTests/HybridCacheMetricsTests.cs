// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TansuCloud.Observability.Caching;
using Xunit;

namespace TansuCloud.Observability.Shared.UnitTests;

public class HybridCacheMetricsTests
{
    [Fact]
    public async Task GetOrCreateWithMetricsAsync_Tracks_Hits_Misses_And_Latency()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();

        var measurements = new List<Measurement>();
        using var listener = CreateListener(measurements);

        static async ValueTask<int> MissFactory(CancellationToken token)
        {
            await Task.Delay(5, token);
            return 42;
        }

        // First call: miss + set
        var missValue = await cache.GetOrCreateWithMetricsAsync(
            "hybrid-cache-test",
            MissFactory,
            service: "testsvc",
            operation: "documents.list"
        );

        // Second call: hit (factory bypassed)
        var hitValue = await cache.GetOrCreateWithMetricsAsync(
            "hybrid-cache-test",
            static async token =>
            {
                await Task.Delay(1, token);
                return 99;
            },
            service: "testsvc",
            operation: "documents.list"
        );

        Assert.Equal(42, missValue);
        Assert.Equal(42, hitValue);

        var hits = measurements.Where(m => m.Name == "tansu_hybridcache_hits_total").ToList();
        var misses = measurements.Where(m => m.Name == "tansu_hybridcache_misses_total").ToList();
        var sets = measurements.Where(m => m.Name == "tansu_hybridcache_sets_total").ToList();
        var latencies = measurements.Where(m => m.Name == "tansu_hybridcache_latency_ms").ToList();

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Value);
        Assert.Equal("testsvc", hits[0].Tags["service"]);
        Assert.Equal("documents.list", hits[0].Tags["operation"]);

        Assert.Single(misses);
        Assert.Equal(1, misses[0].Value);
        Assert.Equal("testsvc", misses[0].Tags["service"]);
        Assert.Equal("documents.list", misses[0].Tags["operation"]);

        Assert.Single(sets);
        Assert.Equal(1, sets[0].Value);
        Assert.Equal("testsvc", sets[0].Tags["service"]);
        Assert.Equal("documents.list", sets[0].Tags["operation"]);

        Assert.Equal(2, latencies.Count);
        Assert.Contains(latencies, m => Equals(m.Tags["outcome"], "miss"));
        Assert.Contains(latencies, m => Equals(m.Tags["outcome"], "hit"));
    }

    [Fact]
    public void RecordEviction_Adds_Reason_Tag()
    {
        var measurements = new List<Measurement>();
        using var listener = CreateListener(measurements);

        HybridCacheMetrics.RecordEviction("database", "documents.delete", "version_increment");

        var evictions = measurements.Where(m => m.Name == "tansu_hybridcache_evictions_total").ToList();
        Assert.Single(evictions);
        Assert.Equal(1, evictions[0].Value);
        Assert.Equal("database", evictions[0].Tags["service"]);
        Assert.Equal("documents.delete", evictions[0].Tags["operation"]);
        Assert.Equal("version_increment", evictions[0].Tags["reason"]);
    }

    private static MeterListener CreateListener(List<Measurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "TansuCloud.HybridCache")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
}
