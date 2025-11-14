// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Text.Json.Serialization;

namespace TansuCloud.Dashboard.Observability.Tempo;

#region Public Result Types

/// <summary>
/// Search result containing matching traces.
/// </summary>
public sealed record TempoTraceSearchResult
{
    public List<TempoTraceMetadata> Traces { get; init; } = [];
    public TempoSearchMetrics? Metrics { get; init; }
} // End of Record TempoTraceSearchResult

/// <summary>
/// Metadata for a single trace in search results.
/// </summary>
public sealed record TempoTraceMetadata
{
    public string TraceId { get; init; } = string.Empty;
    public string RootServiceName { get; init; } = string.Empty;
    public string RootTraceName { get; init; } = string.Empty;
    public long StartTimeUnixNano { get; init; }
    public int? DurationMs { get; init; }
} // End of Record TempoTraceMetadata

/// <summary>
/// Search metrics from Tempo API.
/// </summary>
public sealed record TempoSearchMetrics
{
    public int InspectedTraces { get; init; }
    public long InspectedBytes { get; init; }
    public int CompletedJobs { get; init; }
    public int TotalJobs { get; init; }
} // End of Record TempoSearchMetrics

/// <summary>
/// Complete trace with all spans.
/// </summary>
public sealed record TempoTrace
{
    public string TraceId { get; init; } = string.Empty;
    public List<TempoSpan> Spans { get; init; } = [];
} // End of Record TempoTrace

/// <summary>
/// A single span within a trace.
/// </summary>
public sealed record TempoSpan
{
    public string SpanId { get; init; } = string.Empty;
    public string? ParentSpanId { get; init; }
    public string OperationName { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public long StartTimeUnixNano { get; init; }
    public long DurationNano { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
    public List<TempoSpanEvent> Events { get; init; } = [];
    public string? Status { get; init; }
} // End of Record TempoSpan

/// <summary>
/// An event within a span (e.g., exception, log).
/// </summary>
public sealed record TempoSpanEvent
{
    public long TimeUnixNano { get; init; }
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Attributes { get; init; } = [];
} // End of Record TempoSpanEvent

/// <summary>
/// Search filters for trace queries.
/// </summary>
public sealed record TempoSearchFilters
{
    /// <summary>
    /// Service name to filter by (exact match).
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Minimum trace duration in milliseconds.
    /// </summary>
    public int? MinDurationMs { get; init; }

    /// <summary>
    /// Maximum trace duration in milliseconds.
    /// </summary>
    public int? MaxDurationMs { get; init; }

    /// <summary>
    /// Trace status filter (e.g., "ok", "error").
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Start of time range (Unix seconds).
    /// </summary>
    public long? StartUnixSeconds { get; init; }

    /// <summary>
    /// End of time range (Unix seconds).
    /// </summary>
    public long? EndUnixSeconds { get; init; }

    /// <summary>
    /// Maximum number of results to return (default: 20).
    /// </summary>
    public int Limit { get; init; } = 20;

    /// <summary>
    /// Optional TraceQL query for advanced filtering.
    /// Example: "{.http.status_code = 500}"
    /// </summary>
    public string? TraceQLQuery { get; init; }
} // End of Record TempoSearchFilters

#endregion

#region Configuration

/// <summary>
/// Configuration options for Tempo query service.
/// </summary>
public sealed class TempoQueryOptions
{
    public const string SectionName = "TempoQuery";

    /// <summary>
    /// Base URL for Tempo HTTP API (e.g., http://tempo:3200).
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://tempo:3200";

    /// <summary>
    /// Timeout for HTTP requests to Tempo (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
} // End of Class TempoQueryOptions

#endregion

#region Internal API Response Types

/// <summary>
/// Internal representation of Tempo search API response.
/// Maps directly from JSON response at /api/search.
/// </summary>
internal sealed record TempoSearchApiResponse
{
    [JsonPropertyName("traces")]
    public List<TempoSearchApiTrace>? Traces { get; init; }

    [JsonPropertyName("metrics")]
    public TempoSearchApiMetrics? Metrics { get; init; }
} // End of Record TempoSearchApiResponse

internal sealed record TempoSearchApiTrace
{
    [JsonPropertyName("traceID")]
    public string? TraceId { get; init; }

    [JsonPropertyName("rootServiceName")]
    public string? RootServiceName { get; init; }

    [JsonPropertyName("rootTraceName")]
    public string? RootTraceName { get; init; }

    [JsonPropertyName("startTimeUnixNano")]
    public string? StartTimeUnixNano { get; init; }

    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; init; }
} // End of Record TempoSearchApiTrace

internal sealed record TempoSearchApiMetrics
{
    [JsonPropertyName("inspectedTraces")]
    public int InspectedTraces { get; init; }

    [JsonPropertyName("inspectedBytes")]
    public string? InspectedBytes { get; init; }

    [JsonPropertyName("completedJobs")]
    public int CompletedJobs { get; init; }

    [JsonPropertyName("totalJobs")]
    public int TotalJobs { get; init; }
} // End of Record TempoSearchApiMetrics

/// <summary>
/// Internal representation of Tempo trace API response.
/// Tempo returns traces in OTLP JSON format.
/// This is a simplified mapping - full OTLP format is more complex.
/// </summary>
internal sealed record TempoTraceApiResponse
{
    [JsonPropertyName("batches")]
    public List<TempoTraceBatch>? Batches { get; init; }
} // End of Record TempoTraceApiResponse

internal sealed record TempoTraceBatch
{
    [JsonPropertyName("resource")]
    public TempoResource? Resource { get; init; }

    [JsonPropertyName("scopeSpans")]
    public List<TempoScopeSpans>? ScopeSpans { get; init; }
} // End of Record TempoTraceBatch

internal sealed record TempoResource
{
    [JsonPropertyName("attributes")]
    public List<TempoAttribute>? Attributes { get; init; }
} // End of Record TempoResource

internal sealed record TempoScopeSpans
{
    [JsonPropertyName("spans")]
    public List<TempoOtlpSpan>? Spans { get; init; }
} // End of Record TempoScopeSpans

internal sealed record TempoOtlpSpan
{
    [JsonPropertyName("spanId")]
    public string? SpanId { get; init; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("startTimeUnixNano")]
    public string? StartTimeUnixNano { get; init; }

    [JsonPropertyName("endTimeUnixNano")]
    public string? EndTimeUnixNano { get; init; }

    [JsonPropertyName("attributes")]
    public List<TempoAttribute>? Attributes { get; init; }

    [JsonPropertyName("events")]
    public List<TempoOtlpEvent>? Events { get; init; }

    [JsonPropertyName("status")]
    public TempoOtlpStatus? Status { get; init; }
} // End of Record TempoOtlpSpan

internal sealed record TempoAttribute
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("value")]
    public TempoAttributeValue? Value { get; init; }
} // End of Record TempoAttribute

internal sealed record TempoAttributeValue
{
    [JsonPropertyName("stringValue")]
    public string? StringValue { get; init; }

    [JsonPropertyName("intValue")]
    public string? IntValue { get; init; }

    [JsonPropertyName("boolValue")]
    public bool? BoolValue { get; init; }
} // End of Record TempoAttributeValue

internal sealed record TempoOtlpEvent
{
    [JsonPropertyName("timeUnixNano")]
    public string? TimeUnixNano { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("attributes")]
    public List<TempoAttribute>? Attributes { get; init; }
} // End of Record TempoOtlpEvent

internal sealed record TempoOtlpStatus
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
} // End of Record TempoOtlpStatus

#endregion
