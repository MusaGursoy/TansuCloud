// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class GatewayAdminRateLimitsValidationTests
{
    private static string GetGatewayBaseUrl()
    {
        return TestUrls.GatewayBaseUrl;
    }

    private sealed record RateLimitDefaults
    {
        public int PermitLimit { get; set; }
        public int QueueLimit { get; set; }
    }

    private sealed record RateLimitRouteOverride
    {
        public int? PermitLimit { get; set; }
        public int? QueueLimit { get; set; }
    }

    private sealed record RateLimitConfigDto
    {
        public int WindowSeconds { get; set; }
        public RateLimitDefaults? Defaults { get; set; }
        public Dictionary<string, RateLimitRouteOverride>? Routes { get; set; }
    }

    [Fact(DisplayName = "Admin POST /admin/api/rate-limits validates input and returns 400")]
    public async Task Admin_Post_RateLimits_Invalid_Returns_400_ProblemDetails()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var baseUrl = GetGatewayBaseUrl();

        // Wait briefly for gateway readiness
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/health/live", cts.Token);
                if ((int)ping.StatusCode < 500)
                    break;
            }
            catch { }
            await Task.Delay(250, cts.Token);
        }

        var invalid = new RateLimitConfigDto
        {
            WindowSeconds = 0 // invalid: must be >= 1
        };

        using var res = await client.PostAsJsonAsync(
            $"{baseUrl}/admin/api/rate-limits",
            invalid,
            cts.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var json = await res.Content.ReadAsStringAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(json));

        // Parse as generic ProblemDetails-like object to assert structure
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("title", out var titleProp));
        Assert.Contains("Validation", titleProp.GetString() ?? string.Empty);
    }
} // End of Class GatewayAdminRateLimitsValidationTests
