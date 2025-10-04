// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Represents full details of a telemetry envelope.
/// </summary>
public sealed record TelemetryEnvelopeDetail(
    Guid Id,
    DateTime ReceivedAtUtc,
    string Host,
    string Environment,
    string Service,
    string SeverityThreshold,
    int WindowMinutes,
    int MaxItems,
    int ItemCount,
    bool IsAcknowledged,
    bool IsDeleted,
    IReadOnlyList<TelemetryItemView> Items
); // End of Record TelemetryEnvelopeDetail
