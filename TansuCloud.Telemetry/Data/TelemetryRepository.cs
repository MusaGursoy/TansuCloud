// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TansuCloud.Telemetry.Data.Entities;

namespace TansuCloud.Telemetry.Data;

/// <summary>
/// Repository encapsulating telemetry persistence operations.
/// </summary>
public sealed class TelemetryRepository
{
    private readonly TelemetryDbContext _dbContext;
    private readonly ILogger<TelemetryRepository> _logger;

    public TelemetryRepository(TelemetryDbContext dbContext, ILogger<TelemetryRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    } // End of Constructor TelemetryRepository

    /// <summary>
    /// Persists the supplied envelope and items in a single transaction.
    /// </summary>
    public async Task PersistAsync(
        TelemetryEnvelopeEntity envelope,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.Envelopes.AddAsync(envelope, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Persisted telemetry envelope {EnvelopeId} with {ItemCount} items at {Timestamp}",
            envelope.Id,
            envelope.ItemCount,
            envelope.ReceivedAtUtc
        );
    } // End of Method PersistAsync

    /// <summary>
    /// Retrieves envelopes based on the supplied filter.
    /// </summary>
    public async Task<PagedResult<TelemetryEnvelopeEntity>> QueryEnvelopesAsync(
        TelemetryEnvelopeQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        var envelopeQuery = ApplyEnvelopeFilters(_dbContext.Envelopes.AsNoTracking(), query);

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var skip = Math.Max((page - 1) * pageSize, 0);

        var totalCount = await envelopeQuery
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        var envelopes = await envelopeQuery
            .OrderByDescending(e => e.ReceivedAtUtc)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<TelemetryEnvelopeEntity>(totalCount, envelopes);
    } // End of Method QueryEnvelopesAsync

    /// <summary>
    /// Retrieves a single envelope including its items.
    /// </summary>
    public Task<TelemetryEnvelopeEntity?> GetEnvelopeAsync(
        Guid id,
        bool includeDeleted,
        CancellationToken cancellationToken
    )
    {
        var query = _dbContext
            .Envelopes.AsNoTracking()
            .Include(e => e.Items)
            .Where(e => e.Id == id);

        if (!includeDeleted)
        {
            query = query.Where(e => e.DeletedAtUtc == null);
        }
        return query.FirstOrDefaultAsync(cancellationToken);
    } // End of Method GetEnvelopeAsync

    /// <summary>
    /// Retrieves envelopes for export respecting the configured limit.
    /// </summary>
    public async Task<IReadOnlyList<TelemetryEnvelopeEntity>> ExportEnvelopesAsync(
        TelemetryEnvelopeQuery query,
        int maxItems,
        bool includeItems,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "maxItems must be positive.");
        }

        var source = _dbContext.Envelopes.AsNoTracking();
        if (includeItems)
        {
            source = source.Include(e => e.Items);
        }

        var filtered = ApplyEnvelopeFilters(source, query)
            .OrderByDescending(e => e.ReceivedAtUtc)
            .Take(maxItems);

        var results = await filtered.ToListAsync(cancellationToken).ConfigureAwait(false);
        return results;
    } // End of Method ExportEnvelopesAsync

    /// <summary>
    /// Marks the specified envelope as acknowledged.
    /// </summary>
    public async Task<bool> TryAcknowledgeAsync(
        Guid id,
        DateTime timestampUtc,
        CancellationToken cancellationToken
    )
    {
        var envelope = await _dbContext
            .Envelopes.FirstOrDefaultAsync(
                e => e.Id == id && e.DeletedAtUtc == null,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (envelope is null)
        {
            return false;
        }

        if (envelope.AcknowledgedAtUtc.HasValue)
        {
            return true;
        }

        envelope.AcknowledgedAtUtc = timestampUtc;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    } // End of Method TryAcknowledgeAsync

    /// <summary>
    /// Soft deletes (archives) the specified envelope.
    /// </summary>
    public async Task<bool> TrySoftDeleteAsync(
        Guid id,
        DateTime timestampUtc,
        CancellationToken cancellationToken
    )
    {
        var envelope = await _dbContext
            .Envelopes.FirstOrDefaultAsync(
                e => e.Id == id && e.DeletedAtUtc == null,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (envelope is null)
        {
            return false;
        }

        envelope.DeletedAtUtc = timestampUtc;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    } // End of Method TrySoftDeleteAsync

    private static IQueryable<TelemetryEnvelopeEntity> ApplyEnvelopeFilters(
        IQueryable<TelemetryEnvelopeEntity> queryable,
        TelemetryEnvelopeQuery query
    )
    {
        ArgumentNullException.ThrowIfNull(queryable);
        ArgumentNullException.ThrowIfNull(query);

        if (!string.IsNullOrWhiteSpace(query.Service))
        {
            queryable = queryable.Where(e => e.Service == query.Service);
        }

        if (!string.IsNullOrWhiteSpace(query.Environment))
        {
            queryable = queryable.Where(e => e.Environment == query.Environment);
        }

        if (!string.IsNullOrWhiteSpace(query.SeverityThreshold))
        {
            queryable = queryable.Where(e => e.SeverityThreshold == query.SeverityThreshold);
        }

        if (!string.IsNullOrWhiteSpace(query.Host))
        {
            queryable = queryable.Where(e => e.Host == query.Host);
        }

        if (query.FromUtc.HasValue)
        {
            queryable = queryable.Where(e => e.ReceivedAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            queryable = queryable.Where(e => e.ReceivedAtUtc <= query.ToUtc.Value);
        }

        if (query.Acknowledged.HasValue)
        {
            queryable = query.Acknowledged.Value
                ? queryable.Where(e => e.AcknowledgedAtUtc != null)
                : queryable.Where(e => e.AcknowledgedAtUtc == null);
        }
        else if (!query.IncludeAcknowledged)
        {
            queryable = queryable.Where(e => e.AcknowledgedAtUtc == null);
        }

        if (query.Deleted.HasValue)
        {
            queryable = query.Deleted.Value
                ? queryable.Where(e => e.DeletedAtUtc != null)
                : queryable.Where(e => e.DeletedAtUtc == null);
        }
        else if (!query.IncludeDeleted)
        {
            queryable = queryable.Where(e => e.DeletedAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            queryable = queryable.Where(e =>
                EF.Functions.Like(e.Host, $"%{term}%")
                || EF.Functions.Like(e.Service, $"%{term}%")
                || EF.Functions.Like(e.Environment, $"%{term}%")
            );
        }

        return queryable;
    } // End of Method ApplyEnvelopeFilters
} // End of Class TelemetryRepository
