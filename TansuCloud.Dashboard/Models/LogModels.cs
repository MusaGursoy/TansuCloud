// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Text.Json;

namespace TansuCloud.Dashboard.Models;

/// <summary>
/// Request model for searching logs in SigNoz.
/// </summary>
public record LogSearchRequest
{
    /// <summary>
    /// Start time in nanoseconds (Unix epoch).
    /// </summary>
    public long StartTimeNano { get; init; }

    /// <summary>
    /// End time in nanoseconds (Unix epoch).
    /// </summary>
    public long EndTimeNano { get; init; }

    /// <summary>
    /// Filter by service name (optional).
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Filter by log severity level (optional).
    /// Values: TRACE, DEBUG, INFO, WARN, ERROR, FATAL
    /// </summary>
    public string? SeverityText { get; init; }

    /// <summary>
    /// Text search in log body (optional).
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Filter by trace ID for correlation (optional).
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Filter by span ID for correlation (optional).
    /// </summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// Maximum number of results to return (default: 100).
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Offset for pagination (default: 0).
    /// </summary>
    public int Offset { get; init; } = 0;

    /// <summary>
    /// Sort order: "asc" or "desc" by timestamp (default: "desc" - newest first).
    /// </summary>
    public string OrderBy { get; init; } = "desc";
} // End of Record LogSearchRequest

/// <summary>
/// Result model for log search operations.
/// </summary>
public record LogSearchResult
{
    /// <summary>
    /// List of log entries matching the search criteria.
    /// </summary>
    public List<LogEntry> Logs { get; init; } = new();

    /// <summary>
    /// Total number of matching logs (for pagination).
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Indicates if there are more results beyond the current page.
    /// </summary>
    public bool HasMore { get; init; }
} // End of Record LogSearchResult

/// <summary>
/// Represents a single log entry from SigNoz.
/// </summary>
public record LogEntry
{
    /// <summary>
    /// Unique identifier for the log entry (timestamp + unique suffix).
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Log timestamp in nanoseconds (Unix epoch).
    /// </summary>
    public long TimestampNano { get; init; }

    /// <summary>
    /// Formatted timestamp for display.
    /// </summary>
    public DateTime Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampNano / 1_000_000).UtcDateTime;

    /// <summary>
    /// Log message body.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Severity level text (TRACE, DEBUG, INFO, WARN, ERROR, FATAL).
    /// </summary>
    public string SeverityText { get; init; } = "INFO";

    /// <summary>
    /// Numeric severity level.
    /// </summary>
    public int SeverityNumber { get; init; }

    /// <summary>
    /// Service name that generated the log.
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Trace ID for correlation with distributed traces (optional).
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Span ID for correlation with specific spans (optional).
    /// </summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// Resource attributes (service metadata).
    /// </summary>
    public Dictionary<string, string> ResourceAttributes { get; init; } = new();

    /// <summary>
    /// Log-specific attributes (structured data).
    /// </summary>
    public Dictionary<string, JsonElement> Attributes { get; init; } = new();

    /// <summary>
    /// Error indicator (true if severity is ERROR or FATAL).
    /// </summary>
    public bool IsError => SeverityText is "ERROR" or "FATAL";
} // End of Record LogEntry

/// <summary>
/// Represents a log field (attribute) available for filtering.
/// </summary>
public record LogField
{
    /// <summary>
    /// Field name (attribute key).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Field data type (string, number, boolean, etc.).
    /// </summary>
    public string Type { get; init; } = "string";

    /// <summary>
    /// Indicates if the field is indexed for fast filtering.
    /// </summary>
    public bool IsIndexed { get; init; }
} // End of Record LogField
