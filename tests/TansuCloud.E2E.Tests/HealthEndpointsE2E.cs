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
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');
        // Default to repo's dev gateway binding
        return "http://localhost:8080";
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
        for (var i = 0; i < 12; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/", ct);
                if ((int)ping.StatusCode < 500)
                {
                    return;
                }
            }
            catch
            {
                // swallow and retry
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
} // End of Class HealthEndpointsE2E
