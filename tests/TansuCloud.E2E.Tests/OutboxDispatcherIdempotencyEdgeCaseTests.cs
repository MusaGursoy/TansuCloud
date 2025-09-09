// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Edge-case idempotency test: Same IdempotencyKey but different Type values must NOT be suppressed.
/// Ensures suppression logic keys on the (Type, IdempotencyKey) pair, not the IdempotencyKey alone.
/// </summary>
public class OutboxDispatcherIdempotencyEdgeCaseTests
{
    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<string> Sent { get; } = new();

        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.CompletedTask;
        }
    } // End of Class RecordingPublisher

    [Xunit.Fact(
        DisplayName = "Dispatcher does NOT suppress when only IdempotencyKey matches but Type differs"
    )]
    public async Task Dispatcher_DoesNot_Suppress_Different_Type_Same_IdempotencyKey()
    {
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(
            Guid.NewGuid().ToString()
        );
        await using var ctx = new TansuDbContext(dbOpts.Options);
        var idem = "edge-key-1";
        using var p1 = JsonDocument.Parse("{\"v\":1}");
        using var p2 = JsonDocument.Parse("{\"v\":2}");
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                Type = "type.a",
                Payload = p1,
                Status = OutboxStatus.Pending,
                IdempotencyKey = idem
            }
        );
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                Type = "type.b",
                Payload = p2,
                Status = OutboxStatus.Pending,
                IdempotencyKey = idem
            }
        );
        await ctx.SaveChangesAsync();

        var publisher = new RecordingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            Options.Create(new OutboxOptions { RedisConnection = "unused", DispatchTenant = "x" }),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>(),
            sp,
            publisher
        );

        await dispatcher.DispatchPendingAsync(ctx, publisher, CancellationToken.None);

        publisher.Sent.Should().HaveCount(2); // both distinct (Type, IdempotencyKey) pairs must publish
        (await ctx.OutboxEvents.CountAsync(e => e.Status == OutboxStatus.Dispatched))
            .Should()
            .Be(2);
    }
} // End of Class OutboxDispatcherIdempotencyEdgeCaseTests
