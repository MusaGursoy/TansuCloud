// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;
using TansuCloud.Database.Services;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Full E2E style test: spins the real OutboxDispatcher ExecuteAsync loop (short poll) against an in-memory EF context
/// by faking tenant resolution and publishes to a real Redis (if REDIS_URL provided). Verifies publish + status update.
/// Skips silently if REDIS_URL not set to avoid CI flakiness.
/// </summary>
public class OutboxDispatcherFullE2ERedisTests
{
    private sealed class SingleTenantFactory : ITenantDbContextFactory
    {
        private readonly DbContextOptions<TansuDbContext> _options;

        public SingleTenantFactory(DbContextOptions<TansuDbContext> options) => _options = options;

        // Always return a fresh context instance backed by the same in-memory database root.
        public Task<TansuDbContext> CreateAsync(HttpContext httpContext, CancellationToken ct) =>
            Task.FromResult(new TansuDbContext(_options));

        public Task<TansuDbContext> CreateAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(new TansuDbContext(_options));
    } // End of Class SingleTenantFactory

    [RedisFact(DisplayName = "Full dispatcher loop publishes to Redis and marks dispatched (E2E)")]
    public async Task Full_Dispatcher_Redis_Publish()
    {
        // Default to localhost:6379 for E2E tests when Redis is exposed by docker-compose
        var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "127.0.0.1:6379";

        // EF in-memory context seeded with two events. Use a shared root so multiple contexts see the same store.
        var dbName = Guid.NewGuid().ToString();
        var dbRoot = new InMemoryDatabaseRoot();
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>()
            .UseInMemoryDatabase(dbName, dbRoot)
            .Options;
        await using var seedCtx = new TansuDbContext(dbOpts);
        using var p1 = JsonDocument.Parse("{\"name\":\"alpha\"}");
        using var p2 = JsonDocument.Parse("{\"name\":\"beta\"}");
        seedCtx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                Type = "demo.created",
                Payload = p1,
                Status = OutboxStatus.Pending
            }
        );
        seedCtx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                Type = "demo.created",
                Payload = p2,
                Status = OutboxStatus.Pending
            }
        );
        await seedCtx.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFilter(_ => true));
    services.AddSingleton<ITenantDbContextFactory>(new SingleTenantFactory(dbOpts));
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        var opts = new OutboxOptions
        {
            RedisConnection = redisUrl,
            DispatchTenant = "tenant-x",
            PollSeconds = 1,
            BatchSize = 10
        };
        var sp = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            Options.Create(opts),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>(),
            sp
        );

        // Redis subscription to capture actual published messages from dispatcher
        var mux = await ConnectionMultiplexer.ConnectAsync(redisUrl);
        var sub = mux.GetSubscriber();
        var received = new List<string>();
        await sub.SubscribeAsync(
            RedisChannel.Literal(opts.Channel),
            (_, value) =>
            {
                lock (received)
                    received.Add(value!);
            }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Run dispatcher loop until it processes (we rely on poll 1s)
        var runTask = dispatcher.StartAsync(cts.Token); // BackgroundService extension

        // Wait until received both or timeout
        var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < waitUntil)
        {
            lock (received)
            {
                if (received.Count >= 2)
                    break;
            }
            await Task.Delay(100);
        }
        cts.Cancel();
        try
        {
            await runTask;
        }
        catch { }

        int finalCount;
        lock (received)
            finalCount = received.Count;
        finalCount.Should().BeGreaterThanOrEqualTo(2);
        // Use a fresh context instance for verification to avoid disposed instance issues.
        await using var verifyCtx = new TansuDbContext(dbOpts);
        (await verifyCtx.OutboxEvents.CountAsync(e => e.Status == OutboxStatus.Dispatched))
            .Should()
            .Be(2);
    }
}
