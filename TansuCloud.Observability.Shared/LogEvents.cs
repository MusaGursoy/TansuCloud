// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TansuCloud.Observability;

/// <summary>
/// Central EventId taxonomy (Task 38). Do not repurpose existing numeric values; allocate new IDs within a valid range.
/// Temporary diagnostics MUST use the 9000–9099 block.
/// </summary>
public static class LogEvents
{
    // Gateway core 1000–1049
    public static readonly EventId GatewayRouteMatched = new(1000, nameof(GatewayRouteMatched));
    public static readonly EventId GatewayTransformApplied = new(1001, nameof(GatewayTransformApplied));

    // Gateway rate limiting 1050–1074
    public static readonly EventId RateLimitPartitionResolved = new(1050, nameof(RateLimitPartitionResolved));
    public static readonly EventId RateLimitRejectedSummary = new(1051, nameof(RateLimitRejectedSummary));
    public static readonly EventId RateLimitRejectedDebug = new(1052, nameof(RateLimitRejectedDebug));

    // Gateway output/static cache 1075–1099
    public static readonly EventId OutputCacheVaryComputed = new(1075, nameof(OutputCacheVaryComputed));

    // Identity / OIDC 1100–1149
    public static readonly EventId OidcMetadataSourceChosen = new(1100, nameof(OidcMetadataSourceChosen));
    public static readonly EventId OidcIssuerValidation = new(1101, nameof(OidcIssuerValidation));

    // AuthZ placeholder 1150–1199

    // Database tenant normalization/provisioning 1200–1249
    public static readonly EventId TenantNormalized = new(1200, nameof(TenantNormalized));
    public static readonly EventId TenantDbPrewarm = new(1201, nameof(TenantDbPrewarm));

    // Outbox lifecycle 1250–1299
    public static readonly EventId OutboxEnqueued = new(1250, nameof(OutboxEnqueued));
    public static readonly EventId OutboxDispatchAttempt = new(1251, nameof(OutboxDispatchAttempt));
    public static readonly EventId OutboxBatchCompleted = new(1252, nameof(OutboxBatchCompleted));
    public static readonly EventId OutboxRetryExhausted = new(1253, nameof(OutboxRetryExhausted));

    // Storage transforms & cache 1300–1349
    public static readonly EventId StorageTransformApplied = new(1300, nameof(StorageTransformApplied));
    public static readonly EventId StorageCacheHit = new(1301, nameof(StorageCacheHit));
    public static readonly EventId StorageCacheMiss = new(1302, nameof(StorageCacheMiss));

    // Dashboard metrics proxy 1350–1399
    public static readonly EventId MetricsProxyQueryValidated = new(1350, nameof(MetricsProxyQueryValidated));
    public static readonly EventId MetricsProxyCacheHit = new(1351, nameof(MetricsProxyCacheHit));
    public static readonly EventId MetricsProxyCacheMiss = new(1352, nameof(MetricsProxyCacheMiss));

    // Performance insights reserved 1400–1499

    // Synthesized perf anomaly (future) 1500–1599

    // Audit mirrors 3000–3099 (optional) – none defined yet

    // Telemetry reporter internal 4000–4099
    public static readonly EventId TelemetryBatchSend = new(4000, nameof(TelemetryBatchSend));
    public static readonly EventId TelemetryBatchFailed = new(4001, nameof(TelemetryBatchFailed));

    // ML / recommendations 5000–5499 (future)

    // Startup / migration diagnostics 8000–8049
    public static readonly EventId MigrationApplied = new(8000, nameof(MigrationApplied));
    public static readonly EventId ExtensionFeatureDetected = new(8001, nameof(ExtensionFeatureDetected));
    public static readonly EventId HealthStatusTransition = new(8002, nameof(HealthStatusTransition));

    // Temporary deep diagnostics 9000–9099
    public static readonly EventId DiagnosticTemporary = new(9000, nameof(DiagnosticTemporary));
} // End of Class LogEvents

/// <summary>
/// Publishes Info logs when overall health transitions between Healthy/Degraded/Unhealthy.
/// Intended for ops visibility in Phase 0. Uses memory to track last status.
/// </summary>
public sealed class HealthTransitionPublisher : IHealthCheckPublisher
{
    private HealthStatus _last = HealthStatus.Healthy;
    private readonly ILogger<HealthTransitionPublisher> _logger;

    public HealthTransitionPublisher(ILogger<HealthTransitionPublisher> logger)
    {
        _logger = logger;
    } // End of Constructor HealthTransitionPublisher

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        try
        {
            var current = report.Status;
            if (current != _last)
            {
                _logger.LogInformation(
                    LogEvents.HealthStatusTransition,
                    "Health transition {From} -> {To} at {WhenUtc}",
                    _last,
                    current,
                    DateTimeOffset.UtcNow
                );
                _last = current;
            }
        }
        catch
        {
            // best-effort only
        }
        return Task.CompletedTask;
    } // End of Method PublishAsync
} // End of Class HealthTransitionPublisher

/// <summary>
/// Request enrichment middleware: attaches CorrelationId, Tenant, RouteBase, TraceId, SpanId to logging scope.
/// </summary>
public sealed class RequestEnrichmentMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestEnrichmentMiddleware> _logger;

    public RequestEnrichmentMiddleware(RequestDelegate next, ILogger<RequestEnrichmentMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    } // End of Constructor RequestEnrichmentMiddleware

    public async Task InvokeAsync(HttpContext context)
    {
        var activity = System.Diagnostics.Activity.Current;
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? activity?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("n");
        var tenant = context.Request.Headers["X-Tansu-Tenant"].FirstOrDefault() ?? string.Empty;
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : string.Empty;
        var routeBase = path.Trim('/').Split('/', 2)[0];

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["Tenant"] = tenant,
            ["RouteBase"] = routeBase,
            ["TraceId"] = activity?.TraceId.ToString(),
            ["SpanId"] = activity?.SpanId.ToString()
        }))
        {
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            await _next(context);
        }
    } // End of Method InvokeAsync
} // End of Class RequestEnrichmentMiddleware

/// <summary>
/// Allows temporary runtime log level overrides with TTL.
/// </summary>
public interface IDynamicLogLevelOverride
{
    void Set(string category, LogLevel level, TimeSpan ttl);
    LogLevel? Get(string category);
    IReadOnlyDictionary<string, (LogLevel Level, DateTimeOffset Expires)> Snapshot();
}

internal sealed class DynamicLogLevelOverride : IDynamicLogLevelOverride, IDisposable
{
    private readonly ConcurrentDictionary<string, (LogLevel Level, DateTimeOffset Expires)> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly TimeSpan _sweep = TimeSpan.FromMinutes(1);

    public DynamicLogLevelOverride()
    {
        _timer = new Timer(Sweep, null, _sweep, _sweep);
    } // End of Constructor DynamicLogLevelOverride

    public void Set(string category, LogLevel level, TimeSpan ttl)
    {
        var expires = DateTimeOffset.UtcNow.Add(ttl);
        _overrides[category] = (level, expires);
    } // End of Method Set

    public LogLevel? Get(string category)
    {
        if (_overrides.TryGetValue(category, out var entry))
        {
            if (entry.Expires > DateTimeOffset.UtcNow)
                return entry.Level;
            _overrides.TryRemove(category, out var _discard1);
        }
        return null;
    } // End of Method Get

    public IReadOnlyDictionary<string, (LogLevel Level, DateTimeOffset Expires)> Snapshot() => _overrides;

    private void Sweep(object? _)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _overrides)
        {
            if (kvp.Value.Expires <= now)
            {
                _overrides.TryRemove(kvp.Key, out var _discard2);
            }
        }
    } // End of Method Sweep

    public void Dispose() => _timer.Dispose();
} // End of Class DynamicLogLevelOverride

/// <summary>
/// Logger extensions providing consistent structured templates.
/// </summary>
public static class LoggingExtensions
{
    public static void LogTenantNormalized(this ILogger logger, string rawTenant, string normalized, string dbName)
        => logger.LogDebug(LogEvents.TenantNormalized, "Tenant '{RawTenant}' normalized='{Normalized}' db='{Db}'", rawTenant, normalized, dbName); // End of Method LogTenantNormalized

    public static void LogRateLimitPartition(this ILogger logger, string routeBase, string tenant, string partitionKey, int permitLimit, int queueLimit, int windowSeconds)
        => logger.LogDebug(LogEvents.RateLimitPartitionResolved, "RateLimit partition route='{Route}' tenant='{Tenant}' key='{Key}' permits={PermitLimit} queue={QueueLimit} window={Window}", routeBase, tenant, partitionKey, permitLimit, queueLimit, windowSeconds); // End of Method LogRateLimitPartition

    public static void LogOutboxEnqueued(this ILogger logger, Guid id, string type)
        => logger.LogDebug(LogEvents.OutboxEnqueued, "Outbox enqueued id={Id} type={Type}", id, type); // End of Method LogOutboxEnqueued

    public static void LogOutboxDispatchAttempt(this ILogger logger, Guid id, int attempt)
        => logger.LogDebug(LogEvents.OutboxDispatchAttempt, "Outbox dispatch attempt id={Id} attempt={Attempt}", id, attempt); // End of Method LogOutboxDispatchAttempt

    public static void LogCacheHit(this ILogger logger, string key)
        => logger.LogDebug(LogEvents.StorageCacheHit, "Cache HIT key='{Key}'", key); // End of Method LogCacheHit

    public static void LogCacheMiss(this ILogger logger, string key)
        => logger.LogDebug(LogEvents.StorageCacheMiss, "Cache MISS key='{Key}'", key); // End of Method LogCacheMiss

    public static void LogOidcMetadataChoice(this ILogger logger, string source, string authority, string metadataAddress)
        => logger.LogDebug(LogEvents.OidcMetadataSourceChosen, "OIDC metadata source='{Source}' authority='{Authority}' metadata='{Metadata}'", source, authority, metadataAddress); // End of Method LogOidcMetadataChoice
} // End of Class LoggingExtensions

/// <summary>
/// DI helpers for observability shared components.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddTansuObservabilityCore(this IServiceCollection services)
    {
        services.AddSingleton<IDynamicLogLevelOverride, DynamicLogLevelOverride>();
        return services;
    } // End of Method AddTansuObservabilityCore
} // End of Class ObservabilityServiceCollectionExtensions
