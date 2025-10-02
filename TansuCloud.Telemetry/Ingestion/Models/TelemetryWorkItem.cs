// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using TansuCloud.Telemetry.Data.Entities;

namespace TansuCloud.Telemetry.Ingestion.Models;

/// <summary>
/// Represents a telemetry payload awaiting persistence.
/// </summary>
public sealed record TelemetryWorkItem(TelemetryEnvelopeEntity Envelope); // End of Record TelemetryWorkItem
