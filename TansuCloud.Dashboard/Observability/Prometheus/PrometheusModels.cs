// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.Prometheus;

/// <summary>
/// Wrapper for Prometheus query results that includes staleness metadata.
/// Used when returning cached data during circuit breaker open state.
/// </summary>
/// <typeparam name="T">The underlying result type.</typeparam>
public sealed record PrometheusResultWithStaleness<T>(
    T Data,
    bool IsStale,
    DateTime? CachedAt,
    double? AgeSeconds
)
{
    /// <summary>
    /// Create a fresh (non-stale) result from live Prometheus data.
    /// </summary>
    public static PrometheusResultWithStaleness<T> Fresh(T data) =>
        new(data, IsStale: false, CachedAt: null, AgeSeconds: null);

    /// <summary>
    /// Create a stale result from cached data.
    /// </summary>
    public static PrometheusResultWithStaleness<T> Stale(T data, DateTime cachedAt) =>
        new(
            data,
            IsStale: true,
            CachedAt: cachedAt,
            AgeSeconds: (DateTime.UtcNow - cachedAt).TotalSeconds
        );

    /// <summary>
    /// Get human-readable staleness description for UI display.
    /// </summary>
    public string GetStalenessDescription()
    {
        if (!IsStale || AgeSeconds == null)
            return "Live data";

        var age = TimeSpan.FromSeconds(AgeSeconds.Value);
        return age.TotalMinutes < 1
            ? $"{age.TotalSeconds:F0} seconds old"
            : age.TotalHours < 1
                ? $"{age.TotalMinutes:F0} minutes old"
                : $"{age.TotalHours:F1} hours old";
    }
} // End of Record PrometheusResultWithStaleness

/// <summary>
/// Service status summary with error rates and latency metrics from Prometheus.
/// </summary>
public sealed record ServiceStatusResult(
    string ServiceName,
    double ErrorRatePercent,
    double P95LatencyMs,
    double P99LatencyMs,
    long RequestCount,
    DateTime StartTime,
    DateTime EndTime
); // End of Record ServiceStatusResult

/// <summary>
/// Service topology with nodes and edges (basic implementation from metrics).
/// </summary>
public sealed record ServiceTopologyResult(
    IReadOnlyList<ServiceNode> Nodes,
    IReadOnlyList<ServiceEdge> Edges
); // End of Record ServiceTopologyResult

/// <summary>
/// Service node in the topology graph.
/// </summary>
public sealed record ServiceNode(
    string ServiceName,
    string ServiceType,
    double ErrorRate,
    double CallRate
); // End of Record ServiceNode

/// <summary>
/// Service dependency edge in the topology graph.
/// </summary>
public sealed record ServiceEdge(
    string SourceService,
    string TargetService,
    long CallCount,
    double ErrorRate
); // End of Record ServiceEdge

/// <summary>
/// List of services reporting to Prometheus.
/// </summary>
public sealed record ServiceListResult(IReadOnlyList<ServiceInfo> Services); // End of Record ServiceListResult

/// <summary>
/// Basic service information.
/// </summary>
public sealed record ServiceInfo(
    string ServiceName,
    DateTime? LastSeen,
    IReadOnlyList<string> Tags
); // End of Record ServiceInfo

/// <summary>
/// Correlated logs for a trace/span (Phase 3 - Loki integration).
/// </summary>
public sealed record CorrelatedLogsResult(
    string TraceId,
    string? SpanId,
    IReadOnlyList<LogEntry> Logs
); // End of Record CorrelatedLogsResult

/// <summary>
/// Individual log entry.
/// </summary>
public sealed record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    string ServiceName,
    string? SpanId,
    IReadOnlyDictionary<string, string> Attributes
); // End of Record LogEntry

/// <summary>
/// OTLP exporter health status from Prometheus metrics.
/// </summary>
public sealed record OtlpHealthResult(IReadOnlyList<OtlpExporterStatus> Exporters); // End of Record OtlpHealthResult

/// <summary>
/// Status of an individual OTLP exporter (derived from Prometheus 'up' metric).
/// </summary>
public sealed record OtlpExporterStatus(
    string ServiceName,
    bool IsHealthy,
    DateTime? LastExport,
    string? ErrorMessage
); // End of Record OtlpExporterStatus

/// <summary>
/// Recent errors from services (Phase 2 - Tempo traces integration).
/// </summary>
public sealed record RecentErrorsResult(IReadOnlyList<ErrorTrace> Errors); // End of Record RecentErrorsResult

/// <summary>
/// Individual error trace.
/// </summary>
public sealed record ErrorTrace(
    string TraceId,
    string SpanId,
    DateTime Timestamp,
    string ServiceName,
    string ErrorMessage,
    string? ExceptionType,
    string? StackTrace,
    IReadOnlyDictionary<string, string> Attributes
); // End of Record ErrorTrace

/// <summary>
/// Full trace details with all spans (Phase 2 - Tempo integration).
/// </summary>
public sealed record TraceDetailsResult(
    string TraceId,
    DateTime StartTime,
    DateTime EndTime,
    double DurationMs,
    string RootServiceName,
    int TotalSpans,
    int ErrorSpans,
    IReadOnlyList<TraceSpan> Spans
); // End of Record TraceDetailsResult

/// <summary>
/// Individual span within a trace.
/// </summary>
public sealed record TraceSpan(
    string SpanId,
    string? ParentSpanId,
    string SpanName,
    string ServiceName,
    string SpanKind,
    DateTime StartTime,
    DateTime EndTime,
    double DurationMs,
    string StatusCode,
    string? StatusMessage,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<SpanEvent> Events,
    IReadOnlyList<SpanLink> Links
); // End of Record TraceSpan

/// <summary>
/// Event within a span (e.g., exception, log).
/// </summary>
public sealed record SpanEvent(
    string Name,
    DateTime Timestamp,
    IReadOnlyDictionary<string, string> Attributes
); // End of Record SpanEvent

/// <summary>
/// Link to another span (for distributed traces).
/// </summary>
public sealed record SpanLink(
    string TraceId,
    string SpanId,
    string TraceState
); // End of Record SpanLink

/// <summary>
/// Result from searching for traces (Phase 2 - Tempo integration).
/// </summary>
public sealed record TracesSearchResult(
    IReadOnlyList<TraceListItem> Traces,
    int TotalCount
); // End of Record TracesSearchResult

/// <summary>
/// Summary information for a trace in search results.
/// </summary>
public sealed record TraceListItem(
    string TraceId,
    string RootServiceName,
    string RootOperationName,
    DateTime StartTime,
    double DurationMs,
    int SpanCount,
    int ErrorCount
); // End of Record TraceListItem

/// <summary>
/// Configuration options for Prometheus query service.
/// </summary>
public sealed class PrometheusQueryOptions
{
    public const string SectionName = "PrometheusQuery";

    /// <summary>
    /// Base URL for Prometheus HTTP API.
    /// Example: http://prometheus:9090 (internal Docker network)
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://prometheus:9090";

    /// <summary>
    /// HTTP client timeout in seconds. Default 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for failed requests. Default 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Enable query allowlist for security. Default true.
    /// </summary>
    public bool EnableQueryAllowlist { get; set; } = true;

    /// <summary>
    /// Base URL for Prometheus UI (for link-outs).
    /// Example: http://127.0.0.1:9090 (dev) or https://prometheus.yourdomain.com (prod)
    /// </summary>
    public string PrometheusUiBaseUrl { get; set; } = "http://127.0.0.1:9090";
} // End of Class PrometheusQueryOptions

// ============================================================================
// Prometheus API Response DTOs (internal, for deserialization)
// ============================================================================

/// <summary>
/// Response from Prometheus HTTP API (instant query and range query).
/// </summary>
internal sealed record PrometheusApiResponse<T>(
    string? Status,
    T? Data,
    string? ErrorType,
    string? Error
); // End of Record PrometheusApiResponse

/// <summary>
/// Data payload for instant query responses.
/// </summary>
internal sealed record PrometheusQueryData(
    string? ResultType,
    List<PrometheusQueryResult>? Result
); // End of Record PrometheusQueryData

/// <summary>
/// Data payload for range query responses.
/// </summary>
internal sealed record PrometheusRangeQueryData(
    string? ResultType,
    List<PrometheusRangeQueryResult>? Result
); // End of Record PrometheusRangeQueryData

/// <summary>
/// Individual result from an instant query.
/// </summary>
internal sealed record PrometheusQueryResult(
    Dictionary<string, string>? Metric,
    List<object>? Value  // [timestamp, value] where timestamp is Unix epoch seconds
); // End of Record PrometheusQueryResult

/// <summary>
/// Individual result from a range query.
/// </summary>
internal sealed record PrometheusRangeQueryResult(
    Dictionary<string, string>? Metric,
    List<List<object>>? Values  // [[timestamp, value], [timestamp, value], ...]
); // End of Record PrometheusRangeQueryResult

/// <summary>
/// Response from Prometheus /api/v1/label/{label}/values endpoint.
/// </summary>
internal sealed record PrometheusLabelValuesResponse(
    string? Status,
    List<string>? Data
); // End of Record PrometheusLabelValuesResponse

/// <summary>
/// Response from Prometheus /api/v1/targets endpoint.
/// </summary>
internal sealed record PrometheusTargetsResponse(
    string? Status,
    PrometheusTargetsData? Data
); // End of Record PrometheusTargetsResponse

/// <summary>
/// Data payload for targets response.
/// </summary>
internal sealed record PrometheusTargetsData(
    List<PrometheusTarget>? ActiveTargets
); // End of Record PrometheusTargetsData

/// <summary>
/// Individual scrape target from Prometheus.
/// </summary>
internal sealed record PrometheusTarget(
    Dictionary<string, string>? DiscoveredLabels,
    Dictionary<string, string>? Labels,
    string? ScrapePool,
    string? ScrapeUrl,
    string? Health,
    DateTime? LastScrape,
    double? ScrapeIntervalSeconds
); // End of Record PrometheusTarget
