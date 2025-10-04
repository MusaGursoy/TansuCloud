// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Represents a summary of a telemetry envelope for administrative views.
/// </summary>
public sealed record TelemetryEnvelopeSummary(
    Guid Id,
    DateTime ReceivedAtUtc,
    string Host,
    string Environment,
    string Service,
    string SeverityThreshold,
    int ItemCount,
    bool IsAcknowledged,
    bool IsDeleted
); // End of Record TelemetryEnvelopeSummary
