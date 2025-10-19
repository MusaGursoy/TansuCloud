// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

namespace TansuCloud.E2E.Tests.Infrastructure;

/// <summary>
/// Centralized helper for cleaning up test policies from Gateway and PostgreSQL.
/// Provides both API-based and direct database cleanup with automatic Gateway restart.
/// </summary>
public static class TestPolicyCleanupHelper
{
    /// <summary>
    /// Cleans up all test policies (test-* and e2e-* prefixes) using Gateway API first,
    /// then falls back to direct PostgreSQL cleanup if API fails.
    /// Automatically restarts Gateway after database cleanup to reload clean state.
    /// </summary>
    public static async Task CleanupTestPoliciesAsync()
    {
        var apiCleanupSucceeded = await TryCleanupViaGatewayApiAsync();
        
        if (!apiCleanupSucceeded)
        {
            Console.WriteLine("[Cleanup] Gateway API cleanup failed, using direct PostgreSQL cleanup...");
            await CleanupViaDatabaseDirectlyAsync();
        }
    }

    private static async Task<bool> TryCleanupViaGatewayApiAsync()
    {
        try
        {
            var gatewayUrl = TestUrls.GatewayBaseUrl;
            using var client = new HttpClient
            {
                BaseAddress = new Uri(gatewayUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var listResponse = await client.GetAsync("/admin/api/policies");
            if (!listResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Cleanup] Gateway API list failed with status: {listResponse.StatusCode}");
                return false;
            }

            var json = await listResponse.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var deletedCount = 0;

            foreach (var policy in doc.RootElement.EnumerateArray())
            {
                var id = policy.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id) && (id.StartsWith("test-") || id.StartsWith("e2e-")))
                {
                    var deleteResp = await client.DeleteAsync($"/admin/api/policies/{id}");
                    if (deleteResp.IsSuccessStatusCode)
                    {
                        deletedCount++;
                    }
                }
            }

            if (deletedCount > 0)
            {
                Console.WriteLine($"[Cleanup] Deleted {deletedCount} test policies via Gateway API");
                await Task.Delay(1000); // Give Gateway time to reload
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cleanup] Gateway API cleanup failed: {ex.Message}");
            return false;
        }
    }

    private static async Task CleanupViaDatabaseDirectlyAsync()
    {
        try
        {
            var host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
            var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
            var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
            var pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres";
            var cs = $"Host={host};Port={port};Database=tansu_identity;Username={user};Password={pass}";

            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "DELETE FROM gateway_policies WHERE id LIKE 'test-%' OR id LIKE 'e2e-%'",
                connection
            );

            var deleted = await cmd.ExecuteNonQueryAsync();
            if (deleted > 0)
            {
                Console.WriteLine($"[Cleanup] Deleted {deleted} test policies via direct PostgreSQL access");

                // Restart Gateway to reload clean state
                await RestartGatewayAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cleanup] Direct database cleanup failed: {ex.Message}");
        }
    }

    private static async Task RestartGatewayAsync()
    {
        try
        {
            Console.WriteLine("[Cleanup] Restarting Gateway to reload clean policy state...");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "restart tansu-gateway",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                Console.WriteLine("[Cleanup] Warning: Failed to start docker restart command");
                return;
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                Console.WriteLine($"[Cleanup] Warning: docker restart failed with exit code {process.ExitCode}: {stderr}");
                return;
            }

            // Wait for Gateway to become healthy again
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("[Cleanup] Gateway restart complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cleanup] Warning: Failed to restart Gateway: {ex.Message}");
        }
    }
} // End of Class TestPolicyCleanupHelper
