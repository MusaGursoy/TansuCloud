// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Wrapper for SigNoz query results that includes staleness metadata.
/// Used when returning cached data during circuit breaker open state.
/// </summary>
/// <typeparam name="T">The underlying result type.</typeparam>
public sealed record SigNozResultWithStaleness<T>(
    T Data,
    bool IsStale,
    DateTime? CachedAt,
    double? AgeSeconds
)
{
    /// <summary>
    /// Create a fresh (non-stale) result from live SigNoz data.
    /// </summary>
    public static SigNozResultWithStaleness<T> Fresh(T data) =>
        new(data, IsStale: false, CachedAt: null, AgeSeconds: null);

    /// <summary>
    /// Create a stale result from cached data.
    /// </summary>
    public static SigNozResultWithStaleness<T> Stale(T data, DateTime cachedAt) =>
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
} // End of Record SigNozResultWithStaleness

/// <summary>
/// Service status summary with error rates and latency metrics.
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
/// Service topology with nodes and edges.
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
/// List of services reporting to SigNoz.
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
/// Correlated logs for a trace/span.
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
/// OTLP exporter health status.
/// </summary>
public sealed record OtlpHealthResult(IReadOnlyList<OtlpExporterStatus> Exporters); // End of Record OtlpHealthResult

/// <summary>
/// Status of an individual OTLP exporter.
/// </summary>
public sealed record OtlpExporterStatus(
    string ServiceName,
    bool IsHealthy,
    DateTime? LastExport,
    string? ErrorMessage
); // End of Record OtlpExporterStatus

/// <summary>
/// Recent errors from services.
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
/// Full trace details with all spans.
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
/// Result from searching for traces.
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
/// Configuration options for SigNoz query service.
/// </summary>
public sealed class SigNozQueryOptions
{
    public const string SectionName = "SigNozQuery";

    /// <summary>
    /// Base URL for SigNoz query service API.
    /// Example: http://signoz-query-service:8080 (internal Docker network)
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://signoz-query-service:8080";

    /// <summary>
    /// API key for authenticating with SigNoz.
    /// Should be stored securely (environment variable, Key Vault).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Email for SigNoz authentication (used to obtain JWT token).
    /// Should be stored securely (environment variable, Key Vault).
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Password for SigNoz authentication (used to obtain JWT token).
    /// Should be stored securely (environment variable, Key Vault).
    /// </summary>
    public string? Password { get; set; }

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
    /// Base URL for SigNoz UI (for link-outs).
    /// Example: http://127.0.0.1:3301 (dev) or https://signoz.yourdomain.com (prod)
    /// </summary>
    public string SigNozUiBaseUrl { get; set; } = "http://127.0.0.1:3301";
} // End of Class SigNozQueryOptions

// ============================================================================
// SigNoz API Response DTOs (internal, for deserialization)
// ============================================================================

/// <summary>
/// Response from SigNoz /api/v1/services endpoint.
/// </summary>
internal sealed record SigNozServicesResponse(string? Status, List<SigNozServiceDto>? Data); // End of Record SigNozServicesResponse

/// <summary>
/// Service DTO from SigNoz API.
/// </summary>
internal sealed record SigNozServiceDto(string? ServiceName, List<string>? Tags); // End of Record SigNozServiceDto

/// <summary>
/// Response from SigNoz query_range endpoint (metrics/traces).
/// </summary>
internal sealed record SigNozQueryRangeResponse(string? Status, SigNozQueryData? Data); // End of Record SigNozQueryRangeResponse

/// <summary>
/// Data payload for query_range responses.
/// </summary>
internal sealed record SigNozQueryData(string? ResultType, List<SigNozQueryResult>? Result); // End of Record SigNozQueryData

/// <summary>
/// Individual result from a query.
/// </summary>
internal sealed record SigNozQueryResult(
    Dictionary<string, string>? Metric,
    List<List<object>>? Values
); // End of Record SigNozQueryResult

/// <summary>
/// Response from SigNoz logs API.
/// </summary>
internal sealed record SigNozLogsResponse(string? Status, List<SigNozLogDto>? Data); // End of Record SigNozLogsResponse

/// <summary>
/// Log entry DTO from SigNoz API.
/// </summary>
internal sealed record SigNozLogDto(
    long? Timestamp,
    string? Body,
    string? SeverityText,
    Dictionary<string, string>? Attributes,
    Dictionary<string, string>? Resources
); // End of Record SigNozLogDto

/// <summary>
/// Response from SigNoz /api/v1/traces/{traceId} endpoint.
/// </summary>
internal sealed record SigNozTraceResponse(
    string? Status,
    List<SigNozSpanDto>? Data
); // End of Record SigNozTraceResponse

/// <summary>
/// Span DTO from SigNoz traces API.
/// </summary>
internal sealed record SigNozSpanDto(
    string? SpanId,
    string? ParentSpanId,
    string? Name,
    string? ServiceName,
    string? Kind,
    long? StartTimeUnixNano,
    long? EndTimeUnixNano,
    long? DurationNano,
    Dictionary<string, object>? Attributes,
    List<SigNozSpanEventDto>? Events,
    SigNozSpanStatusDto? Status
); // End of Record SigNozSpanDto

/// <summary>
/// Span event DTO from SigNoz API.
/// </summary>
internal sealed record SigNozSpanEventDto(
    string? Name,
    long? TimeUnixNano,
    Dictionary<string, object>? Attributes
); // End of Record SigNozSpanEventDto

/// <summary>
/// Span status DTO from SigNoz API.
/// </summary>
internal sealed record SigNozSpanStatusDto(
    string? Code,
    string? Message
); // End of Record SigNozSpanStatusDto

/// <summary>
/// Response from SigNoz POST /api/v1/traces (traces search).
/// </summary>
internal sealed record SigNozTracesSearchResponse(
    string? Status,
    SigNozTracesSearchData? Data
); // End of Record SigNozTracesSearchResponse

/// <summary>
/// Data payload for traces search response.
/// </summary>
internal sealed record SigNozTracesSearchData(
    List<SigNozTraceItemDto>? Traces,
    int? Total
); // End of Record SigNozTracesSearchData

/// <summary>
/// Trace item summary from search results.
/// </summary>
internal sealed record SigNozTraceItemDto(
    string? TraceId,
    string? RootServiceName,
    string? RootTraceName,
    long? StartTimeUnixNano,
    long? DurationNano,
    int? SpanCount,
    int? ErrorCount
); // End of Record SigNozTraceItemDto
