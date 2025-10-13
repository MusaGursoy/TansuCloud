// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.Data.Records;

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
        ArgumentNullException.ThrowIfNull(envelope);

        var record = ConvertToActiveRecord(envelope);

        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.ActiveEnvelopes.AddAsync(record, cancellationToken).ConfigureAwait(false);
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

        var (activeQuery, archivedQuery) = BuildFilteredQueries(query);

        IQueryable<EnvelopeProjection> combinedQuery = CombineActiveArchivedQueries(
            activeQuery,
            archivedQuery
        );

        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var skip = Math.Max((page - 1) * pageSize, 0);

        var totalCount = await combinedQuery
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);

        var pageResults = await combinedQuery
            .OrderByDescending(e => e.ReceivedAtUtc)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var envelopes = pageResults
            .Select(p => ConvertToDomain(p))
            .ToList();

        return new PagedResult<TelemetryEnvelopeEntity>(totalCount, envelopes);
    } // End of Method QueryEnvelopesAsync

    /// <summary>
    /// Retrieves a single envelope including its items.
    /// </summary>
    public async Task<TelemetryEnvelopeEntity?> GetEnvelopeAsync(
        Guid id,
        bool includeDeleted,
        CancellationToken cancellationToken
    )
    {
        var active = await _dbContext
            .ActiveEnvelopes.AsNoTracking()
            .Include(e => e.Items)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (active is not null)
        {
            return ConvertToDomain(active);
        }

        if (!includeDeleted)
        {
            return null;
        }

        var archived = await _dbContext
            .ArchivedEnvelopes.AsNoTracking()
            .Include(e => e.Items)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return archived is null ? null : ConvertToDomain(archived);
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

        var (activeQuery, archivedQuery) = BuildFilteredQueries(query);
        var combinedQuery = CombineActiveArchivedQueries(activeQuery, archivedQuery)
            .OrderByDescending(e => e.ReceivedAtUtc)
            .Take(maxItems);

        var projections = await combinedQuery.ToListAsync(cancellationToken).ConfigureAwait(false);

        if (projections.Count == 0)
        {
            return Array.Empty<TelemetryEnvelopeEntity>();
        }

        var activeIds = projections.Where(p => !p.IsArchived).Select(p => p.Id).ToArray();
        var archivedIds = projections.Where(p => p.IsArchived).Select(p => p.Id).ToArray();

        var envelopes = new List<TelemetryEnvelopeEntity>(projections.Count);
        var lookup = new Dictionary<Guid, TelemetryEnvelopeEntity>();

        if (activeIds.Length > 0)
        {
            var activeRecordsQuery = _dbContext
                .ActiveEnvelopes.AsNoTracking()
                .Where(e => activeIds.Contains(e.Id));

            if (includeItems)
            {
                activeRecordsQuery = activeRecordsQuery.Include(e => e.Items);
            }

            var records = await activeRecordsQuery
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                var entity = ConvertToDomain(record, includeItems);
                lookup[entity.Id] = entity;
            }
        }

        if (archivedIds.Length > 0)
        {
            var archivedRecordsQuery = _dbContext
                .ArchivedEnvelopes.AsNoTracking()
                .Where(e => archivedIds.Contains(e.Id));

            if (includeItems)
            {
                archivedRecordsQuery = archivedRecordsQuery.Include(e => e.Items);
            }

            var records = await archivedRecordsQuery
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                var entity = ConvertToDomain(record, includeItems);
                lookup[entity.Id] = entity;
            }
        }

        foreach (var projection in projections.OrderByDescending(p => p.ReceivedAtUtc))
        {
            if (lookup.TryGetValue(projection.Id, out var envelope))
            {
                envelopes.Add(envelope);
            }
        }

        return envelopes;
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
            .ActiveEnvelopes.FirstOrDefaultAsync(
                e => e.Id == id,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (envelope is not null)
        {
            if (envelope.AcknowledgedAtUtc.HasValue)
            {
                return true;
            }

            envelope.AcknowledgedAtUtc = timestampUtc;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var archived = await _dbContext
            .ArchivedEnvelopes.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (archived is null)
        {
            return false;
        }

        if (archived.AcknowledgedAtUtc.HasValue)
        {
            return true;
        }

        archived.AcknowledgedAtUtc = timestampUtc;
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
            .ActiveEnvelopes.Include(e => e.Items)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null)
        {
            return false;
        }

        if (envelope.DeletedAtUtc.HasValue)
        {
            return true;
        }

        var archivedRecord = ConvertToArchivedRecord(envelope, timestampUtc);

        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        _dbContext.ActiveEnvelopes.Remove(envelope);
        await _dbContext.ArchivedEnvelopes.AddAsync(archivedRecord, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    } // End of Method TrySoftDeleteAsync

    private (IQueryable<EnvelopeProjection>? Active, IQueryable<EnvelopeProjection>? Archived)
        BuildFilteredQueries(TelemetryEnvelopeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var includeActive = query.Deleted is not true;
        var includeArchived = query.IncludeDeleted || query.Deleted == true;

        IQueryable<EnvelopeProjection>? activeQuery = null;
        IQueryable<EnvelopeProjection>? archivedQuery = null;

        if (includeActive)
        {
            var activeRecords = ApplyEnvelopeFilters<TelemetryActiveEnvelopeRecord, TelemetryActiveItemRecord>(
                _dbContext.ActiveEnvelopes.AsNoTracking(), query);

            activeQuery = activeRecords.Select(e => new EnvelopeProjection
            {
                Id = e.Id,
                ReceivedAtUtc = e.ReceivedAtUtc,
                Host = e.Host,
                Environment = e.Environment,
                Service = e.Service,
                SeverityThreshold = e.SeverityThreshold,
                WindowMinutes = e.WindowMinutes,
                MaxItems = e.MaxItems,
                ItemCount = e.ItemCount,
                AcknowledgedAtUtc = e.AcknowledgedAtUtc,
                DeletedAtUtc = e.DeletedAtUtc,
                IsArchived = false
            });
        }

        if (includeArchived)
        {
            var archivedRecords = ApplyEnvelopeFilters<TelemetryArchivedEnvelopeRecord, TelemetryArchivedItemRecord>(
                _dbContext.ArchivedEnvelopes.AsNoTracking(), query);

            archivedQuery = archivedRecords.Select(e => new EnvelopeProjection
            {
                Id = e.Id,
                ReceivedAtUtc = e.ReceivedAtUtc,
                Host = e.Host,
                Environment = e.Environment,
                Service = e.Service,
                SeverityThreshold = e.SeverityThreshold,
                WindowMinutes = e.WindowMinutes,
                MaxItems = e.MaxItems,
                ItemCount = e.ItemCount,
                AcknowledgedAtUtc = e.AcknowledgedAtUtc,
                DeletedAtUtc = e.DeletedAtUtc,
                IsArchived = true
            });
        }

        return (activeQuery, archivedQuery);
    } // End of Method BuildFilteredQueries

    private static IQueryable<EnvelopeProjection> CombineActiveArchivedQueries(
        IQueryable<EnvelopeProjection>? active,
        IQueryable<EnvelopeProjection>? archived
    )
    {
        if (active is null && archived is null)
        {
            throw new InvalidOperationException(
                "No telemetry envelope sources were selected for the requested query."
            );
        }

        if (active is null)
        {
            return archived!;
        }

        return archived is null ? active : active.Concat(archived);
    } // End of Method CombineActiveArchivedQueries

    private static IQueryable<TEnvelope> ApplyEnvelopeFilters<TEnvelope, TItem>(
        IQueryable<TEnvelope> queryable,
        TelemetryEnvelopeQuery query
    )
        where TEnvelope : TelemetryEnvelopeRecordBase<TItem>
        where TItem : TelemetryItemRecordBase
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

    private static TelemetryEnvelopeEntity ConvertToDomain(EnvelopeProjection projection)
    {
        return new TelemetryEnvelopeEntity
        {
            Id = projection.Id,
            ReceivedAtUtc = projection.ReceivedAtUtc,
            Host = projection.Host,
            Environment = projection.Environment,
            Service = projection.Service,
            SeverityThreshold = projection.SeverityThreshold,
            WindowMinutes = projection.WindowMinutes,
            MaxItems = projection.MaxItems,
            ItemCount = projection.ItemCount,
            AcknowledgedAtUtc = projection.AcknowledgedAtUtc,
            DeletedAtUtc = projection.DeletedAtUtc,
            Items = new List<TelemetryItemEntity>()
        };
    } // End of Method ConvertToDomain

    private static TelemetryEnvelopeEntity ConvertToDomain(
        TelemetryActiveEnvelopeRecord record,
        bool includeItems = true
    )
    {
        return ConvertToDomainCore<TelemetryActiveEnvelopeRecord, TelemetryActiveItemRecord>(
            record,
            includeItems,
            isArchived: false
        );
    } // End of Method ConvertToDomain

    private static TelemetryEnvelopeEntity ConvertToDomain(
        TelemetryArchivedEnvelopeRecord record,
        bool includeItems = true
    )
    {
        return ConvertToDomainCore<TelemetryArchivedEnvelopeRecord, TelemetryArchivedItemRecord>(
            record,
            includeItems,
            isArchived: true
        );
    } // End of Method ConvertToDomain

    private static TelemetryEnvelopeEntity ConvertToDomainCore<TEnvelope, TItem>(
        TEnvelope record,
        bool includeItems,
        bool isArchived
    )
        where TEnvelope : TelemetryEnvelopeRecordBase<TItem>
        where TItem : TelemetryItemRecordBase
    {
        var entity = new TelemetryEnvelopeEntity
        {
            Id = record.Id,
            ReceivedAtUtc = record.ReceivedAtUtc,
            Host = record.Host,
            Environment = record.Environment,
            Service = record.Service,
            SeverityThreshold = record.SeverityThreshold,
            WindowMinutes = record.WindowMinutes,
            MaxItems = record.MaxItems,
            ItemCount = record.ItemCount,
            AcknowledgedAtUtc = record.AcknowledgedAtUtc,
            DeletedAtUtc = isArchived ? record.DeletedAtUtc : null,
            Items = new List<TelemetryItemEntity>()
        };

        if (!includeItems)
        {
            return entity;
        }

        foreach (var item in record.Items)
        {
            var mapped = ConvertItemToDomain(item, entity);
            entity.Items.Add(mapped);
        }

        return entity;
    } // End of Method ConvertToDomainCore

    private static TelemetryItemEntity ConvertItemToDomain(
        TelemetryItemRecordBase item,
        TelemetryEnvelopeEntity envelope
    )
    {
        return new TelemetryItemEntity
        {
            Id = item.Id,
            EnvelopeId = envelope.Id,
            Envelope = envelope,
            Kind = item.Kind,
            TimestampUtc = item.TimestampUtc,
            Level = item.Level,
            Message = item.Message,
            TemplateHash = item.TemplateHash,
            Exception = item.Exception,
            Service = item.Service,
            Environment = item.Environment,
            TenantHash = item.TenantHash,
            CorrelationId = item.CorrelationId,
            TraceId = item.TraceId,
            SpanId = item.SpanId,
            Category = item.Category,
            EventId = item.EventId,
            Count = item.Count,
            PropertiesJson = item.PropertiesJson
        };
    } // End of Method ConvertItemToDomain

    private static TelemetryActiveEnvelopeRecord ConvertToActiveRecord(
        TelemetryEnvelopeEntity envelope
    )
    {
        var record = new TelemetryActiveEnvelopeRecord
        {
            Id = envelope.Id,
            Host = envelope.Host,
            Environment = envelope.Environment,
            Service = envelope.Service,
            SeverityThreshold = envelope.SeverityThreshold,
            ReceivedAtUtc = envelope.ReceivedAtUtc,
            WindowMinutes = envelope.WindowMinutes,
            MaxItems = envelope.MaxItems,
            ItemCount = envelope.ItemCount,
            AcknowledgedAtUtc = envelope.AcknowledgedAtUtc,
            DeletedAtUtc = null,
            Items = new List<TelemetryActiveItemRecord>()
        };

        foreach (var item in envelope.Items)
        {
            var recordItem = new TelemetryActiveItemRecord
            {
                EnvelopeId = envelope.Id,
                Envelope = record,
                Kind = item.Kind,
                TimestampUtc = item.TimestampUtc,
                Level = item.Level,
                Message = item.Message,
                TemplateHash = item.TemplateHash,
                Exception = item.Exception,
                Service = item.Service,
                Environment = item.Environment,
                TenantHash = item.TenantHash,
                CorrelationId = item.CorrelationId,
                TraceId = item.TraceId,
                SpanId = item.SpanId,
                Category = item.Category,
                EventId = item.EventId,
                Count = item.Count,
                PropertiesJson = item.PropertiesJson
            };

            record.Items.Add(recordItem);
        }

        record.ItemCount = record.Items.Count;
        return record;
    } // End of Method ConvertToActiveRecord

    private static TelemetryArchivedEnvelopeRecord ConvertToArchivedRecord(
        TelemetryActiveEnvelopeRecord active,
        DateTime deletedAtUtc
    )
    {
        var archived = new TelemetryArchivedEnvelopeRecord
        {
            Id = active.Id,
            Host = active.Host,
            Environment = active.Environment,
            Service = active.Service,
            SeverityThreshold = active.SeverityThreshold,
            ReceivedAtUtc = active.ReceivedAtUtc,
            WindowMinutes = active.WindowMinutes,
            MaxItems = active.MaxItems,
            ItemCount = active.ItemCount,
            AcknowledgedAtUtc = active.AcknowledgedAtUtc,
            DeletedAtUtc = deletedAtUtc,
            Items = new List<TelemetryArchivedItemRecord>()
        };

        foreach (var item in active.Items)
        {
            var archivedItem = new TelemetryArchivedItemRecord
            {
                Id = item.Id,
                EnvelopeId = archived.Id,
                Envelope = archived,
                Kind = item.Kind,
                TimestampUtc = item.TimestampUtc,
                Level = item.Level,
                Message = item.Message,
                TemplateHash = item.TemplateHash,
                Exception = item.Exception,
                Service = item.Service,
                Environment = item.Environment,
                TenantHash = item.TenantHash,
                CorrelationId = item.CorrelationId,
                TraceId = item.TraceId,
                SpanId = item.SpanId,
                Category = item.Category,
                EventId = item.EventId,
                Count = item.Count,
                PropertiesJson = item.PropertiesJson
            };

            archived.Items.Add(archivedItem);
        }

        archived.ItemCount = archived.Items.Count;
        return archived;
    } // End of Method ConvertToArchivedRecord

    private sealed class EnvelopeProjection
    {
        public Guid Id { get; init; }
        public DateTime ReceivedAtUtc { get; init; }
        public string Host { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public string Service { get; init; } = string.Empty;
        public string SeverityThreshold { get; init; } = string.Empty;
        public int WindowMinutes { get; init; }
        public int MaxItems { get; init; }
        public int ItemCount { get; init; }
        public DateTime? AcknowledgedAtUtc { get; init; }
        public DateTime? DeletedAtUtc { get; init; }
        public bool IsArchived { get; init; }
    } // End of Class EnvelopeProjection
} // End of Class TelemetryRepository
