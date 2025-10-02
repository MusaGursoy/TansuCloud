// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json.Serialization;

namespace TansuCloud.Telemetry.Contracts;

public sealed record LogItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("templateHash")] string TemplateHash,
    [property: JsonPropertyName("exception")] string? Exception,
    [property: JsonPropertyName("service")] string? Service,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("tenantHash")] string? TenantHash,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("traceId")] string? TraceId,
    [property: JsonPropertyName("spanId")] string? SpanId,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("eventId")] int? EventId,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("properties")] object? Properties
); // End of Record LogItem

public sealed record LogReportRequest(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("severityThreshold")] string SeverityThreshold,
    [property: JsonPropertyName("windowMinutes")] int WindowMinutes,
    [property: JsonPropertyName("maxItems")] int MaxItems,
    [property: JsonPropertyName("items")] IReadOnlyList<LogItem> Items
); // End of Record LogReportRequest

public sealed record LogReportResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("message")] string? Message
); // End of Record LogReportResponse
