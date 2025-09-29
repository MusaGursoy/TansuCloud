// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class NightlyContinuityE2E
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
        return TestUrls.GatewayBaseUrl;
    }

    private static async Task WaitForAllAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/health/live", ct);
                if ((int)r.StatusCode < 500)
                    break;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        for (var i = 0; i < 90; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/identity/health/live", ct);
                if ((int)r.StatusCode < 500)
                    break;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        for (var i = 0; i < 90; i++)
        {
            try
            {
                using var r = await client.GetAsync($"{baseUrl}/db/health/live", ct);
                if ((int)r.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Services not ready");
    }

    private static async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            last?.Dispose();
            try
            {
                using var tokenReq = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/identity/connect/token"
                );
                tokenReq.Content = new StringContent(
                    "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=db.write%20db.read",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                );
                last = await client.SendAsync(tokenReq, ct);
                if (last.StatusCode == HttpStatusCode.OK)
                {
                    var okBody = await last.Content.ReadAsStringAsync(ct);
                    using var okDoc = JsonDocument.Parse(okBody);
                    return okDoc.RootElement.GetProperty("access_token").GetString()!;
                }
            }
            catch { }
            await Task.Delay(250, ct);
        }

        // Final attempt with assertion
        using var finalReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/identity/connect/token"
        );
        finalReq.Content = new StringContent(
            "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=db.write%20db.read",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );
        using var finalRes = await client.SendAsync(finalReq, ct);
        var finalBody = await finalRes.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, finalRes.StatusCode);
        using var tokenDoc = JsonDocument.Parse(finalBody);
        return tokenDoc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task ProvisionTenantIfNeededAsync(
        HttpClient client,
        string tenant,
        CancellationToken ct
    )
    {
        // Dev-only bypass provisioning to ensure tenant DB exists before writes
        var baseUrl = GetGatewayBaseUrl();
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/db/api/provisioning/tenants"
        );
        req.Headers.TryAddWithoutValidation("X-Provision-Key", "letmein");
        var body = new { tenantId = tenant, displayName = tenant };
        req.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        );
        using var res = await client.SendAsync(req, ct);
        // Accept 200 OK or 201 Created; anything else is unexpected but non-fatal for idempotency
        if (res.StatusCode != HttpStatusCode.OK && res.StatusCode != HttpStatusCode.Created)
        {
            var txt = await res.Content.ReadAsStringAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Tenant provision failed: {(int)res.StatusCode} {res.StatusCode}. Body: {txt}"
            );
        }
    }

    private static string SharedNightlyDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "tansu-nightly");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string JwksKidFile => Path.Combine(SharedNightlyDir, "jwks_kid.txt");

    private static async Task<string?> GetFirstJwksKidAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        using var res = await client.GetAsync($"{baseUrl}/identity/.well-known/jwks", ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("keys");
        if (keys.GetArrayLength() == 0)
            return null;
        var kidProp = keys[0].TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
        return kidProp;
    }

    [Trait("TestCategory", "NightlyPre")]
    [Fact(DisplayName = "Nightly pre: JWKS present and token/API works; store kid")]
    public async Task Nightly_Jwks_Pre()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        using var client = CreateClient();
        await WaitForAllAsync(client, cts.Token);

        var kid = await GetFirstJwksKidAsync(client, cts.Token) ?? string.Empty;
        await File.WriteAllTextAsync(JwksKidFile, kid, cts.Token);

        var token = await GetAccessTokenAsync(client, cts.Token);
        var baseUrl = GetGatewayBaseUrl();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/db/health/ready");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await client.SendAsync(req, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Trait("TestCategory", "NightlyPost")]
    [Fact(DisplayName = "Nightly post: JWKS usable after restart; DB write still works")]
    public async Task Nightly_Jwks_Post_PgCat_Resilience()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        using var client = CreateClient();
        await WaitForAllAsync(client, cts.Token);

        var oldKid = File.Exists(JwksKidFile)
            ? await File.ReadAllTextAsync(JwksKidFile, cts.Token)
            : string.Empty;
        var newKid = await GetFirstJwksKidAsync(client, cts.Token) ?? string.Empty;
        Console.WriteLine($"Nightly JWKS kid old='{oldKid}', new='{newKid}'");

        var token = await GetAccessTokenAsync(client, cts.Token);
        var baseUrl = GetGatewayBaseUrl();

        var tenant = $"nightly-{Environment.MachineName.ToLowerInvariant()}";
        var collName = $"nightly-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // Ensure tenant DB exists and pgcat will discover it shortly
        await ProvisionTenantIfNeededAsync(client, tenant, cts.Token);

        // Retry the write for up to ~30s to allow pgcat config reload to pick up the new pool
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? last = null;
        string? lastBody = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            last?.Dispose();
            using var cres = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/db/api/collections"
            );
            cres.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            cres.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);
            cres.Content = new StringContent(
                JsonSerializer.Serialize(new { name = collName }),
                Encoding.UTF8,
                "application/json"
            );
            last = await client.SendAsync(cres, cts.Token);
            if (last.StatusCode == HttpStatusCode.Created)
            {
                return; // success
            }
            lastBody = await last.Content.ReadAsStringAsync(cts.Token);
            // Retry on 401/403 (token warmup) and 5xx (pgcat pool not yet present)
            if (
                last.StatusCode == HttpStatusCode.Unauthorized
                || last.StatusCode == HttpStatusCode.Forbidden
                || (int)last.StatusCode >= 500
            )
            {
                await Task.Delay(1000, cts.Token);
                continue;
            }
            break; // other non-retryable status
        }
        if (last is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected 201 Created but got {(int)last.StatusCode} {last.StatusCode}. Body: {lastBody}"
            );
        }
        throw new Xunit.Sdk.XunitException("Expected 201 Created but request did not complete");
    }
}
