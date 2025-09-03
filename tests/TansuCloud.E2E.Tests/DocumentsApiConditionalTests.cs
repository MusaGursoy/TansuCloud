// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class DocumentsApiConditionalTests
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
        // Mitigate startup races where Database JWT bearer hasn't fetched OIDC/JWKS yet.
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

    private static async Task WarmUpAuthAsync(
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
                    HttpMethod.Get,
                    $"{baseUrl}/db/api/collections?page=1&pageSize=1"
                );
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                return req;
            },
            ct
        );
        if (res.StatusCode != HttpStatusCode.OK)
        {
            var diag = new StringBuilder();
            if (res.Headers.TryGetValues("WWW-Authenticate", out var www))
                diag.AppendLine("WWW-Authenticate: " + string.Join(" ", www));
            if (res.Headers.TryGetValues("X-Auth-Prefix", out var xp))
                diag.AppendLine("X-Auth-Prefix: " + string.Join(" ", xp));
            if (res.Headers.TryGetValues("X-Jwt-ErrType", out var et))
                diag.AppendLine("X-Jwt-ErrType: " + string.Join(" ", et));
            if (res.Headers.TryGetValues("X-Jwt-Err", out var em))
                diag.AppendLine("X-Jwt-Err: " + string.Join(" ", em));
            if (res.Headers.TryGetValues("X-Jwt-Issuer", out var iss))
                diag.AppendLine("X-Jwt-Issuer: " + string.Join(" ", iss));
            if (res.Headers.TryGetValues("X-Jwt-Metadata", out var md))
                diag.AppendLine("X-Jwt-Metadata: " + string.Join(" ", md));
            if (res.Headers.TryGetValues("X-Jwt-HdrOk", out var hk))
                diag.AppendLine("X-Jwt-HdrOk: " + string.Join(" ", hk));
            if (res.Headers.TryGetValues("X-Jwt-Segs", out var js))
                diag.AppendLine("X-Jwt-Segs: " + string.Join(" ", js));
            if (res.Headers.TryGetValues("X-Jwt-Hdr", out var hdr))
                diag.AppendLine("X-Jwt-Hdr: " + string.Join(" ", hdr));
            if (res.Headers.TryGetValues("X-Jwt-HdrB64", out var hdrb64))
                diag.AppendLine("X-Jwt-HdrB64: " + string.Join(" ", hdrb64));
            if (res.Headers.TryGetValues("X-Jwt-Seg0-Len", out var s0))
                diag.AppendLine("X-Jwt-Seg0-Len: " + string.Join(" ", s0));
            if (res.Headers.TryGetValues("X-Jwt-HdrErr", out var herr))
                diag.AppendLine("X-Jwt-HdrErr: " + string.Join(" ", herr));
            if (res.Headers.TryGetValues("X-Jwt-CandidateLen", out var clen))
                diag.AppendLine("X-Jwt-CandidateLen: " + string.Join(" ", clen));
            if (res.Headers.TryGetValues("X-Jwt-CandidateHead", out var chead))
                diag.AppendLine("X-Jwt-CandidateHead: " + string.Join(" ", chead));
            if (res.Headers.TryGetValues("X-Jwt-TokenHead", out var thead))
                diag.AppendLine("X-Jwt-TokenHead: " + string.Join(" ", thead));
            if (res.Headers.TryGetValues("X-Jwt-TokenEqCandidate", out var teq))
                diag.AppendLine("X-Jwt-TokenEqCandidate: " + string.Join(" ", teq));
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Auth warm-up failed: {(int)res.StatusCode} {res.StatusCode}.\n{diag}\n{body}"
            );
        }
    } // End of Method WarmUpAuthAsync

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
        // Give Database service a moment to ensure JWT metadata/JWKS warm-up
        await Task.Delay(1000, ct);
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
        // Prefer the structured ETag header if present
        var tag = res.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(tag))
            return tag!;
        // Fallback to raw header
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
        if (res.StatusCode != HttpStatusCode.Created)
        {
            var diag = new StringBuilder();
            if (res.Headers.TryGetValues("WWW-Authenticate", out var www))
                diag.AppendLine("WWW-Authenticate: " + string.Join(" ", www));
            if (res.Headers.TryGetValues("X-Auth-Prefix", out var xp))
                diag.AppendLine("X-Auth-Prefix: " + string.Join(" ", xp));
            if (res.Headers.TryGetValues("X-Jwt-ErrType", out var et))
                diag.AppendLine("X-Jwt-ErrType: " + string.Join(" ", et));
            if (res.Headers.TryGetValues("X-Jwt-Err", out var em))
                diag.AppendLine("X-Jwt-Err: " + string.Join(" ", em));
            if (res.Headers.TryGetValues("X-Jwt-Issuer", out var iss))
                diag.AppendLine("X-Jwt-Issuer: " + string.Join(" ", iss));
            if (res.Headers.TryGetValues("X-Jwt-Metadata", out var md))
                diag.AppendLine("X-Jwt-Metadata: " + string.Join(" ", md));
            if (res.Headers.TryGetValues("X-Jwt-HdrOk", out var hk))
                diag.AppendLine("X-Jwt-HdrOk: " + string.Join(" ", hk));
            if (res.Headers.TryGetValues("X-Jwt-Segs", out var js))
                diag.AppendLine("X-Jwt-Segs: " + string.Join(" ", js));
            var bodyText = await res.Content.ReadAsStringAsync(ct);
            Assert.Fail(
                $"Expected Created, got {(int)res.StatusCode} {res.StatusCode}\n{diag}\n{bodyText}"
            );
        }
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetGuid();
        var etag = GetETag(res);
        Assert.False(string.IsNullOrWhiteSpace(etag));
        return (id, etag!);
    } // End of Method CreateCollectionAsync

    private static async Task<(Guid id, string etag)> CreateDocumentAsync(
        HttpClient client,
        string token,
        string tenantId,
        Guid collectionId,
        object? content,
        CancellationToken ct
    )
    {
        var baseUrl = GetGatewayBaseUrl();
        using var res = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/db/api/documents");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { collectionId, content }),
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
    } // End of Method CreateDocumentAsync

    [Fact(DisplayName = "Documents: List supports ETag 304 and sort/filter basics")]
    public async Task Documents_List_IfNoneMatch_304_And_SortFilter()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);
        await WaitForIdentityAsync(client, cts.Token);
        await WaitForDbAsync(client, cts.Token);

        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenantId = $"e2e-{Environment.MachineName.ToLowerInvariant()}";
        await EnsureTenantAsync(client, token, tenantId, cts.Token);

        // Arrange: two collections, 2 docs in A, 1 doc in B
        // Warm-up DB auth path to avoid transient 401s right after startup
        await WarmUpAuthAsync(client, token, tenantId, cts.Token);
        // Small delay to allow JWKS/config caches to warm
        await Task.Delay(500, cts.Token);
        var (colA, _) = await CreateCollectionAsync(client, token, tenantId, "A", cts.Token);
        var (colB, _) = await CreateCollectionAsync(client, token, tenantId, "B", cts.Token);
        var _d1 = await CreateDocumentAsync(
            client,
            token,
            tenantId,
            colA,
            new { name = "a1" },
            cts.Token
        );
        await Task.Delay(10, cts.Token);
        var _d2 = await CreateDocumentAsync(
            client,
            token,
            tenantId,
            colA,
            new { name = "a2" },
            cts.Token
        );
        await Task.Delay(10, cts.Token);
        var _d3 = await CreateDocumentAsync(
            client,
            token,
            tenantId,
            colB,
            new { name = "b1" },
            cts.Token
        );

        // First list to capture ETag
        var baseUrl = GetGatewayBaseUrl();
        using var listRes = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var listReq = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/db/api/documents?collectionId={colA}&page=1&pageSize=50&sortBy=createdAt&sortDir=asc"
                );
                listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                listReq.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                return listReq;
            },
            cts.Token
        );
        Assert.True(
            listRes.StatusCode == HttpStatusCode.OK,
            $"List failed: {(int)listRes.StatusCode} {listRes.StatusCode}\n{await listRes.Content.ReadAsStringAsync(cts.Token)}"
        );
        var etag = GetETag(listRes);
        Assert.False(string.IsNullOrWhiteSpace(etag));
        var listJson = await listRes.Content.ReadAsStringAsync(cts.Token);
        using var listDoc = JsonDocument.Parse(listJson);
        var total = listDoc.RootElement.GetProperty("total").GetInt32();
        Assert.True(total >= 2);
        var items = listDoc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.True(items.All(e => e.GetProperty("collectionId").GetGuid() == colA));
        // ascending by createdAt
        Assert.True(items.Length >= 2);
        var c0 = items[0].GetProperty("createdAt").GetDateTimeOffset();
        var c1 = items[^1].GetProperty("createdAt").GetDateTimeOffset();
        Assert.True(c0 <= c1);

        // Second list with If-None-Match must return 304
        using var listRes2 = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var listReq2 = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/db/api/documents?collectionId={colA}&page=1&pageSize=50&sortBy=createdAt&sortDir=asc"
                );
                listReq2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                listReq2.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                listReq2.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return listReq2;
            },
            cts.Token
        );
        Assert.True(
            listRes2.StatusCode == HttpStatusCode.NotModified,
            $"Expected 304, got {(int)listRes2.StatusCode} {listRes2.StatusCode}\n{await listRes2.Content.ReadAsStringAsync(cts.Token)}"
        );
    } // End of Method Documents_List_IfNoneMatch_304_And_SortFilter

    [Fact(DisplayName = "Documents: Update honors If-Match 412 on mismatch")]
    public async Task Documents_Update_IfMatch_412_On_Mismatch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);
        await WaitForIdentityAsync(client, cts.Token);
        await WaitForDbAsync(client, cts.Token);

        var token = await GetAccessTokenAsync(client, cts.Token);
        var tenantId = $"e2e-{Environment.MachineName.ToLowerInvariant()}";
        await EnsureTenantAsync(client, token, tenantId, cts.Token);

        // Arrange: one collection + doc
        var (colId, _) = await CreateCollectionAsync(
            client,
            token,
            tenantId,
            "Cond-Update",
            cts.Token
        );
        var (docId, etag) = await CreateDocumentAsync(
            client,
            token,
            tenantId,
            colId,
            new { name = "to-upd" },
            cts.Token
        );

        // Use a bogus ETag to force 412
        var baseUrl = GetGatewayBaseUrl();
        using var putRes = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var putReq = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{baseUrl}/db/api/documents/{docId}"
                );
                putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                putReq.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                putReq.Headers.TryAddWithoutValidation("If-Match", "\"bogus\"");
                putReq.Content = new StringContent(
                    JsonSerializer.Serialize(new { content = new { name = "updated" } }),
                    Encoding.UTF8,
                    "application/json"
                );
                return putReq;
            },
            cts.Token
        );
        Assert.True(
            putRes.StatusCode == HttpStatusCode.PreconditionFailed,
            $"Expected 412, got {(int)putRes.StatusCode} {putRes.StatusCode}\n{await putRes.Content.ReadAsStringAsync(cts.Token)}"
        );

        // Now correct If-Match should succeed
        using var putRes2 = await SendWithAuthRetryAsync(
            client,
            () =>
            {
                var putReq2 = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{baseUrl}/db/api/documents/{docId}"
                );
                putReq2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                putReq2.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenantId);
                putReq2.Headers.TryAddWithoutValidation("If-Match", etag);
                putReq2.Content = new StringContent(
                    JsonSerializer.Serialize(new { content = new { name = "updated" } }),
                    Encoding.UTF8,
                    "application/json"
                );
                return putReq2;
            },
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, putRes2.StatusCode);
    } // End of Method Documents_Update_IfMatch_412_On_Mismatch
} // End of Class DocumentsApiConditionalTests
