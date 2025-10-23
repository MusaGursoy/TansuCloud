// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TansuCloud.Database.EF;

/// <summary>
/// Primary EF Core DbContext for tenant databases.
/// Note: Vector column DDL and Citus distribution are applied via raw SQL in migrations.
/// </summary>
public class TansuDbContext(DbContextOptions<TansuDbContext> options) : DbContext(options)
{
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Detect InMemory provider; it cannot map JsonDocument directly, so apply a simple string converter.
        var provider = Database.ProviderName;
        var isInMemory =
            provider != null && provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase);

        // ValueComparer for JsonDocument - compares the raw JSON text representation
        // This MUST be set for ALL providers, not just InMemory, because EF Core requires it
        // for change tracking of JsonDocument properties.
        var jsonComparer =
            new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<JsonDocument?>(
                (a, b) =>
                    (a == null && b == null)
                    || (
                        a != null
                        && b != null
                        && a.RootElement.GetRawText() == b.RootElement.GetRawText()
                    ),
                v => v == null ? 0 : v.RootElement.GetRawText().GetHashCode(),
                v => v // Snapshot function - just return the same instance
            );

        var jsonConverter = new ValueConverter<JsonDocument?, string?>(
            v => v == null ? null : v.RootElement.GetRawText(),
            v => v == null ? null : JsonDocument.Parse(v, new JsonDocumentOptions())
        );
        // Collections
        modelBuilder.Entity<Collection>(eb =>
        {
            eb.ToTable("collections");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            eb.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // Documents (embedding column will be created in migrations as type vector)
        modelBuilder.Entity<Document>(eb =>
        {
            eb.ToTable("documents");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.CollectionId).HasColumnName("collection_id");
            var docContent = eb.Property(x => x.Content);
            docContent.HasColumnName("content");
            // ALWAYS set ValueComparer for JsonDocument (required for EF Core change tracking)
            docContent.Metadata.SetValueComparer(jsonComparer);
            if (isInMemory)
            {
                docContent.HasConversion(jsonConverter);
            }
            else
            {
                docContent.HasColumnType("jsonb"); // map JsonDocument to jsonb
            }
            eb.Property(x => x.CreatedAt).HasColumnName("created_at");
            eb.HasOne<Collection>()
                .WithMany()
                .HasForeignKey(x => x.CollectionId)
                .HasConstraintName("fk_documents_collection");
            eb.HasIndex(x => x.CollectionId).HasDatabaseName("ix_documents_collection_id");
        });

        // Outbox events
        modelBuilder.Entity<OutboxEvent>(eb =>
        {
            eb.ToTable("outbox_events");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            eb.Property(x => x.Type).HasColumnName("type");
            var outboxPayload = eb.Property(x => x.Payload);
            outboxPayload.HasColumnName("payload");
            // ALWAYS set ValueComparer for JsonDocument (required for EF Core change tracking)
            outboxPayload.Metadata.SetValueComparer(jsonComparer);
            if (isInMemory)
            {
                outboxPayload.HasConversion(jsonConverter);
            }
            else
            {
                outboxPayload.HasColumnType("jsonb");
            }
            eb.Property(x => x.Status).HasColumnName("status");
            eb.Property(x => x.Attempts).HasColumnName("attempts");
            eb.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            eb.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            eb.HasIndex(x => new { x.Status, x.NextAttemptAt })
                .HasDatabaseName("ix_outbox_status_next");
        });
    } // End of Method OnModelCreating
} // End of Class TansuDbContext

public sealed class Collection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
} // End of Class Collection

public sealed class Document
{
    public Guid Id { get; set; }
    public Guid CollectionId { get; set; }
    public JsonDocument? Content { get; set; } // jsonb in database
    public DateTimeOffset CreatedAt { get; set; }
    // Embedding vector column is created via migration as type vector(1536)
} // End of Class Document

public enum OutboxStatus : short
{
    Pending = 0,
    Dispatched = 1,
    Failed = 2,
    DeadLettered = 3
} // End of Enum OutboxStatus

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Type { get; set; } = string.Empty;
    public JsonDocument? Payload { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? IdempotencyKey { get; set; }
} // End of Class OutboxEvent
