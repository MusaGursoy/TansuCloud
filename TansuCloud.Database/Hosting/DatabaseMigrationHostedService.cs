// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TansuCloud.Audit;

namespace TansuCloud.Database.Hosting;

/// <summary>
/// Applies database migrations on startup before accepting HTTP traffic.
/// Ensures Audit database is created and migrations are applied automatically.
/// This service runs before DatabaseSchemaHostedService validation.
/// </summary>
public sealed class DatabaseMigrationHostedService : IHostedService
{
    private readonly ILogger<DatabaseMigrationHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly bool _isDevelopment;
    private readonly string _connectionString;

    private const int MaxRetries = 30;
    private const int RetryDelayMilliseconds = 2000;

    public DatabaseMigrationHostedService(
        ILogger<DatabaseMigrationHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        IHostEnvironment environment
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _lifetime = lifetime;
        _isDevelopment = environment.IsDevelopment();
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string not found.");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "DatabaseMigrationHostedService: Starting database initialization and migrations..."
            );

            // Step 1: Ensure base extensions are installed in postgres database
            await EnsureBaseExtensionsAsync(cancellationToken);

            // Step 2: Ensure extensions in template1 for inheritance by new databases
            await EnsureTemplate1ExtensionsAsync(cancellationToken);

            // Step 3: Ensure Identity database exists with required extensions
            await EnsureIdentityDatabaseAsync(cancellationToken);

            // Step 4: Ensure Audit database exists, apply migrations, and install extensions
            await EnsureAuditDatabaseAsync(cancellationToken);

            _logger.LogInformation(
                "DatabaseMigrationHostedService: All database initialization and migrations completed successfully."
            );
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "DatabaseMigrationHostedService: Database initialization/migration failed. Application cannot start."
            );

            // In production, fail fast. In development, log and continue to allow troubleshooting.
            if (!_isDevelopment)
            {
                _lifetime.StopApplication();
                throw;
            }
            else
            {
                _logger.LogWarning(
                    "DatabaseMigrationHostedService: Continuing in Development mode despite initialization failure."
                );
            }
        }

        return;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retry helper for database operations that may fail due to PostgreSQL template1 locks or temporary unavailability.
    /// Common during container startup when multiple services connect simultaneously.
    /// </summary>
    private async Task<T> RetryDatabaseOperationAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken ct
    )
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (PostgresException ex)
                when (ex.SqlState == "55006"
                    || // source database is being accessed by other users (template1 lock)
                    ex.SqlState == "08006"
                    || // connection failure
                    ex.SqlState == "08003"
                    || // connection does not exist
                    ex.SqlState == "57P03"
                ) // cannot connect now (database starting up)
            {
                if (attempt == MaxRetries)
                {
                    _logger.LogError(
                        ex,
                        "{OperationName} failed after {MaxRetries} attempts. SqlState: {SqlState}",
                        operationName,
                        MaxRetries,
                        ex.SqlState
                    );
                    throw;
                }

                _logger.LogWarning(
                    "{OperationName} failed (attempt {Attempt}/{MaxRetries}). SqlState: {SqlState}. Retrying in {DelayMs}ms...",
                    operationName,
                    attempt,
                    MaxRetries,
                    ex.SqlState,
                    RetryDelayMilliseconds
                );

                await Task.Delay(RetryDelayMilliseconds, ct);
            }
        }

        throw new InvalidOperationException($"{operationName} retry logic failed unexpectedly.");
    }

    private async Task EnsureBaseExtensionsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Ensuring base PostgreSQL extensions are installed...");

        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "postgres" // Connect to default postgres database
        };

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        var extensions = new[] { "citus", "vector", "pg_trgm" };

        foreach (var extension in extensions)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"CREATE EXTENSION IF NOT EXISTS {extension}",
                    conn
                );
                await cmd.ExecuteNonQueryAsync(ct);
                _logger.LogInformation(
                    "Extension '{Extension}' ensured in postgres database.",
                    extension
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to create extension '{Extension}' in postgres database. This may be expected if the extension is not available.",
                    extension
                );
            }
        }
    }

    private async Task EnsureTemplate1ExtensionsAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Ensuring extensions in template1 for inheritance by new databases..."
        );

        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "template1"
        };

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        // Only vector and pg_trgm are typically needed in template1
        // Citus should not be in template1 as it requires specific setup per database
        var extensions = new[] { "vector", "pg_trgm" };

        foreach (var extension in extensions)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"CREATE EXTENSION IF NOT EXISTS {extension}",
                    conn
                );
                await cmd.ExecuteNonQueryAsync(ct);
                _logger.LogInformation("Extension '{Extension}' ensured in template1.", extension);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to create extension '{Extension}' in template1. New databases will not inherit this extension.",
                    extension
                );
            }
        }

        // Explicitly close connection to template1 to release lock
        await conn.CloseAsync();

        // Clear the connection pool for template1 to force immediate cleanup
        // This helps prevent "template1 is being accessed by other users" errors
        NpgsqlConnection.ClearPool(conn);

        // Give PostgreSQL time to fully release the template1 lock
        // Increased from 500ms to 2000ms to handle slower lock release on fresh installations
        await Task.Delay(2000, ct);

        _logger.LogInformation(
            "Template1 extensions installation complete and connection released."
        );
    }

    private async Task EnsureIdentityDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Ensuring Identity database exists with required extensions...");

        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "postgres" // Connect to default database to create identity database
        };

        // Use retry logic for CREATE DATABASE which can fail due to template1 lock
        await RetryDatabaseOperationAsync(
            async () =>
            {
                await using var conn = new NpgsqlConnection(builder.ToString());
                await conn.OpenAsync(ct);

                // Check if identity database exists
                await using var checkCmd = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname = 'tansu_identity'",
                    conn
                );
                var exists = await checkCmd.ExecuteScalarAsync(ct);

                if (exists == null)
                {
                    _logger.LogInformation("Creating Identity database 'tansu_identity'...");

                    await using var createCmd = new NpgsqlCommand(
                        "CREATE DATABASE tansu_identity",
                        conn
                    );
                    await createCmd.ExecuteNonQueryAsync(ct);

                    _logger.LogInformation(
                        "Identity database 'tansu_identity' created successfully."
                    );
                }
                else
                {
                    _logger.LogInformation("Identity database 'tansu_identity' already exists.");
                }

                return true;
            },
            "Create Identity Database",
            ct
        );

        // Now ensure extensions in the identity database
        builder.Database = "tansu_identity";
        await using (var conn = new NpgsqlConnection(builder.ToString()))
        {
            await conn.OpenAsync(ct);

            var extensions = new[] { "vector", "pg_trgm" };

            foreach (var extension in extensions)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        $"CREATE EXTENSION IF NOT EXISTS {extension}",
                        conn
                    );
                    await cmd.ExecuteNonQueryAsync(ct);
                    _logger.LogInformation(
                        "Extension '{Extension}' ensured in tansu_identity database.",
                        extension
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to create extension '{Extension}' in tansu_identity database.",
                        extension
                    );
                }
            }
        }

        _logger.LogInformation("Identity database initialization complete.");

        // Apply Identity database migrations using the shared AppDbContext
        _logger.LogInformation("Applying Identity database migrations...");

        using (var scope = _serviceProvider.CreateScope())
        {
            var identityContext = scope.ServiceProvider.GetRequiredService<
                TansuCloud.Identity.Data.AppDbContext
            >();
            await identityContext.Database.MigrateAsync(ct);
        }

        _logger.LogInformation("Identity database migrations applied successfully.");
    }

    private async Task EnsureAuditDatabaseAsync(CancellationToken ct)
    {
        _logger.LogInformation("Ensuring audit database exists and applying migrations...");

        // First, ensure the tansu_audit database exists
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "postgres" // Connect to default database to create audit database
        };

        // Use retry logic for CREATE DATABASE which can fail due to template1 lock
        await RetryDatabaseOperationAsync(
            async () =>
            {
                await using var conn = new NpgsqlConnection(builder.ToString());
                await conn.OpenAsync(ct);

                // Check if audit database exists
                await using var checkCmd = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname = 'tansu_audit'",
                    conn
                );
                var exists = await checkCmd.ExecuteScalarAsync(ct);

                if (exists == null)
                {
                    _logger.LogInformation("Creating audit database 'tansu_audit'...");

                    // Create the database
                    await using var createCmd = new NpgsqlCommand(
                        "CREATE DATABASE tansu_audit",
                        conn
                    );
                    await createCmd.ExecuteNonQueryAsync(ct);

                    _logger.LogInformation("Audit database 'tansu_audit' created successfully.");
                }
                else
                {
                    _logger.LogInformation("Audit database 'tansu_audit' already exists.");
                }

                return true;
            },
            "Create Audit Database",
            ct
        );

        // Ensure extensions in the audit database
        builder.Database = "tansu_audit";
        await using (var conn = new NpgsqlConnection(builder.ToString()))
        {
            await conn.OpenAsync(ct);

            var extensions = new[] { "vector", "pg_trgm" };

            foreach (var extension in extensions)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(
                        $"CREATE EXTENSION IF NOT EXISTS {extension}",
                        conn
                    );
                    await cmd.ExecuteNonQueryAsync(ct);
                    _logger.LogInformation(
                        "Extension '{Extension}' ensured in tansu_audit database.",
                        extension
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to create extension '{Extension}' in tansu_audit database.",
                        extension
                    );
                }
            }
        }

        // Now apply EF Core migrations
        using var scope = _serviceProvider.CreateScope();
        var auditContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var pendingMigrations = await auditContext.Database.GetPendingMigrationsAsync(ct);
        if (pendingMigrations.Any())
        {
            _logger.LogInformation(
                "Applying {Count} pending migration(s) to audit database: {Migrations}",
                pendingMigrations.Count(),
                string.Join(", ", pendingMigrations)
            );

            await auditContext.Database.MigrateAsync(ct);

            _logger.LogInformation("Audit database migrations applied successfully.");
        }
        else
        {
            _logger.LogInformation("Audit database is up to date, no pending migrations.");
        }
    }
} // End of Class DatabaseMigrationHostedService
