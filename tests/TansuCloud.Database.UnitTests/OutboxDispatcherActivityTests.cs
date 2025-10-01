// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Database.EF;
using TansuCloud.Database.Outbox;
using TansuCloud.Observability;
using Xunit;

namespace TansuCloud.Database.UnitTests;

public class OutboxDispatcherActivityTests
{
    [Fact]
    public async Task DispatchPendingAsync_EmitsActivityWithTenantTag()
    {
        var dbOptions = new DbContextOptionsBuilder<TansuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var ctx = new TansuDbContext(dbOptions);
        ctx.OutboxEvents.Add(
            new OutboxEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                Type = "test",
                Status = OutboxStatus.Pending
            }
        );
        await ctx.SaveChangesAsync();

        var publisher = new CapturingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            Options.Create(
                new OutboxOptions
                {
                    RedisConnection = "unused",
                    DispatchTenant = "activity",
                    MaxAttempts = 3
                }
            ),
            provider.GetRequiredService<ILogger<OutboxDispatcher>>(),
            provider,
            publisher
        );

        const string backgroundSourceName = "TansuCloud.Background";
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source?.Name == backgroundSourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.Source.Name == backgroundSourceName)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await dispatcher.DispatchPendingAsync(ctx, publisher, "activity-tenant", CancellationToken.None);

        activities.Should().Contain(a => a.DisplayName == "OutboxDispatch");
        var dispatchActivity = activities.First(a => a.DisplayName == "OutboxDispatch");
        dispatchActivity.GetTagItem(TelemetryConstants.Tenant).Should().Be("activity-tenant");
        dispatchActivity.GetTagItem("outbox.events.dispatched").Should().Be(1);

        activities.Should().Contain(a => a.DisplayName == "OutboxDispatch.Event");
        publisher.Payloads.Should().HaveCount(1);
    }

    private sealed class CapturingPublisher : IOutboxPublisher
    {
        public List<string> Payloads { get; } = new();

        public Task PublishAsync(string channel, string payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
    }
} // End of Class OutboxDispatcherActivityTests
