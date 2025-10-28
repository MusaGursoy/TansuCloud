// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Service for querying observability data from SigNoz API.
/// Provides admin-only access to metrics, traces, logs, and service topology.
/// </summary>
public interface ISigNozQueryService
{
    /// <summary>
    /// Query service status including error rates and latency percentiles.
    /// </summary>
    /// <param name="serviceName">Optional service name filter. If null, returns all services.</param>
    /// <param name="timeRangeMinutes">Time range in minutes from now. Default 60 minutes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Service status summary with error rates and latency metrics.</returns>
    Task<ServiceStatusResult> GetServiceStatusAsync(
        string? serviceName = null,
        int timeRangeMinutes = 60,
        CancellationToken cancellationToken = default
    ); // End of Method GetServiceStatusAsync

    /// <summary>
    /// Get service dependency graph (topology).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Service topology with nodes and edges representing dependencies.</returns>
    Task<ServiceTopologyResult> GetServiceTopologyAsync(
        CancellationToken cancellationToken = default
    ); // End of Method GetServiceTopologyAsync

    /// <summary>
    /// Get list of all services reporting to SigNoz.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of service names and basic metadata.</returns>
    Task<ServiceListResult> GetServiceListAsync(CancellationToken cancellationToken = default); // End of Method GetServiceListAsync

    /// <summary>
    /// Query logs correlated with a specific trace or span ID.
    /// </summary>
    /// <param name="traceId">Trace ID to correlate logs with.</param>
    /// <param name="spanId">Optional span ID for more specific correlation.</param>
    /// <param name="limit">Maximum number of log entries to return. Default 10.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Correlated log entries.</returns>
    Task<CorrelatedLogsResult> GetCorrelatedLogsAsync(
        string traceId,
        string? spanId = null,
        int limit = 10,
        CancellationToken cancellationToken = default
    ); // End of Method GetCorrelatedLogsAsync

    /// <summary>
    /// Get OTLP exporter health status for services.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health status of OTLP exporters.</returns>
    Task<OtlpHealthResult> GetOtlpHealthAsync(CancellationToken cancellationToken = default); // End of Method GetOtlpHealthAsync

    /// <summary>
    /// Query recent errors by service.
    /// </summary>
    /// <param name="serviceName">Optional service name filter.</param>
    /// <param name="timeRangeMinutes">Time range in minutes from now. Default 60 minutes.</param>
    /// <param name="limit">Maximum number of error entries to return. Default 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recent error traces.</returns>
    Task<RecentErrorsResult> GetRecentErrorsAsync(
        string? serviceName = null,
        int timeRangeMinutes = 60,
        int limit = 100,
        CancellationToken cancellationToken = default
    ); // End of Method GetRecentErrorsAsync

    /// <summary>
    /// Get circuit breaker state for diagnostics and UI display.
    /// </summary>
    /// <returns>Current circuit breaker state.</returns>
    CircuitBreakerState GetCircuitBreakerState(); // End of Method GetCircuitBreakerState
} // End of Interface ISigNozQueryService
