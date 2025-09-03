// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class ProvisioningE2E
{
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static string GetGatewayBaseUrl()
    {
        // Prefer env var override, fallback to the dev Kestrel HTTP endpoint
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var uri = new Uri(env);
                var host = (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "::1")
                    ? "127.0.0.1"
                    : uri.Host;
                var builder = new UriBuilder(uri) { Host = host };
                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return env.TrimEnd('/');
            }
        }
        return "http://127.0.0.1:8080";
    }

    private static async Task WaitForGatewayAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/health/live", ct);
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

    [Fact(DisplayName = "Provisioning: idempotent tenant create via gateway with token")]
    public async Task Provision_Tenant_Idempotent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var baseUrl = GetGatewayBaseUrl();

        // 1) Get access token via client_credentials through the gateway
        var tokenReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/identity/connect/token"
        );
        tokenReq.Content = new StringContent(
            "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=db.write%20admin.full",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );
        using var tokenRes = await client.SendAsync(tokenReq, cts.Token);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        var tokenJson = await tokenRes.Content.ReadAsStringAsync(cts.Token);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));

        // Stable tenant id for idempotency verification, namespaced to avoid collisions
        var tenantId = $"e2e-{Environment.MachineName.ToLowerInvariant()}";
        var body = JsonSerializer.Serialize(new { tenantId, displayName = "E2E Tenant" });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        // 2) First provision call
        var req1 = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/db/api/provisioning/tenants"
        );
        req1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        req1.Content = content;
        using var res1 = await client.SendAsync(req1, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var json1 = await res1.Content.ReadAsStringAsync(cts.Token);
        using var doc1 = JsonDocument.Parse(json1);
        // Created may be true on first run, false on subsequent runs; we don't assert here except 200

        // 3) Second provision call (idempotency must yield Created=false)
        var req2 = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/db/api/provisioning/tenants"
        );
        req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        req2.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res2 = await client.SendAsync(req2, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var json2 = await res2.Content.ReadAsStringAsync(cts.Token);
        using var doc2 = JsonDocument.Parse(json2);
        var created2 = doc2.RootElement.GetProperty("created").GetBoolean();
        Assert.False(created2); // must not create twice
    }
} // End of Class ProvisioningE2E
