// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Ingestion;

namespace TansuCloud.Telemetry.HealthChecks;

/// <summary>
/// Health check that reports the current telemetry queue backlog.
/// </summary>
public sealed class TelemetryQueueHealthCheck : IHealthCheck
{
    private readonly ITelemetryIngestionQueue _queue;
    private readonly IOptionsMonitor<TelemetryIngestionOptions> _options;

    public TelemetryQueueHealthCheck(
        ITelemetryIngestionQueue queue,
        IOptionsMonitor<TelemetryIngestionOptions> options
    )
    {
        _queue = queue;
        _options = options;
    } // End of Constructor TelemetryQueueHealthCheck

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var depth = _queue.GetDepth();
        var capacity = Math.Max(1, _options.CurrentValue.QueueCapacity);
        var usage = (double)depth / capacity;

        var data = new Dictionary<string, object?>
        {
            ["queueDepth"] = depth,
            ["queueCapacity"] = capacity,
            ["queueUsage"] = usage
        };

        return Task.FromResult(usage switch
        {
            >= 1 => HealthCheckResult.Unhealthy("Queue is full.", data: data),
            >= 0.75 => HealthCheckResult.Degraded("Queue backlog is high.", data: data),
            _ => HealthCheckResult.Healthy("Queue is operating normally.", data)
        });
    } // End of Method CheckHealthAsync
} // End of Class TelemetryQueueHealthCheck