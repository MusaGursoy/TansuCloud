// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Text.Json;
using Npgsql;

namespace TansuCloud.Database.Services;

/// <summary>
/// Service for managing schema versions across all databases.
/// Provides methods to track, validate, and report schema versions.
/// </summary>
public sealed class SchemaVersionService
{
    private readonly ILogger<SchemaVersionService> _logger;
    private readonly string _connectionString;

    public SchemaVersionService(IConfiguration configuration, ILogger<SchemaVersionService> logger)
    {
        _logger = logger;

        // Use the default postgres database for schema version operations
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not configured");
    } // End of Constructor SchemaVersionService

    /// <summary>
    /// Expected schema versions for system databases.
    /// </summary>
    public static class ExpectedVersions
    {
        public const string Identity = "1.0.0";
        public const string Audit = "1.0.0";
        public const string Tenant = "1.0.0";
    } // End of Class ExpectedVersions

    /// <summary>
    /// Ensures the __SchemaVersion table exists in the specified database.
    /// </summary>
    public async Task EnsureSchemaVersionTableAsync(
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        var connString = GetConnectionStringForDatabase(databaseName);

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(cancellationToken);

        var createTableSql = """
            CREATE TABLE IF NOT EXISTS public.__SchemaVersion (
                id UUID PRIMARY KEY,
                database_name VARCHAR(256) NOT NULL,
                version VARCHAR(32) NOT NULL,
                applied_at TIMESTAMPTZ NOT NULL,
                description TEXT,
                metadata JSONB
            );

            CREATE INDEX IF NOT EXISTS ix_schemaversion_database_name 
                ON public.__SchemaVersion(database_name);

            CREATE INDEX IF NOT EXISTS ix_schemaversion_applied_at 
                ON public.__SchemaVersion(applied_at);
            """;

        await using var cmd = new NpgsqlCommand(createTableSql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Ensured __SchemaVersion table exists in database {DatabaseName}",
            databaseName
        );
    } // End of Method EnsureSchemaVersionTableAsync

    /// <summary>
    /// Records a new schema version for a database.
    /// </summary>
    public async Task RecordSchemaVersionAsync(
        string databaseName,
        string version,
        string? description = null,
        object? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        await EnsureSchemaVersionTableAsync(databaseName, cancellationToken);

        var connString = GetConnectionStringForDatabase(databaseName);

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(cancellationToken);

        var insertSql = """
            INSERT INTO public.__SchemaVersion (id, database_name, version, applied_at, description, metadata)
            VALUES (@id, @database_name, @version, @applied_at, @description, @metadata::jsonb)
            """;

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("database_name", databaseName);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("applied_at", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "metadata",
            metadata != null ? JsonSerializer.Serialize(metadata) : DBNull.Value
        );

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Recorded schema version {Version} for database {DatabaseName}",
            version,
            databaseName
        );
    } // End of Method RecordSchemaVersionAsync

    /// <summary>
    /// Gets the current schema version for a database.
    /// Returns null if no version is recorded.
    /// </summary>
    public async Task<SchemaVersion?> GetCurrentVersionAsync(
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        // First ensure the table exists
        await EnsureSchemaVersionTableAsync(databaseName, cancellationToken);

        var connString = GetConnectionStringForDatabase(databaseName);

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(cancellationToken);

        var querySql = """
            SELECT id, database_name, version, applied_at, description, metadata
            FROM public.__SchemaVersion
            WHERE database_name = @database_name
            ORDER BY applied_at DESC
            LIMIT 1
            """;

        await using var cmd = new NpgsqlCommand(querySql, conn);
        cmd.Parameters.AddWithValue("database_name", databaseName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return new SchemaVersion
            {
                Id = reader.GetGuid(0),
                DatabaseName = reader.GetString(1),
                Version = reader.GetString(2),
                AppliedAt = reader.GetFieldValue<DateTimeOffset>(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                Metadata = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }

        return null;
    } // End of Method GetCurrentVersionAsync

    /// <summary>
    /// Validates that a database exists and has the expected schema version.
    /// </summary>
    public async Task<(
        bool Exists,
        bool VersionMatch,
        string? CurrentVersion
    )> ValidateDatabaseSchemaAsync(
        string databaseName,
        string expectedVersion,
        CancellationToken cancellationToken = default
    )
    {
        // Check if database exists
        if (!await DatabaseExistsAsync(databaseName, cancellationToken))
        {
            return (false, false, null);
        }

        // Get current version
        var currentVersion = await GetCurrentVersionAsync(databaseName, cancellationToken);

        if (currentVersion == null)
        {
            _logger.LogWarning(
                "Database {DatabaseName} exists but has no schema version recorded",
                databaseName
            );
            return (true, false, null);
        }

        var versionMatch = currentVersion.Version == expectedVersion;

        if (!versionMatch)
        {
            _logger.LogWarning(
                "Database {DatabaseName} schema version mismatch: expected {ExpectedVersion}, found {CurrentVersion}",
                databaseName,
                expectedVersion,
                currentVersion.Version
            );
        }

        return (true, versionMatch, currentVersion.Version);
    } // End of Method ValidateDatabaseSchemaAsync

    /// <summary>
    /// Checks if a database exists.
    /// </summary>
    public async Task<bool> DatabaseExistsAsync(
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var checkSql = "SELECT 1 FROM pg_database WHERE datname = @database_name";

        await using var cmd = new NpgsqlCommand(checkSql, conn);
        cmd.Parameters.AddWithValue("database_name", databaseName);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null;
    } // End of Method DatabaseExistsAsync

    /// <summary>
    /// Gets all tenant databases (prefixed with tansu_tenant_).
    /// </summary>
    public async Task<List<string>> GetTenantDatabasesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var querySql = "SELECT datname FROM pg_database WHERE datname LIKE 'tansu_tenant_%'";

        await using var cmd = new NpgsqlCommand(querySql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var databases = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    } // End of Method GetTenantDatabasesAsync

    /// <summary>
    /// Gets a connection string for a specific database.
    /// </summary>
    private string GetConnectionStringForDatabase(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = databaseName
        };
        return builder.ToString();
    } // End of Method GetConnectionStringForDatabase
} // End of Class SchemaVersionService
