// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class VectorSearchE2E
{
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static string GetGatewayBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var uri = new Uri(env);
                var host =
                    (
                        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || uri.Host == "::1"
                    )
                        ? "127.0.0.1"
                        : uri.Host;
                var b = new UriBuilder(uri) { Host = host };
                return b.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return env.TrimEnd('/');
            }
        }
        return "http://127.0.0.1:8080";
    }

    private static async Task WaitForAllAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        // gateway
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/health/live", ct);
                if ((int)r.StatusCode < 500)
                    break;
            }
            catch { }
            await Task.Delay(300, ct);
        }
        // identity
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/identity/health/live", ct);
                if ((int)r.StatusCode < 500)
                    break;
            }
            catch { }
            await Task.Delay(300, ct);
        }
        // db
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/db/health/live", ct);
                if ((int)r.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(300, ct);
        }
        throw new TimeoutException("Services not ready");
    }

    private static async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        CancellationToken ct
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            last?.Dispose();
            try
            {
                using var req = createRequest();
                last = await client.SendAsync(req, ct);
                if (
                    last.StatusCode != HttpStatusCode.Unauthorized
                    && last.StatusCode != HttpStatusCode.Forbidden
                    && (int)last.StatusCode < 500
                )
                    return last;
            }
            catch { }
            await Task.Delay(250, ct);
        }
        return last!;
    }

    private static async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        using var tokenReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/identity/connect/token"
        );
        tokenReq.Content = new StringContent(
            "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=db.write%20db.read",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );
        using var tokenRes = await client.SendAsync(tokenReq, ct);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        var tokenJson = await tokenRes.Content.ReadAsStringAsync(ct);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        return tokenDoc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<(Guid id, Guid coll)> SeedDocAsync(
        HttpClient client,
        string token,
        string tenant,
        string collName,
        float[]? embedding,
        CancellationToken ct
    )
    {
        var baseUrl = GetGatewayBaseUrl();
        // create collection
        using var cres = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/db/api/collections");
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                r.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(new { name = collName }),
                    Encoding.UTF8,
                    "application/json"
                );
                return r;
            },
            ct
        );
        Assert.Equal(HttpStatusCode.Created, cres.StatusCode);
        var cjson = await cres.Content.ReadAsStringAsync(ct);
        using var cdoc = JsonDocument.Parse(cjson);
        var collId = cdoc.RootElement.GetProperty("id").GetGuid();

        // create document
        using var dres = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/db/api/documents");
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                r.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            collectionId = collId,
                            content = "vdoc",
                            embedding = embedding
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                );
                return r;
            },
            ct
        );
        Assert.Equal(HttpStatusCode.Created, dres.StatusCode);
        var djson = await dres.Content.ReadAsStringAsync(ct);
        using var ddoc = JsonDocument.Parse(djson);
        var id = ddoc.RootElement.GetProperty("id").GetGuid();
        return (id, collId);
    }

    [Fact(DisplayName = "Vector search: per-collection returns 200 or 501 fallback")]
    public async Task Vector_PerCollection_Basic()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForAllAsync(client, cts.Token);
        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenant = $"e2e-{Environment.MachineName.ToLowerInvariant()}";

        // Seed with an embedding length 4 (pgvector schema expects 1536, but controller tolerates and updates when column exists)
        var (_, coll) = await SeedDocAsync(
            client,
            token,
            tenant,
            "Vec-A",
            new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
            cts.Token
        );

        var baseUrl = GetGatewayBaseUrl();
        using var res = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var r = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/db/api/documents/search/vector"
                );
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                r.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            collectionId = coll,
                            query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
                            k = 5
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                );
                return r;
            },
            cts.Token
        );

        Assert.True(
            res.StatusCode == HttpStatusCode.OK || res.StatusCode == HttpStatusCode.NotImplemented,
            $"Unexpected status {(int)res.StatusCode} {res.StatusCode}: {await res.Content.ReadAsStringAsync(cts.Token)}"
        );
    }

    [Fact(DisplayName = "Vector search: global returns 200 or 501 fallback")]
    public async Task Vector_Global_Basic()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForAllAsync(client, cts.Token);
        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenant = $"e2e-{Environment.MachineName.ToLowerInvariant()}";

        // Seed two collections
        _ = await SeedDocAsync(
            client,
            token,
            tenant,
            "Vec-G1",
            new float[] { 0.05f, 0.06f, 0.07f, 0.08f },
            cts.Token
        );
        _ = await SeedDocAsync(
            client,
            token,
            tenant,
            "Vec-G2",
            new float[] { 0.11f, 0.12f, 0.13f, 0.14f },
            cts.Token
        );

        var baseUrl = GetGatewayBaseUrl();
        using var res = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var r = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/db/api/documents/search/vector-global"
                );
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                r.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);
                r.Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
                            k = 5,
                            perCollection = 2
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                );
                return r;
            },
            cts.Token
        );
        Assert.True(
            res.StatusCode == HttpStatusCode.OK || res.StatusCode == HttpStatusCode.NotImplemented,
            $"Unexpected status {(int)res.StatusCode} {res.StatusCode}: {await res.Content.ReadAsStringAsync(cts.Token)}"
        );
    }
}
