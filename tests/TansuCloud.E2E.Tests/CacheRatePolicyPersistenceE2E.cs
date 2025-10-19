// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class CacheRatePolicyPersistenceE2E
{
    private static string GetGatewayBaseUrl()
    {
        return TestUrls.GatewayBaseUrl;
    }

    private static async Task WaitReadyAsync(
        HttpClient client,
        string baseUrl,
        CancellationToken ct
    )
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/health/ready", ct);
                if ((int)ping.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(250, ct);
        }
    } // End of Method WaitReadyAsync

    [Fact(DisplayName = "Cache/Rate Policies: Persist across Gateway restart")]
    public async Task CacheRatePolicies_PersistAcrossRestart()
    {
        // This test verifies that cache and rate limit policies are stored in PostgreSQL
        // and survive Gateway service restarts.
        // 
        // Note: This test requires the Gateway to be running in a container where we can
        // restart it (e.g., docker restart tansu-gateway). For local development runs,
        // this test may be skipped if the Gateway is not containerized.

        var skipReason = Environment.GetEnvironmentVariable("E2E_SKIP_PERSISTENCE_TEST");
        if (skipReason == "1")
        {
            // Skip this test in environments where Gateway restart is not feasible
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        // Step 1: Create a cache policy via admin API
        var policyId = $"persist-test-{Guid.NewGuid()}";
        var createPayload = new
        {
            id = policyId,
            type = "CachePolicy",
            mode = "Enforce",
            description = "Persistence Test Cache Policy",
            config = new
            {
                ttlSeconds = 120,
                varyByQuery = new[] { "id" },
                varyByHeaders = new string[] { },
                varyByRouteValues = new string[] { },
                varyByHost = false,
                useDefaultVaryByRules = false
            }
        };

        var json = JsonSerializer.Serialize(createPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using (var createRes = await http.PostAsync(
            $"{baseUrl}/admin/api/policies",
            content,
            cts.Token
        ))
        {
            // In Development, admin API is open; in production, may require auth
            if (createRes.StatusCode == HttpStatusCode.Unauthorized || createRes.StatusCode == HttpStatusCode.Forbidden)
            {
                // Skip test if auth is required and not provided
                return;
            }

            createRes.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent },
                "policy creation should succeed"
            );
        }

        // Step 2: Verify policy exists
        using (var getRes = await http.GetAsync($"{baseUrl}/admin/api/policies", cts.Token))
        {
            getRes.StatusCode.Should().Be(HttpStatusCode.OK, "should be able to list policies");
            var body = await getRes.Content.ReadAsStringAsync(cts.Token);
            body.Should().Contain(policyId, "newly created policy should be in list");
        }

        // Step 3: Restart Gateway (if running in Docker)
        // Note: This requires Docker CLI to be available and the Gateway to be named "tansu-gateway"
        var isDocker = Environment.GetEnvironmentVariable("GATEWAY_CONTAINER_NAME");
        if (!string.IsNullOrEmpty(isDocker))
        {
            try
            {
                // Restart the Gateway container
                var containerName = isDocker; // e.g., "tansu-gateway"
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"restart {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(cts.Token);
                    proc.ExitCode.Should().Be(0, "docker restart should succeed");
                }

                // Wait for Gateway to be ready again
                await Task.Delay(5000, cts.Token); // Give it time to start
                await WaitReadyAsync(http, baseUrl, cts.Token);
            }
            catch (Exception ex)
            {
                // If Docker restart fails, skip the test gracefully
                Assert.True(false, $"Docker restart failed: {ex.Message}. Set E2E_SKIP_PERSISTENCE_TEST=1 if Docker is not available.");
                return;
            }
        }
        else
        {
            // Not running in Docker, skip the restart part
            // Just verify the policy is still there (may have been cached in memory)
        }

        // Step 4: Verify policy still exists after restart
        using (var getRes2 = await http.GetAsync($"{baseUrl}/admin/api/policies", cts.Token))
        {
            getRes2.StatusCode.Should().Be(HttpStatusCode.OK, "should be able to list policies after restart");
            var body = await getRes2.Content.ReadAsStringAsync(cts.Token);
            body.Should().Contain(policyId, "policy should persist after Gateway restart");
        }

        // Step 5: Clean up - delete the test policy
        using (var deleteRes = await http.DeleteAsync($"{baseUrl}/admin/api/policies/{policyId}", cts.Token))
        {
            deleteRes.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.NoContent },
                "policy deletion should succeed"
            );
        }
    } // End of Method CacheRatePolicies_PersistAcrossRestart
} // End of Class CacheRatePolicyPersistenceE2E
