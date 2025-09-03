// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class CollectionsApiConditionalTests
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
    } // End of Method CreateClient

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
    } // End of Method GetGatewayBaseUrl

    private static async Task WaitForGatewayAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/health/live", ct);
                if ((int)ping.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Gateway not ready");
    } // End of Method WaitForGatewayAsync

    private static async Task WaitForDbAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/db/health/live", ct);
                if ((int)ping.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Database service not ready");
    } // End of Method WaitForDbAsync

    private static async Task WaitForIdentityAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/identity/health/live", ct);
                if ((int)ping.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Identity service not ready");
    } // End of Method WaitForIdentityAsync

    private static async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        CancellationToken ct
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? last = null;
        Exception? lastEx = null;
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
                    return last; // success or deterministic client error
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
            await Task.Delay(250, ct);
        }
        if (last is not null)
            return last;
        throw new HttpRequestException("Request failed after retries", lastEx);
    } // End of Method SendWithAuthRetryAsync

    private static async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        using var tokenReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/identity/connect/token"
        );
        tokenReq.Content = new StringContent(
            "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=db.write%20db.read%20admin.full",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );
        using var tokenRes = await client.SendAsync(tokenReq, ct);
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        var tokenJson = await tokenRes.Content.ReadAsStringAsync(ct);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        await Task.Delay(1000, ct); // warm-up
        return accessToken!;
    } // End of Method GetAccessTokenAsync

    private static async Task EnsureTenantAsync(
        HttpClient client,
        string token,
        string tenantId,
        CancellationToken ct
    )
    {
        var baseUrl = GetGatewayBaseUrl();
        using var res = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/db/api/provisioning/tenants"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var body = JsonSerializer.Serialize(new { tenantId, displayName = "E2E Tenant" });
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return req;
            },
            ct
        );
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    } // End of Method EnsureTenantAsync

    private static string GetETag(HttpResponseMessage res)
    {
        var tag = res.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(tag))
            return tag!;
        if (res.Headers.TryGetValues("ETag", out var vals))
        {
            var first = vals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return first!;
        }
        return string.Empty;
    } // End of Method GetETag

    private static async Task<(Guid id, string etag)> CreateCollectionAsync(
        HttpClient client,
        string token,
        string tenantId,
        string name,
        CancellationToken ct
    )
    {
        var baseUrl = GetGatewayBaseUrl();
        using var res = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/db/api/collections");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { name }),
                    Encoding.UTF8,
                    "application/json"
                );
                return req;
            },
            ct
        );
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetGuid();
        var etag = GetETag(res);
        Assert.False(string.IsNullOrWhiteSpace(etag));
        return (id, etag!);
    } // End of Method CreateCollectionAsync

    [Fact(DisplayName = "Collections: List supports ETag 304")]
    public async Task Collections_List_IfNoneMatch_304()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);
        await WaitForIdentityAsync(client, cts.Token);
        await WaitForDbAsync(client, cts.Token);

        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenantId = $"e2e-{Environment.MachineName.ToLowerInvariant()}";
        await EnsureTenantAsync(client, token, tenantId, cts.Token);

        _ = await CreateCollectionAsync(client, token, tenantId, "C1", cts.Token);
        _ = await CreateCollectionAsync(client, token, tenantId, "C2", cts.Token);

        var baseUrl = GetGatewayBaseUrl();
        using var listRes = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/db/api/collections?page=1&pageSize=10"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                return req;
            },
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var etag = GetETag(listRes);
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using var listRes2 = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/db/api/collections?page=1&pageSize=10"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return req;
            },
            cts.Token
        );
        Assert.Equal(HttpStatusCode.NotModified, listRes2.StatusCode);
    } // End of Method Collections_List_IfNoneMatch_304

    [Fact(DisplayName = "Collections: Update honors If-Match 412 on mismatch")]
    public async Task Collections_Update_IfMatch_412_On_Mismatch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);
        await WaitForIdentityAsync(client, cts.Token);
        await WaitForDbAsync(client, cts.Token);

        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenantId = $"e2e-{Environment.MachineName.ToLowerInvariant()}";
        await EnsureTenantAsync(client, token, tenantId, cts.Token);

        var (id, etag) = await CreateCollectionAsync(
            client,
            token,
            tenantId,
            "Cond-Coll",
            cts.Token
        );

        var baseUrl = GetGatewayBaseUrl();
        using var badPut = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{baseUrl}/db/api/collections/{id}"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                req.Headers.TryAddWithoutValidation("If-Match", "\"bogus\"");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { name = "Updated" }),
                    Encoding.UTF8,
                    "application/json"
                );
                return req;
            },
            cts.Token
        );
        Assert.Equal(HttpStatusCode.PreconditionFailed, badPut.StatusCode);

        using var okPut = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{baseUrl}/db/api/collections/{id}"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                req.Headers.TryAddWithoutValidation("If-Match", etag);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { name = "Updated" }),
                    Encoding.UTF8,
                    "application/json"
                );
                return req;
            },
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, okPut.StatusCode);
        var json = await okPut.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Updated");
    } // End of Method Collections_Update_IfMatch_412_On_Mismatch
} // End of Class CollectionsApiConditionalTests
