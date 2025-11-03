// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TansuCloud.Dashboard.Models;
using TansuCloud.Dashboard.Observability.SigNoz;
using SigNozSpanEvent = TansuCloud.Dashboard.Observability.SigNoz.SpanEvent;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Service for querying traces from SigNoz Query Service API.
/// Uses SigNoz v3 query_range API and v1 traces API.
/// </summary>
public sealed class SigNozTracesService : ISigNozTracesService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<SigNozQueryOptions> _options;
    private readonly ILogger<SigNozTracesService> _logger;
    private readonly ISigNozAuthenticationService _authService;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public SigNozTracesService(
        HttpClient httpClient,
        IOptionsMonitor<SigNozQueryOptions> options,
        ILogger<SigNozTracesService> logger,
        ISigNozAuthenticationService authService
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        ConfigureHttpClient();
    } // End of Constructor SigNozTracesService

    private void ConfigureHttpClient()
    {
        var opts = _options.CurrentValue;
        _httpClient.BaseAddress = new Uri(opts.ApiBaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    } // End of Method ConfigureHttpClient

    /// <inheritdoc/>
    public async Task<TraceSearchResult> SearchTracesAsync(
        TraceSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // Build SigNoz query_range API request
            var queryPayload = BuildTraceSearchQuery(request);

            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                "api/v3/query_range",
                cancellationToken
            );
            httpRequest.Content = JsonContent.Create(queryPayload, options: s_jsonOptions);

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "SigNoz trace search failed: {StatusCode}, {Error}",
                    response.StatusCode,
                    errorBody
                );
                return new TraceSearchResult
                {
                    Traces = new(),
                    Total = 0,
                    HasMore = false
                };
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(
                s_jsonOptions,
                cancellationToken
            );

            // Parse SigNoz response and map to our DTOs
            var traces = ParseTraceSearchResponse(jsonResponse);

            return new TraceSearchResult
            {
                Traces = traces,
                Total = traces.Count,
                HasMore = traces.Count >= request.Limit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching traces in SigNoz");
            return new TraceSearchResult
            {
                Traces = new(),
                Total = 0,
                HasMore = false
            };
        }
    } // End of Method SearchTracesAsync

    /// <inheritdoc/>
    public async Task<TraceDetail?> GetTraceByIdAsync(
        string traceId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            _logger.LogWarning("GetTraceById called with empty traceId");
            return null;
        }

        try
        {
            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"api/v1/traces/{traceId}",
                cancellationToken
            );

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SigNoz get trace failed: {StatusCode}, TraceId={TraceId}",
                    response.StatusCode,
                    traceId
                );
                return null;
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(
                s_jsonOptions,
                cancellationToken
            );

            // Parse SigNoz trace detail response
            return ParseTraceDetail(jsonResponse, traceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trace {TraceId} from SigNoz", traceId);
            return null;
        }
    } // End of Method GetTraceByIdAsync

    /// <inheritdoc/>
    public async Task<List<string>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Query for distinct service names from traces
            var queryPayload = new
            {
                start = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * 1_000_000,
                end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                queries = new[]
                {
                    new
                    {
                        queryName = "A",
                        aggregateOperator = "noop",
                        aggregateAttribute = new
                        {
                            key = "serviceName",
                            dataType = "string",
                            type = "tag",
                            isColumn = false
                        },
                        groupBy = new[] { new { key = "serviceName", dataType = "string" } }
                    }
                }
            };

            var httpRequest = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                "api/v3/query_range",
                cancellationToken
            );
            httpRequest.Content = JsonContent.Create(queryPayload, options: s_jsonOptions);

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SigNoz get services failed: {StatusCode}", response.StatusCode);
                return new List<string>();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(
                s_jsonOptions,
                cancellationToken
            );

            // Parse service names from response
            return ParseServiceList(jsonResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service list from SigNoz");
            return new List<string>();
        }
    } // End of Method GetServicesAsync

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var request = new HttpRequestMessage(method, url);

        // Add JWT auth if configured
        var token = await _authService.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token
            );
        }

        return request;
    } // End of Method CreateAuthenticatedRequestAsync

    private object BuildTraceSearchQuery(TraceSearchRequest request)
    {
        // Build filters based on search criteria
        var filters = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.ServiceName))
        {
            filters.Add(
                new
                {
                    key = "serviceName",
                    op = "=",
                    value = request.ServiceName
                }
            );
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            filters.Add(
                new
                {
                    key = "status",
                    op = "=",
                    value = request.Status
                }
            );
        }

        if (request.MinDurationMs.HasValue)
        {
            filters.Add(
                new
                {
                    key = "durationNano",
                    op = ">=",
                    value = request.MinDurationMs.Value * 1_000_000 // ms to nano
                }
            );
        }

        if (request.MaxDurationMs.HasValue)
        {
            filters.Add(
                new
                {
                    key = "durationNano",
                    op = "<=",
                    value = request.MaxDurationMs.Value * 1_000_000 // ms to nano
                }
            );
        }

        return new
        {
            start = request.StartTimeNano,
            end = request.EndTimeNano,
            queries = new[]
            {
                new
                {
                    queryName = "A",
                    dataSource = "traces",
                    aggregateOperator = "noop",
                    filters = filters.ToArray(),
                    limit = request.Limit,
                    offset = request.Offset,
                    orderBy = new[] { new { columnName = "timestamp", order = "desc" } }
                }
            }
        };
    } // End of Method BuildTraceSearchQuery

    private List<TraceSummary> ParseTraceSearchResponse(JsonElement response)
    {
        var traces = new List<TraceSummary>();

        try
        {
            // SigNoz v3 query_range response structure:
            // { "data": { "result": [ { "list": [ ... ] } ] } }
            if (!response.TryGetProperty("data", out var data))
                return traces;

            if (!data.TryGetProperty("result", out var result))
                return traces;

            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
                return traces;

            var firstResult = result[0];
            if (!firstResult.TryGetProperty("list", out var list))
                return traces;

            foreach (var item in list.EnumerateArray())
            {
                var trace = ParseTraceSummary(item);
                if (trace != null)
                    traces.Add(trace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing SigNoz trace search response");
        }

        return traces;
    } // End of Method ParseTraceSearchResponse

    private TraceSummary? ParseTraceSummary(JsonElement item)
    {
        try
        {
            var traceId = item.GetProperty("traceID").GetString() ?? string.Empty;
            var serviceName = item.TryGetProperty("serviceName", out var svc)
                ? svc.GetString() ?? "unknown"
                : "unknown";
            var operationName = item.TryGetProperty("name", out var op)
                ? op.GetString() ?? "unknown"
                : "unknown";

            var durationNano = item.TryGetProperty("durationNano", out var dur)
                ? dur.GetInt64()
                : 0;
            var durationMs = durationNano / 1_000_000;

            var spanCount = item.TryGetProperty("spanCount", out var sc) ? sc.GetInt32() : 1;

            var timestampNano = item.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0;
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampNano / 1_000_000);

            var status = item.TryGetProperty("status", out var st)
                ? st.GetString() ?? "UNSET"
                : "UNSET";

            var errorMessage = item.TryGetProperty("errorMessage", out var em)
                ? em.GetString()
                : null;

            return new TraceSummary
            {
                TraceId = traceId,
                ServiceName = serviceName,
                OperationName = operationName,
                DurationMs = durationMs,
                SpanCount = spanCount,
                Timestamp = timestamp,
                Status = status,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing trace summary item");
            return null;
        }
    } // End of Method ParseTraceSummary

    private TraceDetail? ParseTraceDetail(JsonElement response, string traceId)
    {
        try
        {
            // SigNoz v1 traces/{id} response structure:
            // { "data": { "spans": [ ... ] } }
            if (!response.TryGetProperty("data", out var data))
                return null;

            if (!data.TryGetProperty("spans", out var spans))
                return null;

            var spanList = new List<SpanDetail>();
            string? rootServiceName = null;
            long minStartTime = long.MaxValue;
            long maxEndTime = long.MinValue;

            foreach (var spanElement in spans.EnumerateArray())
            {
                var span = ParseSpanDetail(spanElement);
                if (span != null)
                {
                    spanList.Add(span);

                    // Track root service and time bounds
                    if (string.IsNullOrEmpty(span.ParentSpanId))
                        rootServiceName = span.ServiceName;

                    if (span.StartTimeNano < minStartTime)
                        minStartTime = span.StartTimeNano;
                    if (span.EndTimeNano > maxEndTime)
                        maxEndTime = span.EndTimeNano;
                }
            }

            if (spanList.Count == 0)
                return null;

            var durationMs = (maxEndTime - minStartTime) / 1_000_000;
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(minStartTime / 1_000_000);

            return new TraceDetail
            {
                TraceId = traceId,
                ServiceName = rootServiceName ?? "unknown",
                DurationMs = durationMs,
                Timestamp = timestamp,
                Spans = spanList
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing trace detail for {TraceId}", traceId);
            return null;
        }
    } // End of Method ParseTraceDetail

    private SpanDetail? ParseSpanDetail(JsonElement spanElement)
    {
        try
        {
            var spanId = spanElement.GetProperty("spanID").GetString() ?? string.Empty;
            var parentSpanId = spanElement.TryGetProperty("parentSpanID", out var psi)
                ? psi.GetString()
                : null;
            var serviceName = spanElement.GetProperty("serviceName").GetString() ?? "unknown";
            var name = spanElement.GetProperty("name").GetString() ?? "unknown";

            var kind = spanElement.TryGetProperty("kind", out var k)
                ? k.GetString() ?? "INTERNAL"
                : "INTERNAL";

            var startTimeNano = spanElement.GetProperty("startTimeUnixNano").GetInt64();
            var endTimeNano = spanElement.GetProperty("endTimeUnixNano").GetInt64();
            var durationMs = (endTimeNano - startTimeNano) / 1_000_000;

            var status = spanElement.TryGetProperty("statusCode", out var sc)
                ? ParseSpanStatus(sc.GetInt32())
                : "UNSET";

            var statusMessage = spanElement.TryGetProperty("statusMessage", out var sm)
                ? sm.GetString()
                : null;

            var attributes = new Dictionary<string, string>();
            if (spanElement.TryGetProperty("attributes", out var attrs))
            {
                foreach (var attr in attrs.EnumerateArray())
                {
                    var key = attr.GetProperty("key").GetString();
                    var value = attr.GetProperty("value").ToString();
                    if (key != null)
                        attributes[key] = value;
                }
            }

            var events = new List<Models.SpanEvent>();
            if (spanElement.TryGetProperty("events", out var evts))
            {
                foreach (var evt in evts.EnumerateArray())
                {
                    var eventName = evt.GetProperty("name").GetString() ?? "unknown";
                    var eventTime = evt.GetProperty("timeUnixNano").GetInt64();
                    var eventAttrs = new Dictionary<string, string>();

                    if (evt.TryGetProperty("attributes", out var eventAttrsList))
                    {
                        foreach (var attr in eventAttrsList.EnumerateArray())
                        {
                            var key = attr.GetProperty("key").GetString();
                            var value = attr.GetProperty("value").ToString();
                            if (key != null)
                                eventAttrs[key] = value;
                        }
                    }

                    events.Add(
                        new Models.SpanEvent
                        {
                            Name = eventName,
                            TimestampNano = eventTime,
                            Attributes = eventAttrs
                        }
                    );
                }
            }

            return new SpanDetail
            {
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                ServiceName = serviceName,
                Name = name,
                Kind = kind,
                StartTimeNano = startTimeNano,
                EndTimeNano = endTimeNano,
                DurationMs = durationMs,
                Status = status,
                StatusMessage = statusMessage,
                Attributes = attributes,
                Events = events
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing span detail");
            return null;
        }
    } // End of Method ParseSpanDetail

    private string ParseSpanStatus(int statusCode)
    {
        // OpenTelemetry status codes: 0=UNSET, 1=OK, 2=ERROR
        return statusCode switch
        {
            0 => "UNSET",
            1 => "OK",
            2 => "ERROR",
            _ => "UNSET"
        };
    } // End of Method ParseSpanStatus

    private List<string> ParseServiceList(JsonElement response)
    {
        var services = new List<string>();

        try
        {
            if (!response.TryGetProperty("data", out var data))
                return services;

            if (!data.TryGetProperty("result", out var result))
                return services;

            if (result.ValueKind != JsonValueKind.Array)
                return services;

            foreach (var item in result.EnumerateArray())
            {
                if (item.TryGetProperty("list", out var list))
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        if (entry.TryGetProperty("serviceName", out var svc))
                        {
                            var serviceName = svc.GetString();
                            if (!string.IsNullOrWhiteSpace(serviceName))
                                services.Add(serviceName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing service list response");
        }

        return services.Distinct().OrderBy(s => s).ToList();
    } // End of Method ParseServiceList
} // End of Class SigNozTracesService
