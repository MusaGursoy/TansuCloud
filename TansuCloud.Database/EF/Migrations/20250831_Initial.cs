// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TansuCloud.Database.EF.Migrations;

[Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(EF.TansuDbContext))]
[Migration("20250831_Initial")]
public partial class _20250831_Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Ensure extensions are available when installed on the server (idempotent, guarded). Some extensions cannot be created inside a transaction.
        migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_trgm') THEN
        EXECUTE 'CREATE EXTENSION IF NOT EXISTS pg_trgm';
    END IF;
END$$;
", suppressTransaction: true);
        migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'citus') THEN
        EXECUTE 'CREATE EXTENSION IF NOT EXISTS citus';
    END IF;
END$$;
", suppressTransaction: true);
        migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
        EXECUTE 'CREATE EXTENSION IF NOT EXISTS vector';
    END IF;
END$$;
", suppressTransaction: true);

        // Base table: collections first
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS collections (
                id uuid PRIMARY KEY,
                name varchar(200) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
        ");

        // Citus topology: make collections a reference table (idempotent)
        migrationBuilder.Sql(@"
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus') THEN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_dist_partition WHERE logicalrelid = 'public.collections'::regclass
                ) THEN
                    PERFORM create_reference_table('collections');
                END IF;
            END IF;
        END$$;
        ");

        // Now create documents table (no FK here to keep Citus-compatible); add FK later only if not using Citus
        // Note: embedding column depends on pgvector extension; add it conditionally below.
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS documents (
                id uuid PRIMARY KEY,
                collection_id uuid NOT NULL,
                content jsonb NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_documents_collection_id ON documents(collection_id);
        ");

        // Conditionally add the vector embedding column and its HNSW index only when pgvector is available
        migrationBuilder.Sql(@"
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')
               OR EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
                -- Ensure extension is created if available
                BEGIN
                    EXECUTE 'CREATE EXTENSION IF NOT EXISTS vector';
                EXCEPTION WHEN others THEN
                    NULL; -- ignore if cannot be created in this context
                END;
                -- Add column if missing
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'documents' AND column_name = 'embedding'
                ) THEN
                    EXECUTE 'ALTER TABLE documents ADD COLUMN embedding vector(1536) NULL';
                END IF;
                -- Create HNSW index if column exists
                BEGIN
                    EXECUTE 'CREATE INDEX IF NOT EXISTS ix_documents_embedding_hnsw ON documents USING hnsw (embedding vector_cosine_ops)';
                EXCEPTION WHEN others THEN
                    NULL;
                END;
            END IF;
        END$$;
        ");

        // If not Citus, enforce FK at the database level
        migrationBuilder.Sql(@"
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus') THEN
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_type = 'FOREIGN KEY' AND table_name = 'documents' AND constraint_name = 'fk_documents_collection_id') THEN
                        EXECUTE 'ALTER TABLE documents ADD CONSTRAINT fk_documents_collection_id FOREIGN KEY (collection_id) REFERENCES collections(id) ON DELETE CASCADE';
                    END IF;
                EXCEPTION WHEN others THEN
                    -- ignore if already present or not applicable
                    NULL;
                END;
            END IF;
        END$$;
        ");

    // Citus distribution: distribute documents by id (idempotent) if Citus is present
        migrationBuilder.Sql(@"
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus') THEN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_dist_partition WHERE logicalrelid = 'public.documents'::regclass
                ) THEN
            PERFORM create_distributed_table('documents', 'id', 'hash');
                END IF;
            END IF;
        END$$;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    migrationBuilder.Sql("DROP INDEX IF EXISTS ix_documents_embedding_hnsw;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS documents;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS collections;");
    }
} // End of Class _20250831_Initial
