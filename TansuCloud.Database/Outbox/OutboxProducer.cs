// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using TansuCloud.Database.EF;

namespace TansuCloud.Database.Outbox;

public interface IOutboxProducer
{
    void Enqueue(TansuDbContext db, string type, JsonDocument? payload, string? idempotencyKey = null);
}

internal sealed class OutboxProducer(ILogger<OutboxProducer> logger) : IOutboxProducer
{
    private readonly ILogger<OutboxProducer> _logger = logger;

    public void Enqueue(TansuDbContext db, string type, JsonDocument? payload, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var e = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Type = type,
            Payload = payload,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = null,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey
        };
        db.OutboxEvents.Add(e);
        _logger.LogDebug("Enqueued outbox event {Id} type={Type}", e.Id, e.Type);
        // Note: SaveChanges is owned by the caller to keep transactional boundaries aligned with domain write.
    }
}
