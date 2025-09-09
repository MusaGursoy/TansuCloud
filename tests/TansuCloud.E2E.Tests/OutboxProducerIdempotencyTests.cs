// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Tests for producer-side idempotent enqueue behavior. Ensures duplicates with same (Type, IdempotencyKey)
/// within a single transactional unit are suppressed while different Type values with same key are allowed.
/// </summary>
public class OutboxProducerIdempotencyTests
{
    private static TansuDbContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<TansuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TansuDbContext(opts);
    }

    [Xunit.Fact(
        DisplayName = "Producer suppresses duplicate (Type,IdempotencyKey) in same context"
    )]
    public void Producer_Suppresses_Duplicate_In_Same_Context()
    {
        using var ctx = NewCtx();
        var prod = new OutboxProducer(new NullLogger<OutboxProducer>());
        using var p1 = JsonDocument.Parse("{\"v\":1}");
        using var p2 = JsonDocument.Parse("{\"v\":2}");

        prod.Enqueue(ctx, "demo.created", p1, "dup-key-1");
        prod.Enqueue(ctx, "demo.created", p2, "dup-key-1"); // duplicate should be suppressed
        ctx.SaveChanges();

        ctx.OutboxEvents.Count().Should().Be(1);
        var ev = ctx.OutboxEvents.Single();
        ev.IdempotencyKey.Should().Be("dup-key-1");
    }

    [Xunit.Fact(DisplayName = "Producer allows different Type with same IdempotencyKey")]
    public void Producer_Allows_Different_Type_Same_Key()
    {
        using var ctx = NewCtx();
        var prod = new OutboxProducer(new NullLogger<OutboxProducer>());
        prod.Enqueue(ctx, "type.a", null, "edge-key-2");
        prod.Enqueue(ctx, "type.b", null, "edge-key-2");
        ctx.SaveChanges();
        ctx.OutboxEvents.Count().Should().Be(2);
        ctx.OutboxEvents.Select(e => e.Type)
            .Distinct()
            .Should()
            .BeEquivalentTo(new[] { "type.a", "type.b" });
    }

    [Xunit.Fact(DisplayName = "Producer skips persisted duplicate in subsequent call")]
    public void Producer_Skips_Duplicate_Persisted()
    {
        using var ctx = NewCtx();
        var prod = new OutboxProducer(new NullLogger<OutboxProducer>());
        prod.Enqueue(ctx, "persist.test", null, "persist-key-1");
        ctx.SaveChanges();
        // Simulate new operation within same DbContext after first SaveChanges
        prod.Enqueue(ctx, "persist.test", null, "persist-key-1");
        ctx.SaveChanges();
        ctx.OutboxEvents.Count().Should().Be(1);
    }
}
