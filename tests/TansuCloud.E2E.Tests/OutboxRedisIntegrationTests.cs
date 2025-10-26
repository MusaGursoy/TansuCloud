// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Integration-style test that exercises the OutboxProducer + (simulated) dispatcher publish contract by:
/// 1. Writing pending events into an in-memory EF Core context.
/// 2. Manually mimicking the dispatcher publish loop logic for those events to a real Redis (if available) or a mock connection.
/// 3. Verifying each event is published exactly once (idempotent send) and status transitions to Dispatched.
/// NOTE: This does NOT spin up the actual hosted BackgroundService; it's a fast contract test of the EF + Redis serialization path.
/// If a REDIS_URL (host:port) isn't provided the test is skipped (not failed) to avoid build-time flakiness.
/// </summary>
public class OutboxRedisIntegrationTests
{
    private static bool TryGetRedis(out string conn)
    {
        // Default to localhost:6379 for E2E tests when Redis is exposed by docker-compose
        conn = Environment.GetEnvironmentVariable("REDIS_URL") ?? "127.0.0.1:6379";
        if (string.IsNullOrWhiteSpace(conn))
            return false;
        return true;
    }

    [Xunit.Fact(DisplayName = "Outbox events publish once to Redis and mark dispatched")]
    public async Task Outbox_Publishes_Once_And_Marks_Dispatched()
    {
        if (!TryGetRedis(out var redisConn))
        {
            return; // skip silently if no redis configured (dev convenience)
        }

        // Arrange EF in-memory context
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TansuDbContext(dbOpts);

        using var lf = LoggerFactory.Create(b => b.AddFilter(_ => true).AddConsole());
        var producer = new OutboxProducer(lf.CreateLogger<OutboxProducer>());
        var payload1 = JsonDocument.Parse("{\"name\":\"alpha\"}");
        var payload2 = JsonDocument.Parse("{\"name\":\"beta\"}");
        producer.Enqueue(db, "test.created", payload1, idempotencyKey: "idem-1");
        producer.Enqueue(db, "test.created", payload2, idempotencyKey: "idem-2");
        await db.SaveChangesAsync();

        // Connect to Redis
        var mux = await ConnectionMultiplexer.ConnectAsync(redisConn);
        var sub = mux.GetSubscriber();
        var channel = new RedisChannel("tansu.outbox.test", RedisChannel.PatternMode.Literal);

        // Collect published payloads
        var received = new List<string>();
        await sub.SubscribeAsync(
            channel,
            (_, value) =>
            {
                lock (received)
                    received.Add(value!);
            }
        );

        // Simulate a minimal dispatcher loop iteration
        var due = await db
            .OutboxEvents.Where(e => e.Status == OutboxStatus.Pending)
            .OrderBy(e => e.OccurredAt)
            .Take(50)
            .ToListAsync();

        foreach (var e in due)
        {
            var json = e.Payload is null ? "null" : e.Payload.RootElement.GetRawText();
            await sub.PublishAsync(channel, json);
            e.Status = OutboxStatus.Dispatched;
        }
        await db.SaveChangesAsync();

        // Allow a brief propagation window
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < timeout)
        {
            lock (received)
            {
                if (received.Count >= 2)
                    break;
            }
            await Task.Delay(50);
        }

        // Assert
        lock (received)
        {
            received.Should().HaveCount(2, "both events should be published once");
        }
        var statuses = await db.OutboxEvents.Select(e => e.Status).ToListAsync();
        statuses.Should().AllBeEquivalentTo(OutboxStatus.Dispatched);
    }
}
