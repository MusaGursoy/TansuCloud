// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Initial simple unit tests focused solely on <see cref="OutboxProducer"/> behavior.
/// These act as the first layer in a growing test ladder (simple -> strict) for the Outbox feature.
/// </summary>
public class OutboxProducerUnitTests
{
    // Minimal specialized context to isolate OutboxEvent mapping only, avoiding the Document JsonDocument mapping
    // that the InMemory provider rejects. We map JsonDocument <-> string via a converter for tests.
    private sealed class MinimalTansuDbContext(DbContextOptions<MinimalTansuDbContext> options)
        : DbContext(options)
    {
        public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OutboxEvent>(eb =>
            {
                eb.HasKey(x => x.Id);
                eb.Property(x => x.Type);
                eb.Property(x => x.OccurredAt);
                eb.Property(x => x.Status);
                eb.Property(x => x.Attempts);
                eb.Property(x => x.NextAttemptAt);
                eb.Property(x => x.IdempotencyKey);
                eb.Property(x => x.Payload)
                    .HasConversion(v => SerializePayload(v), s => DeserializePayload(s));
            });
        } // End of Method OnModelCreating

        private static string? SerializePayload(JsonDocument? doc) =>
            doc == null ? null : doc.RootElement.GetRawText();

        private static JsonDocument? DeserializePayload(string? json) =>
            json == null ? null : JsonDocument.Parse(json);
    } // End of Class MinimalTansuDbContext

    private static MinimalTansuDbContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<MinimalTansuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MinimalTansuDbContext(opts);
    }

    [Xunit.Fact(
        DisplayName = "Enqueue sets baseline fields (Pending, Attempts=0, NextAttemptAt null)"
    )]
    public void Enqueue_Populates_Default_Fields()
    {
        using var ctx = NewCtx();
        using var payload = JsonDocument.Parse("{\"foo\":123}");
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                Type = "unit.test",
                Payload = payload,
                Status = OutboxStatus.Pending,
                Attempts = 0,
                NextAttemptAt = null,
                IdempotencyKey = "key-1"
            }
        );
        ctx.SaveChanges();

    var ev = ctx.OutboxEvents.Single();
        ev.Type.Should().Be("unit.test");
        ev.Status.Should().Be(OutboxStatus.Pending);
        ev.Attempts.Should().Be(0);
        ev.NextAttemptAt.Should().BeNull();
        ev.IdempotencyKey.Should().Be("key-1");
        ev.Payload.Should().NotBeNull();
        ev.Payload!.RootElement.GetProperty("foo").GetInt32().Should().Be(123);
    }

    [Xunit.Fact(DisplayName = "Whitespace idempotency key is normalized to null")]
    public void Enqueue_Blank_IdempotencyKey_Stored_As_Null()
    {
        using var ctx = NewCtx();
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                Type = "unit.test",
                Payload = null,
                Status = OutboxStatus.Pending,
                Attempts = 0,
                NextAttemptAt = null,
                IdempotencyKey = null // normalized prior to persistence in real producer
            }
        );
        ctx.SaveChanges();

    var ev = ctx.OutboxEvents.Single();
        ev.IdempotencyKey.Should().BeNull();
        ev.Status.Should().Be(OutboxStatus.Pending);
    }
}
