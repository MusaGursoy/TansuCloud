// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using System.Text.Json;
using TansuCloud.Gateway.Services;

namespace TansuCloud.Gateway.OutputCache;

/// <summary>
/// Dynamic output cache policy that reads from PolicyRuntime.
/// Applies cache policies with type CachePolicy (3) to matching routes.
/// </summary>
public class DynamicCachePolicy : IOutputCachePolicy
{
    private readonly IPolicyRuntime _policyRuntime;
    private readonly ILogger<DynamicCachePolicy> _logger;

    public DynamicCachePolicy(IPolicyRuntime policyRuntime, ILogger<DynamicCachePolicy> logger)
    {
        _policyRuntime = policyRuntime;
        _logger = logger;
    } // End of Constructor DynamicCachePolicy

    async ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var request = context.HttpContext.Request;
        var path = request.Path.ToString();

        // Get all enabled cache policies
        var allPolicies = await _policyRuntime.GetAllAsync();
        var cachePolicies = allPolicies
            .Where(p => p.Type == PolicyType.CachePolicy && p.Enabled)
            .ToList();

        if (!cachePolicies.Any())
        {
            // No cache policies configured, allow default caching behavior
            context.EnableOutputCaching = true;
            context.AllowCacheLookup = true;
            context.AllowCacheStorage = true;
            context.AllowLocking = true;
            return;
        }

        // For simplicity, apply the first matching policy
        // In production, you might want route-based matching or priority ordering
        var policy = cachePolicies.First();
        var config = DeserializeCacheConfig(policy.Config);

        if (config is null)
        {
            _logger.LogWarning("Failed to deserialize cache config for policy {PolicyId}", policy.Id);
            context.EnableOutputCaching = false;
            return;
        }

        // Apply enforcement mode
        switch (policy.Mode)
        {
            case PolicyEnforcementMode.Shadow:
                // Shadow mode: log what would happen but don't actually cache
                _logger.LogInformation(
                    "Cache policy {PolicyId} evaluated (Shadow mode): Path={Path}, TTL={Ttl}s",
                    policy.Id,
                    path,
                    config.TtlSeconds
                );
                context.EnableOutputCaching = false;
                return;

            case PolicyEnforcementMode.AuditOnly:
                // Audit mode: cache but log everything
                _logger.LogInformation(
                    "Cache policy {PolicyId} applied (Audit mode): Path={Path}, TTL={Ttl}s",
                    policy.Id,
                    path,
                    config.TtlSeconds
                );
                break;

            case PolicyEnforcementMode.Enforce:
                // Enforce mode: normal caching with logging
                _logger.LogDebug(
                    "Cache policy {PolicyId} applied: Path={Path}, TTL={Ttl}s",
                    policy.Id,
                    path,
                    config.TtlSeconds
                );
                break;
        }

        // Enable caching
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;

        // Set cache duration
        context.ResponseExpirationTimeSpan = TimeSpan.FromSeconds(config.TtlSeconds);

        // Apply VaryBy rules
        var varyByRules = context.CacheVaryByRules;

        if (config.VaryByHost)
        {
            varyByRules.VaryByHost = true;
        }

        if (config.VaryByQuery is not null && config.VaryByQuery.Count > 0)
        {
            var queryKeysList = new List<string>();
            foreach (var queryKey in config.VaryByQuery)
            {
                queryKeysList.Add(queryKey);
            }
            varyByRules.QueryKeys = new StringValues(queryKeysList.ToArray());
        }

        if (config.VaryByHeaders?.Count > 0)
        {
            var headersList = new List<string>();
            foreach (var header in config.VaryByHeaders)
            {
                headersList.Add(header);
            }
            varyByRules.HeaderNames = new StringValues(headersList.ToArray());
        }

        if (config.VaryByRouteValues?.Count > 0)
        {
            var routeValuesList = new List<string>();
            foreach (var routeKey in config.VaryByRouteValues)
            {
                routeValuesList.Add(routeKey);
            }
            varyByRules.RouteValueNames = new StringValues(routeValuesList.ToArray());
        }
    } // End of Method CacheRequestAsync

    async ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        // Allow serving from cache for all cache hits
        context.AllowCacheStorage = true;
        await ValueTask.CompletedTask;
    } // End of Method ServeFromCacheAsync

    async ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        // Determine if this was a cache hit or miss
        var isCacheHit = context.HttpContext.Items.ContainsKey("OutputCacheHit");
        
        // Get the policy that was applied (stored during CacheRequestAsync)
        var allPolicies = await _policyRuntime.GetAllAsync();
        var cachePolicies = allPolicies
            .Where(p => p.Type == PolicyType.CachePolicy && p.Enabled)
            .ToList();

        if (cachePolicies.Any())
        {
            var policy = cachePolicies.First();
            
            // Emit metrics
            if (isCacheHit)
            {
                CacheMetrics.CacheHitsTotal.Add(1,
                    new("policy.id", policy.Id),
                    new("policy.type", "cache"),
                    new("policy.mode", policy.Mode.ToString()));
            }
            else
            {
                CacheMetrics.CacheMissesTotal.Add(1,
                    new("policy.id", policy.Id),
                    new("policy.type", "cache"),
                    new("policy.mode", policy.Mode.ToString()));
            }
        }
    } // End of Method ServeResponseAsync

    private CacheConfig? DeserializeCacheConfig(JsonElement config)
    {
        try
        {
            return JsonSerializer.Deserialize<CacheConfig>(config.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize cache config");
            return null;
        }
    } // End of Method DeserializeCacheConfig
} // End of Class DynamicCachePolicy

/// <summary>
/// OpenTelemetry metrics for cache policy enforcement.
/// </summary>
public static class CacheMetrics
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = 
        new("TansuCloud.Gateway.Cache", "1.0.0");

    public static readonly System.Diagnostics.Metrics.Counter<long> CacheHitsTotal = 
        Meter.CreateCounter<long>(
            name: "tansu_gateway_cache_hits_total",
            unit: "hits",
            description: "Total cache hits");

    public static readonly System.Diagnostics.Metrics.Counter<long> CacheMissesTotal = 
        Meter.CreateCounter<long>(
            name: "tansu_gateway_cache_misses_total",
            unit: "misses",
            description: "Total cache misses");

    public static readonly System.Diagnostics.Metrics.Counter<long> CacheEvictionsTotal = 
        Meter.CreateCounter<long>(
            name: "tansu_gateway_cache_evictions_total",
            unit: "evictions",
            description: "Total cache evictions");
} // End of Class CacheMetrics
