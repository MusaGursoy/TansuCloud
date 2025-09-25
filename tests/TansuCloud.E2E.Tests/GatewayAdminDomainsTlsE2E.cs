// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class GatewayAdminDomainsTlsE2E
{
    private static string GetGatewayBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.TrimEnd('/');
        return "http://127.0.0.1:8080";
    }

    private static async Task WaitReadyAsync(HttpClient client, string baseUrl, CancellationToken ct)
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
    } // End of Method WaitReadyAsync

    private static async Task<string> TryGetAnyAccessTokenAsync(HttpClient http, string baseUrl, CancellationToken ct)
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
    } // End of Method TryGetAnyAccessTokenAsync

    private static (byte[] Pfx, string Password, X509Certificate2 Cert) CreateSelfSignedPfx(string host)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var password = "test";
        var pfx = cert.Export(X509ContentType.Pfx, password);
        return (pfx, password, new X509Certificate2(cert));
    } // End of Method CreateSelfSignedPfx

    private static (string CertPem, string KeyPem, X509Certificate2 Cert) CreateSelfSignedPem(string host)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var certDer = cert.Export(X509ContentType.Cert);
        var certB64 = Convert.ToBase64String(certDer, Base64FormattingOptions.InsertLineBreaks);
        var certPem = $"-----BEGIN CERTIFICATE-----\n{certB64}\n-----END CERTIFICATE-----\n";

        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var keyB64 = Convert.ToBase64String(pkcs8, Base64FormattingOptions.InsertLineBreaks);
        var keyPem = $"-----BEGIN PRIVATE KEY-----\n{keyB64}\n-----END PRIVATE KEY-----\n";

        return (certPem, keyPem, new X509Certificate2(cert));
    } // End of Method CreateSelfSignedPem

    [Fact(DisplayName = "Admin Domains/TLS bind-list-delete roundtrip")]
    public async Task Admin_DomainsTls_Bind_List_Delete()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        // Dev open case probe
        bool devOpen = false;
        try
        {
            using var probe = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token);
            devOpen = probe.StatusCode == HttpStatusCode.OK;
        }
        catch { }

        if (!devOpen)
        {
            var token = await TryGetAnyAccessTokenAsync(http, baseUrl, cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                if (Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1")
                    Assert.Fail("E2E_REQUIRE_ADMIN_TOKEN=1 but unable to acquire admin token.");
                return; // skip gracefully in constrained envs
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var host = $"test{Guid.NewGuid():N}.local";
        var (pfx, password, cert) = CreateSelfSignedPfx(host);
        var payloadObj = new { host, pfxBase64 = Convert.ToBase64String(pfx), pfxPassword = password };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        // POST bind
        using (var post = await http.PostAsync($"{baseUrl}/admin/api/domains", content, cts.Token))
        {
            Assert.True(post.StatusCode == HttpStatusCode.OK || post.StatusCode == HttpStatusCode.Created, $"Unexpected status: {(int)post.StatusCode}");
            var text = await post.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(text);
            var thumb = doc.RootElement.GetProperty("thumbprint").GetString() ?? string.Empty;
            Assert.True(string.Equals(thumb, cert.Thumbprint, StringComparison.OrdinalIgnoreCase), "Thumbprint mismatch after bind");
        }

        // GET list
        using (var list = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token))
        {
            list.EnsureSuccessStatusCode();
            var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cts.Token)).RootElement;
            Assert.True(arr.ValueKind == JsonValueKind.Array, "List should be an array");
            var found = arr.EnumerateArray().Any(e => string.Equals(e.GetProperty("host").GetString(), host, StringComparison.OrdinalIgnoreCase));
            Assert.True(found, "Newly bound host not found in list");
        }

        // DELETE
        using (var del = await http.DeleteAsync($"{baseUrl}/admin/api/domains/{Uri.EscapeDataString(host)}", cts.Token))
        {
            Assert.True(del.StatusCode == HttpStatusCode.NoContent || del.StatusCode == HttpStatusCode.NotFound);
        }

        // Confirm removal (best-effort)
        using (var list2 = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token))
        {
            if (list2.IsSuccessStatusCode)
            {
                var arr = JsonDocument.Parse(await list2.Content.ReadAsStringAsync(cts.Token)).RootElement;
                var found = arr.ValueKind == JsonValueKind.Array && arr.EnumerateArray().Any(e => string.Equals(e.GetProperty("host").GetString(), host, StringComparison.OrdinalIgnoreCase));
                Assert.False(found, "Host should be removed after delete");
            }
        }
    } // End of Method Admin_DomainsTls_Bind_List_Delete

    [Fact(DisplayName = "Admin Domains/TLS PEM bind-list-delete roundtrip")]
    public async Task Admin_DomainsTls_Pem_Bind_List_Delete()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        bool devOpen = false;
        try
        {
            using var probe = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token);
            devOpen = probe.StatusCode == HttpStatusCode.OK;
        }
        catch { }

        if (!devOpen)
        {
            var token = await TryGetAnyAccessTokenAsync(http, baseUrl, cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                if (Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1")
                    Assert.Fail("E2E_REQUIRE_ADMIN_TOKEN=1 but unable to acquire admin token.");
                return; // skip gracefully
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var host = $"pem{Guid.NewGuid():N}.local";
        var (certPem, keyPem, cert) = CreateSelfSignedPem(host);
        var payloadObj = new { host, certPem, keyPem };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        // POST bind (PEM)
        using (var post = await http.PostAsync($"{baseUrl}/admin/api/domains/pem", content, cts.Token))
        {
            Assert.True(post.StatusCode == HttpStatusCode.OK || post.StatusCode == HttpStatusCode.Created, $"Unexpected status: {(int)post.StatusCode}");
            var text = await post.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(text);
            var thumb = doc.RootElement.GetProperty("thumbprint").GetString() ?? string.Empty;
            Assert.True(string.Equals(thumb, cert.Thumbprint, StringComparison.OrdinalIgnoreCase), "Thumbprint mismatch after PEM bind");
        }

        // GET list
        using (var list = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token))
        {
            list.EnsureSuccessStatusCode();
            var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cts.Token)).RootElement;
            Assert.True(arr.ValueKind == JsonValueKind.Array, "List should be an array");
            var found = arr.EnumerateArray().Any(e => string.Equals(e.GetProperty("host").GetString(), host, StringComparison.OrdinalIgnoreCase));
            Assert.True(found, "Newly PEM-bound host not found in list");
        }

        // DELETE
        using (var del = await http.DeleteAsync($"{baseUrl}/admin/api/domains/{Uri.EscapeDataString(host)}", cts.Token))
        {
            Assert.True(del.StatusCode == HttpStatusCode.NoContent || del.StatusCode == HttpStatusCode.NotFound);
        }
    } // End of Method Admin_DomainsTls_Pem_Bind_List_Delete

    [Fact(DisplayName = "Admin Domains/TLS rotate returns previous binding")]
    public async Task Admin_DomainsTls_Rotate_Returns_Previous()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var baseUrl = GetGatewayBaseUrl();
        await WaitReadyAsync(http, baseUrl, cts.Token);

        bool devOpen = false;
        try
        {
            using var probe = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token);
            devOpen = probe.StatusCode == HttpStatusCode.OK;
        }
        catch { }

        if (!devOpen)
        {
            var token = await TryGetAnyAccessTokenAsync(http, baseUrl, cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                if (Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1")
                    Assert.Fail("E2E_REQUIRE_ADMIN_TOKEN=1 but unable to acquire admin token.");
                return; // skip gracefully
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var host = $"rot{Guid.NewGuid():N}.local";
        var (certPem1, keyPem1, cert1) = CreateSelfSignedPem(host);

        // Initial bind via PEM
        var bind1 = new StringContent(JsonSerializer.Serialize(new { host, certPem = certPem1, keyPem = keyPem1 }), Encoding.UTF8, "application/json");
        using (var post1 = await http.PostAsync($"{baseUrl}/admin/api/domains/pem", bind1, cts.Token))
        {
            Assert.True(post1.StatusCode == HttpStatusCode.OK || post1.StatusCode == HttpStatusCode.Created);
        }

        // Rotate with a new PEM pair
        var (certPem2, keyPem2, cert2) = CreateSelfSignedPem(host);
        var rotatePayload = new { host, certPem = certPem2, keyPem = keyPem2 };
        using var rotateContent = new StringContent(JsonSerializer.Serialize(rotatePayload), Encoding.UTF8, "application/json");
        using (var rot = await http.PostAsync($"{baseUrl}/admin/api/domains/rotate", rotateContent, cts.Token))
        {
            rot.EnsureSuccessStatusCode();
            var json = JsonDocument.Parse(await rot.Content.ReadAsStringAsync(cts.Token)).RootElement;
            var currentThumb = json.GetProperty("current").GetProperty("thumbprint").GetString() ?? string.Empty;
            var previousThumb = json.GetProperty("previous").GetProperty("thumbprint").GetString() ?? string.Empty;
            Assert.True(currentThumb.Equals(cert2.Thumbprint, StringComparison.OrdinalIgnoreCase), "Rotate: current thumbprint mismatch");
            Assert.True(previousThumb.Equals(cert1.Thumbprint, StringComparison.OrdinalIgnoreCase), "Rotate: previous thumbprint mismatch");
        }

        // Optional: list and verify
        using (var list = await http.GetAsync($"{baseUrl}/admin/api/domains", cts.Token))
        {
            if (list.IsSuccessStatusCode)
            {
                var arr = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cts.Token)).RootElement;
                var item = arr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && string.Equals(e.GetProperty("host").GetString(), host, StringComparison.OrdinalIgnoreCase));
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var thumb = item.GetProperty("thumbprint").GetString() ?? string.Empty;
                    Assert.True(thumb.Equals(cert2.Thumbprint, StringComparison.OrdinalIgnoreCase), "List after rotate should show new cert");
                }
            }
        }

        // Cleanup
        using (var del = await http.DeleteAsync($"{baseUrl}/admin/api/domains/{Uri.EscapeDataString(host)}", cts.Token))
        {
            Assert.True(del.StatusCode == HttpStatusCode.NoContent || del.StatusCode == HttpStatusCode.NotFound);
        }
    } // End of Method Admin_DomainsTls_Rotate_Returns_Previous
} // End of Class GatewayAdminDomainsTlsE2E
