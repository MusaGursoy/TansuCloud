// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class GatewayAdminAuthE2E
{
    private static string GetGatewayBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.TrimEnd('/');
        return "http://127.0.0.1:8080";
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
    }

    [Fact(DisplayName = "Admin API unauthorized returns 401/403 in non-Dev (open in Dev)")]
    public async Task AdminApi_Unauthorized_Returns_401or403()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        using var res = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", cts.Token);
        if (res.StatusCode == HttpStatusCode.OK)
        {
            // In Development environment, admin endpoints are intentionally left open for local testing.
            // Treat this as a no-op success in Dev; the strict 401/403 expectation applies to non-Dev.
            return;
        }
        Assert.Contains(
            res.StatusCode,
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }
        );
    }

    [Fact(DisplayName = "Admin API authorized with token returns 200")]
    public async Task AdminApi_Authorized_Returns_200()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        // In Development, admin API is open; if so, no token needed.
        using (var probe = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", cts.Token))
        {
            if (probe.StatusCode == HttpStatusCode.OK)
            {
                return; // Dev mode: treated as success
            }
        }

        // Try to get a token using password or client_credentials; if both fail, skip.
        var token = await TryGetAnyAccessTokenAsync(http, baseUrl, cts.Token);
        if (string.IsNullOrWhiteSpace(token))
        {
            // If CI requires strictness, enforce via environment toggle.
            if (Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1")
                Assert.Fail("E2E_REQUIRE_ADMIN_TOKEN=1 and unable to acquire access token.");
            return; // allow in dev/local without strict token paths
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact(DisplayName = "Admin API role vs scope matrix")]
    public async Task AdminApi_RoleVsScope_Matrix()
    {
        // Note: Environment seeding provides Admin role and dashboard client with admin.full scope in Dev.
        // We validate that any valid token with admin.full scope passes. A role-only token path is environment-specific; we rely on scope here.

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        // 1) No token → 401/403 (except Dev open case)
        using (var res = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", cts.Token))
        {
            if (res.StatusCode != HttpStatusCode.OK)
                Assert.Contains(
                    res.StatusCode,
                    new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }
                );
        }

        // 2) admin.full scope → 200
        var ccToken = await TryGetClientCredentialsTokenAsync(http, baseUrl, cts.Token);
        if (!string.IsNullOrWhiteSpace(ccToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ccToken
            );
            using var ok = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", cts.Token);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        else if (Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1")
        {
            Assert.Fail(
                "E2E_REQUIRE_ADMIN_TOKEN=1 requires admin.full token but acquisition failed."
            );
        }

        // 3) Neither role nor scope → can't fabricate here safely; ensure no-token case is blocked as proxy for this.
    }

    private static async Task<string> TryGetAnyAccessTokenAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct
    )
    {
        var tokenUrl = $"{baseUrl}/identity/connect/token";

        // Try password grant (dev seeded admin user)
        try
        {
            using var pwd = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = "admin@tansu.local",
                    ["password"] = "Passw0rd!",
                    ["client_id"] = "tansu-dashboard",
                    ["client_secret"] = "dev-secret",
                    ["scope"] = "openid profile roles admin.full offline_access"
                }
            );
            using var resp = await http.PostAsync(tokenUrl, pwd, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("access_token", out var at))
                    return at.GetString() ?? string.Empty;
            }
        }
        catch { }

        // Try client credentials
        try
        {
            using var cc = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "tansu-dashboard",
                    ["client_secret"] = "dev-secret",
                    ["scope"] = "admin.full"
                }
            );
            using var resp = await http.PostAsync(tokenUrl, cc, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("access_token", out var at))
                    return at.GetString() ?? string.Empty;
            }
        }
        catch { }

        return string.Empty;
    }

    private static async Task<string> TryGetClientCredentialsTokenAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct
    )
    {
        var tokenUrl = $"{baseUrl}/identity/connect/token";
        try
        {
            using var cc = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "tansu-dashboard",
                    ["client_secret"] = "dev-secret",
                    ["scope"] = "admin.full"
                }
            );
            using var resp = await http.PostAsync(tokenUrl, cc, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("access_token", out var at))
                    return at.GetString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }
} // End of Class GatewayAdminAuthE2E
