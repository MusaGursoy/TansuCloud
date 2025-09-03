// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TansuCloud.Database.EF;

namespace TansuCloud.Database.EF.Migrations;

[DbContext(typeof(TansuDbContext))]
public class TansuDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "9.0.0");

        // collections
        modelBuilder.Entity<Collection>(eb =>
        {
            eb.ToTable("collections");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            eb.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // documents
        modelBuilder.Entity<Document>(eb =>
        {
            eb.ToTable("documents");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.CollectionId).HasColumnName("collection_id");
            eb.Property(x => x.Content).HasColumnName("content").HasColumnType("jsonb");
            eb.Property(x => x.CreatedAt).HasColumnName("created_at");
                        eb.HasOne<Collection>()
                            .WithMany()
                            .HasForeignKey(x => x.CollectionId)
                            .HasConstraintName("fk_documents_collection");
            eb.HasIndex(x => x.CollectionId).HasDatabaseName("ix_documents_collection_id");
        });

        // outbox_events
        modelBuilder.Entity<OutboxEvent>(eb =>
        {
            eb.ToTable("outbox_events");
            eb.HasKey(x => x.Id);
            eb.Property(x => x.Id).HasColumnName("id");
            eb.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            eb.Property(x => x.Type).HasColumnName("type");
            eb.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            eb.Property(x => x.Status).HasColumnName("status");
            eb.Property(x => x.Attempts).HasColumnName("attempts");
            eb.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            eb.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            eb.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_outbox_status_next");
        });
    } // End of Method BuildModel
} // End of Class TansuDbContextModelSnapshot
