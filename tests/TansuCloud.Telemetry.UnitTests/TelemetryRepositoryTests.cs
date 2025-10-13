// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Linq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.Data.Records;

namespace TansuCloud.Telemetry.UnitTests;

public sealed class TelemetryRepositoryTests
{
    [Fact]
    public async Task ExportEnvelopesAsync_ShouldRespectLimitAndOrdering()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(connection)
            .Options;

        using var dbContext = new TelemetryDbContext(options);
        dbContext.Database.EnsureCreated();

        var olderEnvelope = CreateActiveEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: null,
            deletedAtUtc: null,
            service: "gateway"
        );
    olderEnvelope.Items.Add(CreateActiveItem(olderEnvelope, new DateTime(2025, 10, 1, 7, 45, 0, DateTimeKind.Utc)));
    olderEnvelope.ItemCount = olderEnvelope.Items.Count;

        var recentEnvelope = CreateActiveEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: DateTime.UtcNow,
            deletedAtUtc: null,
            service: "gateway"
        );
    recentEnvelope.Items.Add(CreateActiveItem(recentEnvelope, new DateTime(2025, 10, 1, 8, 55, 0, DateTimeKind.Utc)));
    recentEnvelope.ItemCount = recentEnvelope.Items.Count;

        var deletedEnvelope = CreateArchivedEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 9, 30, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: null,
            deletedAtUtc: DateTime.UtcNow,
            service: "gateway"
        );
    deletedEnvelope.Items.Add(CreateArchivedItem(deletedEnvelope, new DateTime(2025, 10, 1, 9, 25, 0, DateTimeKind.Utc)));
    deletedEnvelope.ItemCount = deletedEnvelope.Items.Count;

        dbContext.ActiveEnvelopes.AddRange(olderEnvelope, recentEnvelope);
        dbContext.ArchivedEnvelopes.Add(deletedEnvelope);
        dbContext.SaveChanges();

        var repository = new TelemetryRepository(dbContext, NullLogger<TelemetryRepository>.Instance);
        var query = new TelemetryEnvelopeQuery
        {
            IncludeAcknowledged = true,
            IncludeDeleted = true
        };

        var results = await repository.ExportEnvelopesAsync(
            query,
            maxItems: 2,
            includeItems: true,
            CancellationToken.None
        );

        results.Should().HaveCount(2);
        results.Select(e => e.Id)
            .Should()
            .ContainInOrder(deletedEnvelope.Id, recentEnvelope.Id);
        results.All(e => e.Items.Count == 1).Should().BeTrue();
    } // End of Method ExportEnvelopesAsync_ShouldRespectLimitAndOrdering

    [Fact]
    public async Task ExportEnvelopesAsync_ShouldApplyServiceAndDeletionFilters()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(connection)
            .Options;

        using var dbContext = new TelemetryDbContext(options);
        dbContext.Database.EnsureCreated();

        var matchingEnvelope = CreateActiveEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: null,
            deletedAtUtc: null,
            service: "api"
        );
    matchingEnvelope.Items.Add(CreateActiveItem(matchingEnvelope, new DateTime(2025, 10, 1, 9, 59, 0, DateTimeKind.Utc)));
    matchingEnvelope.ItemCount = matchingEnvelope.Items.Count;

        var filteredByService = CreateActiveEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 10, 5, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: null,
            deletedAtUtc: null,
            service: "gateway"
        );

        var filteredByDeletion = CreateArchivedEnvelope(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 10, 10, 0, DateTimeKind.Utc),
            acknowledgedAtUtc: null,
            deletedAtUtc: DateTime.UtcNow,
            service: "api"
        );
        filteredByDeletion.Items.Add(CreateArchivedItem(filteredByDeletion, new DateTime(2025, 10, 1, 10, 9, 0, DateTimeKind.Utc)));
        filteredByDeletion.ItemCount = filteredByDeletion.Items.Count;

        dbContext.ActiveEnvelopes.AddRange(matchingEnvelope, filteredByService);
        dbContext.ArchivedEnvelopes.Add(filteredByDeletion);
        dbContext.SaveChanges();

        var repository = new TelemetryRepository(dbContext, NullLogger<TelemetryRepository>.Instance);
        var query = new TelemetryEnvelopeQuery
        {
            Service = "api",
            IncludeDeleted = false
        };

        var results = await repository.ExportEnvelopesAsync(
            query,
            maxItems: 5,
            includeItems: false,
            CancellationToken.None
        );

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(matchingEnvelope.Id);
    } // End of Method ExportEnvelopesAsync_ShouldApplyServiceAndDeletionFilters

    [Fact]
    public async Task ExportEnvelopesAsync_ShouldThrow_WhenMaxItemsNotPositive()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(connection)
            .Options;

        using var dbContext = new TelemetryDbContext(options);
        dbContext.Database.EnsureCreated();

        var repository = new TelemetryRepository(dbContext, NullLogger<TelemetryRepository>.Instance);

        var query = new TelemetryEnvelopeQuery();

        var act = async () =>
            await repository.ExportEnvelopesAsync(query, 0, includeItems: false, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    } // End of Method ExportEnvelopesAsync_ShouldThrow_WhenMaxItemsNotPositive

    private static TelemetryActiveEnvelopeRecord CreateActiveEnvelope(
        Guid id,
        DateTime receivedAtUtc,
        DateTime? acknowledgedAtUtc,
        DateTime? deletedAtUtc,
        string service
    )
    {
        return new TelemetryActiveEnvelopeRecord
        {
            Id = id,
            ReceivedAtUtc = receivedAtUtc,
            Host = "host",
            Environment = "Production",
            Service = service,
            SeverityThreshold = "Warning",
            WindowMinutes = 60,
            MaxItems = 500,
            ItemCount = 0,
            AcknowledgedAtUtc = acknowledgedAtUtc,
            DeletedAtUtc = deletedAtUtc
        };
    } // End of Method CreateActiveEnvelope

    private static TelemetryActiveItemRecord CreateActiveItem(
        TelemetryActiveEnvelopeRecord envelope,
        DateTime timestampUtc
    )
    {
        return new TelemetryActiveItemRecord
        {
            EnvelopeId = envelope.Id,
            Envelope = envelope,
            Kind = "log",
            TimestampUtc = timestampUtc,
            Level = "Warning",
            Message = "Test message",
            TemplateHash = "hash",
            Count = 1
        };
    } // End of Method CreateActiveItem

    private static TelemetryArchivedEnvelopeRecord CreateArchivedEnvelope(
        Guid id,
        DateTime receivedAtUtc,
        DateTime? acknowledgedAtUtc,
        DateTime? deletedAtUtc,
        string service
    )
    {
        return new TelemetryArchivedEnvelopeRecord
        {
            Id = id,
            ReceivedAtUtc = receivedAtUtc,
            Host = "host",
            Environment = "Production",
            Service = service,
            SeverityThreshold = "Warning",
            WindowMinutes = 60,
            MaxItems = 500,
            ItemCount = 0,
            AcknowledgedAtUtc = acknowledgedAtUtc,
            DeletedAtUtc = deletedAtUtc
        };
    } // End of Method CreateArchivedEnvelope

    private static TelemetryArchivedItemRecord CreateArchivedItem(
        TelemetryArchivedEnvelopeRecord envelope,
        DateTime timestampUtc
    )
    {
        return new TelemetryArchivedItemRecord
        {
            EnvelopeId = envelope.Id,
            Envelope = envelope,
            Kind = "log",
            TimestampUtc = timestampUtc,
            Level = "Warning",
            Message = "Test message",
            TemplateHash = "hash",
            Count = 1
        };
    } // End of Method CreateArchivedItem
} // End of Class TelemetryRepositoryTests
