// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace TansuCloud.E2E.Tests.Infrastructure;

/// <summary>
/// Global test fixture ensuring core infra (Postgres + Identity JWKS) is available before tests run.
/// Reduces flakiness from racing container startup.
/// </summary>
public sealed class TestInfrastructureFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        Console.WriteLine("[Fixture] Initializing infrastructure readiness checks...");
        
        // Clean up any orphaned test policies from previous runs BEFORE any tests start
        await TestPolicyCleanupHelper.CleanupTestPoliciesAsync();
        
        await WaitForPostgres();
        Console.WriteLine("[Fixture] Postgres ready.");
        await WaitForIdentityJwks();
        Console.WriteLine("[Fixture] Identity JWKS ready.");
    } // End of Method InitializeAsync

    public async Task DisposeAsync()
    {
        // Clean up after all tests complete
        await TestPolicyCleanupHelper.CleanupTestPoliciesAsync();
    } // End of Method DisposeAsync

    private static async Task WaitForPostgres()
    {
        var host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
        var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres";
        var cs = $"Host={host};Port={port};Database=postgres;Username={user};Password={pass}";
        var attempt = 0;
        Exception? last = null;
        const int maxAttempts = 40; // ~ (exponential backoff) ~ >30s
        while (attempt < maxAttempts)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or SocketException)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(1.2, attempt), 1500));
                await Task.Delay(delay);
            }
        }
        throw new InvalidOperationException(
            $"Postgres not ready after {attempt} attempts to {host}:{port}. Last error: {last?.GetType().Name}: {last?.Message}. Ensure 'docker compose up -d postgres' (service name 'postgres') has been run before tests.",
            last
        );
    } // End of Method WaitForPostgres

    private static async Task WaitForIdentityJwks()
    {
        // Use shared helper to resolve base URLs from environment/.env defaults.
        var baseUrl = TestUrls.PublicBaseUrl;
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        var jwksUrl = new Uri(new Uri(baseUrl), "identity/.well-known/jwks").ToString();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var attempt = 0;
        while (attempt < 30)
        {
            attempt++;
            try
            {
                var json = await http.GetStringAsync(jwksUrl);
                using var doc = JsonDocument.Parse(json);
                if (
                    doc.RootElement.TryGetProperty("keys", out var keys)
                    && keys.GetArrayLength() > 0
                )
                {
                    Console.WriteLine(
                        $"[Fixture] JWKS keys loaded (count={keys.GetArrayLength()}) from {jwksUrl}"
                    );
                    return;
                }
            }
            catch
            {
                // ignore; retry
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 + 75 * attempt));
        }
        throw new InvalidOperationException(
            $"Identity JWKS not ready after {attempt} attempts at {jwksUrl}"
        );
    } // End of Method WaitForIdentityJwks
} // End of Class TestInfrastructureFixture

// Define a collection so tests can opt-in and share this single fixture instance.
[CollectionDefinition("Global", DisableParallelization = true)]
public sealed class GlobalTestCollection : ICollectionFixture<TestInfrastructureFixture> { } // End of Class GlobalTestCollection
