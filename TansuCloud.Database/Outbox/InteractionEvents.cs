// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using TansuCloud.Database.EF;

namespace TansuCloud.Database.Outbox;

/// <summary>
/// Convenience helpers and payload types for recording user interactions as outbox events.
/// This enables future ML/recommendations without changing domain write logic.
/// </summary>
public static class InteractionEvents
{
    public const string InteractionRecordedType = "interaction.recorded";

    /// <summary>
    /// Minimal interaction payload for analytics/ML pipelines.
    /// PII should be minimized or hashed upstream when appropriate.
    /// </summary>
    public readonly record struct InteractionEventPayload(
        string TenantId,
        string? UserId,
        string? ItemId,
        string Action,
        DateTimeOffset Timestamp,
        JsonElement? Metadata
    );

    /// <summary>
    /// Enqueue a normalized InteractionRecorded outbox event.
    /// Idempotency can be provided by the caller (e.g., a composite key) when desired.
    /// </summary>
    public static void EnqueueInteraction(
        this IOutboxProducer producer,
        TansuDbContext db,
        InteractionEventPayload payload,
        string? idempotencyKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(db);

        using var doc = JsonSerializer.SerializeToDocument(payload);
        producer.Enqueue(db, InteractionRecordedType, doc, idempotencyKey);
    }
}
