// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using TansuCloud.Database.EF;
using TansuCloud.Database.Provisioning;
using TansuCloud.Observability;

namespace TansuCloud.Database.Services;

public interface ITenantDbContextFactory
{
    Task<TansuDbContext> CreateAsync(HttpContext httpContext, CancellationToken ct);
    Task<TansuDbContext> CreateAsync(string tenantId, CancellationToken ct);
} // End of Interface ITenantDbContextFactory

internal sealed class TenantDbContextFactory(
    IOptions<ProvisioningOptions> options,
    ILogger<TenantDbContextFactory> logger
) : ITenantDbContextFactory
{
    private readonly ProvisioningOptions _opts = options.Value;
    private readonly ILogger<TenantDbContextFactory> _logger = logger;

    public Task<TansuDbContext> CreateAsync(HttpContext httpContext, CancellationToken ct)
    {
        var tenant = httpContext.Request.Headers["X-Tansu-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new InvalidOperationException("Missing X-Tansu-Tenant header");
        }

        // Build tenant connection string by swapping the database name
        // Prefer runtime connection (e.g., pgcat) when available; fallback to admin connection
        var baseConn = string.IsNullOrWhiteSpace(_opts.RuntimeConnectionString)
            ? _opts.AdminConnectionString
            : _opts.RuntimeConnectionString!;
        var b = new NpgsqlConnectionStringBuilder(baseConn);
        var dbName = NormalizeDbName(tenant, _opts.DatabaseNamePrefix);
        b.Database = dbName;

        // Surface for diagnostics in dev/test and log what we will use
        try
        {
            var env = httpContext.RequestServices.GetService<IHostEnvironment>();
            if (env?.IsDevelopment() == true || env?.IsEnvironment("E2E") == true)
            {
                httpContext.Response.Headers["X-Tansu-Db"] = dbName;
            }
            _logger.LogTenantNormalized(tenant, NormalizeTenant(tenant), dbName);
        }
        catch { }

        var dbOptsBuilder = new DbContextOptionsBuilder<TansuDbContext>()
            .UseNpgsql(b.ConnectionString);
        TryUseCompiledModel(dbOptsBuilder);
        var ctx = new TansuDbContext(dbOptsBuilder.Options);
        return Task.FromResult(ctx);
    } // End of Method CreateAsync

    public Task<TansuDbContext> CreateAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var baseConn = string.IsNullOrWhiteSpace(_opts.RuntimeConnectionString)
            ? _opts.AdminConnectionString
            : _opts.RuntimeConnectionString!;
        var b = new Npgsql.NpgsqlConnectionStringBuilder(baseConn);
        var dbName = NormalizeDbName(tenantId, _opts.DatabaseNamePrefix);
        b.Database = dbName;

    _logger.LogTenantNormalized(tenantId, NormalizeTenant(tenantId), dbName);

        var dbOptsBuilder = new DbContextOptionsBuilder<TansuDbContext>()
            .UseNpgsql(b.ConnectionString);
        TryUseCompiledModel(dbOptsBuilder);
        var ctx = new TansuDbContext(dbOptsBuilder.Options);
        return Task.FromResult(ctx);
    } // End of Method CreateAsync (tenantId)

    private static string NormalizeDbName(string tenantId, string? prefix)
    {
        // postgres db name rules: lowercase, digits, underscore; keep short
        var cleaned = new string(
            tenantId.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()
        );
        var name = string.IsNullOrWhiteSpace(prefix) ? "tansu_tenant_" : prefix!;
        return $"{name}{cleaned}";
    } // End of Method NormalizeDbName

    private static string NormalizeTenant(string tenantId)
    {
        return new string(
            tenantId.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()
        );
    } // End of Method NormalizeTenant

    private static void TryUseCompiledModel(DbContextOptionsBuilder<TansuDbContext> builder)
    {
        try
        {
            var t = Type.GetType("TansuCloud.Database.EF.TansuDbContextModel, TansuCloud.Database");
            if (t is not null)
            {
                var prop = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop?.GetValue(null) is Microsoft.EntityFrameworkCore.Metadata.IModel model)
                {
                    builder.UseModel(model);
                }
            }
        }
        catch { }
    } // End of Method TryUseCompiledModel
} // End of Class TenantDbContextFactory

internal static class ETagHelper
{
    public static string ComputeWeakETag(params string[] parts)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var input = string.Join("|", parts);
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return $"W/\"{Convert.ToHexString(hash)}\"";
    }
} // End of Class ETagHelper
