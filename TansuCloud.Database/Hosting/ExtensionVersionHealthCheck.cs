// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TansuCloud.Database.Hosting;

/// <summary>
/// Health check that validates PostgreSQL extension versions across all tenant databases.
/// Reports Degraded when version mismatches are detected after container upgrades.
/// </summary>
public sealed class ExtensionVersionHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExtensionVersionHealthCheck> _logger;
    private readonly string[] _extensionsToCheck = { "citus", "vector" };

    public ExtensionVersionHealthCheck(
        IConfiguration configuration,
        ILogger<ExtensionVersionHealthCheck> logger
    )
    {
        _configuration = configuration;
        _logger = logger;
    } // End of Constructor ExtensionVersionHealthCheck

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var connectionString =
                _configuration["Provisioning:AdminConnectionString"]
                ?? throw new InvalidOperationException(
                    "Provisioning:AdminConnectionString not configured"
                );

            // Get all tenant databases
            var databases = await GetTenantDatabasesAsync(connectionString, cancellationToken);

            if (databases.Count == 0)
            {
                return HealthCheckResult.Healthy("No tenant databases found to check");
            }

            // Check extensions in each database
            var allVersions = new Dictionary<string, Dictionary<string, string>>();
            var mismatches = new List<string>();

            foreach (var db in databases)
            {
                var versions = await GetExtensionVersionsAsync(
                    connectionString,
                    db,
                    cancellationToken
                );
                allVersions[db] = versions;
            }

            // Determine the most common (expected) version for each extension
            var expectedVersions = new Dictionary<string, string>();
            foreach (var extName in _extensionsToCheck)
            {
                var versionCounts = allVersions
                    .Values.Where(v => v.ContainsKey(extName))
                    .Select(v => v[extName])
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                if (versionCounts != null)
                {
                    expectedVersions[extName] = versionCounts.Key;
                }
            }

            // Check for mismatches
            foreach (var (db, versions) in allVersions)
            {
                foreach (var extName in _extensionsToCheck)
                {
                    if (
                        versions.TryGetValue(extName, out var actualVersion)
                        && expectedVersions.TryGetValue(extName, out var expectedVersion)
                        && actualVersion != expectedVersion
                    )
                    {
                        mismatches.Add(
                            $"{db} ({extName} {actualVersion} expected {expectedVersion})"
                        );
                    }
                }
            }

            if (mismatches.Any())
            {
                var message =
                    $"Extension version mismatch detected in {mismatches.Count} database(s): {string.Join(", ", mismatches)}";
                _logger.LogWarning("Extension version health check degraded: {Message}", message);
                return HealthCheckResult.Degraded(message);
            }

            // Build success message with version summary
            var versionSummary = string.Join(
                ", ",
                expectedVersions.Select(kv => $"{kv.Key} {kv.Value}")
            );
            var message2 =
                $"All extensions up to date: {versionSummary} ({databases.Count} databases)";

            return HealthCheckResult.Healthy(message2);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extension version health check failed");
            return HealthCheckResult.Unhealthy("Could not verify extension versions", ex);
        }
    } // End of Method CheckHealthAsync

    private async Task<List<string>> GetTenantDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        var databases = new List<string>();
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
            databases.Add(reader.GetString(0));
        }

        return databases;
    } // End of Method GetTenantDatabasesAsync

    private async Task<Dictionary<string, string>> GetExtensionVersionsAsync(
        string connectionString,
        string database,
        CancellationToken cancellationToken
    )
    {
        var versions = new Dictionary<string, string>();
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = database };

        await using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            @"SELECT extname, extversion 
              FROM pg_extension 
              WHERE extname = ANY(@extensions)",
            connection
        );
        cmd.Parameters.AddWithValue("extensions", _extensionsToCheck);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var extName = reader.GetString(0);
            var extVersion = reader.GetString(1);
            versions[extName] = extVersion;
        }

        return versions;
    } // End of Method GetExtensionVersionsAsync
} // End of Class ExtensionVersionHealthCheck
