// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Tempo;

/// <summary>
/// Service for querying Grafana Tempo trace data via HTTP API.
/// Implements ITempoTracesService using HttpClient to access Tempo's REST endpoints.
/// </summary>
public sealed class TempoTracesService : ITempoTracesService
{
    private readonly HttpClient _httpClient;
    private readonly TempoQueryOptions _options;
    private readonly ILogger<TempoTracesService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TempoTracesService(
        HttpClient httpClient,
        IOptions<TempoQueryOptions> options,
        ILogger<TempoTracesService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Configure HttpClient base address and timeout
        _httpClient.BaseAddress = new Uri(_options.ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    } // End of Constructor TempoTracesService

    /// <inheritdoc />
    public async Task<TempoTraceSearchResult> SearchTracesAsync(
        TempoSearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        try
        {
            // Build query string from filters
            var queryString = BuildSearchQueryString(filters);
            var requestUri = $"/api/search?{queryString}";

            _logger.LogDebug("Searching Tempo traces: {RequestUri}", requestUri);

            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<TempoSearchApiResponse>(
                JsonOptions,
                cancellationToken);

            if (apiResponse is null)
            {
                _logger.LogWarning("Tempo search returned null response");
                return new TempoTraceSearchResult();
            }

            // Map API response to public result type
            return MapToSearchResult(apiResponse);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to search Tempo traces");
            return new TempoTraceSearchResult();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tempo search response");
            return new TempoTraceSearchResult();
        }
    } // End of Method SearchTracesAsync

    /// <inheritdoc />
    public async Task<TempoTrace?> GetTraceByIdAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceId));
        }

        try
        {
            var requestUri = $"/api/traces/{traceId}";
            _logger.LogDebug("Fetching Tempo trace by ID: {TraceId}", traceId);

            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Trace not found: {TraceId}", traceId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<TempoTraceApiResponse>(
                JsonOptions,
                cancellationToken);

            if (apiResponse is null || apiResponse.Batches is null || apiResponse.Batches.Count == 0)
            {
                _logger.LogWarning("Tempo trace response is empty for trace ID: {TraceId}", traceId);
                return null;
            }

            // Map OTLP batches to simplified trace model
            return MapToTrace(traceId, apiResponse);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Tempo trace by ID: {TraceId}", traceId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tempo trace response for ID: {TraceId}", traceId);
            return null;
        }
    } // End of Method GetTraceByIdAsync

    /// <inheritdoc />
    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Tempo doesn't have a dedicated services endpoint
            // We need to query tag values for the "service.name" tag
            var requestUri = "/api/search/tag/service.name/values";
            _logger.LogDebug("Fetching Tempo services list");

            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var services = await response.Content.ReadFromJsonAsync<List<string>>(
                JsonOptions,
                cancellationToken);

            return services ?? [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Tempo services list");
            return [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tempo services response");
            return [];
        }
    } // End of Method GetServicesAsync

    /// <inheritdoc />
    public async Task<List<string>> GetOperationsAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        }

        try
        {
            // Query tag values for "name" (span name) filtered by service
            // Tempo's tag API: /api/search/tag/{tagName}/values?q={traceql}
            var traceQL = HttpUtility.UrlEncode($"{{.service.name=\"{serviceName}\"}}");
            var requestUri = $"/api/search/tag/name/values?q={traceQL}";
            
            _logger.LogDebug("Fetching Tempo operations for service: {ServiceName}", serviceName);

            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var operations = await response.Content.ReadFromJsonAsync<List<string>>(
                JsonOptions,
                cancellationToken);

            return operations ?? [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Tempo operations for service: {ServiceName}", serviceName);
            return [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Tempo operations response");
            return [];
        }
    } // End of Method GetOperationsAsync

    #region Private Mapping Methods

    private static string BuildSearchQueryString(TempoSearchFilters filters)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);

        // Build TraceQL query from filters
        var traceQLParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(filters.ServiceName))
        {
            traceQLParts.Add($".service.name=\"{filters.ServiceName}\"");
        }

        if (filters.MinDurationMs.HasValue)
        {
            traceQLParts.Add($"duration >= {filters.MinDurationMs.Value}ms");
        }

        if (filters.MaxDurationMs.HasValue)
        {
            traceQLParts.Add($"duration <= {filters.MaxDurationMs.Value}ms");
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            traceQLParts.Add($".status=\"{filters.Status}\"");
        }

        // If user provided custom TraceQL, append it
        if (!string.IsNullOrWhiteSpace(filters.TraceQLQuery))
        {
            traceQLParts.Add(filters.TraceQLQuery);
        }

        // Combine TraceQL parts
        if (traceQLParts.Count > 0)
        {
            var traceQL = "{" + string.Join(" && ", traceQLParts) + "}";
            query["q"] = traceQL;
        }

        // Time range
        if (filters.StartUnixSeconds.HasValue)
        {
            query["start"] = filters.StartUnixSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (filters.EndUnixSeconds.HasValue)
        {
            query["end"] = filters.EndUnixSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        // Limit
        query["limit"] = filters.Limit.ToString(CultureInfo.InvariantCulture);

        return query.ToString() ?? string.Empty;
    } // End of Method BuildSearchQueryString

    private static TempoTraceSearchResult MapToSearchResult(TempoSearchApiResponse apiResponse)
    {
        var traces = apiResponse.Traces?
            .Where(t => !string.IsNullOrWhiteSpace(t.TraceId))
            .Select(t => new TempoTraceMetadata
            {
                TraceId = t.TraceId!,
                RootServiceName = t.RootServiceName ?? "unknown",
                RootTraceName = t.RootTraceName ?? "unknown",
                StartTimeUnixNano = long.TryParse(t.StartTimeUnixNano, out var start) ? start : 0,
                DurationMs = t.DurationMs
            })
            .ToList() ?? [];

        var metrics = apiResponse.Metrics is not null
            ? new TempoSearchMetrics
            {
                InspectedTraces = apiResponse.Metrics.InspectedTraces,
                InspectedBytes = long.TryParse(apiResponse.Metrics.InspectedBytes, out var bytes) ? bytes : 0,
                CompletedJobs = apiResponse.Metrics.CompletedJobs,
                TotalJobs = apiResponse.Metrics.TotalJobs
            }
            : null;

        return new TempoTraceSearchResult
        {
            Traces = traces,
            Metrics = metrics
        };
    } // End of Method MapToSearchResult

    private static TempoTrace MapToTrace(string traceId, TempoTraceApiResponse apiResponse)
    {
        var spans = new List<TempoSpan>();

        foreach (var batch in apiResponse.Batches ?? [])
        {
            // Extract service name from resource attributes
            var serviceName = batch.Resource?.Attributes?
                .FirstOrDefault(a => a.Key == "service.name")
                ?.Value?.StringValue ?? "unknown";

            foreach (var scopeSpan in batch.ScopeSpans ?? [])
            {
                foreach (var otlpSpan in scopeSpan.Spans ?? [])
                {
                    if (string.IsNullOrWhiteSpace(otlpSpan.SpanId))
                        continue;

                    // Calculate duration from start/end times
                    var startNano = long.TryParse(otlpSpan.StartTimeUnixNano, out var start) ? start : 0;
                    var endNano = long.TryParse(otlpSpan.EndTimeUnixNano, out var end) ? end : 0;
                    var durationNano = endNano - startNano;

                    // Map attributes to simple key-value dictionary
                    var tags = otlpSpan.Attributes?
                        .Where(a => !string.IsNullOrWhiteSpace(a.Key))
                        .ToDictionary(
                            a => a.Key!,
                            a => a.Value?.StringValue 
                                 ?? a.Value?.IntValue 
                                 ?? a.Value?.BoolValue?.ToString() 
                                 ?? string.Empty
                        ) ?? [];

                    // Map events
                    var events = otlpSpan.Events?
                        .Select(e => new TempoSpanEvent
                        {
                            TimeUnixNano = long.TryParse(e.TimeUnixNano, out var time) ? time : 0,
                            Name = e.Name ?? "event",
                            Attributes = e.Attributes?
                                .Where(a => !string.IsNullOrWhiteSpace(a.Key))
                                .ToDictionary(
                                    a => a.Key!,
                                    a => a.Value?.StringValue ?? string.Empty
                                ) ?? []
                        })
                        .ToList() ?? [];

                    var span = new TempoSpan
                    {
                        SpanId = otlpSpan.SpanId!,
                        ParentSpanId = string.IsNullOrWhiteSpace(otlpSpan.ParentSpanId) 
                            ? null 
                            : otlpSpan.ParentSpanId,
                        OperationName = otlpSpan.Name ?? "unknown",
                        ServiceName = serviceName,
                        StartTimeUnixNano = startNano,
                        DurationNano = durationNano,
                        Tags = tags,
                        Events = events,
                        Status = otlpSpan.Status?.Code == 0 
                            ? "ok" 
                            : otlpSpan.Status?.Message ?? "error"
                    };

                    spans.Add(span);
                }
            }
        }

        return new TempoTrace
        {
            TraceId = traceId,
            Spans = spans
        };
    } // End of Method MapToTrace

    #endregion

} // End of Class TempoTracesService
