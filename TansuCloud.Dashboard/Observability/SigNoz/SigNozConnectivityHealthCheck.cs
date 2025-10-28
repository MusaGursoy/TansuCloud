// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Health check that verifies SigNoz API connectivity by pinging the /api/v1/version endpoint.
/// This ensures the Dashboard can retrieve observability data before marking the service as ready.
///
/// Thresholds:
/// - Healthy: Response received in &lt; 5 seconds
/// - Degraded: Response received in 5-15 seconds (slow but functional)
/// - Unhealthy: No response within 15 seconds or connection failed
///
/// In Development environment, connection failures are downgraded to Degraded to reduce
/// local bring-up flakiness when SigNoz container may not be ready yet.
/// </summary>
public sealed class SigNozConnectivityHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<SigNozQueryOptions> _options;
    private readonly HttpClient _httpClient;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SigNozConnectivityHealthCheck> _logger;

    // Health thresholds
    private const int HealthyThresholdMs = 5000;
    private const int DegradedThresholdMs = 15000;

    public SigNozConnectivityHealthCheck(
        IOptionsMonitor<SigNozQueryOptions> options,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        ILogger<SigNozConnectivityHealthCheck> logger
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create dedicated HttpClient for health checks (separate from SigNozQueryService client)
        _httpClient =
            httpClientFactory?.CreateClient("SigNozHealthCheck")
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var opts = _options.CurrentValue;
        var apiBaseUrl = opts.ApiBaseUrl?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            var status = _environment.IsDevelopment()
                ? HealthStatus.Degraded
                : HealthStatus.Unhealthy;
            return new HealthCheckResult(
                status,
                description: "SigNoz API base URL not configured",
                data: new Dictionary<string, object> { ["configured"] = false }
            );
        }

        // Ping SigNoz /api/v1/version endpoint (lightweight, no auth required typically)
        var versionUrl = $"{apiBaseUrl}/api/v1/version";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DegradedThresholdMs); // Absolute timeout: 15 seconds

            using var request = new HttpRequestMessage(HttpMethod.Get, versionUrl);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            var data = new Dictionary<string, object>
            {
                ["signoz.api_base_url"] = apiBaseUrl,
                ["signoz.version_endpoint"] = versionUrl,
                ["signoz.response_time_ms"] = elapsedMs,
                ["signoz.status_code"] = (int)response.StatusCode,
                ["signoz.reachable"] = true
            };

            if (response.IsSuccessStatusCode)
            {
                // Determine health status based on response time
                if (elapsedMs < HealthyThresholdMs)
                {
                    return HealthCheckResult.Healthy($"SigNoz API reachable ({elapsedMs}ms)", data);
                }
                else
                {
                    _logger.LogWarning(
                        "SigNoz API responded but is slow: {ElapsedMs}ms (threshold: {HealthyThresholdMs}ms)",
                        elapsedMs,
                        HealthyThresholdMs
                    );
                    return HealthCheckResult.Degraded(
                        $"SigNoz API reachable but slow ({elapsedMs}ms, threshold {HealthyThresholdMs}ms)",
                        data: data
                    );
                }
            }
            else
            {
                // Non-2xx response: degraded in dev, unhealthy in production
                var status = _environment.IsDevelopment()
                    ? HealthStatus.Degraded
                    : HealthStatus.Unhealthy;

                _logger.LogWarning(
                    "SigNoz API returned non-success status: {StatusCode}",
                    response.StatusCode
                );

                return new HealthCheckResult(
                    status,
                    description: $"SigNoz API returned {response.StatusCode}",
                    data: data
                );
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout
            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            _logger.LogWarning(
                "SigNoz API health check timed out after {ElapsedMs}ms (threshold: {DegradedThresholdMs}ms)",
                elapsedMs,
                DegradedThresholdMs
            );

            var status = _environment.IsDevelopment()
                ? HealthStatus.Degraded
                : HealthStatus.Unhealthy;

            return new HealthCheckResult(
                status,
                description: $"SigNoz API timeout after {elapsedMs}ms (threshold {DegradedThresholdMs}ms)",
                data: new Dictionary<string, object>
                {
                    ["signoz.api_base_url"] = apiBaseUrl,
                    ["signoz.version_endpoint"] = versionUrl,
                    ["signoz.timeout_ms"] = elapsedMs,
                    ["signoz.reachable"] = false
                }
            );
        }
        catch (HttpRequestException ex)
        {
            // Connection failed
            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            _logger.LogError(ex, "SigNoz API health check failed: {Message}", ex.Message);

            var status = _environment.IsDevelopment()
                ? HealthStatus.Degraded
                : HealthStatus.Unhealthy;

            return new HealthCheckResult(
                status,
                description: $"SigNoz API connection failed: {ex.Message}",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["signoz.api_base_url"] = apiBaseUrl,
                    ["signoz.version_endpoint"] = versionUrl,
                    ["signoz.elapsed_ms"] = elapsedMs,
                    ["signoz.reachable"] = false,
                    ["signoz.error"] = ex.Message
                }
            );
        }
        catch (Exception ex)
        {
            // Unexpected error
            sw.Stop();

            _logger.LogError(
                ex,
                "SigNoz API health check encountered unexpected error: {Message}",
                ex.Message
            );

            return HealthCheckResult.Unhealthy(
                description: $"SigNoz API health check error: {ex.Message}",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["signoz.api_base_url"] = apiBaseUrl,
                    ["signoz.version_endpoint"] = versionUrl,
                    ["signoz.error"] = ex.Message
                }
            );
        }
    }
} // End of Class SigNozConnectivityHealthCheck
