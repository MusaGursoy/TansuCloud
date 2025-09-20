// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;

namespace TansuCloud.Dashboard.Observability.Logging;

/// <summary>
/// A structured log record captured locally for diagnostics and reporting.
/// </summary>
public sealed record LogRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; // End of Property Timestamp
    public string Level { get; init; } = "Information"; // End of Property Level
    public string Category { get; init; } = ""; // End of Property Category
    public string Message { get; init; } = ""; // End of Property Message
    public string? Exception { get; init; } // End of Property Exception
    public JsonElement? State { get; init; } // End of Property State
    public string? Scope { get; init; } // End of Property Scope
    public string? Tenant { get; init; } // End of Property Tenant
    public string? RequestId { get; init; } // End of Property RequestId
    public string? TraceId { get; init; } // End of Property TraceId
    public string? SpanId { get; init; } // End of Property SpanId
    public string? ServiceName { get; init; } // End of Property ServiceName
    public string? EnvironmentName { get; init; } // End of Property EnvironmentName
} // End of Record LogRecord
