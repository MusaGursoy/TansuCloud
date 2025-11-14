// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Prometheus;

/// <summary>
/// Implementation of Prometheus query service using HTTP API.
/// Phase 1: Metrics queries (error rates, latency, health).
/// Phase 2: Integration with Tempo for traces.
/// Phase 3: Integration with Loki for logs.
/// </summary>
public sealed class PrometheusQueryService : IPrometheusQueryService
{
    private readonly HttpClient _httpClient;
    private readonly PrometheusQueryOptions _options;
    private readonly ILogger<PrometheusQueryService> _logger;
    private readonly CircuitBreakerState _circuitBreakerState;

    public PrometheusQueryService(
        HttpClient httpClient,
        IOptions<PrometheusQueryOptions> options,
        ILogger<PrometheusQueryService> logger
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize HttpClient base address and timeout
        _httpClient.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        // Simple circuit breaker state (will be enhanced with actual circuit breaker in future)
        _circuitBreakerState = new CircuitBreakerState(
            State: "Closed",
            FailureCount: 0,
            LastFailureTime: null,
            NextRetryTime: null
        );
    } // End of Constructor PrometheusQueryService

    /// <inheritdoc />
    public async Task<ServiceStatusResult> GetServiceStatusAsync(
        string? serviceName,
        int timeRangeMinutes,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "Getting service status for service: {ServiceName}, timeRange: {TimeRange}m",
            serviceName ?? "all",
            timeRangeMinutes
        );

        var endTime = DateTime.UtcNow;
        var startTime = endTime.AddMinutes(-timeRangeMinutes);

        // Build PromQL queries
        var serviceFilter = string.IsNullOrWhiteSpace(serviceName) ? "" : $"{{job=\"{serviceName}\"}}";

        // Query 1: Error rate (percentage of 5xx responses in last 5 minutes)
        var errorRateQuery = $"rate(aspnetcore_http_server_request_duration_seconds_count{{code=~\"5..\"{(string.IsNullOrWhiteSpace(serviceName) ? "" : $",job=\"{serviceName}\"")}}}[5m]) * 100";

        // Query 2: P95 latency in milliseconds
        var p95LatencyQuery = $"histogram_quantile(0.95, rate(aspnetcore_http_server_request_duration_seconds_bucket{serviceFilter}[5m])) * 1000";

        // Query 3: P99 latency in milliseconds
        var p99LatencyQuery = $"histogram_quantile(0.99, rate(aspnetcore_http_server_request_duration_seconds_bucket{serviceFilter}[5m])) * 1000";

        // Query 4: Total request count in time range
        var requestCountQuery = $"sum(increase(aspnetcore_http_server_request_duration_seconds_count{serviceFilter}[{timeRangeMinutes}m]))";

        try
        {
            // Execute queries in parallel
            var errorRateTask = ExecuteInstantQueryAsync<double>(errorRateQuery, cancellationToken);
            var p95LatencyTask = ExecuteInstantQueryAsync<double>(p95LatencyQuery, cancellationToken);
            var p99LatencyTask = ExecuteInstantQueryAsync<double>(p99LatencyQuery, cancellationToken);
            var requestCountTask = ExecuteInstantQueryAsync<long>(requestCountQuery, cancellationToken);

            await Task.WhenAll(errorRateTask, p95LatencyTask, p99LatencyTask, requestCountTask);

            var errorRate = await errorRateTask;
            var p95Latency = await p95LatencyTask;
            var p99Latency = await p99LatencyTask;
            var requestCount = await requestCountTask;

            return new ServiceStatusResult(
                ServiceName: serviceName ?? "all",
                ErrorRatePercent: errorRate,
                P95LatencyMs: p95Latency,
                P99LatencyMs: p99Latency,
                RequestCount: requestCount,
                StartTime: startTime,
                EndTime: endTime
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service status from Prometheus");
            // Return empty result on failure
            return new ServiceStatusResult(
                ServiceName: serviceName ?? "all",
                ErrorRatePercent: 0,
                P95LatencyMs: 0,
                P99LatencyMs: 0,
                RequestCount: 0,
                StartTime: startTime,
                EndTime: endTime
            );
        }
    } // End of Method GetServiceStatusAsync

    /// <inheritdoc />
    public async Task<ServiceTopologyResult> GetServiceTopologyAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[TOPOLOGY] Getting service topology from Prometheus at {_httpClient.BaseAddress}");
        _logger.LogInformation("Getting service topology from Prometheus metrics");

        try
        {
            // Get all active services from 'up' metric
            var services = await GetServiceListAsync(cancellationToken);
            Console.WriteLine($"[TOPOLOGY] Found {services.Services.Count} services");

            // Calculate metrics for each service in parallel
            var nodesTasks = services.Services.Select(async svc =>
            {
                try
                {
                    Console.WriteLine($"[TOPOLOGY] Calculating metrics for service: {svc.ServiceName}");
                    
                    // Infrastructure services (postgres, redis) use different metrics
                    if (svc.ServiceName == "postgres")
                    {
                        // PostgreSQL health: pg_up metric
                        var healthQuery = "pg_up";
                        var health = await ExecuteInstantQueryAsync<double>(healthQuery, cancellationToken);
                        Console.WriteLine($"[TOPOLOGY] Postgres health (pg_up): {health}");
                        
                        return new ServiceNode(
                            ServiceName: svc.ServiceName,
                            ServiceType: "database",
                            ErrorRate: health == 1 ? 0 : 1, // Down = 100% error rate
                            CallRate: 0 // No call rate for infrastructure
                        );
                    }
                    
                    if (svc.ServiceName == "redis")
                    {
                        // Redis health: redis_up metric
                        var healthQuery = "redis_up";
                        var health = await ExecuteInstantQueryAsync<double>(healthQuery, cancellationToken);
                        Console.WriteLine($"[TOPOLOGY] Redis health (redis_up): {health}");
                        
                        return new ServiceNode(
                            ServiceName: svc.ServiceName,
                            ServiceType: "cache",
                            ErrorRate: health == 1 ? 0 : 1, // Down = 100% error rate
                            CallRate: 0 // No call rate for infrastructure
                        );
                    }
                    
                    if (svc.ServiceName == "tempo")
                    {
                        // Tempo health: tempo_build_info metric (presence indicates healthy)
                        var healthQuery = "tempo_build_info";
                        var health = await ExecuteInstantQueryAsync<double>(healthQuery, cancellationToken);
                        Console.WriteLine($"[TOPOLOGY] Tempo health (tempo_build_info): {health}");
                        
                        return new ServiceNode(
                            ServiceName: svc.ServiceName,
                            ServiceType: "tracing",
                            ErrorRate: health >= 1 ? 0 : 1, // Present = healthy
                            CallRate: 0 // No call rate for infrastructure
                        );
                    }
                    
                    if (svc.ServiceName == "loki")
                    {
                        // Loki health: loki_build_info metric (presence indicates healthy)
                        var healthQuery = "loki_build_info";
                        var health = await ExecuteInstantQueryAsync<double>(healthQuery, cancellationToken);
                        Console.WriteLine($"[TOPOLOGY] Loki health (loki_build_info): {health}");
                        
                        return new ServiceNode(
                            ServiceName: svc.ServiceName,
                            ServiceType: "logging",
                            ErrorRate: health >= 1 ? 0 : 1, // Present = healthy
                            CallRate: 0 // No call rate for infrastructure
                        );
                    }
                    
                    // Application services use HTTP metrics
                    // Query error rate: percentage of 5xx responses in last 5 minutes
                    // Fixed: Use http_server_request_duration_seconds_count (not aspnetcore_) and http_response_status_code label
                    var errorRateQuery = $"sum(rate(http_server_request_duration_seconds_count{{job=\"{svc.ServiceName}\",http_response_status_code=~\"5..\"}}[5m])) / sum(rate(http_server_request_duration_seconds_count{{job=\"{svc.ServiceName}\"}}[5m]))";
                    Console.WriteLine($"[TOPOLOGY] Error rate query: {errorRateQuery}");
                    var errorRate = await ExecuteInstantQueryAsync<double>(errorRateQuery, cancellationToken);
                    Console.WriteLine($"[TOPOLOGY] Error rate for {svc.ServiceName}: {errorRate}");

                    // Query call rate: requests per second in last 5 minutes
                    var callRateQuery = $"sum(rate(http_server_request_duration_seconds_count{{job=\"{svc.ServiceName}\"}}[5m]))";
                    Console.WriteLine($"[TOPOLOGY] Call rate query: {callRateQuery}");
                    var callRate = await ExecuteInstantQueryAsync<double>(callRateQuery, cancellationToken);
                    Console.WriteLine($"[TOPOLOGY] Call rate for {svc.ServiceName}: {callRate}");

                    return new ServiceNode(
                        ServiceName: svc.ServiceName,
                        ServiceType: "service",
                        ErrorRate: double.IsNaN(errorRate) ? 0 : errorRate,
                        CallRate: double.IsNaN(callRate) ? 0 : callRate
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TOPOLOGY] ERROR for {svc.ServiceName}: {ex.Message}");
                    _logger.LogWarning(ex, "Failed to calculate metrics for service {ServiceName}", svc.ServiceName);
                    return new ServiceNode(
                        ServiceName: svc.ServiceName,
                        ServiceType: "service",
                        ErrorRate: 0,
                        CallRate: 0
                    );
                }
            }).ToList();

            var nodes = await Task.WhenAll(nodesTasks);

            return new ServiceTopologyResult(
                Nodes: nodes.ToList(),
                Edges: Array.Empty<ServiceEdge>() // No edges yet (Phase 2 - Tempo will provide traces)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service topology from Prometheus");
            return new ServiceTopologyResult(
                Nodes: Array.Empty<ServiceNode>(),
                Edges: Array.Empty<ServiceEdge>()
            );
        }
    } // End of Method GetServiceTopologyAsync

    /// <inheritdoc />
    public async Task<ServiceListResult> GetServiceListAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting service list from Prometheus");

        try
        {
            // Get unique job labels from Prometheus
            var url = "api/v1/label/job/values";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var apiResponse = JsonSerializer.Deserialize<PrometheusLabelValuesResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (apiResponse?.Status != "success" || apiResponse.Data == null)
            {
                _logger.LogWarning("Prometheus label values response was not successful");
                return new ServiceListResult(Array.Empty<ServiceInfo>());
            }

            // For each job, query the 'up' metric to get last seen timestamp
            var services = new List<ServiceInfo>();
            foreach (var job in apiResponse.Data)
            {
                // Filter for TansuCloud services, Prometheus, and infrastructure exporters
                // Infrastructure: postgres, redis, tempo, loki (Task 47 Phase 1 - infrastructure visibility)
                var isInfrastructure = job.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
                                      job.Equals("redis", StringComparison.OrdinalIgnoreCase) ||
                                      job.Equals("tempo", StringComparison.OrdinalIgnoreCase) ||
                                      job.Equals("loki", StringComparison.OrdinalIgnoreCase);
                
                var isTansuService = job.StartsWith("tansu.", StringComparison.OrdinalIgnoreCase) ||
                                    job.Equals("prometheus", StringComparison.OrdinalIgnoreCase);

                if (!isTansuService && !isInfrastructure)
                {
                    continue;
                }

                // Query up metric for this job (or pg_up/redis_up/tempo_build_info/loki_build_info for infrastructure)
                string upQuery;
                if (job.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                {
                    upQuery = "pg_up";
                }
                else if (job.Equals("redis", StringComparison.OrdinalIgnoreCase))
                {
                    upQuery = "redis_up";
                }
                else if (job.Equals("tempo", StringComparison.OrdinalIgnoreCase))
                {
                    upQuery = "tempo_build_info";
                }
                else if (job.Equals("loki", StringComparison.OrdinalIgnoreCase))
                {
                    upQuery = "loki_build_info";
                }
                else
                {
                    upQuery = $"up{{job=\"{job}\"}}";
                }
                
                var upResult = await ExecuteInstantQueryAsync<double>(upQuery, cancellationToken);

                services.Add(new ServiceInfo(
                    ServiceName: job,
                    LastSeen: upResult > 0 ? DateTime.UtcNow : null, // If up=1, service is active
                    Tags: isInfrastructure 
                        ? new[] { "infrastructure", "exporter" } 
                        : new[] { "prometheus", "metrics" }
                ));
            }

            return new ServiceListResult(services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service list from Prometheus");
            return new ServiceListResult(Array.Empty<ServiceInfo>());
        }
    } // End of Method GetServiceListAsync

    /// <inheritdoc />
    public Task<CorrelatedLogsResult> GetCorrelatedLogsAsync(
        string traceId,
        string? spanId,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "GetCorrelatedLogsAsync called (Phase 3 - Loki integration). TraceId: {TraceId}",
            traceId
        );

        // Phase 3: Will integrate with Loki for log correlation
        // For now, return empty result
        return Task.FromResult(new CorrelatedLogsResult(
            TraceId: traceId,
            SpanId: spanId,
            Logs: Array.Empty<LogEntry>()
        ));
    } // End of Method GetCorrelatedLogsAsync

    /// <inheritdoc />
    public async Task<OtlpHealthResult> GetOtlpHealthAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting OTLP exporter health from Prometheus");

        try
        {
            // Get service list first, then check 'up' metric for each tansu.* service
            var serviceList = await GetServiceListAsync(cancellationToken);
            var tansuServices = serviceList.Services
                .Where(s => s.ServiceName.StartsWith("tansu.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!tansuServices.Any())
            {
                _logger.LogWarning("No TansuCloud services found in service list");
                return new OtlpHealthResult(Array.Empty<OtlpExporterStatus>());
            }

            // Check each service's 'up' metric
            var exporters = new List<OtlpExporterStatus>();
            foreach (var service in tansuServices)
            {
                var query = $"up{{job=\"{service.ServiceName}\"}}";
                var upValue = await ExecuteInstantQueryAsync<double>(query, cancellationToken);

                exporters.Add(new OtlpExporterStatus(
                    ServiceName: service.ServiceName,
                    IsHealthy: upValue == 1,
                    LastExport: upValue == 1 ? DateTime.UtcNow : null,
                    ErrorMessage: upValue == 1 ? null : "Service down"
                ));
            }

            return new OtlpHealthResult(exporters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get OTLP health from Prometheus");
            return new OtlpHealthResult(Array.Empty<OtlpExporterStatus>());
        }
    } // End of Method GetOtlpHealthAsync

    /// <inheritdoc />
    public Task<RecentErrorsResult> GetRecentErrorsAsync(
        string? serviceName,
        int timeRangeMinutes,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "GetRecentErrorsAsync called (Phase 2 - Tempo integration). Service: {ServiceName}",
            serviceName ?? "all"
        );

        // Phase 2: Will integrate with Tempo for error traces
        // For now, return empty result
        return Task.FromResult(new RecentErrorsResult(Array.Empty<ErrorTrace>()));
    } // End of Method GetRecentErrorsAsync

    /// <inheritdoc />
    public Task<TraceDetailsResult?> GetTraceDetailsAsync(
        string traceId,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "GetTraceDetailsAsync called (Phase 2 - Tempo integration). TraceId: {TraceId}",
            traceId
        );

        // Phase 2: Will integrate with Tempo for trace details
        // For now, return null
        return Task.FromResult<TraceDetailsResult?>(null);
    } // End of Method GetTraceDetailsAsync

    /// <inheritdoc />
    public Task<TracesSearchResult> SearchTracesAsync(
        string? serviceName,
        int timeRangeMinutes,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "SearchTracesAsync called (Phase 2 - Tempo integration). Service: {ServiceName}",
            serviceName ?? "all"
        );

        // Phase 2: Will integrate with Tempo for trace search
        // For now, return empty result
        return Task.FromResult(new TracesSearchResult(
            Traces: Array.Empty<TraceListItem>(),
            TotalCount: 0
        ));
    } // End of Method SearchTracesAsync

    /// <inheritdoc />
    public CircuitBreakerState GetCircuitBreakerState()
    {
        // Simple implementation for now (will enhance with actual circuit breaker)
        return _circuitBreakerState;
    } // End of Method GetCircuitBreakerState

    // ============================================================================
    // Private Helper Methods
    // ============================================================================

    /// <summary>
    /// Execute a Prometheus instant query and return the first result value.
    /// </summary>
    private async Task<T> ExecuteInstantQueryAsync<T>(string query, CancellationToken cancellationToken)
        where T : struct
    {
        var url = $"api/v1/query?query={Uri.EscapeDataString(query)}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var apiResponse = JsonSerializer.Deserialize<PrometheusApiResponse<PrometheusQueryData>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        if (apiResponse?.Status != "success" || apiResponse.Data?.Result == null || apiResponse.Data.Result.Count == 0)
        {
            return default;
        }

        var firstResult = apiResponse.Data.Result[0];
        if (firstResult.Value == null || firstResult.Value.Count < 2)
        {
            return default;
        }

        // Value is [timestamp, value_string]
        var valueString = firstResult.Value[1]?.ToString() ?? "0";

        // Convert to requested type
        if (typeof(T) == typeof(double))
        {
            return (T)(object)(double.TryParse(valueString, out var d) ? d : 0.0);
        }
        else if (typeof(T) == typeof(long))
        {
            return (T)(object)(long.TryParse(valueString, out var l) ? l : 0L);
        }
        else if (typeof(T) == typeof(int))
        {
            return (T)(object)(int.TryParse(valueString, out var i) ? i : 0);
        }

        return default;
    } // End of Method ExecuteInstantQueryAsync

} // End of Class PrometheusQueryService
