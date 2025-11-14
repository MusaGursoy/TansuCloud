// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.Prometheus;

/// <summary>
/// Service for querying Prometheus metrics via HTTP API.
/// Provides metrics, basic topology, and health information.
/// Traces and logs will be integrated in Phase 2 (Tempo) and Phase 3 (Loki).
/// </summary>
public interface IPrometheusQueryService
{
    /// <summary>
    /// Get service status metrics (error rates, latency percentiles, request count).
    /// Uses PromQL queries for HTTP metrics.
    /// </summary>
    /// <param name="serviceName">Optional service name to filter (null for all services).</param>
    /// <param name="timeRangeMinutes">Time range in minutes to query (default 60).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Service status with error rates and latency metrics.</returns>
    Task<ServiceStatusResult> GetServiceStatusAsync(
        string? serviceName,
        int timeRangeMinutes,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get service topology (dependency graph derived from metrics).
    /// In Phase 1, this provides a basic topology from Prometheus scrape targets.
    /// Will be enhanced in Phase 2 with Tempo trace-based topology.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Service topology with nodes and edges.</returns>
    Task<ServiceTopologyResult> GetServiceTopologyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of all services reporting metrics to Prometheus.
    /// Uses label_values API to get unique 'job' labels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of services with metadata.</returns>
    Task<ServiceListResult> GetServiceListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get correlated logs for a trace/span (Phase 3 - Loki integration).
    /// Returns empty result in Phase 1 (metrics only).
    /// </summary>
    /// <param name="traceId">Trace ID to correlate.</param>
    /// <param name="spanId">Optional span ID to filter.</param>
    /// <param name="limit">Maximum number of logs to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Correlated logs for the trace/span.</returns>
    Task<CorrelatedLogsResult> GetCorrelatedLogsAsync(
        string traceId,
        string? spanId,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get OTLP exporter health status by querying Prometheus 'up' metric.
    /// Checks if all TansuCloud services are reporting metrics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health status for all OTLP exporters.</returns>
    Task<OtlpHealthResult> GetOtlpHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent errors from services (Phase 2 - Tempo traces integration).
    /// Returns empty result in Phase 1 (metrics only).
    /// </summary>
    /// <param name="serviceName">Optional service name to filter.</param>
    /// <param name="timeRangeMinutes">Time range in minutes to query.</param>
    /// <param name="limit">Maximum number of errors to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recent error traces.</returns>
    Task<RecentErrorsResult> GetRecentErrorsAsync(
        string? serviceName,
        int timeRangeMinutes,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get full trace details with all spans (Phase 2 - Tempo integration).
    /// Returns null in Phase 1 (metrics only).
    /// </summary>
    /// <param name="traceId">Trace ID to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full trace details or null if not found.</returns>
    Task<TraceDetailsResult?> GetTraceDetailsAsync(
        string traceId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Search for traces matching filters (Phase 2 - Tempo integration).
    /// Returns empty result in Phase 1 (metrics only).
    /// </summary>
    /// <param name="serviceName">Optional service name to filter.</param>
    /// <param name="timeRangeMinutes">Time range in minutes to search.</param>
    /// <param name="limit">Maximum number of traces to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results with trace summaries.</returns>
    Task<TracesSearchResult> SearchTracesAsync(
        string? serviceName,
        int timeRangeMinutes,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get circuit breaker state for diagnostics.
    /// </summary>
    /// <returns>Current circuit breaker state.</returns>
    CircuitBreakerState GetCircuitBreakerState();
} // End of Interface IPrometheusQueryService

/// <summary>
/// Circuit breaker state for Prometheus query service diagnostics.
/// </summary>
public sealed record CircuitBreakerState(
    string State,
    int FailureCount,
    DateTime? LastFailureTime,
    DateTime? NextRetryTime
); // End of Record CircuitBreakerState
