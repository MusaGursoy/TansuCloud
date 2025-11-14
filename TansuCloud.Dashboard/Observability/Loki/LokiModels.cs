// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Text.Json.Serialization;

namespace TansuCloud.Dashboard.Observability.Loki;

#region Public Result Types

/// <summary>
/// Search result containing matching log entries.
/// </summary>
public sealed record LokiLogSearchResult
{
    public List<LokiLogEntry> Logs { get; init; } = [];
    public LokiSearchStats? Stats { get; init; }
} // End of Record LokiLogSearchResult

/// <summary>
/// A single log entry from Loki.
/// </summary>
public sealed record LokiLogEntry
{
    public long TimestampNano { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Labels { get; init; } = [];
} // End of Record LokiLogEntry

/// <summary>
/// Search statistics from Loki query.
/// </summary>
public sealed record LokiSearchStats
{
    public int TotalEntriesReturned { get; init; }
    public int StreamsQueried { get; init; }
} // End of Record LokiSearchStats

/// <summary>
/// Search filters for log queries.
/// </summary>
public sealed record LokiSearchFilters
{
    /// <summary>
    /// Service name to filter by (exact match on service_name label).
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Log level to filter by (e.g., "Information", "Warning", "Error").
    /// </summary>
    public string? Level { get; init; }

    /// <summary>
    /// Start of time range (Unix nanoseconds).
    /// </summary>
    public long? StartNano { get; init; }

    /// <summary>
    /// End of time range (Unix nanoseconds).
    /// </summary>
    public long? EndNano { get; init; }

    /// <summary>
    /// Maximum number of log entries to return (default: 100).
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Direction of results: "backward" (newest first) or "forward" (oldest first).
    /// Default: "backward".
    /// </summary>
    public string Direction { get; init; } = "backward";

    /// <summary>
    /// Optional custom LogQL query for advanced filtering.
    /// Example: {service_name="tansu.gateway"} |= "error" | json
    /// If provided, overrides ServiceName and Level filters.
    /// </summary>
    public string? LogQLQuery { get; init; }
} // End of Record LokiSearchFilters

#endregion

#region Configuration

/// <summary>
/// Configuration options for Loki query service.
/// </summary>
public sealed class LokiQueryOptions
{
    public const string SectionName = "LokiQuery";

    /// <summary>
    /// Base URL for Loki HTTP API (e.g., http://loki:3100).
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://loki:3100";

    /// <summary>
    /// Timeout for HTTP requests to Loki (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
} // End of Class LokiQueryOptions

#endregion

#region Internal API Response Types

/// <summary>
/// Internal representation of Loki query_range API response.
/// Maps directly from JSON response at /loki/api/v1/query_range.
/// </summary>
internal sealed record LokiQueryRangeResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("data")]
    public LokiQueryData? Data { get; init; }
} // End of Record LokiQueryRangeResponse

internal sealed record LokiQueryData
{
    [JsonPropertyName("resultType")]
    public string? ResultType { get; init; }

    [JsonPropertyName("result")]
    public List<LokiStream>? Result { get; init; }

    [JsonPropertyName("stats")]
    public LokiApiStats? Stats { get; init; }
} // End of Record LokiQueryData

internal sealed record LokiStream
{
    [JsonPropertyName("stream")]
    public Dictionary<string, string>? Stream { get; init; }

    [JsonPropertyName("values")]
    public List<List<string>>? Values { get; init; }
} // End of Record LokiStream

internal sealed record LokiApiStats
{
    [JsonPropertyName("summary")]
    public LokiStatsSummary? Summary { get; init; }
} // End of Record LokiApiStats

internal sealed record LokiStatsSummary
{
    [JsonPropertyName("bytesProcessedPerSecond")]
    public long BytesProcessedPerSecond { get; init; }

    [JsonPropertyName("linesProcessedPerSecond")]
    public long LinesProcessedPerSecond { get; init; }

    [JsonPropertyName("totalBytesProcessed")]
    public long TotalBytesProcessed { get; init; }

    [JsonPropertyName("totalLinesProcessed")]
    public long TotalLinesProcessed { get; init; }

    [JsonPropertyName("execTime")]
    public double ExecTime { get; init; }
} // End of Record LokiStatsSummary

/// <summary>
/// Internal representation of Loki labels API response.
/// Maps from /loki/api/v1/labels and /loki/api/v1/label/{name}/values.
/// </summary>
internal sealed record LokiLabelsResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("data")]
    public List<string>? Data { get; init; }
} // End of Record LokiLabelsResponse

#endregion
