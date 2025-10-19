// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using TansuCloud.E2E.Tests.Infrastructure;
using Xunit;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// E2E tests for Policy Enforcement Middleware.
/// Tests CORS, IP Allow, IP Deny policies with Shadow, Audit Only, and Enforce modes.
/// </summary>
[Collection("Global")]
public class PolicyEnforcementE2E : IAsyncLifetime
{
    private HttpClient? _client;
    private readonly string _gatewayBaseUrl;

    public PolicyEnforcementE2E()
    {
        _gatewayBaseUrl = TestUrls.GatewayBaseUrl;
    }

    public async Task InitializeAsync()
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(_gatewayBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Wait for gateway readiness (cleanup now handled by Global fixture)
        var maxWait = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            try
            {
                var response = await _client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    await Task.Delay(1000); // Extra stabilization
                    return;
                }
            }
            catch
            {
                // Ignore, retry
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("Gateway did not become ready in time");
    }

    [Fact(DisplayName = "CORS policy in Enforce mode blocks disallowed origin")]
    public async Task Cors_Enforce_BlocksDisallowedOrigin()
    {
        // Skip if not in compose environment (requires admin API)
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-cors-enforce-{Guid.NewGuid():N}";

        try
        {
            // Create CORS policy in Enforce mode allowing only specific origin
            var corsPolicy = new
            {
                id = policyId,
                type = 0, // Cors
                mode = 2, // Enforce
                description = "Test CORS enforcement",
                enabled = true,
                config = new
                {
                    origins = new[] { "https://allowed.example.com" },
                    methods = new[] { "GET", "POST" },
                    headers = new[] { "Content-Type" },
                    allowCredentials = false,
                    maxAgeSeconds = 3600
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", corsPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue("policy should be created");

            // Wait for policy to be applied
            await Task.Delay(500);

            // Test with disallowed origin - should be blocked
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("Origin", "https://evil.example.com");

            var response = await _client.SendAsync(request);
            
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "disallowed origin should be blocked in Enforce mode");

            // Test with allowed origin - should succeed
            var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            allowedRequest.Headers.Add("Origin", "https://allowed.example.com");

            var allowedResponse = await _client.SendAsync(allowedRequest);
            
            allowedResponse.IsSuccessStatusCode.Should().BeTrue("allowed origin should succeed");
            allowedResponse.Headers.Should().Contain(h => h.Key == "Access-Control-Allow-Origin", "CORS headers should be present");
        }
        finally
        {
            // Cleanup
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "CORS policy in Shadow mode logs but does not block")]
    public async Task Cors_Shadow_LogsButDoesNotBlock()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-cors-shadow-{Guid.NewGuid():N}";

        try
        {
            // Create CORS policy in Shadow mode
            var corsPolicy = new
            {
                id = policyId,
                type = 0, // Cors
                mode = 0, // Shadow
                description = "Test CORS shadow mode",
                enabled = true,
                config = new
                {
                    origins = new[] { "https://allowed.example.com" },
                    methods = new[] { "GET" },
                    headers = new[] { "Content-Type" },
                    allowCredentials = false,
                    maxAgeSeconds = 3600
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", corsPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Test with disallowed origin - should NOT be blocked in Shadow mode
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("Origin", "https://evil.example.com");

            var response = await _client.SendAsync(request);
            
            response.IsSuccessStatusCode.Should().BeTrue("Shadow mode should not block requests");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "CORS preflight request receives proper headers")]
    public async Task Cors_Preflight_ReceivesProperHeaders()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-cors-preflight-{Guid.NewGuid():N}";

        try
        {
            var corsPolicy = new
            {
                id = policyId,
                type = 0, // Cors
                mode = 2, // Enforce
                description = "Test CORS preflight",
                enabled = true,
                config = new
                {
                    origins = new[] { "https://app.example.com" },
                    methods = new[] { "GET", "POST", "PUT" },
                    headers = new[] { "Content-Type", "Authorization" },
                    allowCredentials = true,
                    maxAgeSeconds = 7200
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", corsPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Send OPTIONS preflight request to a proxied route (not health check endpoint)
            // Health checks bypass middleware, so we use identity OIDC discovery endpoint
            var request = new HttpRequestMessage(HttpMethod.Options, "/identity/.well-known/openid-configuration");
            request.Headers.Add("Origin", "https://app.example.com");
            request.Headers.Add("Access-Control-Request-Method", "GET");

            var response = await _client.SendAsync(request);
            
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "preflight should return 204");
            response.Headers.Should().Contain(h => h.Key == "Access-Control-Allow-Origin");
            response.Headers.Should().Contain(h => h.Key == "Access-Control-Allow-Methods");
            response.Headers.Should().Contain(h => h.Key == "Access-Control-Allow-Headers");
            response.Headers.Should().Contain(h => h.Key == "Access-Control-Max-Age");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "IP Deny policy blocks listed IPs in Enforce mode")]
    public async Task IpDeny_Enforce_BlocksListedIps()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-ip-deny-{Guid.NewGuid():N}";

        try
        {
            // Create IP Deny policy for loopback (127.0.0.1)
            var ipDenyPolicy = new
            {
                id = policyId,
                type = 2, // IpDeny
                mode = 2, // Enforce
                description = "Test IP deny enforcement",
                enabled = true,
                config = new
                {
                    cidrs = new[] { "127.0.0.1/32" },
                    description = "Deny localhost"
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", ipDenyPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Request from localhost should be blocked
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var response = await _client.SendAsync(request);
            
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "IP in deny list should be blocked");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "IP Deny policy in Shadow mode does not block")]
    public async Task IpDeny_Shadow_DoesNotBlock()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-ip-deny-shadow-{Guid.NewGuid():N}";

        try
        {
            var ipDenyPolicy = new
            {
                id = policyId,
                type = 2, // IpDeny
                mode = 0, // Shadow
                description = "Test IP deny shadow mode",
                enabled = true,
                config = new
                {
                    cidrs = new[] { "127.0.0.1/32" },
                    description = "Deny localhost (shadow)"
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", ipDenyPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Request should succeed despite IP being in deny list (Shadow mode)
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var response = await _client.SendAsync(request);
            
            response.IsSuccessStatusCode.Should().BeTrue("Shadow mode should not block");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "IP Allow policy allows only listed IPs in Enforce mode")]
    public async Task IpAllow_Enforce_AllowsOnlyListedIps()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        var policyId = $"test-ip-allow-{Guid.NewGuid():N}";

        try
        {
            // Create IP Allow policy for loopback AND Docker internal network
            // IMPORTANT: Must include Docker network (172.16.0.0/12) to avoid blocking Gateway itself
            var ipAllowPolicy = new
            {
                id = policyId,
                type = 1, // IpAllow
                mode = 2, // Enforce
                description = "Test IP allow enforcement",
                enabled = true,
                config = new
                {
                    cidrs = new[] 
                    { 
                        "127.0.0.1/32",      // IPv4 loopback
                        "::1/128",           // IPv6 loopback
                        "172.16.0.0/12"      // Docker internal network (prevents Gateway blocking)
                    },
                    description = "Allow localhost and Docker internal network"
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", ipAllowPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Request from localhost should succeed (in allow list)
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var response = await _client.SendAsync(request);
            
            response.IsSuccessStatusCode.Should().BeTrue("IP in allow list should succeed");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "Multiple policies are evaluated in correct order (Deny > Allow > CORS)")]
    public async Task MultiplePolicies_EvaluatedInOrder()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        // Clean up any orphaned policies from previous tests
        await CleanupTestPolicies();

        var denyPolicyId = $"test-deny-{Guid.NewGuid():N}";
        var allowPolicyId = $"test-allow-{Guid.NewGuid():N}";
        var corsPolicyId = $"test-cors-{Guid.NewGuid():N}";

        try
        {
            // Create IP Deny policy in Shadow mode (should log but not block)
            var denyPolicy = new
            {
                id = denyPolicyId,
                type = 2, // IpDeny
                mode = 0, // Shadow
                description = "Test deny (shadow)",
                enabled = true,
                config = new
                {
                    cidrs = new[] { "127.0.0.1/32" },
                    description = "Deny localhost (shadow)"
                }
            };

            // Create IP Allow policy in Enforce mode (should allow localhost AND Docker network)
            var allowPolicy = new
            {
                id = allowPolicyId,
                type = 1, // IpAllow
                mode = 2, // Enforce
                description = "Test allow (enforce)",
                enabled = true,
                config = new
                {
                    cidrs = new[] 
                    { 
                        "127.0.0.1/32",      // Loopback
                        "172.16.0.0/12"      // Docker internal network
                    },
                    description = "Allow localhost and Docker internal"
                }
            };

            // Create CORS policy in Enforce mode
            var corsPolicy = new
            {
                id = corsPolicyId,
                type = 0, // Cors
                mode = 2, // Enforce
                description = "Test CORS (enforce)",
                enabled = true,
                config = new
                {
                    origins = new[] { "https://app.example.com" },
                    methods = new[] { "GET" },
                    headers = new[] { "Content-Type" },
                    allowCredentials = false,
                    maxAgeSeconds = 3600
                }
            };

            await _client!.PostAsJsonAsync("/admin/api/policies", denyPolicy);
            await _client.PostAsJsonAsync("/admin/api/policies", allowPolicy);
            await _client.PostAsJsonAsync("/admin/api/policies", corsPolicy);

            await Task.Delay(500);

            // Request without origin - should succeed (IP allowed, no CORS check needed)
            var simpleResponse = await _client.GetAsync("/health/live");
            simpleResponse.IsSuccessStatusCode.Should().BeTrue("IP is in allow list");

            // Request with allowed origin - should succeed with CORS headers
            var corsRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            corsRequest.Headers.Add("Origin", "https://app.example.com");
            var corsResponse = await _client.SendAsync(corsRequest);
            
            corsResponse.IsSuccessStatusCode.Should().BeTrue("allowed origin should succeed");
            corsResponse.Headers.Should().Contain(h => h.Key == "Access-Control-Allow-Origin");

            // Request with disallowed origin - should be blocked by CORS
            var badCorsRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            badCorsRequest.Headers.Add("Origin", "https://evil.example.com");
            var badCorsResponse = await _client.SendAsync(badCorsRequest);
            
            badCorsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "disallowed origin should be blocked");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{denyPolicyId}");
            await _client.DeleteAsync($"/admin/api/policies/{allowPolicyId}");
            await _client.DeleteAsync($"/admin/api/policies/{corsPolicyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    [Fact(DisplayName = "Disabled policy is not enforced")]
    public async Task DisabledPolicy_NotEnforced()
    {
        if (!IsComposeEnvironment())
        {
            return;
        }

        // Clean up any orphaned policies from previous tests
        await CleanupTestPolicies();

        var policyId = $"test-disabled-{Guid.NewGuid():N}";

        try
        {
            // Create CORS policy that would block, but disabled
            var corsPolicy = new
            {
                id = policyId,
                type = 0, // Cors
                mode = 2, // Enforce
                description = "Test disabled policy",
                enabled = false, // Disabled
                config = new
                {
                    origins = new[] { "https://never-matches.example.com" },
                    methods = new[] { "GET" },
                    headers = new[] { "Content-Type" },
                    allowCredentials = false,
                    maxAgeSeconds = 3600
                }
            };

            var createResponse = await _client!.PostAsJsonAsync("/admin/api/policies", corsPolicy);
            createResponse.IsSuccessStatusCode.Should().BeTrue();

            await Task.Delay(500);

            // Request with any origin - should succeed because policy is disabled
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("Origin", "https://any.example.com");

            var response = await _client.SendAsync(request);
            
            response.IsSuccessStatusCode.Should().BeTrue("disabled policy should not be enforced");
        }
        finally
        {
            await _client!.DeleteAsync($"/admin/api/policies/{policyId}");
            // Give Gateway time to reload policies after deletion
            await Task.Delay(500);
        }
    }

    private async Task CleanupTestPolicies()
    {
        // Use centralized cleanup helper
        await TestPolicyCleanupHelper.CleanupTestPoliciesAsync();
    }

    private bool IsComposeEnvironment()
    {
        // Check if we're in a compose environment where admin API is available
        var runningInCompose = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        return runningInCompose || _gatewayBaseUrl.Contains("127.0.0.1");
    }

    public async Task DisposeAsync()
    {
        // Use centralized cleanup helper
        await TestPolicyCleanupHelper.CleanupTestPoliciesAsync();
        
        _client?.Dispose();
    }
} // End of Class PolicyEnforcementE2E
