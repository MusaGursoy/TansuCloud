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

public class OutboxDispatcherHappyPathTests
{
    private sealed class TestPublisher : IOutboxPublisher
    {
        public List<string> Sent = new();

        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(payload);
            return Task.CompletedTask;
        }
    } // End of Class TestPublisher

    // No subclassing required now that dispatcher accepts injected publisher and exposes DispatchPendingAsync

    [Xunit.Fact(DisplayName = "Dispatcher publishes pending events and marks them dispatched")]
    public async Task Dispatcher_Publishes_And_Marks_Dispatched()
    {
        // Arrange: real DB context (in-memory) and two events
        var optsBuilder = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(
            Guid.NewGuid().ToString()
        );
        await using var ctx = new TansuDbContext(optsBuilder.Options);
        using var payload1 = JsonDocument.Parse("{\"v\":1}");
        using var payload2 = JsonDocument.Parse("{\"v\":2}");
        ctx.OutboxEvents.AddRange(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                Type = "x",
                Payload = payload1,
                Status = OutboxStatus.Pending
            },
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-4),
                Type = "x",
                Payload = payload2,
                Status = OutboxStatus.Pending
            }
        );
        await ctx.SaveChangesAsync();

        var publisher = new TestPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            Options.Create(new OutboxOptions { RedisConnection = "unused", DispatchTenant = "x" }),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>(),
            sp,
            publisher
        );

        // Use internal seam to process pending once
        await dispatcher.DispatchPendingAsync(
            ctx,
            publisher,
            "x",
            CancellationToken.None
        );

        // Assert
        publisher.Sent.Count.Should().Be(2);
        (await ctx.OutboxEvents.AllAsync(e => e.Status == OutboxStatus.Dispatched))
            .Should()
            .BeTrue();
    }
}
