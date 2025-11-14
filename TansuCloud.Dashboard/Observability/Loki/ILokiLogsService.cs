// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Dashboard.Observability.Loki;

/// <summary>
/// Service for querying Grafana Loki log aggregation API.
/// Provides access to TansuCloud application logs stored in Loki.
/// </summary>
public interface ILokiLogsService
{
    /// <summary>
    /// Search logs using filters and LogQL queries.
    /// </summary>
    /// <param name="filters">Search filters (service, severity, time range, query).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search result containing matching log entries.</returns>
    Task<LokiLogSearchResult> SearchLogsAsync(
        LokiSearchFilters filters,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get unique list of services that are sending logs to Loki.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of service names.</returns>
    Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available log labels (fields) for filtering.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of label names.</returns>
    Task<List<string>> GetLabelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unique values for a specific label.
    /// </summary>
    /// <param name="labelName">Label name (e.g., "level", "service_name").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of unique values for the label.</returns>
    Task<List<string>> GetLabelValuesAsync(
        string labelName,
        CancellationToken cancellationToken = default
    );

} // End of Interface ILokiLogsService
