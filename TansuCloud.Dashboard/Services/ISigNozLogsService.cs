// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using TansuCloud.Dashboard.Models;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Service interface for querying logs from SigNoz.
/// Provides structured log search, filtering, and correlation with traces.
/// </summary>
public interface ISigNozLogsService
{
    /// <summary>
    /// Search logs using filters and pagination.
    /// </summary>
    /// <param name="request">Search request with filters (time range, service, severity, text search)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated log search results with entries</returns>
    Task<LogSearchResult> SearchLogsAsync(LogSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed log entry by ID.
    /// </summary>
    /// <param name="logId">Log entry ID (timestamp + unique identifier)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed log entry with all attributes</returns>
    Task<LogEntry?> GetLogByIdAsync(string logId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available log fields (attributes) for filtering and display.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available log fields with types</returns>
    Task<List<LogField>> GetLogFieldsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available service names from logs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of distinct service names</returns>
    Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default);
} // End of Interface ISigNozLogsService
