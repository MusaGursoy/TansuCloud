// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Npgsql;

namespace TansuCloud.Database.Hosting;

/// <summary>
/// Reconciles PgCat connection pools with tenant databases.
/// Discovers all tenant databases and ensures PgCat has corresponding pools configured.
/// </summary>
public sealed class PgCatPoolHostedService : BackgroundService
{
    private readonly ILogger<PgCatPoolHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly TimeSpan _reconciliationInterval;

    public PgCatPoolHostedService(
        ILogger<PgCatPoolHostedService> logger,
        IConfiguration configuration
    )
    {
        _logger = logger;
        _configuration = configuration;
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string not found.");

        // Default to 5 minutes; allow configuration override
        var intervalMinutes = configuration.GetValue("PgCat:ReconciliationIntervalMinutes", 5);
        _reconciliationInterval = TimeSpan.FromMinutes(intervalMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let database validation complete
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        _logger.LogInformation(
            "PgCatPoolHostedService: Starting pool reconciliation (interval: {Interval})...",
            _reconciliationInterval
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcilePoolsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "PgCatPoolHostedService: Pool reconciliation failed. Will retry after interval."
                );
            }

            await Task.Delay(_reconciliationInterval, stoppingToken);
        }
    }

    private async Task ReconcilePoolsAsync(CancellationToken ct)
    {
        _logger.LogDebug("PgCatPoolHostedService: Discovering tenant databases...");

        var tenantDatabases = await GetTenantDatabasesAsync(ct);

        _logger.LogInformation(
            "PgCatPoolHostedService: Found {Count} tenant databases.",
            tenantDatabases.Count
        );

        // Check PgCat configuration
        var pgCatHost = _configuration["PgCat:Host"];
        var pgCatAdminPort = _configuration.GetValue("PgCat:AdminPort", 0);

        if (string.IsNullOrWhiteSpace(pgCatHost) || pgCatAdminPort == 0)
        {
            _logger.LogWarning(
                "PgCatPoolHostedService: PgCat not configured (Host or AdminPort missing). Pool reconciliation skipped."
            );
            return;
        }

        _logger.LogInformation(
            "PgCatPoolHostedService: PgCat configured at {Host}:{Port}. Reconciliation ready (implementation pending).",
            pgCatHost,
            pgCatAdminPort
        );

        // TODO: Implement actual PgCat pool reconciliation via admin API
        // For now, this service just discovers databases and logs the count
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
