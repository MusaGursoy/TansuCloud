// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability;

public interface IPrometheusQueryService
{
    Task<PromRangeResult?> QueryRangeAsync(
        string chartId,
        string? tenant,
        string? service,
        TimeSpan? range = null,
        TimeSpan? step = null,
        CancellationToken ct = default
    );

    Task<PromInstantResult?> QueryInstantAsync(
        string chartId,
        string? tenant,
        string? service,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    );
}

public sealed class PrometheusQueryService(
    IHttpClientFactory httpClientFactory,
    IOptions<PrometheusOptions> options,
    ILogger<PrometheusQueryService> logger,
    IHttpContextAccessor httpContextAccessor
) : IPrometheusQueryService
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly PrometheusOptions _options = options.Value;
    private readonly ILogger<PrometheusQueryService> _logger = logger;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    // simple in-memory cache to avoid hammering Prometheus
    private static readonly Dictionary<string, (DateTimeOffset At, PromRangeResult? Data)> _cache =
        new();

    public async Task<PromRangeResult?> QueryRangeAsync(
        string chartId,
        string? tenant,
        string? service,
        TimeSpan? range = null,
        TimeSpan? step = null,
        CancellationToken ct = default
    )
    {
        // Validate options
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning("Prometheus BaseUrl not configured; returning null");
            return null;
        }
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

        // Normalize inputs and clamp
        var window = range ?? TimeSpan.FromMinutes(_options.DefaultRangeMinutes);
        if (window > TimeSpan.FromMinutes(_options.MaxRangeMinutes))
            window = TimeSpan.FromMinutes(_options.MaxRangeMinutes);
        var stepDur = step ?? TimeSpan.FromSeconds(_options.MaxStepSeconds);
        if (stepDur > TimeSpan.FromSeconds(_options.MaxStepSeconds))
            stepDur = TimeSpan.FromSeconds(_options.MaxStepSeconds);

        var end = DateTimeOffset.UtcNow;
        var start = end - window;

        // Determine effective tenant with server-side enforcement:
        // - Prefer gateway-provided header X-Tansu-Tenant when present (default multi-tenant scoping)
        // - Allow explicit override via 'tenant' parameter only for admin users
        var httpContext = _httpContextAccessor.HttpContext;
        string? headerTenant = null;
        try
        {
            headerTenant = httpContext?.Request.Headers["X-Tansu-Tenant"].ToString();
            if (string.IsNullOrWhiteSpace(headerTenant))
                headerTenant = null;
        }
        catch
        { /* ignore */
        }

        var isAdmin = IsAdmin(httpContext?.User);
        var effectiveTenant = headerTenant ?? (isAdmin ? tenant : null);
        if (!string.IsNullOrWhiteSpace(tenant) && !isAdmin && tenant != headerTenant)
        {
            _logger.LogInformation(
                "Ignoring tenant override '{Tenant}' from non-admin; using header tenant '{HeaderTenant}'",
                tenant,
                headerTenant
            );
        }

        // Build query using allowlisted templates + enforced tenant scoping
        var query = chartId switch
        {
            // Cache hit ratio for storage by operation
            // hit_ratio = increase(hits[window]) / clamp_min(increase(attempts[window]), 1)
            "storage.cache.hitratio" => BuildCacheHitRatioQuery(effectiveTenant, service, window),
            // Storage HTTP RPS by op,status using responses counter
            "storage.http.rps" => BuildStorageRpsQuery(effectiveTenant, window),
            // Storage HTTP 5xx error rate by op
            "storage.http.errors" => BuildStorageErrorsQuery(effectiveTenant, window),
            // Storage HTTP latency p95 by op using histogram buckets
            "storage.http.latency.p95" => BuildStorageLatencyP95Query(effectiveTenant, window),
            // Storage HTTP latency p50 by op using histogram buckets
            "storage.http.latency.p50" => BuildStorageLatencyP50Query(effectiveTenant, window),
            // Overview: RPS by service (currently from Storage service request counter)
            "overview.rps.byservice" => BuildOverviewRpsByServiceQuery(effectiveTenant, window),
            // Overview: Error rate (4xx/5xx) by service (from Storage responses counter)
            "overview.errors.byservice"
                => BuildOverviewErrorsByServiceQuery(effectiveTenant, window),
            // Overview: Latency p95 by service (from Storage histogram)
            "overview.latency.p95.byservice"
                => BuildOverviewLatencyP95ByServiceQuery(effectiveTenant, window),
            // Overview: Latency p50 by service (from Storage histogram)
            "overview.latency.p50.byservice"
                => BuildOverviewLatencyP50ByServiceQuery(effectiveTenant, window),
            // Storage: ingress/egress bytes rate by tenant
            "storage.bytes.ingress.rate"
                => BuildStorageIngressRateByTenantQuery(effectiveTenant, window),
            "storage.bytes.egress.rate"
                => BuildStorageEgressRateByTenantQuery(effectiveTenant, window),
            // Storage: responses by status class distribution
            "storage.http.status" => BuildStorageStatusByStatusQuery(effectiveTenant, window),
            // Database Outbox: dispatched/retried/deadlettered rates (aggregated)
            "db.outbox.dispatched.rate"
                => BuildDbOutboxCounterRateQuery("outbox_dispatched_total", window),
            "db.outbox.retried.rate"
                => BuildDbOutboxCounterRateQuery("outbox_retried_total", window),
            "db.outbox.deadlettered.rate"
                => BuildDbOutboxCounterRateQuery("outbox_deadlettered_total", window),
            // Gateway proxy: RPS/status/latency (custom metrics exposed by gateway)
            "gateway.http.rps.byroute" => BuildGatewayRpsByRouteQuery(window),
            "gateway.http.status" => BuildGatewayStatusByStatusQuery(window),
            "gateway.http.latency.p95.byroute" => BuildGatewayLatencyP95ByRouteQuery(window),
            // Database HTTP metrics (via ASP.NET Core instrumentation)
            "db.http.rps" => BuildDbHttpRpsQuery(window),
            "db.http.errors.5xx" => BuildDbHttpErrors5xxQuery(window),
            // Add more chart IDs and templates here as needed
            _ => throw new InvalidOperationException($"Unknown chart id '{chartId}'")
        };

        // cache key
        var key =
            $"{chartId}|{tenant}|{service}|{(int)window.TotalSeconds}|{(int)stepDur.TotalSeconds}";
        lock (_cache)
        {
            var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.CacheTtlSeconds));
            if (_cache.TryGetValue(key, out var entry) && (DateTimeOffset.UtcNow - entry.At) < ttl)
            {
                _logger.LogDebug("Prometheus proxy cache HIT for {Key}", key);
                return entry.Data;
            }
        }
        _logger.LogDebug("Prometheus proxy cache MISS for {Key}", key);

        var url =
            $"api/v1/query_range?query={Uri.EscapeDataString(query)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step={(int)stepDur.TotalSeconds}";
        var data = await SendWithRetryAsync<PromRangeResult>(baseUri, url, ct);
        lock (_cache)
        {
            _cache[key] = (DateTimeOffset.UtcNow, data);
        }
        return data;
    }

    public async Task<PromInstantResult?> QueryInstantAsync(
        string chartId,
        string? tenant,
        string? service,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogWarning("Prometheus BaseUrl not configured; returning null");
            return null;
        }
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

        var httpContext = _httpContextAccessor.HttpContext;
        string? headerTenant = null;
        try
        {
            headerTenant = httpContext?.Request.Headers["X-Tansu-Tenant"].ToString();
            if (string.IsNullOrWhiteSpace(headerTenant))
                headerTenant = null;
        }
        catch { }
        var isAdmin = IsAdmin(httpContext?.User);
        var effectiveTenant = headerTenant ?? (isAdmin ? tenant : null);
        if (!string.IsNullOrWhiteSpace(tenant) && !isAdmin && tenant != headerTenant)
        {
            _logger.LogInformation(
                "Ignoring tenant override '{Tenant}' from non-admin; using header tenant '{HeaderTenant}'",
                tenant,
                headerTenant
            );
        }

        var query = chartId switch
        {
            "storage.cache.hitratio"
                => BuildCacheHitRatioQuery(
                    effectiveTenant,
                    service,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.http.rps"
                => BuildStorageRpsQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.http.errors"
                => BuildStorageErrorsQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.http.latency.p95"
                => BuildStorageLatencyP95Query(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.http.latency.p50"
                => BuildStorageLatencyP50Query(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "overview.rps.byservice"
                => BuildOverviewRpsByServiceQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "overview.errors.byservice"
                => BuildOverviewErrorsByServiceQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "overview.latency.p95.byservice"
                => BuildOverviewLatencyP95ByServiceQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "overview.latency.p50.byservice"
                => BuildOverviewLatencyP50ByServiceQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.bytes.ingress.rate"
                => BuildStorageIngressRateByTenantQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.bytes.egress.rate"
                => BuildStorageEgressRateByTenantQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "storage.http.status"
                => BuildStorageStatusByStatusQuery(
                    effectiveTenant,
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "db.outbox.dispatched.rate"
                => BuildDbOutboxCounterRateQuery(
                    "outbox_dispatched_total",
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "db.outbox.retried.rate"
                => BuildDbOutboxCounterRateQuery(
                    "outbox_retried_total",
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "db.outbox.deadlettered.rate"
                => BuildDbOutboxCounterRateQuery(
                    "outbox_deadlettered_total",
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "gateway.http.rps.byroute"
                => BuildGatewayRpsByRouteQuery(TimeSpan.FromMinutes(_options.DefaultRangeMinutes)),
            "gateway.http.status"
                => BuildGatewayStatusByStatusQuery(
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "gateway.http.latency.p95.byroute"
                => BuildGatewayLatencyP95ByRouteQuery(
                    TimeSpan.FromMinutes(_options.DefaultRangeMinutes)
                ),
            "db.http.rps" => BuildDbHttpRpsQuery(TimeSpan.FromMinutes(_options.DefaultRangeMinutes)),
            "db.http.errors.5xx" => BuildDbHttpErrors5xxQuery(TimeSpan.FromMinutes(_options.DefaultRangeMinutes)),
            _ => throw new InvalidOperationException($"Unknown chart id '{chartId}'")
        };

        var ts = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var url = $"api/v1/query?query={Uri.EscapeDataString(query)}&time={ts}";
        var data = await SendWithRetryAsync<PromInstantResult>(baseUri, url, ct);
        return data;
    }

    private async Task<T?> SendWithRetryAsync<T>(Uri baseUri, string url, CancellationToken ct)
        where T : class
    {
        var http = _httpClientFactory.CreateClient("prometheus");
        http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        http.BaseAddress = baseUri;

        var attempt = 0;
        var maxAttempts = Math.Max(0, _options.RetryCount) + 1; // initial try + retries
        var delayBase = Math.Max(10, _options.RetryBaseDelayMs);
        while (true)
        {
            try
            {
                var res = await http.GetFromJsonAsync<PromResponse<T>>(url, cancellationToken: ct);
                if (res?.status != "success")
                {
                    _logger.LogWarning("Prometheus status {Status} for {Url}", res?.status, url);
                }
                return res?.data;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // treat as transient timeout
            }
            catch (HttpRequestException)
            {
                // treat as transient
            }
            catch (TaskCanceledException)
            {
                // treat as transient
            }
            attempt++;
            if (attempt >= maxAttempts)
            {
                _logger.LogWarning(
                    "Prometheus request failed after {Attempts} attempts: {Url}",
                    attempt,
                    url
                );
                return default;
            }
            var jitter = Random.Shared.Next(0, delayBase);
            var backoffMs = (int)(delayBase * Math.Pow(2, attempt - 1)) + jitter;
            try
            {
                await Task.Delay(backoffMs, ct);
            }
            catch { }
        }
    }

    private static string BuildCacheHitRatioQuery(string? tenant, string? service, TimeSpan window)
    {
        // Metric names from Task 15: tansu_storage_cache_attempts_total, tansu_storage_cache_hits_total
        // Labels we expect: service, operation, optionally tid (tenant id)
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(service))
            filters.Add($"service=\"{EscapeLabel(service)}\"");
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tid=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;

        var hits =
            $"sum by(service,operation)(increase(tansu_storage_cache_hits_total{selector}[{w}]))";
        var attempts =
            $"sum by(service,operation)(increase(tansu_storage_cache_attempts_total{selector}[{w}]))";
        var ratio = $"{hits} / clamp_min({attempts}, 1)";
        return ratio;
    }

    private static string BuildStorageRpsQuery(string? tenant, TimeSpan window)
    {
        // RPS derived from responses counter: rate over window, grouped by op,status
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var expr = $"sum by(op,status)(rate(tansu_storage_responses_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildStorageErrorsQuery(string? tenant, TimeSpan window)
    {
        // Error rate (5xx) grouped by op
        var w = ToPromWindow(window);
        var filters = new List<string> { "status=\"5xx\"" };
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = "{" + string.Join(",", filters) + "}";
        var expr = $"sum by(op)(rate(tansu_storage_responses_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildStorageLatencyP95Query(string? tenant, TimeSpan window)
    {
        // p95 derived from histogram buckets; group by op
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        // histogram_quantile expects bucket time series with 'le' label
        // NOTE: The OpenTelemetry Prometheus exporter appends the unit to the histogram name, resulting in
        // tansu_storage_request_duration_ms_milliseconds_bucket|_count|_sum. Use that canonicalized name here.
        var buckets =
            $"sum by(op, le)(rate(tansu_storage_request_duration_ms_milliseconds_bucket{selector}[{w}]))";
        var expr = $"histogram_quantile(0.95, {buckets})";
        return expr;
    }

    private static string BuildStorageLatencyP50Query(string? tenant, TimeSpan window)
    {
        // p50 (median) from histogram buckets; group by op
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var buckets =
            $"sum by(op, le)(rate(tansu_storage_request_duration_ms_milliseconds_bucket{selector}[{w}]))";
        var expr = $"histogram_quantile(0.50, {buckets})";
        return expr;
    }

    private static string BuildOverviewRpsByServiceQuery(string? tenant, TimeSpan window)
    {
        // Aggregate request counter rate by service; rely on Resource attribute propagating service label
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var expr = $"sum by(service)(rate(tansu_storage_requests_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildOverviewErrorsByServiceQuery(string? tenant, TimeSpan window)
    {
        // Error rate (4xx/5xx) by service from responses counter
        var w = ToPromWindow(window);
        var filters = new List<string> { "status=~\"4xx|5xx\"" };
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = "{" + string.Join(",", filters) + "}";
        var expr = $"sum by(service)(rate(tansu_storage_responses_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildOverviewLatencyP95ByServiceQuery(string? tenant, TimeSpan window)
    {
        // p95 latency by service from histogram buckets
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var buckets =
            $"sum by(service, le)(rate(tansu_storage_request_duration_ms_milliseconds_bucket{selector}[{w}]))";
        var expr = $"histogram_quantile(0.95, {buckets})";
        return expr;
    }

    private static string BuildOverviewLatencyP50ByServiceQuery(string? tenant, TimeSpan window)
    {
        // p50 latency by service from histogram buckets
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var buckets =
            $"sum by(service, le)(rate(tansu_storage_request_duration_ms_milliseconds_bucket{selector}[{w}]))";
        var expr = $"histogram_quantile(0.50, {buckets})";
        return expr;
    }

    private static string BuildStorageIngressRateByTenantQuery(string? tenant, TimeSpan window)
    {
        // Bytes per second ingress by tenant (uploads)
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var expr = $"sum by(tenant)(rate(tansu_storage_ingress_bytes_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildStorageEgressRateByTenantQuery(string? tenant, TimeSpan window)
    {
        // Bytes per second egress by tenant (downloads)
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var expr = $"sum by(tenant)(rate(tansu_storage_egress_bytes_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildStorageStatusByStatusQuery(string? tenant, TimeSpan window)
    {
        // Responses per second grouped by status class
        var w = ToPromWindow(window);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenant))
            filters.Add($"tenant=\"{EscapeLabel(tenant)}\"");
        var selector = filters.Count > 0 ? "{" + string.Join(",", filters) + "}" : string.Empty;
        var expr = $"sum by(status)(rate(tansu_storage_responses_total{selector}[{w}]))";
        return expr;
    }

    private static string BuildDbOutboxCounterRateQuery(string promMetricName, TimeSpan window)
    {
        // OpenTelemetry Prometheus exporter converts dot-names to underscores and appends _total for Counters.
        // Our Meter instruments are:
        //   TansuCloud.Database.Outbox/outbox.dispatched -> prom: outbox_dispatched_total
        //   TansuCloud.Database.Outbox/outbox.retried    -> prom: outbox_retried_total
        //   TansuCloud.Database.Outbox/outbox.deadlettered -> prom: outbox_deadlettered_total
        // We aggregate across all labels, but preserve 'type' if present in the future.
        var w = ToPromWindow(window);
        // Use sum(rate(metric[window])) without extra filters; group none → single series
        var expr = $"sum(rate({promMetricName}[{w}]))";
        return expr;
    }

    private static string BuildGatewayRpsByRouteQuery(TimeSpan window)
    {
        var w = ToPromWindow(window);
        // Prom metric name via OTEL exporter: tansu_gateway_proxy_requests_total
        var expr = $"sum by(route)(rate(tansu_gateway_proxy_requests_total[{w}]))";
        return expr;
    }

    private static string BuildGatewayStatusByStatusQuery(TimeSpan window)
    {
        var w = ToPromWindow(window);
        var expr = $"sum by(status)(rate(tansu_gateway_proxy_requests_total[{w}]))";
        return expr;
    }

    private static string BuildGatewayLatencyP95ByRouteQuery(TimeSpan window)
    {
        var w = ToPromWindow(window);
        // Prom histogram buckets name will have unit suffix 'milliseconds' added by the exporter
        var buckets =
            $"sum by(route, le)(rate(tansu_gateway_proxy_request_duration_ms_milliseconds_bucket[{w}]))";
        var expr = $"histogram_quantile(0.95, {buckets})";
        return expr;
    }

    private static string BuildDbHttpRpsQuery(TimeSpan window)
    {
        // OpenTelemetry ASP.NET Core instrumentation exposes request duration histogram with unit seconds.
        // Prometheus exporter surfaces it as http_server_request_duration_seconds_{bucket|sum|count} with labels
        // including route, method, and http_response_status_code. Use rate over _count to approximate RPS.
        // Aggregate across all labels → single series.
        var w = ToPromWindow(window);
        var expr = $"sum(rate(http_server_request_duration_seconds_count[{w}]))";
        return expr;
    }

    private static string BuildDbHttpErrors5xxQuery(TimeSpan window)
    {
        // 5xx error rate using OTel ASP.NET Core histogram _count with status code label.
        var w = ToPromWindow(window);
        var selector = "{http_response_status_code=~\"5..\"}";
        var expr = $"sum(rate(http_server_request_duration_seconds_count{selector}[{w}]))";
        return expr;
    }

    private static string ToPromWindow(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static bool IsAdmin(ClaimsPrincipal? user)
    {
        if (user == null)
            return false;
        try
        {
            // Check common admin hints: role = Admin or scope contains admin.full
            var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
            if (roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase)))
                return true;
            var scopes = user.FindAll("scope")
                .Select(c => c.Value)
                .Concat(user.FindAll("scp").Select(c => c.Value));
            if (
                scopes.Any(s =>
                    s.Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        )
                        .Contains("admin.full", StringComparer.OrdinalIgnoreCase)
                )
            )
                return true;
        }
        catch { }
        return false;
    }
}

// Basic models for Prometheus HTTP API
public sealed class PromResponse<T>
{
    public string? status { get; set; }
    public T? data { get; set; }
}

public sealed class PromRangeResult
{
    public string? resultType { get; set; }
    public List<PromSeries> result { get; set; } = new();
}

public sealed class PromSeries
{
    public Dictionary<string, string> metric { get; set; } = new();
    public List<object[]> values { get; set; } = new(); // [unix_ts, value_string]
}

public sealed class PromInstantResult
{
    public string? resultType { get; set; }
    public List<PromInstantSeries> result { get; set; } = new();
}

public sealed class PromInstantSeries
{
    public Dictionary<string, string> metric { get; set; } = new();
    public object[]? value { get; set; } // [unix_ts, value_string]
}
