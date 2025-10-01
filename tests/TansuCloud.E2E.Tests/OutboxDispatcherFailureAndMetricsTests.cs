// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;

namespace TansuCloud.E2E.Tests;

[Collection("OutboxMetricsSerial")]
public class OutboxDispatcherFailureAndMetricsTests
{
    private sealed class FlakyPublisher : IOutboxPublisher
    {
        private readonly int _failuresBeforeSuccess;
        private int _attempts;
        public int Attempts => _attempts;
        public List<string> Payloads = new();

        public FlakyPublisher(int failuresBeforeSuccess) =>
            _failuresBeforeSuccess = failuresBeforeSuccess;

        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            _attempts++;
            if (_attempts <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException("Simulated failure #" + _attempts);
            }
            lock (Payloads)
                Payloads.Add(payload);
            return Task.CompletedTask;
        }
    } // End of Class FlakyPublisher

    private sealed class AlwaysFailPublisher : IOutboxPublisher
    {
        public int Attempts;

        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            Attempts++;
            throw new InvalidOperationException("Permanent failure");
        }
    } // End of Class AlwaysFailPublisher

    private static OutboxDispatcher CreateDispatcher(
        IOutboxPublisher publisher,
        OutboxOptions? opts = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        opts ??= new OutboxOptions
        {
            RedisConnection = "unused",
            DispatchTenant = "x",
            MaxAttempts = 3
        };
        return new OutboxDispatcher(
            Options.Create(opts),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>(),
            sp,
            publisher
        );
    } // End of Method CreateDispatcher

    [Xunit.Fact(
        DisplayName = "Dispatcher retries transient failure then dispatches and updates metrics"
    )]
    public async Task Dispatcher_Retry_Then_Success()
    {
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(
            Guid.NewGuid().ToString()
        );
        await using var ctx = new TansuDbContext(dbOpts.Options);
        using var payload = JsonDocument.Parse("{\"k\":1}");
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                Type = "evt",
                Payload = payload,
                Status = OutboxStatus.Pending
            }
        );
        await ctx.SaveChangesAsync();

        var flaky = new FlakyPublisher(failuresBeforeSuccess: 1);
        var dispatcher = CreateDispatcher(flaky);

        long retried = 0,
            dispatched = 0,
            dead = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "TansuCloud.Database.Outbox")
                    l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (inst, value, tags, state) =>
            {
                switch (inst.Name)
                {
                    case "outbox.dispatched":
                        dispatched += value;
                        break;
                    case "outbox.retried":
                        retried += value;
                        break;
                    case "outbox.deadlettered":
                        dead += value;
                        break;
                }
            }
        );
        listener.Start();

        // First attempt fails -> status moves to Failed with Attempts=1 and NextAttemptAt future
        await dispatcher.DispatchPendingAsync(
            ctx,
            flaky,
            "x",
            CancellationToken.None
        );
        var ev = await ctx.OutboxEvents.SingleAsync();
        ev.Status.Should().Be(OutboxStatus.Failed);
        ev.Attempts.Should().Be(1);
        ev.NextAttemptAt.Should().NotBeNull();
        ev.NextAttemptAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddMilliseconds(-50));

        // Make it due by rewinding NextAttemptAt
        ev.NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(-10);
        await ctx.SaveChangesAsync();

        await dispatcher.DispatchPendingAsync(
            ctx,
            flaky,
            "x",
            CancellationToken.None
        );
        ev = await ctx.OutboxEvents.SingleAsync();
        ev.Status.Should().Be(OutboxStatus.Dispatched);
        flaky.Payloads.Should().HaveCount(1);

        retried.Should().Be(1, "one transient failure should increment retried counter");
        dispatched.Should().Be(1, "successful second attempt should increment dispatched counter");
        dead.Should().Be(0);
    }

    [Xunit.Fact(DisplayName = "Dispatcher dead-letters after max attempts and increments metrics")]
    public async Task Dispatcher_DeadLetters_After_MaxAttempts()
    {
        var dbOpts = new DbContextOptionsBuilder<TansuDbContext>().UseInMemoryDatabase(
            Guid.NewGuid().ToString()
        );
        await using var ctx = new TansuDbContext(dbOpts.Options);
        using var payload = JsonDocument.Parse("{\"k\":2}");
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                Type = "evt",
                Payload = payload,
                Status = OutboxStatus.Pending
            }
        );
        await ctx.SaveChangesAsync();

        var failing = new AlwaysFailPublisher();
        var dispatcher = CreateDispatcher(
            failing,
            new OutboxOptions
            {
                RedisConnection = "unused",
                DispatchTenant = "x",
                MaxAttempts = 2
            }
        );
        long retried = 0,
            dispatched = 0,
            dead = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "TansuCloud.Database.Outbox")
                    l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (inst, value, tags, state) =>
            {
                switch (inst.Name)
                {
                    case "outbox.dispatched":
                        dispatched += value;
                        break;
                    case "outbox.retried":
                        retried += value;
                        break;
                    case "outbox.deadlettered":
                        dead += value;
                        break;
                }
            }
        );
        listener.Start();
        // Run until dead-letter (MaxAttempts=2 means 2 failures then move to DeadLettered on 2nd attempt)
        await dispatcher.DispatchPendingAsync(
            ctx,
            failing,
            "x",
            CancellationToken.None
        ); // attempt 1 -> Failed
        var ev = await ctx.OutboxEvents.SingleAsync();
        ev.Status.Should().Be(OutboxStatus.Failed);
        ev.Attempts.Should().Be(1);
        ev.NextAttemptAt.Should().NotBeNull();
        ev.NextAttemptAt = DateTimeOffset.UtcNow.AddMilliseconds(-5);
        await ctx.SaveChangesAsync();

        await dispatcher.DispatchPendingAsync(
            ctx,
            failing,
            "x",
            CancellationToken.None
        ); // attempt 2 -> DeadLettered
        ev = await ctx.OutboxEvents.SingleAsync();
        ev.Status.Should().Be(OutboxStatus.DeadLettered);
        ev.Attempts.Should().Be(2);
        retried.Should().Be(1, "first failed attempt counts as retried");
        dead.Should().Be(1, "second failure reaches max attempts and dead-letters");
        dispatched.Should().Be(0);
    }
}
