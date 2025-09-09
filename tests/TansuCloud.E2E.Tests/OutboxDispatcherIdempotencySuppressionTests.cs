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

public class OutboxDispatcherIdempotencySuppressionTests
{
    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<string> Sent = new();
        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.CompletedTask;
        }
    } // End of Class RecordingPublisher

    [Xunit.Fact(DisplayName="Dispatcher suppresses duplicate idempotency key publishes")]
    public async Task Dispatcher_Suppresses_Duplicate_Idempotency_Publishes()
    {
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        await using var ctx = new TansuDbContext(dbOpts.Options);
        using var p1 = JsonDocument.Parse("{\"v\":1}");
        using var p2 = JsonDocument.Parse("{\"v\":2}");
        var idem = "dup-key-1";
        ctx.OutboxEvents.Add(new OutboxEvent { Id=Guid.NewGuid(), OccurredAt=DateTimeOffset.UtcNow.AddSeconds(-3), Type="thing.created", Payload=p1, Status=OutboxStatus.Pending, IdempotencyKey=idem });
        ctx.OutboxEvents.Add(new OutboxEvent { Id=Guid.NewGuid(), OccurredAt=DateTimeOffset.UtcNow.AddSeconds(-2), Type="thing.created", Payload=p2, Status=OutboxStatus.Pending, IdempotencyKey=idem });
        await ctx.SaveChangesAsync();

        var publisher = new RecordingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(Options.Create(new OutboxOptions { RedisConnection="unused", DispatchTenant="x" }), sp.GetRequiredService<ILogger<OutboxDispatcher>>(), sp, publisher);

        await dispatcher.DispatchPendingAsync(ctx, publisher, CancellationToken.None);

        // Only first should publish, both should end up dispatched.
        publisher.Sent.Should().HaveCount(1);
        (await ctx.OutboxEvents.CountAsync(e => e.Status == OutboxStatus.Dispatched)).Should().Be(2);
    }
}
