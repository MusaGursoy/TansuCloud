// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using System.Text.Json;

namespace TansuCloud.Database.Services;

/// <summary>
/// Health check that reports infrastructure validation status including:
/// - Schema validation (Identity, Audit, tenant databases)
/// - Tenant count
/// - PgCat pool count (if configured)
/// - ClickHouse connectivity (informational)
/// </summary>
public sealed class InfrastructureHealthCheck : IHealthCheck
{
    private readonly ILogger<InfrastructureHealthCheck> _logger;
    private readonly IConfiguration _configuration;
    private readonly SchemaVersionService _schemaVersionService;
    private readonly string _connectionString;

    public InfrastructureHealthCheck(
        IConfiguration configuration,
        ILogger<InfrastructureHealthCheck> logger,
        SchemaVersionService schemaVersionService
    )
    {
        _configuration = configuration;
        _logger = logger;
        _schemaVersionService = schemaVersionService;

        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not configured");
    } // End of Constructor InfrastructureHealthCheck

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var data = new Dictionary<string, object>();

        try
        {
            // 1. Validate Identity database schema
            var identityVersion = await _schemaVersionService.GetCurrentVersionAsync(
                "tansu_identity",
                cancellationToken
            );
            data["identity_schema_version"] = identityVersion?.Version ?? "unknown";
            data["identity_schema_valid"] =
                identityVersion?.Version == SchemaVersionService.ExpectedVersions.Identity;

            // 2. Validate Audit database schema
            var auditVersion = await _schemaVersionService.GetCurrentVersionAsync(
                "tansu_audit",
                cancellationToken
            );
            data["audit_schema_version"] = auditVersion?.Version ?? "unknown";
            data["audit_schema_valid"] =
                auditVersion?.Version == SchemaVersionService.ExpectedVersions.Audit;

            // 3. Discover tenant databases
            var tenantDatabases = await GetTenantDatabasesAsync(cancellationToken);
            data["tenant_count"] = tenantDatabases.Count;
            data["tenant_databases"] = tenantDatabases;

            // 4. Validate tenant database schemas
            var validTenantSchemas = 0;
            foreach (var dbName in tenantDatabases)
            {
                var version = await _schemaVersionService.GetCurrentVersionAsync(
                    dbName,
                    cancellationToken
                );
                if (version?.Version == SchemaVersionService.ExpectedVersions.Tenant)
                {
                    validTenantSchemas++;
                }
            }
            data["tenant_schemas_valid"] = validTenantSchemas;

            // 5. PgCat pool information (if configured)
            var pgcatHost = _configuration["PgCat:Host"];
            var pgcatConfigPath = _configuration["PgCat:ConfigPath"];
            if (!string.IsNullOrWhiteSpace(pgcatHost))
            {
                data["pgcat_configured"] = true;
                data["pgcat_host"] = pgcatHost;
                // Pool count would require parsing TOML config or querying admin API
                data["pgcat_note"] = "Pool validation requires admin API (future enhancement)";
            }
            else
            {
                data["pgcat_configured"] = false;
                data["pgcat_mode"] = "direct_postgres";
            }

            // 6. ClickHouse connectivity (informational only)
            var clickhouseEndpoint = _configuration["ClickHouse:Endpoint"];
            if (!string.IsNullOrWhiteSpace(clickhouseEndpoint))
            {
                data["clickhouse_endpoint"] = clickhouseEndpoint;
                var clickhouseReachable = await CheckClickHouseConnectivityAsync(
                    clickhouseEndpoint,
                    cancellationToken
                );
                data["clickhouse_reachable"] = clickhouseReachable;
            }
            else
            {
                data["clickhouse_configured"] = false;
            }

            // Determine overall health
            var allSystemDbsValid =
                (identityVersion?.Version == SchemaVersionService.ExpectedVersions.Identity)
                && (auditVersion?.Version == SchemaVersionService.ExpectedVersions.Audit);

            var allTenantDbsValid = validTenantSchemas == tenantDatabases.Count;

            if (allSystemDbsValid && allTenantDbsValid)
            {
                return HealthCheckResult.Healthy(
                    "Infrastructure validation passed: all schemas valid",
                    data
                );
            }
            else
            {
                return HealthCheckResult.Degraded(
                    "Infrastructure validation: some schemas invalid or missing",
                    data: data
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InfrastructureHealthCheck: Validation failed");
            data["error"] = ex.Message;
            return HealthCheckResult.Unhealthy(
                "Infrastructure validation failed with exception",
                ex,
                data
            );
        }
    } // End of Method CheckHealthAsync

    private async Task<List<string>> GetTenantDatabasesAsync(CancellationToken cancellationToken)
    {
        var tenantDatabases = new List<string>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = """
            SELECT datname 
            FROM pg_database 
            WHERE datname LIKE 'tansu_tenant_%' 
            AND datistemplate = false
            ORDER BY datname;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tenantDatabases.Add(reader.GetString(0));
        }

        return tenantDatabases;
    } // End of Method GetTenantDatabasesAsync

    private async Task<bool> CheckClickHouseConnectivityAsync(
        string endpoint,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var pingUrl = $"{endpoint.TrimEnd('/')}/ping";
            var response = await client.GetAsync(pingUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    } // End of Method CheckClickHouseConnectivityAsync
} // End of Class InfrastructureHealthCheck
