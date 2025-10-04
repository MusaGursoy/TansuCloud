// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Represents an item within a telemetry envelope for administrative views.
/// </summary>
public sealed record TelemetryItemView(
    long Id,
    string Kind,
    DateTime TimestampUtc,
    string Level,
    string Message,
    string TemplateHash,
    string? Exception,
    string? Service,
    string? Environment,
    string? TenantHash,
    string? CorrelationId,
    string? TraceId,
    string? SpanId,
    string? Category,
    int? EventId,
    int Count,
    string? PropertiesJson
); // End of Record TelemetryItemView
