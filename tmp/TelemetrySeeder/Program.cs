using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : "telemetry.db";
Console.WriteLine($"Initializing telemetry database at {dbPath}...");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".");

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    ForeignKeys = true
}.ToString();

await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync();

await using (var createCmd = connection.CreateCommand())
{
    createCmd.CommandText = @"
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS telemetry_envelopes (
            Id TEXT PRIMARY KEY,
            ReceivedAtUtc TEXT NOT NULL,
            Host TEXT NOT NULL,
            Environment TEXT NOT NULL,
            Service TEXT NOT NULL,
            SeverityThreshold TEXT NOT NULL,
            WindowMinutes INTEGER NOT NULL,
            MaxItems INTEGER NOT NULL,
            ItemCount INTEGER NOT NULL,
            AcknowledgedAtUtc TEXT NULL,
            DeletedAtUtc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_envelopes_acknowledged_at ON telemetry_envelopes (AcknowledgedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_envelopes_deleted_at ON telemetry_envelopes (DeletedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_envelopes_environment ON telemetry_envelopes (Environment);
        CREATE INDEX IF NOT EXISTS IX_envelopes_received_at ON telemetry_envelopes (ReceivedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_envelopes_service ON telemetry_envelopes (Service);

        CREATE TABLE IF NOT EXISTS telemetry_items (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            EnvelopeId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            TimestampUtc TEXT NOT NULL,
            Level TEXT NOT NULL,
            Message TEXT NOT NULL,
            TemplateHash TEXT NOT NULL,
            Exception TEXT NULL,
            Service TEXT NULL,
            Environment TEXT NULL,
            TenantHash TEXT NULL,
            CorrelationId TEXT NULL,
            TraceId TEXT NULL,
            SpanId TEXT NULL,
            Category TEXT NULL,
            EventId INTEGER NULL,
            Count INTEGER NOT NULL,
            PropertiesJson TEXT NULL,
            FOREIGN KEY (EnvelopeId) REFERENCES telemetry_envelopes (Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_items_envelope_id ON telemetry_items (EnvelopeId);
        CREATE INDEX IF NOT EXISTS IX_items_level ON telemetry_items (Level);
        CREATE INDEX IF NOT EXISTS IX_items_timestamp ON telemetry_items (TimestampUtc);
    ";
    await createCmd.ExecuteNonQueryAsync();
}

// Seed sample data if empty
await using (var checkCmd = connection.CreateCommand())
{
    checkCmd.CommandText = "SELECT COUNT(*) FROM telemetry_envelopes";
    var count = (long)await checkCmd.ExecuteScalarAsync();
    if (count == 0)
    {
        var envelopeId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        await using (var insertEnvelope = connection.CreateCommand())
        {
            insertEnvelope.CommandText = @"
                INSERT INTO telemetry_envelopes (
                    Id, ReceivedAtUtc, Host, Environment, Service, SeverityThreshold,
                    WindowMinutes, MaxItems, ItemCount, AcknowledgedAtUtc, DeletedAtUtc)
                VALUES ($id, $received, $host, $env, $service, $severity, 60, 500, 2, NULL, NULL);
            ";
            insertEnvelope.Parameters.AddWithValue("$id", envelopeId);
            insertEnvelope.Parameters.AddWithValue("$received", now.ToString("o", CultureInfo.InvariantCulture));
            insertEnvelope.Parameters.AddWithValue("$host", "gateway-dev");
            insertEnvelope.Parameters.AddWithValue("$env", "Development");
            insertEnvelope.Parameters.AddWithValue("$service", "tansu.gateway");
            insertEnvelope.Parameters.AddWithValue("$severity", "Warning");
            await insertEnvelope.ExecuteNonQueryAsync();
        }

        await using (var insertItem = connection.CreateCommand())
        {
            insertItem.CommandText = @"
                INSERT INTO telemetry_items (
                    EnvelopeId, Kind, TimestampUtc, Level, Message, TemplateHash,
                    Exception, Service, Environment, TenantHash, CorrelationId,
                    TraceId, SpanId, Category, EventId, Count, PropertiesJson)
                VALUES ($envelopeId, 'log', $timestamp, 'Warning', 'Sample warning entry',
                        'template-001', NULL, 'tansu.gateway', 'Development', NULL,
                        'corr-dev', 'trace-001', 'span-001', 'system', 1001, 1, NULL);
            ";
            insertItem.Parameters.AddWithValue("$envelopeId", envelopeId);
            insertItem.Parameters.AddWithValue("$timestamp", now.ToString("o", CultureInfo.InvariantCulture));
            await insertItem.ExecuteNonQueryAsync();
        }

        await using (var insertItem = connection.CreateCommand())
        {
            insertItem.CommandText = @"
                INSERT INTO telemetry_items (
                    EnvelopeId, Kind, TimestampUtc, Level, Message, TemplateHash,
                    Exception, Service, Environment, TenantHash, CorrelationId,
                    TraceId, SpanId, Category, EventId, Count, PropertiesJson)
                VALUES ($envelopeId, 'log', $timestamp, 'Error', 'Sample error entry',
                        'template-002', 'Stack trace sample', 'tansu.gateway', 'Development', NULL,
                        'corr-dev', 'trace-001', 'span-002', 'system', 1002, 1, NULL);
            ";
            insertItem.Parameters.AddWithValue("$envelopeId", envelopeId);
            insertItem.Parameters.AddWithValue("$timestamp", now.AddSeconds(-30).ToString("o", CultureInfo.InvariantCulture));
            await insertItem.ExecuteNonQueryAsync();
        }

        Console.WriteLine("Seeded sample telemetry envelope.");
    }
    else
    {
        Console.WriteLine("Existing telemetry envelopes detected. Skipping seed.");
    }
}

Console.WriteLine("Telemetry database ready.");
