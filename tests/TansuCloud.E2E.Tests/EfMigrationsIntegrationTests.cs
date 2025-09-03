// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TansuCloud.Database.EF;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class EfMigrationsIntegrationTests
{
    private static string GetBaseConnectionString()
    {
        // Allow overriding host/port/db via env. Defaults to local dev container per repo docs.
        var host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
        var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres";
        var db = Environment.GetEnvironmentVariable("PGDATABASE") ?? "postgres"; // connect to maintenance DB first
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
    }

    [Fact]
    public async Task CreateTenantDb_RunMigrations_VerifyTables()
    {
        // Arrange
        var baseCs = GetBaseConnectionString();
        await using var admin = new NpgsqlConnection(baseCs);
        await admin.OpenAsync();

    // Vector is optional; migration should succeed without it.

        var tenantDb = $"tansu_test_{Guid.NewGuid():N}";

        // Create database idempotently
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{tenantDb}\"";
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P04")
            {
                // already exists
            }
        }

        // Build tenant connection string
        var builder = new NpgsqlConnectionStringBuilder(baseCs) { Database = tenantDb };
        var tenantCs = builder.ToString();

        // Act: run EF migrations via DbContext
        var opts = new DbContextOptionsBuilder<TansuDbContext>()
            .UseNpgsql(tenantCs)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        await using (var ctx = new TansuDbContext(opts))
        {
            await ctx.Database.MigrateAsync(CancellationToken.None);
        }

        // Assert: verify expected tables exist
        await using var check = new NpgsqlConnection(tenantCs);
        await check.OpenAsync();
        await using (var cmd = check.CreateCommand())
        {
            cmd.CommandText = @"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('collections','documents');";
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            count.Should().Be(2);
        }

        // Cleanup (drop DB)
        await admin.CloseAsync();
        await using (var admin2 = new NpgsqlConnection(GetBaseConnectionString()))
        {
            await admin2.OpenAsync();
            // Terminate connections then drop
            await using (var term = admin2.CreateCommand())
            {
                term.CommandText = @"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();";
                term.Parameters.AddWithValue("@db", tenantDb);
                await term.ExecuteNonQueryAsync();
            }
            await using (var drop = admin2.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{tenantDb}\"";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }
}
