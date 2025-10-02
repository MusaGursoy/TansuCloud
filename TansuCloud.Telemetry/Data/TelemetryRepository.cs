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
    public async Task PersistAsync(TelemetryEnvelopeEntity envelope, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await _dbContext.Envelopes.AddAsync(envelope, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Persisted telemetry envelope {EnvelopeId} with {ItemCount} items at {Timestamp}",
            envelope.Id,
            envelope.ItemCount,
            envelope.ReceivedAtUtc
        );
    } // End of Method PersistAsync
} // End of Class TelemetryRepository
