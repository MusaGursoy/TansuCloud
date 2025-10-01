// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Data;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;
using TansuCloud.Database.EF;
using TansuCloud.Observability;

namespace TansuCloud.Database.Provisioning;

public interface ITenantProvisioner
{
    Task<TenantProvisionResult> ProvisionAsync(
        TenantProvisionRequest request,
        CancellationToken ct
    );
}

public sealed record TenantProvisionRequest(
    string TenantId,
    string? DisplayName,
    string? Region = null
);

public sealed record TenantProvisionResult(
    string TenantId,
    string Database,
    bool Created,
    string? Message = null
);

internal sealed class TenantProvisioner(
    IOptions<ProvisioningOptions> options,
    ILogger<TenantProvisioner> logger
) : ITenantProvisioner
{
    private readonly ProvisioningOptions _options = options.Value;
    private readonly ILogger<TenantProvisioner> _logger = logger;

    public async Task<TenantProvisionResult> ProvisionAsync(
        TenantProvisionRequest request,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        var dbName = NormalizeDbName(request.TenantId, _options.DatabaseNamePrefix);
        var stopwatch = Stopwatch.StartNew();
        var extensions = (
            _options.Extensions ?? string.Empty
        )
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var activity = TansuActivitySources.Background.StartActivity("TenantProvision");
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.Tenant, request.TenantId);
            activity.SetTag("tansu.provision.db", dbName);
            activity.SetTag("db.system", "postgresql");
            activity.SetTag("tansu.provision.extensions_total", extensions.Length);
            activity.SetTag("tansu.provision.has_display_name", request.DisplayName is not null);
            if (!string.IsNullOrWhiteSpace(request.Region))
            {
                activity.SetTag("tansu.provision.region", request.Region);
            }
        }

        _logger.LogTenantProvisioningStarted(request.TenantId, dbName);

        var ensuredExtensions = 0;
        var missingExtensions = 0;
        var success = false;

        await using var admin = new NpgsqlConnection(_options.AdminConnectionString);
        try
        {
            await admin.OpenAsync(ct);

            // idempotent create database
            var exists = await DatabaseExistsAsync(admin, dbName, ct);
            if (!exists)
            {
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\";", admin);
                await cmd.ExecuteNonQueryAsync(ct);
                _logger.LogTenantProvisioningDatabaseCreated(dbName);
            }

            activity?.SetTag("tansu.provision.created_db", !exists);

            // enable required extensions in tenant DB (idempotent)
            var tenantConnString = new NpgsqlConnectionStringBuilder(_options.AdminConnectionString)
            {
                Database = dbName
            }.ToString();

            await using var tenant = new NpgsqlConnection(tenantConnString);
            await tenant.OpenAsync(ct);

            foreach (var ext in extensions)
            {
                try
                {
                    var sql = $"CREATE EXTENSION IF NOT EXISTS {ext};";
                    await using var cmd = new NpgsqlCommand(sql, tenant);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _logger.LogTenantProvisioningExtensionEnsured(dbName, ext);
                    ensuredExtensions++;
                }
                catch (PostgresException ex)
                {
                    _logger.LogTenantProvisioningExtensionUnavailable(dbName, ext, ex);
                    missingExtensions++;
                }
            }
            // Apply EF Core migrations into the tenant database (creates schema and vector/HNSW where supported)
            var dbOptsBuilder = new DbContextOptionsBuilder<TansuDbContext>()
                .UseNpgsql(tenantConnString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            TryUseCompiledModel(dbOptsBuilder);
            var dbOpts = dbOptsBuilder.Options;
            using (var ef = new TansuDbContext(dbOpts))
            {
                await ef.Database.MigrateAsync(ct);
            }

            // Minimal marker table for idempotency (kept for backward compatibility)
            const string createTableSql =
                @"CREATE TABLE IF NOT EXISTS tenant_info (
                        id serial PRIMARY KEY,
                        tenant_id varchar(256) NOT NULL,
                        display_name varchar(512) NULL,
                        created_at timestamptz NOT NULL DEFAULT now()
                    );";
            await using (var cmd = new NpgsqlCommand(createTableSql, tenant))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            const string ensureIndexSql =
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_tenant_info_tenant_id ON tenant_info(tenant_id);";
            await using (var cmd = new NpgsqlCommand(ensureIndexSql, tenant))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            const string insertMarkerSql =
                "INSERT INTO tenant_info(tenant_id, display_name) VALUES (@tenant, @name) ON CONFLICT (tenant_id) DO NOTHING;";
            await using (var cmd = new NpgsqlCommand(insertMarkerSql, tenant))
            {
                cmd.Parameters.AddWithValue("@tenant", request.TenantId);
                cmd.Parameters.AddWithValue("@name", (object?)request.DisplayName ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            _logger.LogTenantProvisioningCompleted(request.TenantId, dbName, !exists);
            success = true;

            activity?.SetStatus(ActivityStatusCode.Ok);
            return new TenantProvisionResult(
                request.TenantId,
                dbName,
                !exists,
                exists ? "Already existed" : "Created"
            );
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("tansu.provision.error", ex.Message);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("tansu.provision.extensions_total", extensions.Length);
            activity?.SetTag("tansu.provision.extensions_ensured", ensuredExtensions);
            activity?.SetTag("tansu.provision.extensions_failed", missingExtensions);
            activity?.SetTag("tansu.provision.success", success);
            activity?.SetTag("tansu.provision.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
            activity?.Dispose();
        }
    }

    private static string NormalizeDbName(string tenantId, string prefix)
    {
        // postgres db name rules: lowercase, digits, underscore; keep short
        var cleaned = new string(
            tenantId.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()
        );
        var name = string.IsNullOrWhiteSpace(prefix) ? "tansu_tenant_" : prefix;
        return $"{name}{cleaned}";
    }

    private static async Task<bool> DatabaseExistsAsync(
        NpgsqlConnection admin,
        string dbName,
        CancellationToken ct
    )
    {
        const string sql = "SELECT 1 FROM pg_database WHERE datname = @n;";
        await using var cmd = new NpgsqlCommand(sql, admin);
        cmd.Parameters.AddWithValue("@n", dbName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    // Try to attach EF compiled model when available without hard dependency on the generated type
    private static void TryUseCompiledModel(DbContextOptionsBuilder<TansuDbContext> builder)
    {
        try
        {
            var t = Type.GetType("TansuCloud.Database.EF.TansuDbContextModel, TansuCloud.Database");
            var inst = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (inst is IModel model)
            {
                builder.UseModel(model);
            }
        }
        catch
        {
            // no-op: compiled model not present
        }
    }
} // End of Class TenantProvisioner
