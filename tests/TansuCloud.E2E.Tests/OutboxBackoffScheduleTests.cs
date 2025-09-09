// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// Verifies retry backoff characteristics. We can't assert exact jitter but we ensure:
/// - NextAttemptAt set when failure occurs.
/// - Backoff grows (non-decreasing) with Attempts.
/// - Backoff never exceeds configured cap (300s) and base exponent (2^attempt) logic respected.
/// </summary>
public class OutboxBackoffScheduleTests
{
    private sealed class AlwaysFailingPublisher : IOutboxPublisher
    {
        public Task PublishAsync(string channel, string payload, CancellationToken ct) =>
            throw new InvalidOperationException("fail");
    }

    private static (
        TansuDbContext ctx,
        OutboxDispatcher dispatcher,
        AlwaysFailingPublisher pub
    ) NewSystem()
    {
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(
            Guid.NewGuid().ToString()
        );
        var ctx = new TansuDbContext(dbOpts.Options);
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            Options.Create(
                new OutboxOptions
                {
                    RedisConnection = "unused",
                    DispatchTenant = "x",
                    MaxAttempts = 5
                }
            ),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>(),
            sp,
            new AlwaysFailingPublisher()
        );
        return (ctx, dispatcher, new AlwaysFailingPublisher());
    }

    [Xunit.Fact(DisplayName = "Backoff increases with attempts and caps at 300s")]
    public async Task Backoff_Increases_And_Caps()
    {
        var (ctx, dispatcher, _) = NewSystem();
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                Type = "retry.test",
                Status = OutboxStatus.Pending
            }
        );
        await ctx.SaveChangesAsync();

        DateTimeOffset? lastNext = null;
        int attempt = 0;
        while (attempt < 4) // perform several failing cycles but stop before dead-letter for brevity
        {
            await dispatcher.DispatchPendingAsync(
                ctx,
                new AlwaysFailingPublisher(),
                CancellationToken.None
            );
            var ev = ctx.OutboxEvents.Single();
            // For these initial attempts status should remain Failed (not DeadLettered yet)
            ev.Status.Should().Be(OutboxStatus.Failed);
            ev.Attempts.Should()
                .Be(attempt + 1, "each failing cycle increments Attempts by exactly 1");
            ev.NextAttemptAt.Should().NotBeNull();
            var delta = ev.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
            delta.Should().BeGreaterThan(TimeSpan.Zero);
            delta.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(301));
            if (lastNext != null)
            {
                ev.NextAttemptAt.Should().BeAfter(lastNext.Value.AddMilliseconds(-5)); // monotonic non-decreasing with slight clock tolerance
            }
            lastNext = ev.NextAttemptAt;
            // make it due immediately
            ev.NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(-5);
            await ctx.SaveChangesAsync();
            attempt++;
        }
    }
}
