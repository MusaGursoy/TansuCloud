// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;
using System.Text.Json;
using TansuCloud.Database.EF;
using TansuCloud.Observability;

namespace TansuCloud.Database.Outbox;

public interface IOutboxProducer
{
    void Enqueue(
        TansuDbContext db,
        string type,
        JsonDocument? payload,
        string? idempotencyKey = null
    );
}

public sealed class OutboxProducer(ILogger<OutboxProducer> logger) : IOutboxProducer
{
    private readonly ILogger<OutboxProducer> _logger = logger;
    private static readonly Meter Meter = new("TansuCloud.Database.Outbox", "1.0");
    private static readonly Counter<long> Produced = Meter.CreateCounter<long>("outbox.produced");

    public void Enqueue(
        TansuDbContext db,
        string type,
        JsonDocument? payload,
        string? idempotencyKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;

        // Lightweight idempotency dedupe: if a prior event with same (Type, IdempotencyKey) exists in the current
        // DbContext (either already persisted or tracked for insert) we skip enqueueing a duplicate. Dispatcher
        // side suppression is still kept as a defensive measure for race conditions across parallel transactions.
        if (normalizedKey is not null)
        {
            // Check local tracked entities first (cheap) then fall back to database if none found.
            var existsLocal = db
                .ChangeTracker.Entries<OutboxEvent>()
                .Any(e => e.Entity.IdempotencyKey == normalizedKey && e.Entity.Type == type);
            var existsPersisted =
                !existsLocal
                && db.OutboxEvents.Any(e => e.IdempotencyKey == normalizedKey && e.Type == type);
            if (existsLocal || existsPersisted)
            {
                _logger.LogOutboxDispatchAttempt(Guid.Empty, 0); // signal duplicate path with special values
                return; // End of duplicate fast path
            }
        }

        var e = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Type = type,
            Payload = payload,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = null,
            IdempotencyKey = normalizedKey
        };
        db.OutboxEvents.Add(e);
        Produced.Add(1);
        _logger.LogOutboxEnqueued(e.Id, e.Type);
        // Note: SaveChanges is owned by the caller to keep transactional boundaries aligned with domain write.
    }
}
