// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Models;

/// <summary>
/// Request model for searching traces with filters.
/// </summary>
public record TraceSearchRequest
{
    /// <summary>
    /// Start of the time range (Unix timestamp in nanoseconds).
    /// Default: 1 hour ago
    /// </summary>
    public long StartTimeNano { get; init; } = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() * 1_000_000;

    /// <summary>
    /// End of the time range (Unix timestamp in nanoseconds).
    /// Default: now
    /// </summary>
    public long EndTimeNano { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

    /// <summary>
    /// Filter by service name (exact match or prefix).
    /// Null/empty = all services
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Filter by span status (OK, ERROR, UNSET).
    /// Null/empty = all statuses
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Minimum duration in milliseconds.
    /// Null = no minimum
    /// </summary>
    public long? MinDurationMs { get; init; }

    /// <summary>
    /// Maximum duration in milliseconds.
    /// Null = no maximum
    /// </summary>
    public long? MaxDurationMs { get; init; }

    /// <summary>
    /// Maximum number of traces to return.
    /// Default: 50
    /// </summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Offset for pagination.
    /// Default: 0
    /// </summary>
    public int Offset { get; init; } = 0;
} // End of Record TraceSearchRequest

/// <summary>
/// Result from trace search containing matching traces and pagination info.
/// </summary>
public record TraceSearchResult
{
    /// <summary>
    /// List of trace summaries matching the search criteria.
    /// </summary>
    public List<TraceSummary> Traces { get; init; } = new();

    /// <summary>
    /// Total number of traces matching the search (for pagination).
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Whether there are more results available.
    /// </summary>
    public bool HasMore { get; init; }
} // End of Record TraceSearchResult

/// <summary>
/// Summary information for a single trace (used in search results table).
/// </summary>
public record TraceSummary
{
    /// <summary>
    /// Unique trace identifier (hex string).
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// Root service name (entry point of the trace).
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Root span name (entry operation name).
    /// </summary>
    public string OperationName { get; init; } = string.Empty;

    /// <summary>
    /// Total duration of the trace in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Number of spans in the trace.
    /// </summary>
    public int SpanCount { get; init; }

    /// <summary>
    /// Timestamp when the trace started (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Overall status of the trace (OK, ERROR, UNSET).
    /// Derived from root span or worst span status.
    /// </summary>
    public string Status { get; init; } = "UNSET";

    /// <summary>
    /// Error message if trace has errors.
    /// </summary>
    public string? ErrorMessage { get; init; }
} // End of Record TraceSummary

/// <summary>
/// Detailed trace information including full span tree.
/// </summary>
public record TraceDetail
{
    /// <summary>
    /// Unique trace identifier.
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// Root service name.
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Total duration of the trace in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Timestamp when the trace started (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// All spans in the trace, organized as a flat list.
    /// Parent-child relationships are defined via ParentSpanId.
    /// </summary>
    public List<SpanDetail> Spans { get; init; } = new();
} // End of Record TraceDetail

/// <summary>
/// Detailed information for a single span within a trace.
/// </summary>
public record SpanDetail
{
    /// <summary>
    /// Unique span identifier (hex string).
    /// </summary>
    public string SpanId { get; init; } = string.Empty;

    /// <summary>
    /// Parent span ID (empty/null for root span).
    /// </summary>
    public string? ParentSpanId { get; init; }

    /// <summary>
    /// Service that generated this span.
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Operation/method name for this span.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Span kind (CLIENT, SERVER, INTERNAL, PRODUCER, CONSUMER).
    /// </summary>
    public string Kind { get; init; } = "INTERNAL";

    /// <summary>
    /// Start time of the span (Unix timestamp in nanoseconds).
    /// </summary>
    public long StartTimeNano { get; init; }

    /// <summary>
    /// End time of the span (Unix timestamp in nanoseconds).
    /// </summary>
    public long EndTimeNano { get; init; }

    /// <summary>
    /// Duration of the span in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Span status (OK, ERROR, UNSET).
    /// </summary>
    public string Status { get; init; } = "UNSET";

    /// <summary>
    /// Status message (error message if status is ERROR).
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Span attributes (tags) as key-value pairs.
    /// Examples: http.method, http.status_code, db.statement, etc.
    /// </summary>
    public Dictionary<string, string> Attributes { get; init; } = new();

    /// <summary>
    /// Events (logs) associated with this span.
    /// Examples: exceptions, annotations, custom events
    /// </summary>
    public List<SpanEvent> Events { get; init; } = new();
} // End of Record SpanDetail

/// <summary>
/// Event (log entry) within a span.
/// </summary>
public record SpanEvent
{
    /// <summary>
    /// Event name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the event occurred (Unix timestamp in nanoseconds).
    /// </summary>
    public long TimestampNano { get; init; }

    /// <summary>
    /// Event attributes as key-value pairs.
    /// </summary>
    public Dictionary<string, string> Attributes { get; init; } = new();
} // End of Record SpanEvent
