// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class HealthEndpointsE2E
{
    private static string GetGatewayBaseUrl()
    {
        return TestUrls.GatewayBaseUrl;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async Task WaitForGatewayAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/", ct);
                if ((int)ping.StatusCode < 500)
                {
                    return; // Gateway is up enough to route health requests
                }
            }
            catch
            {
                // swallow and retry until cancellation
            }
            await Task.Delay(500, ct);
        }
    }

    [Theory(DisplayName = "Health: live endpoints return 200 via gateway for all services")]
    [InlineData("/")]
    [InlineData("/identity/")]
    [InlineData("/dashboard/")]
    [InlineData("/db/")]
    [InlineData("/storage/")]
    public async Task Live_Endpoints_200(string basePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var trimmed = basePath.EndsWith('/') ? basePath[..^1] : basePath;
        var url = $"{GetGatewayBaseUrl()}{trimmed}/health/live";
        using var res = await client.GetAsync(url, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory(DisplayName = "Health: readiness endpoints return 200 via gateway for all services")]
    [InlineData("/")]
    [InlineData("/identity/")]
    [InlineData("/dashboard/")]
    [InlineData("/db/")]
    [InlineData("/storage/")]
    public async Task Ready_Endpoints_200(string basePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var trimmed = basePath.EndsWith('/') ? basePath[..^1] : basePath;
        var url = $"{GetGatewayBaseUrl()}{trimmed}/health/ready";
        using var res = await client.GetAsync(url, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact(DisplayName = "Health: Database readiness includes schema validation status")]
    public async Task Database_Ready_Includes_Schema_Status()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var url = $"{GetGatewayBaseUrl()}/db/health/ready";
        using var res = await client.GetAsync(url, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var content = await res.Content.ReadAsStringAsync(cts.Token);
        // Verify response contains schema-related information
        Assert.Contains("Identity", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Health: Database readiness includes tenant count")]
    public async Task Database_Ready_Includes_Tenant_Count()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var url = $"{GetGatewayBaseUrl()}/db/health/ready";
        using var res = await client.GetAsync(url, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var content = await res.Content.ReadAsStringAsync(cts.Token);
        // Verify response contains tenant-related information
        Assert.Contains("tenant", content, StringComparison.OrdinalIgnoreCase);
    }
} // End of Class HealthEndpointsE2E
