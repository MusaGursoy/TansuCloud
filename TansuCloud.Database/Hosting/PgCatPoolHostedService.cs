// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Npgsql;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.Hosting;

/// <summary>
/// Reconciles PgCat connection pools with tenant databases at startup.
/// - Adds pools for databases that exist but have no pool configured (missing pools)
/// - Removes pools for databases that no longer exist (orphaned pools)
/// Runs once at startup to clean up any inconsistencies from previous runs.
/// New tenant provisioning handles pool creation synchronously, so periodic reconciliation is not needed.
/// </summary>
public sealed class PgCatPoolHostedService : BackgroundService
{
    private readonly ILogger<PgCatPoolHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _connectionString;

    public PgCatPoolHostedService(
        ILogger<PgCatPoolHostedService> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string not found.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let database validation and PgCat Admin API become ready
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        _logger.LogInformation(
            "PgCatPoolHostedService: Starting one-time pool reconciliation at startup..."
        );

        try
        {
            await ReconcilePoolsAsync(stoppingToken);
            _logger.LogInformation(
                "PgCatPoolHostedService: Startup reconciliation completed successfully."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PgCatPoolHostedService: Startup pool reconciliation failed.");
        }

        // Reconciliation complete - service will remain running but idle
        // New pools are managed synchronously during tenant provisioning
    }

    private async Task ReconcilePoolsAsync(CancellationToken ct)
    {
        _logger.LogDebug("PgCatPoolHostedService: Discovering tenant databases...");

        var tenantDatabases = await GetTenantDatabasesAsync(ct);

        _logger.LogInformation(
            "PgCatPoolHostedService: Found {Count} tenant databases.",
            tenantDatabases.Count
        );

        // Create scoped service to get PgCatAdminClient
        using var scope = _serviceProvider.CreateScope();
        var pgcatClient = scope.ServiceProvider.GetService<PgCatAdminClient>();

        if (pgcatClient is null)
        {
            _logger.LogWarning(
                "PgCatPoolHostedService: PgCatAdminClient not available. Pool reconciliation skipped."
            );
            return;
        }

        _logger.LogDebug("PgCatPoolHostedService: Fetching configured PgCat pools...");
        var configuredPools = await pgcatClient.ListPoolsAsync(ct);

        // Filter to only tenant pools (tansu_tenant_*)
        var tenantPools = configuredPools
            .Where(p => p.StartsWith("tansu_tenant_", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var databaseSet = tenantDatabases.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "PgCatPoolHostedService: Found {ConfiguredCount} tenant pools configured in PgCat.",
            tenantPools.Count
        );

        // 1. Add missing pools (database exists, no pool)
        var missingPools = databaseSet.Except(tenantPools).ToList();
        if (missingPools.Any())
        {
            _logger.LogInformation(
                "PgCatPoolHostedService: Adding {Count} missing pools: {Databases}",
                missingPools.Count,
                string.Join(", ", missingPools)
            );

            foreach (var database in missingPools)
            {
                var added = await pgcatClient.AddPoolAsync(database, poolSize: 20, ct);
                if (added)
                {
                    _logger.LogInformation(
                        "PgCatPoolHostedService: Added pool for {Database}",
                        database
                    );
                }
            }
        }
        else
        {
            _logger.LogDebug("PgCatPoolHostedService: No missing pools to add.");
        }

        // 2. Remove orphaned pools (pool exists, database doesn't)
        var orphanedPools = tenantPools.Except(databaseSet).ToList();
        if (orphanedPools.Any())
        {
            _logger.LogWarning(
                "PgCatPoolHostedService: Removing {Count} orphaned pools: {Pools}",
                orphanedPools.Count,
                string.Join(", ", orphanedPools)
            );

            foreach (var database in orphanedPools)
            {
                var removed = await pgcatClient.RemovePoolAsync(database, ct);
                if (removed)
                {
                    _logger.LogInformation(
                        "PgCatPoolHostedService: Removed orphaned pool for {Database}",
                        database
                    );
                }
            }
        }
        else
        {
            _logger.LogDebug("PgCatPoolHostedService: No orphaned pools to remove.");
        }

        _logger.LogInformation(
            "PgCatPoolHostedService: Reconciliation complete. Databases: {DbCount}, Pools: {PoolCount}, Added: {Added}, Removed: {Removed}",
            tenantDatabases.Count,
            tenantPools.Count,
            missingPools.Count,
            orphanedPools.Count
        );
    }

    private async Task<List<string>> GetTenantDatabasesAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        builder.Database = "postgres"; // Connect to default database

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        const string sql =
            @"
            SELECT datname 
            FROM pg_database 
            WHERE datname LIKE 'tansu_tenant_%' 
              AND datistemplate = false
            ORDER BY datname;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var databases = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }
} // End of Class PgCatPoolHostedService
