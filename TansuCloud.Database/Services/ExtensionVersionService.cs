// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TansuCloud.Database.EF;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Database.Services;

/// <summary>
/// Service for ensuring PostgreSQL extensions are up-to-date with the loaded library versions.
/// This addresses version mismatches when upgrading Docker images that contain PostgreSQL extensions.
/// Logs all extension version changes to audit table for compliance tracking.
/// </summary>
public sealed class ExtensionVersionService
{
    private readonly ILogger<ExtensionVersionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAuditLogger? _auditLogger;
    private readonly string[] _extensionsToUpdate = { "citus", "vector" };

    public ExtensionVersionService(
        ILogger<ExtensionVersionService> logger,
        IConfiguration configuration,
        IAuditLogger? auditLogger = null
    )
    {
        _logger = logger;
        _configuration = configuration;
        _auditLogger = auditLogger;
    } // End of Constructor ExtensionVersionService

    /// <summary>
    /// Updates PostgreSQL extensions in all tenant databases to match the loaded library versions.
    /// Should be called during application startup to prevent XX000 errors from version mismatches.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of databases processed</returns>
    public async Task<int> EnsureExtensionVersionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var isDevelopment = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase
        );

        // In production, this can be disabled via env var if needed
        var skipUpdateStr = Environment.GetEnvironmentVariable("SKIP_EXTENSION_UPDATE");
        if (
            !string.IsNullOrWhiteSpace(skipUpdateStr)
            && bool.TryParse(skipUpdateStr, out var skip)
            && skip
        )
        {
            _logger.LogInformation("Extension version updates disabled via SKIP_EXTENSION_UPDATE");
            return 0;
        }

        var connectionString =
            _configuration["Provisioning:AdminConnectionString"]
            ?? throw new InvalidOperationException(
                "Provisioning:AdminConnectionString not configured"
            );

        _logger.LogInformation("Starting pre-flight extension version checks...");

        var databases = await GetTenantDatabasesAsync(connectionString, cancellationToken);
        var processedCount = 0;

        foreach (var database in databases)
        {
            try
            {
                await UpdateExtensionsInDatabaseAsync(
                    connectionString,
                    database,
                    cancellationToken
                );
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to update extensions in database {Database}. This may cause runtime errors.",
                    database
                );

                // In production, consider failing startup if critical extensions can't be updated
                if (!isDevelopment)
                {
                    throw;
                }
            }
        }

        _logger.LogInformation(
            "Pre-flight extension checks completed. Processed {Count} database(s)",
            processedCount
        );

        return processedCount;
    } // End of Method EnsureExtensionVersionsAsync

    /// <summary>
    /// Gets the current versions of all tracked extensions across all tenant databases.
    /// Used by health checks to report extension version status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of database name to extension versions</returns>
    public async Task<Dictionary<string, Dictionary<string, string>>> GetExtensionVersionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connectionString =
            _configuration["Provisioning:AdminConnectionString"]
            ?? throw new InvalidOperationException(
                "Provisioning:AdminConnectionString not configured"
            );

        var databases = await GetTenantDatabasesAsync(connectionString, cancellationToken);
        var result = new Dictionary<string, Dictionary<string, string>>();

        foreach (var database in databases)
        {
            var versions = new Dictionary<string, string>();
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = database
            };

            await using var connection = new NpgsqlConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);

            foreach (var extensionName in _extensionsToUpdate)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT extversion FROM pg_extension WHERE extname = @extname",
                        connection
                    );
                    cmd.Parameters.AddWithValue("extname", extensionName);

                    var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        versions[extensionName] = version;
                    }
                }
                catch (PostgresException ex) when (ex.SqlState == "42704")
                {
                    // Extension not installed, skip
                }
            }

            if (versions.Count > 0)
            {
                result[database] = versions;
            }
        }

        return result;
    } // End of Method GetExtensionVersionsAsync

    private async Task<List<string>> GetTenantDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        var databases = new List<string>();

        // Connect to postgres default database to list all databases
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datname LIKE 'tansu_tenant_%'",
            connection
        );

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var dbName = reader.GetString(0);
            databases.Add(dbName);
        }

        _logger.LogInformation("Found {Count} tenant database(s) to check", databases.Count);
        return databases;
    } // End of Method GetTenantDatabasesAsync

    private async Task UpdateExtensionsInDatabaseAsync(
        string connectionString,
        string database,
        CancellationToken cancellationToken
    )
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = database };

        await using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        foreach (var extensionName in _extensionsToUpdate)
        {
            try
            {
                // Check if extension exists
                await using var checkCmd = new NpgsqlCommand(
                    "SELECT extversion FROM pg_extension WHERE extname = @extname",
                    connection
                );
                checkCmd.Parameters.AddWithValue("extname", extensionName);

                var currentVersion = await checkCmd.ExecuteScalarAsync(cancellationToken) as string;

                if (string.IsNullOrWhiteSpace(currentVersion))
                {
                    _logger.LogDebug(
                        "Extension {Extension} not installed in {Database}, skipping",
                        extensionName,
                        database
                    );
                    continue;
                }

                // Try to update the extension
                await using var updateCmd = new NpgsqlCommand(
                    $"ALTER EXTENSION {extensionName} UPDATE",
                    connection
                );

                await updateCmd.ExecuteNonQueryAsync(cancellationToken);

                // Get new version
                await using var versionCmd = new NpgsqlCommand(
                    "SELECT extversion FROM pg_extension WHERE extname = @extname",
                    connection
                );
                versionCmd.Parameters.AddWithValue("extname", extensionName);

                var newVersion = await versionCmd.ExecuteScalarAsync(cancellationToken) as string;

                if (currentVersion == newVersion)
                {
                    _logger.LogDebug(
                        "[{Database}] Extension {Extension} already at version {Version}",
                        database,
                        extensionName,
                        newVersion
                    );
                }
                else
                {
                    _logger.LogInformation(
                        "[{Database}] Updated extension {Extension} from {OldVersion} to {NewVersion}",
                        database,
                        extensionName,
                        currentVersion,
                        newVersion
                    );

                    // Audit log for compliance tracking
                    var detailsJson = System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            database,
                            extension = extensionName,
                            oldVersion = currentVersion,
                            newVersion,
                            timestamp = DateTime.UtcNow
                        }
                    );

                    _auditLogger?.TryEnqueue(
                        new AuditEvent
                        {
                            WhenUtc = DateTime.UtcNow,
                            Service = "tansu.db",
                            Environment =
                                System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                                ?? "Production",
                            Version = GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0",
                            TenantId = ExtractTenantIdFromDatabase(database),
                            Subject = "system",
                            Action = "database.extension.update",
                            Category = "Maintenance",
                            Outcome = "Success",
                            Details = System.Text.Json.JsonDocument.Parse(detailsJson)
                        }
                    );
                }
            }
            catch (PostgresException ex) when (ex.SqlState == "42704") // undefined_object
            {
                _logger.LogDebug(
                    "[{Database}] Extension {Extension} not found, skipping",
                    database,
                    extensionName
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[{Database}] Failed to update extension {Extension}",
                    database,
                    extensionName
                );
                throw;
            }
        }
    } // End of Method UpdateExtensionsInDatabaseAsync

    private static string? ExtractTenantIdFromDatabase(string database)
    {
        // Extract tenant ID from database name: tansu_tenant_{tenantId} -> {tenantId}
        const string prefix = "tansu_tenant_";
        if (database.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return database.Substring(prefix.Length);
        }
        return null;
    } // End of Method ExtractTenantIdFromDatabase
} // End of Class ExtensionVersionService
