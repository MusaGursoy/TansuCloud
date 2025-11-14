// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Dashboard.Observability.Tempo;

/// <summary>
/// Service for querying distributed traces from Grafana Tempo.
/// </summary>
public interface ITempoTracesService
{
    /// <summary>
    /// Searches for traces matching the specified filters using TraceQL.
    /// </summary>
    /// <param name="filters">Search filters (service, duration, status, time range, TraceQL query).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results with matching traces.</returns>
    Task<TempoTraceSearchResult> SearchTracesAsync(
        TempoSearchFilters filters,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Retrieves a complete trace by its ID, including all spans.
    /// </summary>
    /// <param name="traceId">The trace ID (hex string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete trace with all spans, or null if not found.</returns>
    Task<TempoTrace?> GetTraceByIdAsync(string traceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the list of services that have sent traces to Tempo.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of service names.</returns>
    Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the list of operation names for a specific service.
    /// </summary>
    /// <param name="serviceName">The service name to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of operation names.</returns>
    Task<List<string>> GetOperationsAsync(
        string serviceName,
        CancellationToken cancellationToken = default
    );
} // End of Interface ITempoTracesService
