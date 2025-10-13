// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Data;
using Npgsql;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.Hosting;

/// <summary>
/// Validates database schemas at startup before accepting HTTP traffic.
/// Ensures Identity, Audit, and all tenant databases exist with correct schema versions.
/// </summary>
public sealed class DatabaseSchemaHostedService : BackgroundService
{
    private readonly ILogger<DatabaseSchemaHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public DatabaseSchemaHostedService(
        ILogger<DatabaseSchemaHostedService> logger,
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _lifetime = lifetime;
        _configuration = configuration;
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string not found.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation(
                "DatabaseSchemaHostedService: Starting database schema validation..."
            );

            // Validate Identity database
            await ValidateIdentityDatabaseAsync(stoppingToken);

            // Validate Audit database
            await ValidateAuditDatabaseAsync(stoppingToken);

            // Validate all tenant databases
            await ValidateTenantDatabasesAsync(stoppingToken);

            // Validate ClickHouse connectivity (informational only, doesn't fail startup)
            await ValidateClickHouseConnectivityAsync(stoppingToken);

            _logger.LogInformation(
                "DatabaseSchemaHostedService: All database schemas validated successfully."
            );
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "DatabaseSchemaHostedService: Database schema validation failed. Application cannot start."
            );
            _lifetime.StopApplication();
            throw;
        }
    }

    private async Task ValidateIdentityDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Validating Identity database...");

        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        builder.Database = "tansu_identity";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        // Check if database exists and is accessible
        var dbExists = await CheckDatabaseAccessibleAsync(conn, ct);
        if (!dbExists)
        {
            throw new InvalidOperationException(
                "Identity database 'tansu_identity' does not exist or is not accessible."
            );
        }

        // Validate required tables exist
        var requiredTables = new[]
        {
            "AspNetUsers",
            "AspNetRoles",
            "OpenIddictApplications",
            "OpenIddictScopes"
        };
        foreach (var table in requiredTables)
        {
            var tableExists = await TableExistsAsync(conn, table, ct);
            if (!tableExists)
            {
                throw new InvalidOperationException(
                    $"Identity database is missing required table: {table}"
                );
            }
        }

        _logger.LogInformation("Identity database validated successfully.");
    }

    private async Task ValidateAuditDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Validating Audit database...");

        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        builder.Database = "tansu_audit";

        await using var conn = new NpgsqlConnection(builder.ToString());
        
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            var message = "Audit database 'tansu_audit' does not exist or is not accessible. " +
                          "DatabaseMigrationHostedService should have created it during startup.";
            _logger.LogError(ex, message);
            throw new InvalidOperationException(message, ex);
        }

        // Check if database exists and is accessible
        var dbExists = await CheckDatabaseAccessibleAsync(conn, ct);
        if (!dbExists)
        {
            throw new InvalidOperationException(
                "Audit database 'tansu_audit' does not exist or is not accessible."
            );
        }

        // Validate required tables exist
        var tableExists = await TableExistsAsync(conn, "audit_events", ct);
        if (!tableExists)
        {
            var message = "Audit database is missing required table: audit_events. " +
                          "DatabaseMigrationHostedService should have applied migrations during startup.";
            _logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        // Validate schema version
        using var scope = _serviceProvider.CreateScope();
        var schemaService = scope.ServiceProvider.GetRequiredService<SchemaVersionService>();

        var version = await schemaService.GetCurrentVersionAsync("tansu_audit", ct);
        if (version == null)
        {
            _logger.LogWarning(
                "Audit database has no schema version tracked. Initializing to v1.0.0..."
            );
            await schemaService.RecordSchemaVersionAsync(
                "tansu_audit",
                "1.0.0",
                "Initial audit schema",
                cancellationToken: ct
            );
        }

        _logger.LogInformation(
            "Audit database validated successfully (version: {Version}).",
            version?.Version ?? "1.0.0"
        );
    }

    private async Task ValidateTenantDatabasesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Validating tenant databases...");

        using var scope = _serviceProvider.CreateScope();
        var schemaService = scope.ServiceProvider.GetRequiredService<SchemaVersionService>();

        // Get list of all tenant databases from the database service's registry
        var tenantDatabases = await GetTenantDatabasesAsync(ct);

        if (tenantDatabases.Count == 0)
        {
            _logger.LogInformation("No tenant databases found. Validation complete.");
            return;
        }

        var validatedCount = 0;
        var failedDatabases = new List<string>();

        foreach (var tenantDb in tenantDatabases)
        {
            try
            {
                await ValidateTenantDatabaseAsync(tenantDb, schemaService, ct);
                validatedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate tenant database: {Database}", tenantDb);
                failedDatabases.Add(tenantDb);
            }
        }

        if (failedDatabases.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to validate {failedDatabases.Count} tenant database(s): {string.Join(", ", failedDatabases)}"
            );
        }

        _logger.LogInformation(
            "Validated {Count} tenant database(s) successfully.",
            validatedCount
        );
    }

    private async Task ValidateTenantDatabaseAsync(
        string databaseName,
        SchemaVersionService schemaService,
        CancellationToken ct
    )
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        builder.Database = databaseName;

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        // Check if database is accessible
        var dbExists = await CheckDatabaseAccessibleAsync(conn, ct);
        if (!dbExists)
        {
            throw new InvalidOperationException(
                $"Tenant database '{databaseName}' is not accessible."
            );
        }

        // Validate required tables exist
        var requiredTables = new[] { "collections", "documents" };
        foreach (var table in requiredTables)
        {
            var tableExists = await TableExistsAsync(conn, table, ct);
            if (!tableExists)
            {
                throw new InvalidOperationException(
                    $"Tenant database '{databaseName}' is missing required table: {table}"
                );
            }
        }

        // Validate schema version
        var version = await schemaService.GetCurrentVersionAsync(databaseName, ct);
        if (version == null)
        {
            _logger.LogWarning(
                "Tenant database '{Database}' has no schema version tracked. Initializing to v1.0.0...",
                databaseName
            );
            await schemaService.RecordSchemaVersionAsync(
                databaseName,
                "1.0.0",
                "Initial tenant schema",
                cancellationToken: ct
            );
        }

        _logger.LogDebug(
            "Tenant database '{Database}' validated (version: {Version}).",
            databaseName,
            version?.Version ?? "1.0.0"
        );
    }

    private async Task ValidateClickHouseConnectivityAsync(CancellationToken ct)
    {
        // Read-only connectivity check to ClickHouse/SigNoz (informational only)
        var clickHouseEndpoint = _configuration["ClickHouse:Endpoint"];
        if (string.IsNullOrWhiteSpace(clickHouseEndpoint))
        {
            _logger.LogInformation(
                "ClickHouse endpoint not configured. Skipping connectivity check."
            );
            return;
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"{clickHouseEndpoint.TrimEnd('/')}/ping", ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "ClickHouse connectivity validated successfully at {Endpoint}",
                    clickHouseEndpoint
                );
            }
            else
            {
                _logger.LogWarning(
                    "ClickHouse ping returned non-success status {StatusCode} at {Endpoint}. Telemetry may be unavailable.",
                    response.StatusCode,
                    clickHouseEndpoint
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ClickHouse connectivity check failed at {Endpoint}. Telemetry may be unavailable.",
                clickHouseEndpoint
            );
        }
    }

    private async Task<List<string>> GetTenantDatabasesAsync(CancellationToken ct)
    {
        // Query PostgreSQL to get all databases starting with 'tansu_tenant_'
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

    private async Task<bool> CheckDatabaseAccessibleAsync(
        NpgsqlConnection conn,
        CancellationToken ct
    )
    {
        try
        {
            // Simple query to verify connection
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TableExistsAsync(
        NpgsqlConnection conn,
        string tableName,
        CancellationToken ct
    )
    {
        const string sql =
            @"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                  AND table_name = @tableName
            );";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tableName", tableName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool exists && exists;
    }
} // End of Class DatabaseSchemaHostedService
