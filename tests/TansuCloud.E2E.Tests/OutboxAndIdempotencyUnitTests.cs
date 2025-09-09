// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

public class OutboxAndIdempotencyUnitTests
{
    [Xunit.Fact]
    public void Backoff_Should_Grow_With_Attempts_And_Cap()
    {
        var r = new Random(123);
        var d1 = OutboxBackoff.Compute(1, maxSeconds: 10, rng: r);
        var d2 = OutboxBackoff.Compute(2, maxSeconds: 10, rng: r);
        var d5 = OutboxBackoff.Compute(5, maxSeconds: 10, rng: r);
        d2.Should().BeGreaterThan(d1);
        d5.Should().BeGreaterThan(d2);
        var d20 = OutboxBackoff.Compute(20, maxSeconds: 10, rng: r);
        d20.TotalSeconds.Should().BeLessThanOrEqualTo(11); // cap + jitter
    }

    [Xunit.Fact]
    public void Idempotency_Lookup_Finds_Prior_DocumentId()
    {
        var options = new DbContextOptionsBuilder<TansuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var ctx = new TansuDbContext(options);
        var docId = Guid.NewGuid();
        var idem = "test-key-123";
        // In-memory provider can't natively store JsonDocument with our current model configuration; emulate by serializing then parsing back.
        var json = $"{{\"documentId\":\"{docId}\"}}";
        using (var payload = JsonDocument.Parse(json))
        {
            ctx.OutboxEvents.Add(
                new OutboxEvent
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Type = "document.created",
                    Payload = payload,
                    Status = OutboxStatus.Dispatched,
                    Attempts = 1,
                    IdempotencyKey = idem
                }
            );
            ctx.SaveChanges();
        }
        var found = ctx
            .OutboxEvents.AsNoTracking()
            .Where(e => e.IdempotencyKey == idem && e.Type == "document.created")
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefault();
        found.Should().NotBeNull();
        using var doc = found!.Payload!;
        var root = doc.RootElement;
        root.TryGetProperty("documentId", out var idProp).Should().BeTrue();
        Guid.Parse(idProp.GetString()!).Should().Be(docId);
    }
}
