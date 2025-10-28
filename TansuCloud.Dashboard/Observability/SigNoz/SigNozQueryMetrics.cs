// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// OpenTelemetry metrics for SigNoz API query operations.
/// Enables self-monitoring of the Dashboard's observability backend integration.
/// </summary>
public static class SigNozQueryMetrics
{
    private static readonly Meter Meter =
        new(
            "TansuCloud.Dashboard.SigNoz",
            typeof(SigNozQueryMetrics).Assembly.GetName().Version?.ToString()
        );

    /// <summary>
    /// Counter: Total number of SigNoz API calls made by the Dashboard service.
    /// Tags: endpoint, status_code, cache_hit.
    /// </summary>
    public static readonly Counter<long> ApiCallsTotal = Meter.CreateCounter<long>(
        name: "signoz_api_calls_total",
        unit: "calls",
        description: "Total SigNoz API calls made by Dashboard service"
    );

    /// <summary>
    /// Histogram: Duration of SigNoz API calls in milliseconds.
    /// Tags: endpoint, status_code.
    /// </summary>
    public static readonly Histogram<double> ApiDurationMs = Meter.CreateHistogram<double>(
        name: "signoz_api_duration_ms",
        unit: "ms",
        description: "Duration of SigNoz API calls in milliseconds"
    );

    /// <summary>
    /// Counter: Total number of cache hits for SigNoz query results.
    /// Tags: endpoint.
    /// </summary>
    public static readonly Counter<long> CacheHitsTotal = Meter.CreateCounter<long>(
        name: "signoz_cache_hits_total",
        unit: "hits",
        description: "Number of cache hits for SigNoz query results"
    );

    /// <summary>
    /// Counter: Total number of cache misses requiring SigNoz API calls.
    /// Tags: endpoint.
    /// </summary>
    public static readonly Counter<long> CacheMissesTotal = Meter.CreateCounter<long>(
        name: "signoz_cache_misses_total",
        unit: "misses",
        description: "Number of cache misses requiring SigNoz API calls"
    );

    /// <summary>
    /// Counter: Total number of failed SigNoz API calls (timeouts, errors, non-2xx).
    /// Tags: endpoint, error_type.
    /// </summary>
    public static readonly Counter<long> ApiErrorsTotal = Meter.CreateCounter<long>(
        name: "signoz_api_errors_total",
        unit: "errors",
        description: "Total number of failed SigNoz API calls"
    );

    /// <summary>
    /// Helper method to record an API call with standard tags.
    /// </summary>
    public static void RecordApiCall(
        string endpoint,
        int statusCode,
        double durationMs,
        bool cacheHit = false
    )
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "status_code", statusCode.ToString() },
            { "cache_hit", cacheHit.ToString().ToLowerInvariant() }
        };

        ApiCallsTotal.Add(1, tags);
        ApiDurationMs.Record(
            durationMs,
            new TagList { { "endpoint", endpoint }, { "status_code", statusCode.ToString() } }
        );

        if (cacheHit)
        {
            CacheHitsTotal.Add(1, new TagList { { "endpoint", endpoint } });
        }
        else
        {
            CacheMissesTotal.Add(1, new TagList { { "endpoint", endpoint } });
        }
    }

    /// <summary>
    /// Helper method to record an API error with standard tags.
    /// </summary>
    public static void RecordApiError(string endpoint, string errorType)
    {
        var tags = new TagList { { "endpoint", endpoint }, { "error_type", errorType } };
        ApiErrorsTotal.Add(1, tags);
    }
} // End of Class SigNozQueryMetrics
