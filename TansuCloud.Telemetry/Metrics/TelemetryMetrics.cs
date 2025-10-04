// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;

namespace TansuCloud.Telemetry.Metrics;

/// <summary>
/// Provides metric instruments for the telemetry ingestion service.
/// </summary>
public sealed class TelemetryMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _persistedItems;
    private Func<int>? _queueDepthProvider;

    public TelemetryMetrics()
    {
        _meter = new Meter("TansuCloud.Telemetry", "1.0.0");
        _persistedItems = _meter.CreateCounter<long>("telemetry_items_persisted_total");
    } // End of Constructor TelemetryMetrics

    /// <summary>
    /// Records the current queue depth for observability.
    /// </summary>
    public void RecordQueueDepth(int depth)
    {
        // No-op placeholder; gauge observer polls proactively.
        // Method retained for compatibility with queue updates.
    } // End of Method RecordQueueDepth

    /// <summary>
    /// Registers a queue depth observer for instrumentation.
    /// </summary>
    public void RegisterQueueObserver(Func<int> observer)
    {
        _queueDepthProvider = observer;
        _meter.CreateObservableGauge(
            "telemetry_queue_depth",
            () =>
            {
                var provider = _queueDepthProvider;
                var value = provider is null ? 0 : provider();
                return new Measurement<int>(value);
            },
            unit: null,
            description: "Current backlog size of the telemetry ingestion queue."
        );
    } // End of Method RegisterQueueObserver

    /// <summary>
    /// Adds the specified number of items to the persisted counter.
    /// </summary>
    public void RecordPersistedItems(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _persistedItems.Add(count);
    } // End of Method RecordPersistedItems

    public void Dispose()
    {
        _meter.Dispose();
    } // End of Method Dispose
} // End of Class TelemetryMetrics
