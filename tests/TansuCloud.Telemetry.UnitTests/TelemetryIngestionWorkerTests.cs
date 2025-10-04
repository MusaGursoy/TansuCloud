// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.Ingestion;
using TansuCloud.Telemetry.Ingestion.Models;
using TansuCloud.Telemetry.Metrics;

namespace TansuCloud.Telemetry.UnitTests;

public sealed class TelemetryIngestionWorkerTests
{
    [Fact]
    public async Task IngestionWorker_ShouldDrainQueueUnderBurstLoad()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<TelemetryRepository>();

        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        }

        using var metrics = new TelemetryMetrics();
        var queueOptions = Options.Create(
            new TelemetryIngestionOptions
            {
                ApiKey = "0123456789abcdef",
                QueueCapacity = 32,
                EnqueueTimeout = TimeSpan.FromSeconds(1)
            }
        );

        await using var queue = new TelemetryIngestionQueue(
            queueOptions,
            metrics,
            NullLogger<TelemetryIngestionQueue>.Instance
        );

        var worker = new TelemetryIngestionWorker(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            NullLogger<TelemetryIngestionWorker>.Instance
        );

        await worker.StartAsync(CancellationToken.None);

        var totalEnvelopes = 64;
        for (var i = 0; i < totalEnvelopes; i++)
        {
            var envelope = CreateEnvelope(i);
            var enqueued = await queue.TryEnqueueAsync(new TelemetryWorkItem(envelope), CancellationToken.None);
            enqueued.Should().BeTrue();
        }

        await WaitForConditionAsync(
            async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
                var persistedEnvelopes = await dbContext.Envelopes.CountAsync();
                return persistedEnvelopes >= totalEnvelopes;
            },
            TimeSpan.FromSeconds(5)
        );

        await WaitForConditionAsync(
            () => Task.FromResult(queue.GetDepth() == 0),
            TimeSpan.FromSeconds(2)
        );

        await using (var verificationScope = provider.CreateAsyncScope())
        {
            var dbContext = verificationScope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
            var persistedEnvelopes = await dbContext.Envelopes.CountAsync();
            persistedEnvelopes.Should().Be(totalEnvelopes);

            var persistedItems = await dbContext.Items.CountAsync();
            persistedItems.Should().Be(totalEnvelopes * 2);
        }

        await worker.StopAsync(CancellationToken.None);
    } // End of Method IngestionWorker_ShouldDrainQueueUnderBurstLoad

    private static TelemetryEnvelopeEntity CreateEnvelope(int seed)
    {
        var envelopeId = Guid.NewGuid();
        var receivedAt = DateTime.UtcNow.AddMilliseconds(-seed);

        var envelope = new TelemetryEnvelopeEntity
        {
            Id = envelopeId,
            ReceivedAtUtc = receivedAt,
            Host = $"host-{seed % 4}",
            Environment = seed % 2 == 0 ? "Production" : "Development",
            Service = seed % 3 == 0 ? "tansu.dashboard" : "tansu.identity",
            SeverityThreshold = "Warning",
            WindowMinutes = 60,
            MaxItems = 500,
            ItemCount = 2
        };

        envelope.Items.Add(
            new TelemetryItemEntity
            {
                EnvelopeId = envelopeId,
                Envelope = envelope,
                Kind = "log",
                TimestampUtc = receivedAt.AddSeconds(-1),
                Level = "Error",
                Message = $"First test message {seed}",
                TemplateHash = "template-001",
                Count = 1
            }
        );

        envelope.Items.Add(
            new TelemetryItemEntity
            {
                EnvelopeId = envelopeId,
                Envelope = envelope,
                Kind = "log",
                TimestampUtc = receivedAt.AddSeconds(-2),
                Level = "Warning",
                Message = $"Second test message {seed}",
                TemplateHash = "template-002",
                Count = 1
            }
        );

        return envelope;
    } // End of Method CreateEnvelope

    private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    } // End of Method WaitForConditionAsync
} // End of Class TelemetryIngestionWorkerTests
