// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using TansuCloud.Gateway.Services;

namespace TansuCloud.Gateway.Data;

/// <summary>
/// Persistent storage interface for Gateway policies.
/// </summary>
public interface IPolicyStore
{
    /// <summary>Get all policies from database.</summary>
    Task<IReadOnlyList<PolicyEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Get a specific policy by ID.</summary>
    Task<PolicyEntry?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>Get policies by type.</summary>
    Task<IReadOnlyList<PolicyEntry>> GetByTypeAsync(PolicyType type, CancellationToken cancellationToken = default);
    
    /// <summary>Add or update a policy in database.</summary>
    Task UpsertAsync(PolicyEntry policy, CancellationToken cancellationToken = default);
    
    /// <summary>Delete a policy by ID from database.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>Replace all policies atomically in database.</summary>
    Task ReplaceAllAsync(IEnumerable<PolicyEntry> policies, CancellationToken cancellationToken = default);
} // End of Interface IPolicyStore
