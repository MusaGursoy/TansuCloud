// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using TansuCloud.Telemetry.Ingestion.Models;

namespace TansuCloud.Telemetry.Ingestion;

/// <summary>
/// Abstraction over the ingestion queue for telemetry payloads.
/// </summary>
public interface ITelemetryIngestionQueue
{
    /// <summary>
    /// Attempts to enqueue the supplied telemetry work item.
    /// </summary>
    /// <returns><c>true</c> if the item was queued; otherwise <c>false</c>.</returns>
    ValueTask<bool> TryEnqueueAsync(TelemetryWorkItem workItem, CancellationToken cancellationToken);

    /// <summary>
    /// Dequeues the next telemetry work item, awaiting data as necessary.
    /// </summary>
    ValueTask<TelemetryWorkItem> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current number of items buffered within the queue.
    /// </summary>
    int GetDepth();
} // End of Interface ITelemetryIngestionQueue
