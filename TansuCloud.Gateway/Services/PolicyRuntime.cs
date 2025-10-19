// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Enforcement mode for policies: Shadow (log only), AuditOnly (log + alert), Enforce (block/allow).
/// </summary>
public enum PolicyEnforcementMode
{
    /// <summary>Shadow mode: policy evaluated but not enforced; results logged for analysis.</summary>
    Shadow = 0,
    
    /// <summary>Audit-only mode: policy evaluated, violations logged and alerted, but not blocked.</summary>
    AuditOnly = 1,
    
    /// <summary>Enforce mode: policy actively enforced; violations blocked.</summary>
    Enforce = 2
} // End of Enum PolicyEnforcementMode

/// <summary>
/// Type of policy: CORS configuration or IP allow/deny rules.
/// </summary>
public enum PolicyType
{
    /// <summary>CORS (Cross-Origin Resource Sharing) policy.</summary>
    Cors = 0,
    
    /// <summary>IP allowlist policy (only listed IPs/CIDRs allowed).</summary>
    IpAllow = 1,
    
    /// <summary>IP denylist policy (listed IPs/CIDRs blocked).</summary>
    IpDeny = 2,
    
    /// <summary>Output cache policy (TTL and VaryBy rules).</summary>
    CachePolicy = 3,
    
    /// <summary>Rate limit policy (window, permits, partition strategy).</summary>
    RateLimitPolicy = 4
} // End of Enum PolicyType

/// <summary>
/// A policy entry with metadata and configuration.
/// </summary>
public record PolicyEntry
{
    /// <summary>Unique policy identifier.</summary>
    public required string Id { get; init; }
    
    /// <summary>Policy type (CORS, IP allow/deny).</summary>
    public required PolicyType Type { get; init; }
    
    /// <summary>Enforcement mode (shadow, audit, enforce).</summary>
    public required PolicyEnforcementMode Mode { get; init; }
    
    /// <summary>Human-readable description.</summary>
    public string Description { get; init; } = string.Empty;
    
    /// <summary>Policy-specific configuration as JSON (CorsConfig or IpConfig).</summary>
    public required JsonElement Config { get; init; }
    
    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>Last update timestamp (UTC).</summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>Policy enabled flag (for soft delete).</summary>
    public bool Enabled { get; init; } = true;
} // End of Record PolicyEntry

/// <summary>
/// CORS policy configuration.
/// </summary>
public record CorsConfig
{
    /// <summary>Allowed origins (e.g., "https://example.com", "*").</summary>
    public List<string> Origins { get; init; } = new();
    
    /// <summary>Allowed HTTP methods (e.g., "GET", "POST", "*").</summary>
    public List<string> Methods { get; init; } = new();
    
    /// <summary>Allowed headers (e.g., "Content-Type", "Authorization", "*").</summary>
    public List<string> Headers { get; init; } = new();
    
    /// <summary>Exposed headers (Access-Control-Expose-Headers).</summary>
    public List<string> ExposedHeaders { get; init; } = new();
    
    /// <summary>Allow credentials (cookies, authorization headers).</summary>
    public bool AllowCredentials { get; init; } = false;
    
    /// <summary>Max age for preflight cache (seconds).</summary>
    public int MaxAgeSeconds { get; init; } = 600;
} // End of Record CorsConfig

/// <summary>
/// IP policy configuration (allow or deny list).
/// </summary>
public record IpConfig
{
    /// <summary>List of IP addresses or CIDR ranges (e.g., "192.168.1.1", "10.0.0.0/24").</summary>
    public List<string> Cidrs { get; init; } = new();
    
    /// <summary>Optional description for this IP rule.</summary>
    public string Description { get; init; } = string.Empty;
} // End of Record IpConfig

/// <summary>
/// Cache policy configuration (TTL and VaryBy rules).
/// </summary>
public record CacheConfig
{
    /// <summary>Cache duration in seconds (default 60).</summary>
    public int TtlSeconds { get; init; } = 60;
    
    /// <summary>Vary by query string keys (null = all, empty = none, list = specific keys).</summary>
    public List<string>? VaryByQuery { get; init; } = null;
    
    /// <summary>Vary by request header names.</summary>
    public List<string> VaryByHeaders { get; init; } = new();
    
    /// <summary>Vary by route value keys (e.g., "id", "slug").</summary>
    public List<string> VaryByRouteValues { get; init; } = new();
    
    /// <summary>Vary by host (domain name).</summary>
    public bool VaryByHost { get; init; } = false;
    
    /// <summary>Use default VaryBy rules from YARP config if true.</summary>
    public bool UseDefaultVaryByRules { get; init; } = true;
} // End of Record CacheConfig

/// <summary>
/// Rate limit policy configuration (window, permits, partition strategy).
/// </summary>
public record RateLimitConfig
{
    /// <summary>Time window in seconds (default 60).</summary>
    public int WindowSeconds { get; init; } = 60;
    
    /// <summary>Permit limit per window (default 100).</summary>
    public int PermitLimit { get; init; } = 100;
    
    /// <summary>Queue limit for requests waiting for permits (default 0 = no queue).</summary>
    public int QueueLimit { get; init; } = 0;
    
    /// <summary>Partition strategy: "Global" (all requests share limit), "PerIp" (per client IP), "PerUser" (per authenticated user), "PerHost" (per Host header).</summary>
    public string PartitionStrategy { get; init; } = "Global";
    
    /// <summary>HTTP status code for rate limit violations (default 429).</summary>
    public int StatusCode { get; init; } = 429;
    
    /// <summary>Retry-After header value in seconds (default = WindowSeconds).</summary>
    public int? RetryAfterSeconds { get; init; } = null;
} // End of Record RateLimitConfig

/// <summary>
/// Runtime holder for policies with thread-safe updates.
/// Backed by PostgreSQL with in-memory caching for performance.
/// </summary>
public interface IPolicyRuntime
{
    /// <summary>Get all current policies.</summary>
    Task<IReadOnlyList<PolicyEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Get a specific policy by ID.</summary>
    Task<PolicyEntry?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>Get policies by type.</summary>
    Task<IReadOnlyList<PolicyEntry>> GetByTypeAsync(PolicyType type, CancellationToken cancellationToken = default);
    
    /// <summary>Add or update a policy.</summary>
    Task UpsertAsync(PolicyEntry policy, CancellationToken cancellationToken = default);
    
    /// <summary>Delete a policy by ID.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>Replace all policies atomically.</summary>
    Task ReplaceAllAsync(IEnumerable<PolicyEntry> policies, CancellationToken cancellationToken = default);
    
    /// <summary>Load policies from database into cache on startup.</summary>
    Task LoadFromStoreAsync(CancellationToken cancellationToken = default);
} // End of Interface IPolicyRuntime

/// <summary>
/// PostgreSQL-backed implementation with in-memory caching for performance.
/// Uses IServiceScopeFactory to resolve scoped IPolicyStore.
/// </summary>
public class PolicyRuntime : IPolicyRuntime
{
    private readonly ConcurrentDictionary<string, PolicyEntry> _cache = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PolicyRuntime> _logger;

    public PolicyRuntime(IServiceScopeFactory scopeFactory, ILogger<PolicyRuntime> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    } // End of Constructor PolicyRuntime

    public async Task LoadFromStoreAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Data.IPolicyStore>();
        
        var policies = await store.GetAllAsync(cancellationToken);
        _cache.Clear();
        foreach (var policy in policies)
        {
            _cache[policy.Id] = policy;
        }
        _logger.LogInformation("Policies loaded from database: {Count} policies", policies.Count);
    } // End of Method LoadFromStoreAsync

    public Task<IReadOnlyList<PolicyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Read from cache for performance
        IReadOnlyList<PolicyEntry> result = _cache.Values.ToList();
        return Task.FromResult(result);
    } // End of Method GetAllAsync

    public Task<PolicyEntry?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _cache.TryGetValue(id, out var policy);
        return Task.FromResult(policy);
    } // End of Method GetByIdAsync

    public Task<IReadOnlyList<PolicyEntry>> GetByTypeAsync(PolicyType type, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PolicyEntry> result = _cache.Values.Where(p => p.Type == type).ToList();
        return Task.FromResult(result);
    } // End of Method GetByTypeAsync

    public async Task UpsertAsync(PolicyEntry policy, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Data.IPolicyStore>();
        
        // Write to database first
        await store.UpsertAsync(policy, cancellationToken);
        
        // Update cache
        var updatedPolicy = policy with { UpdatedAt = DateTime.UtcNow };
        _cache.AddOrUpdate(
            policy.Id,
            updatedPolicy,
            (_, existing) =>
            {
                _logger.LogInformation(
                    "Policy cache updated: {PolicyId} (Type={Type}, Mode={Mode})",
                    policy.Id,
                    policy.Type,
                    policy.Mode
                );
                return updatedPolicy;
            }
        );
    } // End of Method UpsertAsync

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Data.IPolicyStore>();
        
        // Delete from database first
        var deleted = await store.DeleteAsync(id, cancellationToken);
        
        if (deleted)
        {
            // Remove from cache
            _cache.TryRemove(id, out var policy);
            if (policy != null)
            {
                _logger.LogInformation(
                    "Policy cache removed: {PolicyId} (Type={Type})",
                    id,
                    policy.Type
                );
            }
        }
        
        return deleted;
    } // End of Method DeleteAsync

    public async Task ReplaceAllAsync(IEnumerable<PolicyEntry> policies, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Data.IPolicyStore>();
        
        // Replace in database first
        await store.ReplaceAllAsync(policies, cancellationToken);
        
        // Replace cache
        _cache.Clear();
        foreach (var policy in policies)
        {
            _cache[policy.Id] = policy;
        }
        _logger.LogInformation("Policy cache replaced: {Count} policies loaded", _cache.Count);
    } // End of Method ReplaceAllAsync
} // End of Class PolicyRuntime
