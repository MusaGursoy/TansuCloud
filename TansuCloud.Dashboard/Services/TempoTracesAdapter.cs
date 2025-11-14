// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using TansuCloud.Dashboard.Models;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Tempo-backed implementation of ISigNozTracesService.
/// Provides trace search and retrieval using Grafana Tempo as the backend.
/// This implementation replaces the SigNoz traces service as part of Task 47 Phase 2.
/// </summary>
public sealed class TempoTracesAdapter : ISigNozTracesService
{
    private readonly Observability.Tempo.ITempoTracesService _tempoService;
    private readonly ILogger<TempoTracesAdapter> _logger;

    public TempoTracesAdapter(
        Observability.Tempo.ITempoTracesService tempoService,
        ILogger<TempoTracesAdapter> logger)
    {
        _tempoService = tempoService ?? throw new ArgumentNullException(nameof(tempoService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    } // End of Constructor TempoTracesAdapter

    /// <inheritdoc />
    public async Task<TraceSearchResult> SearchTracesAsync(
        TraceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // Convert Dashboard request to Tempo filters
            var tempoFilters = new Observability.Tempo.TempoSearchFilters
            {
                ServiceName = request.ServiceName,
                MinDurationMs = request.MinDurationMs.HasValue ? (int)request.MinDurationMs.Value : null,
                MaxDurationMs = request.MaxDurationMs.HasValue ? (int)request.MaxDurationMs.Value : null,
                Status = request.Status?.ToLowerInvariant(), // Tempo expects lowercase "ok"/"error"
                StartUnixSeconds = request.StartTimeNano / 1_000_000_000,
                EndUnixSeconds = request.EndTimeNano / 1_000_000_000,
                Limit = request.Limit
            };

            _logger.LogDebug(
                "Searching Tempo traces: Service={Service}, Duration={MinMs}-{MaxMs}ms, Status={Status}, Limit={Limit}",
                tempoFilters.ServiceName ?? "all",
                tempoFilters.MinDurationMs,
                tempoFilters.MaxDurationMs,
                tempoFilters.Status ?? "all",
                tempoFilters.Limit
            );

            var tempoResult = await _tempoService.SearchTracesAsync(tempoFilters, cancellationToken);

            // Convert Tempo result to Dashboard model
            var dashboardResult = Observability.Tempo.TempoAdapter.ToSearchResult(tempoResult);

            _logger.LogInformation(
                "Tempo search completed: {Count} traces found",
                dashboardResult.Traces.Count
            );

            return dashboardResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Tempo traces");
            // Return empty result instead of throwing to gracefully degrade UI
            return new TraceSearchResult { Traces = [], Total = 0, HasMore = false };
        }
    } // End of Method SearchTracesAsync

    /// <inheritdoc />
    public async Task<TraceDetail?> GetTraceByIdAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceId));
        }

        try
        {
            _logger.LogDebug("Fetching Tempo trace: {TraceId}", traceId);

            var tempoTrace = await _tempoService.GetTraceByIdAsync(traceId, cancellationToken);

            if (tempoTrace == null)
            {
                _logger.LogInformation("Trace not found in Tempo: {TraceId}", traceId);
                return null;
            }

            // Convert Tempo trace to Dashboard model
            var dashboardTrace = Observability.Tempo.TempoAdapter.ToTraceDetail(tempoTrace);

            if (dashboardTrace != null)
            {
                _logger.LogInformation(
                    "Tempo trace retrieved: {TraceId}, {SpanCount} spans, {DurationMs}ms",
                    traceId,
                    dashboardTrace.Spans.Count,
                    dashboardTrace.DurationMs
                );
            }

            return dashboardTrace;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Tempo trace: {TraceId}", traceId);
            return null;
        }
    } // End of Method GetTraceByIdAsync

    /// <inheritdoc />
    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Fetching services from Tempo");

            var services = await _tempoService.GetServicesAsync(cancellationToken);

            _logger.LogInformation("Tempo services retrieved: {Count} services", services.Count);

            return services;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch services from Tempo");
            return [];
        }
    } // End of Method GetServicesAsync

} // End of Class TempoTracesAdapter
