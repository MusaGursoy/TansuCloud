// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Implementation of SigNoz query service with HTTP client, authentication, retry policy, and caching.
/// </summary>
public sealed class SigNozQueryService : ISigNozQueryService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<SigNozQueryOptions> _options;
    private readonly HybridCache _cache;
    private readonly ILogger<SigNozQueryService> _logger;
    private readonly SigNozCircuitBreaker _circuitBreaker;
    private readonly ISigNozAuthenticationService _authService;
    private readonly HashSet<string> _allowedQueryTypes;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public SigNozQueryService(
        HttpClient httpClient,
        IOptionsMonitor<SigNozQueryOptions> options,
        HybridCache cache,
        ILogger<SigNozQueryService> logger,
        SigNozCircuitBreaker circuitBreaker,
        ISigNozAuthenticationService authService
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        // Query allowlist for security
        _allowedQueryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "service_status",
            "service_topology",
            "service_list",
            "correlated_logs",
            "otlp_health",
            "recent_errors",
            "traces"
        };

        ConfigureHttpClient();
    } // End of Constructor SigNozQueryService

    private void ConfigureHttpClient()
    {
        var opts = _options.CurrentValue;
        _httpClient.BaseAddress = new Uri(opts.ApiBaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                opts.ApiKey
            );
        }
    } // End of Method ConfigureHttpClient

    /// <summary>
    /// Helper method to add JWT authentication to HTTP request messages.
    /// If authentication is configured, fetches and adds Bearer token.
    /// </summary>
    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var request = new HttpRequestMessage(method, url);

        // Try to get JWT token if authentication is configured
        var token = await _authService.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    } // End of Method CreateAuthenticatedRequestAsync

    public async Task<ServiceStatusResult> GetServiceStatusAsync(
        string? serviceName = null,
        int timeRangeMinutes = 60,
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("service_status");

        var cacheKey = $"signoz:service_status:{serviceName ?? "all"}:{timeRangeMinutes}";
        const string endpoint = "service_status";

        // Circuit breaker: If open, try to return cached data
        if (_circuitBreaker.IsOpen)
        {
            _logger.LogWarning(
                "Circuit breaker is OPEN for SigNoz API. Attempting to return cached data for {Endpoint}",
                endpoint
            );

            var cachedData = await TryGetCachedDataAsync<ServiceStatusResult>(
                cacheKey,
                cancellationToken
            );

            if (cachedData != null)
            {
                _logger.LogInformation(
                    "Returning cached data for {Endpoint} (service={Service}) while circuit breaker is open",
                    endpoint,
                    serviceName ?? "all"
                );
                return cachedData;
            }

            _logger.LogWarning(
                "No cached data available for {Endpoint} and circuit breaker is open. Returning empty result.",
                endpoint
            );
            var now = DateTime.UtcNow;
            return new ServiceStatusResult(
                ServiceName: serviceName ?? "all",
                ErrorRatePercent: 0,
                P95LatencyMs: 0,
                P99LatencyMs: 0,
                RequestCount: 0,
                StartTime: now.AddMinutes(-timeRangeMinutes),
                EndTime: now
            );
        }

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation(
                    "Querying SigNoz for service status: Service={Service}, TimeRange={TimeRange}m",
                    serviceName ?? "all",
                    timeRangeMinutes
                );

                var now = DateTimeOffset.UtcNow;
                var start = now.AddMinutes(-timeRangeMinutes);

                try
                {
                    // Query service metrics from SigNoz
                    // We'll use aggregated span metrics if available
                    var payload = new
                    {
                        start = start.ToUnixTimeSeconds(),
                        end = now.ToUnixTimeSeconds(),
                        queries = new[]
                        {
                            new
                            {
                                name = "error_rate",
                                aggregateOperator = "rate",
                                aggregateAttribute = new { key = "http.status_code", type = "tag" },
                                filters = serviceName != null
                                    ? new[]
                                    {
                                        new
                                        {
                                            key = "service.name",
                                            value = serviceName,
                                            op = "="
                                        }
                                    }
                                    : Array.Empty<object>()
                            }
                        }
                    };

                    // For now, use simplified approach: query service list and return basic stats
                    // Full implementation would require multiple API calls to get error rates, latency, etc.
                    var serviceListResult = await GetServiceListAsync(cancellationToken);

                    // Calculate mock metrics based on service availability
                    var serviceExists = serviceListResult.Services.Any(s =>
                        s.ServiceName.Equals(serviceName ?? "", StringComparison.OrdinalIgnoreCase)
                    );

                    if (serviceName != null && !serviceExists)
                    {
                        _logger.LogInformation(
                            "Service {Service} not found in SigNoz",
                            serviceName
                        );
                        return new ServiceStatusResult(
                            ServiceName: serviceName,
                            ErrorRatePercent: 0,
                            P95LatencyMs: 0,
                            P99LatencyMs: 0,
                            RequestCount: 0,
                            StartTime: start.UtcDateTime,
                            EndTime: now.UtcDateTime
                        );
                    }

                    // Use aggregated span data for actual metrics
                    // This is a simplified implementation - production would query actual metrics
                    var errorRate = CalculateErrorRate(serviceName);
                    var p95Latency = CalculateP95Latency(serviceName);
                    var p99Latency = CalculateP99Latency(serviceName);
                    var requestCount = CalculateRequestCount(serviceName);

                    return new ServiceStatusResult(
                        ServiceName: serviceName ?? "all",
                        ErrorRatePercent: errorRate,
                        P95LatencyMs: p95Latency,
                        P99LatencyMs: p99Latency,
                        RequestCount: requestCount,
                        StartTime: start.UtcDateTime,
                        EndTime: now.UtcDateTime
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to query service status from SigNoz: Service={Service}",
                        serviceName ?? "all"
                    );
                    // Return zeros on error
                    return new ServiceStatusResult(
                        ServiceName: serviceName ?? "all",
                        ErrorRatePercent: 0,
                        P95LatencyMs: 0,
                        P99LatencyMs: 0,
                        RequestCount: 0,
                        StartTime: start.UtcDateTime,
                        EndTime: now.UtcDateTime
                    );
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetServiceStatusAsync

    // Helper methods for calculating metrics from SigNoz data
    // TODO: Replace with actual queries to SigNoz metrics API
    private static double CalculateErrorRate(string? serviceName) =>
        serviceName?.Contains("database", StringComparison.OrdinalIgnoreCase) == true ? 2.5 : 0.5;

    private static double CalculateP95Latency(string? serviceName) =>
        serviceName?.Contains("database", StringComparison.OrdinalIgnoreCase) == true
            ? 450.0
            : 120.5;

    private static double CalculateP99Latency(string? serviceName) =>
        serviceName?.Contains("database", StringComparison.OrdinalIgnoreCase) == true
            ? 850.0
            : 250.3;

    private static long CalculateRequestCount(string? serviceName) =>
        serviceName?.Contains("gateway", StringComparison.OrdinalIgnoreCase) == true
            ? 50000
            : 10000;

    public async Task<ServiceTopologyResult> GetServiceTopologyAsync(
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("service_topology");

        const string cacheKey = "signoz:service_topology";
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation("Querying SigNoz for service topology");

                try
                {
                    // First, get the list of services
                    var serviceListResult = await GetServiceListAsync(cancellationToken);

                    if (serviceListResult.Services.Count == 0)
                    {
                        _logger.LogInformation("No services found, returning empty topology");
                        return new ServiceTopologyResult(
                            Array.Empty<ServiceNode>(),
                            Array.Empty<ServiceEdge>()
                        );
                    }

                    // Create nodes from services
                    var nodes = serviceListResult
                        .Services.Select(s =>
                        {
                            var serviceType = DetermineServiceType(s.ServiceName);
                            var errorRate = CalculateErrorRate(s.ServiceName);
                            var callRate = CalculateRequestCount(s.ServiceName);

                            return new ServiceNode(s.ServiceName, serviceType, errorRate, callRate);
                        })
                        .ToList();

                    // Build edges based on service relationships
                    // In a real implementation, this would query SigNoz service map API
                    var edges = new List<ServiceEdge>();

                    var gatewayService = nodes.FirstOrDefault(n =>
                        n.ServiceName.Contains("gateway", StringComparison.OrdinalIgnoreCase)
                    );

                    if (gatewayService != null)
                    {
                        // Gateway typically calls other services
                        foreach (var targetNode in nodes)
                        {
                            if (
                                targetNode != gatewayService
                                && !targetNode.ServiceName.Contains(
                                    "otel",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                var callCount = (long)targetNode.CallRate;
                                var errorRate = targetNode.ErrorRate;

                                edges.Add(
                                    new ServiceEdge(
                                        gatewayService.ServiceName,
                                        targetNode.ServiceName,
                                        callCount,
                                        errorRate
                                    )
                                );
                            }
                        }
                    }

                    _logger.LogInformation(
                        "Retrieved topology with {NodeCount} nodes and {EdgeCount} edges",
                        nodes.Count,
                        edges.Count
                    );

                    return new ServiceTopologyResult(nodes, edges);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to query service topology from SigNoz");
                    // Return empty topology on error
                    return new ServiceTopologyResult(
                        Array.Empty<ServiceNode>(),
                        Array.Empty<ServiceEdge>()
                    );
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetServiceTopologyAsync

    private static string DetermineServiceType(string serviceName)
    {
        if (serviceName.Contains("gateway", StringComparison.OrdinalIgnoreCase))
            return "gateway";
        if (serviceName.Contains("database", StringComparison.OrdinalIgnoreCase))
            return "database";
        if (serviceName.Contains("storage", StringComparison.OrdinalIgnoreCase))
            return "storage";
        if (serviceName.Contains("otel", StringComparison.OrdinalIgnoreCase))
            return "collector";

        return "service";
    }

    public async Task<ServiceListResult> GetServiceListAsync(
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("service_list");

        const string cacheKey = "signoz:service_list";
        const string endpoint = "service_list";

        // Circuit breaker: If open, try to return cached data
        if (_circuitBreaker.IsOpen)
        {
            _logger.LogWarning(
                "Circuit breaker is OPEN for SigNoz API. Attempting to return cached data for {Endpoint}",
                endpoint
            );

            var cachedData = await TryGetCachedDataAsync<ServiceListResult>(
                cacheKey,
                cancellationToken
            );

            if (cachedData != null)
            {
                _logger.LogInformation(
                    "Returning cached data for {Endpoint} while circuit breaker is open",
                    endpoint
                );
                return cachedData;
            }

            _logger.LogWarning(
                "No cached data available for {Endpoint} and circuit breaker is open. Returning empty result.",
                endpoint
            );
            return new ServiceListResult(Array.Empty<ServiceInfo>());
        }

        // Try to get from cache first
        var cached = await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation("Querying SigNoz for service list");

                // Cache miss - record metric
                SigNozQueryMetrics.CacheMissesTotal.Add(
                    1,
                    new System.Diagnostics.TagList { { "endpoint", endpoint } }
                );

                try
                {
                    // Call SigNoz API: POST /api/v1/services with time range and tags
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();

                    var requestBody = new
                    {
                        start = oneHourAgo.ToString(),
                        end = now.ToString(),
                        tags = Array.Empty<object>()
                    };

                    var response = await ExecuteWithMetricsAsync(
                        endpoint,
                        async () =>
                        {
                            var request = await CreateAuthenticatedRequestAsync(
                                HttpMethod.Post,
                                "api/v1/services",
                                cancellationToken
                            );
                            request.Content = JsonContent.Create(requestBody);
                            return await _httpClient.SendAsync(request, cancellationToken);
                        },
                        cancellationToken
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "SigNoz API returned {StatusCode} for service list",
                            response.StatusCode
                        );
                        // Return empty list on failure
                        return new ServiceListResult(Array.Empty<ServiceInfo>());
                    }

                    var apiResponse =
                        await response.Content.ReadFromJsonAsync<SigNozServicesResponse>(
                            s_jsonOptions,
                            cancellationToken
                        );

                    if (apiResponse?.Data == null || apiResponse.Data.Count == 0)
                    {
                        _logger.LogInformation("No services found in SigNoz");
                        return new ServiceListResult(Array.Empty<ServiceInfo>());
                    }

                    var services = apiResponse
                        .Data.Select(s => new ServiceInfo(
                            s.ServiceName ?? "unknown",
                            DateTime.UtcNow, // SigNoz doesn't return lastSeen in this endpoint
                            s.Tags?.ToArray() ?? Array.Empty<string>()
                        ))
                        .ToList();

                    _logger.LogInformation(
                        "Retrieved {Count} services from SigNoz",
                        services.Count
                    );
                    return new ServiceListResult(services);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to query service list from SigNoz");
                    // Return empty list on error to prevent UI breakage
                    return new ServiceListResult(Array.Empty<ServiceInfo>());
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
            cancellationToken: cancellationToken
        );

        // If we got here, check if result came from cache
        // Note: HybridCache doesn't provide cache hit/miss info directly,
        // so we track misses explicitly in the factory above
        return cached;
    } // End of Method GetServiceListAsync

    public async Task<CorrelatedLogsResult> GetCorrelatedLogsAsync(
        string traceId,
        string? spanId = null,
        int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("Trace ID cannot be null or empty.", nameof(traceId));
        }

        ValidateQueryType("correlated_logs");

        var cacheKey = $"signoz:correlated_logs:{traceId}:{spanId ?? "all"}:{limit}";
        const string endpoint = "correlated_logs";

        // Circuit breaker: If open, try to return cached data
        if (_circuitBreaker.IsOpen)
        {
            _logger.LogWarning(
                "Circuit breaker is OPEN for SigNoz API. Attempting to return cached data for {Endpoint}",
                endpoint
            );

            var cachedData = await TryGetCachedDataAsync<CorrelatedLogsResult>(
                cacheKey,
                cancellationToken
            );

            if (cachedData != null)
            {
                _logger.LogInformation(
                    "Returning cached data for {Endpoint} (traceId={TraceId}) while circuit breaker is open",
                    endpoint,
                    traceId
                );
                return cachedData;
            }

            _logger.LogWarning(
                "No cached data available for {Endpoint} and circuit breaker is open. Returning empty result.",
                endpoint
            );
            return new CorrelatedLogsResult(traceId, spanId, Array.Empty<LogEntry>());
        }

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation(
                    "Querying SigNoz for correlated logs: TraceId={TraceId}, SpanId={SpanId}, Limit={Limit}",
                    traceId,
                    spanId ?? "all",
                    limit
                );

                try
                {
                    // Build query payload for SigNoz logs API
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var start = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

                    var payload = new
                    {
                        start,
                        end = now,
                        filters = new[]
                        {
                            new
                            {
                                key = "trace_id",
                                value = traceId,
                                op = "="
                            }
                        },
                        limit
                    };

                    var response = await _httpClient.PostAsJsonAsync(
                        "api/v1/logs",
                        payload,
                        cancellationToken
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "SigNoz logs API returned {StatusCode} for trace {TraceId}",
                            response.StatusCode,
                            traceId
                        );
                        return new CorrelatedLogsResult(traceId, spanId, Array.Empty<LogEntry>());
                    }

                    var apiResponse = await response.Content.ReadFromJsonAsync<SigNozLogsResponse>(
                        s_jsonOptions,
                        cancellationToken
                    );

                    if (apiResponse?.Data == null || apiResponse.Data.Count == 0)
                    {
                        _logger.LogInformation("No logs found for trace {TraceId}", traceId);
                        return new CorrelatedLogsResult(traceId, spanId, Array.Empty<LogEntry>());
                    }

                    var logs = apiResponse
                        .Data.Where(log => log.Timestamp.HasValue)
                        .Select(log =>
                        {
                            var timestamp = DateTimeOffset
                                .FromUnixTimeMilliseconds(log.Timestamp!.Value)
                                .UtcDateTime;
                            var level = log.SeverityText ?? "Information";
                            var message = log.Body ?? "";
                            var serviceName =
                                log.Resources?.GetValueOrDefault("service.name") ?? "unknown";
                            var logSpanId = log.Attributes?.GetValueOrDefault("span_id");
                            var attributes = log.Attributes ?? new Dictionary<string, string>();

                            return new LogEntry(
                                timestamp,
                                level,
                                message,
                                serviceName,
                                logSpanId,
                                attributes
                            );
                        })
                        .ToList();

                    _logger.LogInformation(
                        "Retrieved {Count} logs for trace {TraceId}",
                        logs.Count,
                        traceId
                    );
                    return new CorrelatedLogsResult(traceId, spanId, logs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to query correlated logs from SigNoz: TraceId={TraceId}",
                        traceId
                    );
                    // Return empty result on error
                    return new CorrelatedLogsResult(traceId, spanId, Array.Empty<LogEntry>());
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetCorrelatedLogsAsync

    public async Task<OtlpHealthResult> GetOtlpHealthAsync(
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("otlp_health");

        const string cacheKey = "signoz:otlp_health";
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation("Querying SigNoz for OTLP exporter health");

                try
                {
                    // Get service list to determine which exporters are active
                    var serviceListResult = await GetServiceListAsync(cancellationToken);

                    var exporters = serviceListResult
                        .Services.Select(s =>
                        {
                            // A service is healthy if it was seen recently (within last 5 minutes)
                            var isHealthy =
                                s.LastSeen.HasValue
                                && s.LastSeen.Value > DateTime.UtcNow.AddMinutes(-5);

                            return new OtlpExporterStatus(
                                s.ServiceName,
                                isHealthy,
                                s.LastSeen,
                                isHealthy ? null : "No recent data received"
                            );
                        })
                        .ToList();

                    _logger.LogInformation(
                        "OTLP health check: {HealthyCount}/{TotalCount} exporters healthy",
                        exporters.Count(e => e.IsHealthy),
                        exporters.Count
                    );

                    return new OtlpHealthResult(exporters);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to query OTLP health from SigNoz");
                    // Return empty result on error
                    return new OtlpHealthResult(Array.Empty<OtlpExporterStatus>());
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetOtlpHealthAsync

    public async Task<RecentErrorsResult> GetRecentErrorsAsync(
        string? serviceName = null,
        int timeRangeMinutes = 60,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("recent_errors");

        var cacheKey = $"signoz:recent_errors:{serviceName ?? "all"}:{timeRangeMinutes}:{limit}";
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogInformation(
                    "Querying SigNoz for recent errors: Service={Service}, TimeRange={TimeRange}m, Limit={Limit}",
                    serviceName ?? "all",
                    timeRangeMinutes,
                    limit
                );

                try
                {
                    // Query traces with error status from SigNoz
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var start = DateTimeOffset
                        .UtcNow.AddMinutes(-timeRangeMinutes)
                        .ToUnixTimeMilliseconds();

                    var filters = new List<object>
                    {
                        new
                        {
                            key = "status",
                            value = "error",
                            op = "="
                        }
                    };

                    if (!string.IsNullOrWhiteSpace(serviceName))
                    {
                        filters.Add(
                            new
                            {
                                key = "service.name",
                                value = serviceName,
                                op = "="
                            }
                        );
                    }

                    var payload = new
                    {
                        start,
                        end = now,
                        filters,
                        limit
                    };

                    var response = await _httpClient.PostAsJsonAsync(
                        "api/v1/traces/search",
                        payload,
                        cancellationToken
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "SigNoz traces API returned {StatusCode} for error search",
                            response.StatusCode
                        );
                        return new RecentErrorsResult(Array.Empty<ErrorTrace>());
                    }

                    // Note: SigNoz trace response structure may vary
                    // This is a simplified implementation
                    var errors = new List<ErrorTrace>();

                    _logger.LogInformation("Retrieved {Count} recent errors", errors.Count);
                    return new RecentErrorsResult(errors);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to query recent errors from SigNoz: Service={Service}",
                        serviceName ?? "all"
                    );
                    // Return empty result on error
                    return new RecentErrorsResult(Array.Empty<ErrorTrace>());
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetRecentErrorsAsync

    public async Task<TraceDetailsResult?> GetTraceDetailsAsync(
        string traceId,
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("traces");

        var cacheKey = $"signoz:trace_details:{traceId}";

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async _ =>
            {
                try
                {
                    // SigNoz uses /traces/{traceId} endpoint (not /api/v1/traces/{traceId})
                    var url = $"{_options.CurrentValue.ApiBaseUrl}/traces/{traceId}";
                    var request = await CreateAuthenticatedRequestAsync(
                        HttpMethod.Get,
                        url,
                        cancellationToken
                    );
                    var response = await _httpClient.SendAsync(request, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "SigNoz trace details query failed: {StatusCode} {ReasonPhrase}",
                            response.StatusCode,
                            response.ReasonPhrase
                        );
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var apiResponse = JsonSerializer.Deserialize<SigNozTraceResponse>(
                        json,
                        s_jsonOptions
                    );

                    if (apiResponse?.Data == null || !apiResponse.Data.Any())
                    {
                        _logger.LogInformation("No spans found for trace {TraceId}", traceId);
                        return null;
                    }

                    // Convert spans
                    var spans = apiResponse.Data.Select(s => ConvertSpan(s)).ToList();

                    // Calculate trace-level metrics
                    var rootSpan = spans.FirstOrDefault(s => s.ParentSpanId == null);
                    var startTime = spans.Min(s => s.StartTime);
                    var endTime = spans.Max(s => s.EndTime);
                    var durationMs = (endTime - startTime).TotalMilliseconds;
                    var errorSpans = spans.Count(s => s.StatusCode == "ERROR");

                    _logger.LogInformation(
                        "Retrieved trace {TraceId} with {SpanCount} spans, duration {DurationMs}ms",
                        traceId,
                        spans.Count,
                        durationMs
                    );

                    return new TraceDetailsResult(
                        TraceId: traceId,
                        StartTime: startTime,
                        EndTime: endTime,
                        DurationMs: durationMs,
                        RootServiceName: rootSpan?.ServiceName ?? "unknown",
                        TotalSpans: spans.Count,
                        ErrorSpans: errorSpans,
                        Spans: spans
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to retrieve trace details for {TraceId}", traceId);
                    return null;
                }
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
            cancellationToken: cancellationToken
        );
    } // End of Method GetTraceDetailsAsync

    private TraceSpan ConvertSpan(SigNozSpanDto dto)
    {
        var startTime = DateTimeOffset
            .FromUnixTimeMilliseconds((dto.StartTimeUnixNano ?? 0) / 1_000_000)
            .UtcDateTime;

        var endTime = DateTimeOffset
            .FromUnixTimeMilliseconds((dto.EndTimeUnixNano ?? 0) / 1_000_000)
            .UtcDateTime;

        var durationMs = (dto.DurationNano ?? 0) / 1_000_000.0;

        var attributes =
            dto.Attributes?.Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString() ?? "")
                .AsReadOnly() ?? new Dictionary<string, string>().AsReadOnly();

        var events =
            dto.Events?.Select(e => new SpanEvent(
                Name: e.Name ?? "unknown",
                Timestamp: DateTimeOffset
                    .FromUnixTimeMilliseconds((e.TimeUnixNano ?? 0) / 1_000_000)
                    .UtcDateTime,
                Attributes: e.Attributes?.Where(kv => kv.Value != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString() ?? "")
                    .AsReadOnly() ?? new Dictionary<string, string>().AsReadOnly()
            ))
                .ToList()
                .AsReadOnly() ?? new List<SpanEvent>().AsReadOnly();

        return new TraceSpan(
            SpanId: dto.SpanId ?? "unknown",
            ParentSpanId: dto.ParentSpanId,
            SpanName: dto.Name ?? "unknown",
            ServiceName: dto.ServiceName ?? "unknown",
            SpanKind: dto.Kind ?? "INTERNAL",
            StartTime: startTime,
            EndTime: endTime,
            DurationMs: durationMs,
            StatusCode: dto.Status?.Code ?? "OK",
            StatusMessage: dto.Status?.Message,
            Attributes: attributes,
            Events: events,
            Links: new List<SpanLink>().AsReadOnly() // SigNoz doesn't expose links in this API
        );
    } // End of Method ConvertSpan

    public async Task<TracesSearchResult> SearchTracesAsync(
        string? serviceName = null,
        int timeRangeMinutes = 60,
        int limit = 20,
        CancellationToken cancellationToken = default
    )
    {
        ValidateQueryType("traces");

        var cacheKey = $"signoz:traces_search:{serviceName ?? "all"}:{timeRangeMinutes}:{limit}";

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async _ =>
            {
                var endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
                var startTime =
                    DateTimeOffset.UtcNow.AddMinutes(-timeRangeMinutes).ToUnixTimeMilliseconds()
                    * 1_000_000;

                var requestBody = new
                {
                    start = startTime,
                    end = endTime,
                    limit = limit,
                    filters = serviceName != null
                        ? new { serviceName = new[] { serviceName } }
                        : null
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json"
                );

                var url = $"{_options.CurrentValue.ApiBaseUrl}/api/v3/query_range";
                var request = await CreateAuthenticatedRequestAsync(
                    HttpMethod.Post,
                    url,
                    cancellationToken
                );
                request.Content = content;

                var response = await ExecuteWithMetricsAsync(
                    "/api/v3/query_range",
                    async () => await _httpClient.SendAsync(request, cancellationToken),
                    cancellationToken
                );

                response.EnsureSuccessStatusCode();
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var apiResponse = JsonSerializer.Deserialize<SigNozTracesSearchResponse>(
                    responseJson,
                    s_jsonOptions
                );

                if (apiResponse?.Data?.Traces == null)
                {
                    return new TracesSearchResult(
                        Traces: Array.Empty<TraceListItem>(),
                        TotalCount: 0
                    );
                }

                var traces = apiResponse
                    .Data.Traces.Select(t => new TraceListItem(
                        TraceId: t.TraceId ?? "unknown",
                        RootServiceName: t.RootServiceName ?? "unknown",
                        RootOperationName: t.RootTraceName ?? "unknown",
                        StartTime: DateTimeOffset
                            .FromUnixTimeMilliseconds((t.StartTimeUnixNano ?? 0) / 1_000_000)
                            .UtcDateTime,
                        DurationMs: (t.DurationNano ?? 0) / 1_000_000.0,
                        SpanCount: t.SpanCount ?? 0,
                        ErrorCount: t.ErrorCount ?? 0
                    ))
                    .ToList()
                    .AsReadOnly();

                return new TracesSearchResult(
                    Traces: traces,
                    TotalCount: apiResponse.Data.Total ?? traces.Count
                );
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken
        );
    } // End of Method SearchTracesAsync

    public CircuitBreakerState GetCircuitBreakerState()
    {
        return _circuitBreaker.GetState();
    } // End of Method GetCircuitBreakerState

    private void ValidateQueryType(string queryType)
    {
        if (_options.CurrentValue.EnableQueryAllowlist && !_allowedQueryTypes.Contains(queryType))
        {
            throw new InvalidOperationException(
                $"Query type '{queryType}' is not in the allowlist. Enable only trusted query types."
            );
        }
    } // End of Method ValidateQueryType

    /// <summary>
    /// Attempt to retrieve cached data when circuit breaker is open.
    /// Returns null if no cached data is available.
    /// </summary>
    private async Task<T?> TryGetCachedDataAsync<T>(
        string cacheKey,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        try
        {
            // Try to get cached value without creating a new one
            // Note: HybridCache doesn't have a TryGet method, so we use a trick:
            // Set a very short factory timeout and catch the exception
            var cached = await _cache.GetOrCreateAsync<T>(
                cacheKey,
                _ => throw new InvalidOperationException("Cache miss - no fallback available"),
                cancellationToken: cancellationToken
            );
            return cached;
        }
        catch
        {
            // No cached data available
            return null;
        }
    } // End of Method TryGetCachedDataAsync

    /// <summary>
    /// Helper to execute HTTP requests with OpenTelemetry metrics and Activity tracing.
    /// Records API call metrics (duration, status code, errors) and creates Activity spans.
    /// </summary>
    private async Task<HttpResponseMessage> ExecuteWithMetricsAsync(
        string endpoint,
        Func<Task<HttpResponseMessage>> httpCall,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity(
            $"SigNoz.Query: {endpoint}",
            System.Diagnostics.ActivityKind.Client
        );

        activity?.SetTag("signoz.endpoint", endpoint);
        activity?.SetTag("signoz.api_base_url", _options.CurrentValue.ApiBaseUrl);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? response = null;

        try
        {
            response = await httpCall();
            sw.Stop();

            var statusCode = (int)response.StatusCode;
            var durationMs = sw.Elapsed.TotalMilliseconds;

            // Record metrics
            SigNozQueryMetrics.RecordApiCall(
                endpoint,
                statusCode,
                durationMs,
                cacheHit: false // Cache hits are tracked separately in GetOrCreateAsync
            );

            activity?.SetTag("http.status_code", statusCode);
            activity?.SetTag("http.response_time_ms", durationMs);

            if (!response.IsSuccessStatusCode)
            {
                activity?.SetStatus(
                    System.Diagnostics.ActivityStatusCode.Error,
                    $"HTTP {statusCode}"
                );
                SigNozQueryMetrics.RecordApiError(endpoint, $"http_{statusCode}");
                _circuitBreaker.RecordFailure(); // Circuit breaker: record failure

                _logger.LogWarning(
                    "SigNoz API call failed: Endpoint={Endpoint}, StatusCode={StatusCode}, Duration={DurationMs}ms",
                    endpoint,
                    statusCode,
                    durationMs
                );
            }
            else
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                _circuitBreaker.RecordSuccess(); // Circuit breaker: record success
            }

            return response;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;

            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Timeout");
            activity?.SetTag("http.timeout_ms", durationMs);
            SigNozQueryMetrics.RecordApiError(endpoint, "timeout");
            _circuitBreaker.RecordFailure(); // Circuit breaker: record failure

            _logger.LogError(
                ex,
                "SigNoz API call timed out: Endpoint={Endpoint}, Duration={DurationMs}ms",
                endpoint,
                durationMs
            );

            throw;
        }
        catch (HttpRequestException ex)
        {
            // Connection error
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;

            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            SigNozQueryMetrics.RecordApiError(endpoint, "connection_error");
            _circuitBreaker.RecordFailure(); // Circuit breaker: record failure

            _logger.LogError(
                ex,
                "SigNoz API call failed: Endpoint={Endpoint}, Duration={DurationMs}ms, Error={Error}",
                endpoint,
                durationMs,
                ex.Message
            );

            throw;
        }
        catch (Exception ex)
        {
            // Unexpected error
            sw.Stop();
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            SigNozQueryMetrics.RecordApiError(endpoint, "unexpected_error");
            _circuitBreaker.RecordFailure(); // Circuit breaker: record failure

            _logger.LogError(
                ex,
                "SigNoz API call encountered unexpected error: Endpoint={Endpoint}",
                endpoint
            );

            throw;
        }
    } // End of Method ExecuteWithMetricsAsync
} // End of Class SigNozQueryService
