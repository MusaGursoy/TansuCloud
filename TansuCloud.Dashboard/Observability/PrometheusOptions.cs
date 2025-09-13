// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability;

/// <summary>
/// Options for connecting to Prometheus via the Dashboard backend.
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>
    /// Base URL of the Prometheus server, e.g. http://prometheus:9090
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Timeout for Prometheus HTTP calls in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum allowed range duration in minutes for UI queries.
    /// </summary>
    public int MaxRangeMinutes { get; set; } = 60; // 1 hour

    /// <summary>
    /// Maximum step resolution in seconds to avoid overloading Prometheus.
    /// </summary>
    public int MaxStepSeconds { get; set; } = 60; // 1 point per minute

    /// <summary>
    /// Default range window in minutes used by UI when not specified.
    /// </summary>
    public int DefaultRangeMinutes { get; set; } = 10;

    /// <summary>
    /// In-memory proxy cache TTL seconds for chart responses.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 10;
} // End of Class PrometheusOptions
