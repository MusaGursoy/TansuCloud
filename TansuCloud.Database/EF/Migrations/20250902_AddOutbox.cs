// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore.Migrations;

namespace TansuCloud.Database.EF.Migrations;

[Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(EF.TansuDbContext))]
[Migration("20250902_AddOutbox")]
public partial class _20250902_AddOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS outbox_events (
                id uuid PRIMARY KEY,
                occurred_at timestamptz NOT NULL DEFAULT now(),
                type text NOT NULL,
                payload jsonb NULL,
                status smallint NOT NULL DEFAULT 0,
                attempts int NOT NULL DEFAULT 0,
                next_attempt_at timestamptz NULL,
                idempotency_key text NULL
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_status_next ON outbox_events(status, next_attempt_at);
        ");

        // Unique index on idempotency_key when not null (partial index)
        migrationBuilder.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ux_outbox_idem_notnull'
                ) THEN
                    EXECUTE 'CREATE UNIQUE INDEX ux_outbox_idem_notnull ON outbox_events(idempotency_key) WHERE idempotency_key IS NOT NULL';
                END IF;
            END$$;
        ");

        // Citus distribution: choose reference or distribute by id
        migrationBuilder.Sql(@"
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus') THEN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_dist_partition WHERE logicalrelid = 'public.outbox_events'::regclass
                ) THEN
                    -- Outbox events are small and global; reference table is often sufficient
                    PERFORM create_reference_table('outbox_events');
                END IF;
            END IF;
        END$$;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS outbox_events;");
    }
} // End of Class _20250902_AddOutbox
