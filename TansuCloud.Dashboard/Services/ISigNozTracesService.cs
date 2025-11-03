// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using TansuCloud.Dashboard.Models;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Service for querying traces from SigNoz Query Service API.
/// Provides search, filtering, and detailed trace retrieval capabilities.
/// </summary>
public interface ISigNozTracesService
{
    /// <summary>
    /// Search for traces matching the specified filters.
    /// </summary>
    /// <param name="request">Search criteria including time range, service filter, status, duration, etc.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation</param>
    /// <returns>List of trace summaries matching the search criteria</returns>
    Task<TraceSearchResult> SearchTracesAsync(TraceSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single trace by its ID, including all spans in the trace tree.
    /// </summary>
    /// <param name="traceId">Unique trace identifier</param>
    /// <param name="cancellationToken">Cancellation token for the async operation</param>
    /// <returns>Complete trace with all spans and their relationships</returns>
    Task<TraceDetail?> GetTraceByIdAsync(string traceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of services that have reported traces.
    /// Useful for populating service filter dropdowns.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the async operation</param>
    /// <returns>List of service names</returns>
    Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default);
} // End of Interface ISigNozTracesService
