// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Npgsql;

namespace TansuCloud.E2E.Tests.Infrastructure;

/// <summary>
/// Helper for cleaning up test policies to prevent orphaning.
/// Tries Gateway API first, falls back to direct PostgreSQL access.
/// </summary>
public static class PolicyCleanupHelper
{
    /// <summary>
    /// Clean up test policies with prefixes: test-, e2e-, persist-test-
    /// </summary>
    public static async Task CleanupAllTestPoliciesAsync(string? gatewayBaseUrl = null)
    {
        var baseUrl = gatewayBaseUrl ?? TestUrls.GatewayBaseUrl;
        
        try
        {
            // Try Gateway API cleanup first (preferred - respects Gateway logic)
            await CleanupViaGatewayApiAsync(baseUrl);
        }
        catch
        {
            // If Gateway API fails (e.g., 403 from blocking policy), use direct PostgreSQL
            await CleanupViaPostgreSqlAsync();
        }
    } // End of Method CleanupAllTestPoliciesAsync
    
    private static async Task CleanupViaGatewayApiAsync(string baseUrl)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        
        var response = await client.GetAsync("/admin/api/policies");
        response.EnsureSuccessStatusCode();
        
        var policies = await response.Content.ReadFromJsonAsync<List<PolicyDto>>();
        if (policies == null) return;
        
        var testPolicies = policies.Where(p => 
            p.Id.StartsWith("test-") || 
            p.Id.StartsWith("e2e-") || 
            p.Id.StartsWith("persist-test-")
        ).ToList();
        
        foreach (var policy in testPolicies)
        {
            await client.DeleteAsync($"/admin/api/policies/{policy.Id}");
        }
        
        if (testPolicies.Count > 0)
        {
            Console.WriteLine($"[PolicyCleanup] Cleaned up {testPolicies.Count} test policies via Gateway API");
        }
    } // End of Method CleanupViaGatewayApiAsync
    
    private static async Task CleanupViaPostgreSqlAsync()
    {
        var host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
        var user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres";
        var cs = $"Host={host};Port={port};Database=tansu_identity;Username={user};Password={pass}";
        
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM gateway_policies WHERE id LIKE 'test-%' OR id LIKE 'e2e-%' OR id LIKE 'persist-test-%'",
            conn
        );
        
        var deleted = await cmd.ExecuteNonQueryAsync();
        if (deleted > 0)
        {
            Console.WriteLine($"[PolicyCleanup] Cleaned up {deleted} test policies via PostgreSQL (fallback)");
            
            // Gateway needs restart to reload policies (should be 0 now)
            await RestartGatewayContainerAsync();
        }
    } // End of Method CleanupViaPostgreSqlAsync
    
    private static async Task RestartGatewayContainerAsync()
    {
        try
        {
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
            if (process == null) return;
            
            await process.WaitForExitAsync();
            
            if (process.ExitCode == 0)
            {
                Console.WriteLine("[PolicyCleanup] Gateway restarted after PostgreSQL cleanup");
                await Task.Delay(TimeSpan.FromSeconds(8)); // Give Gateway time to start
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PolicyCleanup] Warning: Failed to restart Gateway: {ex.Message}");
        }
    } // End of Method RestartGatewayContainerAsync
    
    private record PolicyDto(string Id, int Type, int Mode, string Description, bool Enabled);
} // End of Class PolicyCleanupHelper
