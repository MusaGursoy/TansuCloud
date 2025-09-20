// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json.Serialization;

namespace TansuCloud.Dashboard.Observability.Logging
{
    public sealed record LogItem(
        string Timestamp,
        string Level,
        string Message,
        string? Exception,
        string? Service,
        string? Environment,
        string? Tenant,
        string? TraceId,
        string? SpanId,
        object? Properties
    ); // End of Record LogItem

    public sealed record LogReportRequest(
        string Host,
        string Environment,
        string Service,
        string SeverityThreshold,
        int WindowMinutes,
        int MaxItems,
        IReadOnlyList<LogItem> Items
    ); // End of Record LogReportRequest

    public sealed record LogReportResponse(
        bool Accepted,
        string? Message
    ); // End of Record LogReportResponse
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
