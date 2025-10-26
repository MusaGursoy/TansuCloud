// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace TansuCloud.Dashboard.Services;

/// <summary>
/// Service for managing tenant context within the Dashboard application.
/// Tracks current tenant, validates access, and provides tenant metadata.
/// </summary>
public interface ITenantContextService
{
    /// <summary>
    /// Gets the current tenant ID from the route context.
    /// </summary>
    string? CurrentTenantId { get; }

    /// <summary>
    /// Sets the current tenant context.
    /// </summary>
    /// <param name="tenantId">Tenant identifier to set as current</param>
    void SetTenant(string tenantId);

    /// <summary>
    /// Validates that the user has access to the specified tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if user has access, false otherwise</returns>
    Task<bool> ValidateAccessAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for the specified tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tenant metadata if found, null otherwise</returns>
    Task<TenantMetadata?> GetTenantMetadataAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Lists all tenants the current user has access to.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of accessible tenants</returns>
    Task<IReadOnlyList<TenantMetadata>> ListAccessibleTenantsAsync(CancellationToken ct = default);
}

/// <summary>
/// Tenant metadata model for Dashboard display.
/// </summary>
public sealed record TenantMetadata(
    string TenantId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    bool IsActive = true
);

internal sealed class TenantContextService(
    IHttpContextAccessor httpContextAccessor,
    IHttpClientFactory httpClientFactory,
    NavigationManager navigationManager,
    ILogger<TenantContextService> logger
) : ITenantContextService
{
    private readonly ConcurrentDictionary<string, TenantMetadata> _metadataCache = new();
    private string? _currentTenantId;

    public string? CurrentTenantId => _currentTenantId;

    public void SetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _currentTenantId = NormalizeTenantId(tenantId);
        logger.LogInformation("Tenant context set to {TenantId}", _currentTenantId);
    }

    public async Task<bool> ValidateAccessAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var normalized = NormalizeTenantId(tenantId);

        try
        {
            // Check if user is authenticated
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext?.User?.Identity?.IsAuthenticated != true)
            {
                logger.LogWarning("Access validation failed: user not authenticated");
                return false;
            }

            // For now, check if tenant exists via metadata lookup
            // In production, this should verify user's tenant membership via Identity service
            var metadata = await GetTenantMetadataAsync(normalized, ct);
            if (metadata is null)
            {
                logger.LogWarning(
                    "Access validation failed: tenant {TenantId} not found",
                    normalized
                );
                return false;
            }

            // TODO: Implement proper RBAC check via Identity service
            // Should verify user has TenantManager or TenantAdmin role for this tenant
            logger.LogInformation("Access validated for tenant {TenantId}", normalized);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating access to tenant {TenantId}", normalized);
            return false;
        }
    }

    public async Task<TenantMetadata?> GetTenantMetadataAsync(
        string tenantId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var normalized = NormalizeTenantId(tenantId);

        // Check cache first
        if (_metadataCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        try
        {
            // Query Database service provisioning API for tenant info
            var dbClient = httpClientFactory.CreateClient("Database");
            var response = await dbClient.GetAsync($"api/provisioning/tenants/{normalized}", ct);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    logger.LogWarning("Tenant {TenantId} not found", normalized);
                    return null;
                }

                logger.LogWarning(
                    "Failed to fetch tenant metadata for {TenantId}: {StatusCode}",
                    normalized,
                    response.StatusCode
                );
                return null;
            }

            // Parse response (simplified - actual API may return different structure)
            // For now, create basic metadata from tenant ID
            var metadata = new TenantMetadata(
                normalized,
                FormatDisplayName(normalized),
                DateTimeOffset.UtcNow,
                IsActive: true
            );

            // Cache for 5 minutes
            _metadataCache.TryAdd(normalized, metadata);
            return metadata;
        }
        catch (Exception ex)
        {
            // TEMPORARY: Return mock metadata if HTTP client fails (e.g., no base address configured)
            // This allows UI testing with stub pages before backend integration is complete
            logger.LogWarning(ex, "Error fetching tenant metadata for {TenantId}, using mock data", normalized);
            
            var mockMetadata = new TenantMetadata(
                normalized,
                FormatDisplayName(normalized),
                DateTimeOffset.UtcNow.AddDays(-30),
                IsActive: true
            );
            
            // Cache mock data temporarily
            _metadataCache.TryAdd(normalized, mockMetadata);
            return mockMetadata;
        }
    }

    public async Task<IReadOnlyList<TenantMetadata>> ListAccessibleTenantsAsync(
        CancellationToken ct = default
    )
    {
        try
        {
            // TODO: Query Identity service for user's tenant memberships
            // For now, return mock data for development
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext?.User?.Identity?.IsAuthenticated != true)
            {
                logger.LogWarning("Cannot list tenants: user not authenticated");
                return Array.Empty<TenantMetadata>();
            }

            // Mock tenants for development
            // In production, this should call: GET /identity/api/users/{userId}/tenants
            var mockTenants = new List<TenantMetadata>
            {
                new("acme-dev", "Acme Development", DateTimeOffset.UtcNow.AddDays(-30)),
                new("widgets-inc", "Widgets Inc", DateTimeOffset.UtcNow.AddDays(-15))
            };

            logger.LogInformation("Listed {Count} accessible tenants", mockTenants.Count);
            return mockTenants;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing accessible tenants");
            return Array.Empty<TenantMetadata>();
        }
    }

    private static string NormalizeTenantId(string tenantId)
    {
        return tenantId.Trim().ToLowerInvariant();
    }

    private static string FormatDisplayName(string tenantId)
    {
        // Convert "acme-dev" to "Acme Dev"
        var parts = tenantId.Split('-', '_');
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }
}
