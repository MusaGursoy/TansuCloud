// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TansuCloud.Gateway.Services;

namespace TansuCloud.Gateway.Data;

/// <summary>
/// PostgreSQL-backed implementation of policy storage.
/// </summary>
public class PolicyStore : IPolicyStore
{
    private readonly PolicyDbContext _context;
    private readonly ILogger<PolicyStore> _logger;

    public PolicyStore(PolicyDbContext context, ILogger<PolicyStore> logger)
    {
        _context = context;
        _logger = logger;
    } // End of Constructor PolicyStore

    public async Task<IReadOnlyList<PolicyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.Policies
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        
        return entities.Select(MapToEntry).ToList();
    } // End of Method GetAllAsync

    public async Task<PolicyEntry?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Policies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        
        return entity == null ? null : MapToEntry(entity);
    } // End of Method GetByIdAsync

    public async Task<IReadOnlyList<PolicyEntry>> GetByTypeAsync(PolicyType type, CancellationToken cancellationToken = default)
    {
        var typeValue = (int)type;
        var entities = await _context.Policies
            .AsNoTracking()
            .Where(p => p.Type == typeValue)
            .ToListAsync(cancellationToken);
        
        return entities.Select(MapToEntry).ToList();
    } // End of Method GetByTypeAsync

    public async Task UpsertAsync(PolicyEntry policy, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Policies
            .FirstOrDefaultAsync(p => p.Id == policy.Id, cancellationToken);

        if (existing != null)
        {
            // Update existing
            existing.Type = (int)policy.Type;
            existing.Mode = (int)policy.Mode;
            existing.Description = policy.Description;
            existing.ConfigJson = policy.Config.GetRawText();
            existing.Enabled = policy.Enabled;
            existing.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Policy updated in database: {PolicyId} (Type={Type}, Mode={Mode})",
                policy.Id,
                policy.Type,
                policy.Mode
            );
        }
        else
        {
            // Insert new
            var entity = new PolicyEntity
            {
                Id = policy.Id,
                Type = (int)policy.Type,
                Mode = (int)policy.Mode,
                Description = policy.Description,
                ConfigJson = policy.Config.GetRawText(),
                Enabled = policy.Enabled,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Policies.Add(entity);

            _logger.LogInformation(
                "Policy created in database: {PolicyId} (Type={Type}, Mode={Mode})",
                policy.Id,
                policy.Type,
                policy.Mode
            );
        }

        await _context.SaveChangesAsync(cancellationToken);
    } // End of Method UpsertAsync

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Policies
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        _context.Policies.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Policy deleted from database: {PolicyId} (Type={Type})",
            id,
            (PolicyType)entity.Type
        );

        return true;
    } // End of Method DeleteAsync

    public async Task ReplaceAllAsync(IEnumerable<PolicyEntry> policies, CancellationToken cancellationToken = default)
    {
        // Delete all existing policies
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM gateway_policies", cancellationToken);

        // Insert new policies
        var entities = policies.Select(p => new PolicyEntity
        {
            Id = p.Id,
            Type = (int)p.Type,
            Mode = (int)p.Mode,
            Description = p.Description,
            ConfigJson = p.Config.GetRawText(),
            Enabled = p.Enabled,
            CreatedAt = p.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        }).ToList();

        _context.Policies.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Policies replaced in database: {Count} policies", entities.Count);
    } // End of Method ReplaceAllAsync

    private static PolicyEntry MapToEntry(PolicyEntity entity)
    {
        return new PolicyEntry
        {
            Id = entity.Id,
            Type = (PolicyType)entity.Type,
            Mode = (PolicyEnforcementMode)entity.Mode,
            Description = entity.Description,
            Config = JsonDocument.Parse(entity.ConfigJson).RootElement,
            Enabled = entity.Enabled,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    } // End of Method MapToEntry
} // End of Class PolicyStore
