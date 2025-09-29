// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminRateLimitsUiE2E : IAsyncLifetime
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;

    private static string BaseUrl()
    {
        return TestUrls.GatewayBaseUrl;
    }

    public async Task InitializeAsync()
    {
        _pw = await Microsoft.Playwright.Playwright.CreateAsync();
        var headless = (Environment.GetEnvironmentVariable("PW_HEADFUL") != "1");
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = headless });
        _ctx = await _browser.NewContextAsync(
            new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 1280, Height = 800 }
            }
        );
        _page = await _ctx.NewPageAsync();
    }

    [Fact(DisplayName = "Admin UI: tighten defaults, ping shows 429, then revert")]
    public async Task AdminUi_Tighten_Defaults_Ping_Shows429()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady)
        {
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail("Identity discovery not reachable; cannot perform UI admin test.");
            return; // skip gracefully when identity isn't up
        }

        // Navigate and ensure session
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/rate-limits";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureLoggedInAsync();
        await _page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

    // Prefer waiting for inputs directly; component auto-loads on init
    var defaultPermitInput = _page.GetByTestId("input-default-permit");
    var defaultQueueInput = _page.GetByTestId("input-default-queue");
        try
        {
            await defaultPermitInput.First.WaitForAsync(new() { Timeout = 30000 });
            await defaultQueueInput.First.WaitForAsync(new() { Timeout = 30000 });
        }
        catch
        {
            // If inputs aren't present, check for error and bail gracefully in non-strict envs
            var loadError = await _page.QuerySelectorAsync(".alert.alert-danger");
            if (loadError is not null)
            {
                var err = (await loadError.InnerTextAsync()) ?? string.Empty;
                var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
                if (require)
                    Assert.Fail($"Admin UI load failed: {err}");
                return;
            }
            await DumpPageAsync(_page, "pw-adminui-inputs-timeout");
            throw;
        }

    await defaultPermitInput.WaitForAsync();
    await defaultQueueInput.WaitForAsync();

        // Snapshot originals
        var originalPermitStr = await defaultPermitInput.InputValueAsync();
        var originalQueueStr = await defaultQueueInput.InputValueAsync();
        int.TryParse(originalPermitStr, out var originalPermit);
        int.TryParse(originalQueueStr, out var originalQueue);

        // Tighten to Permit=1, Queue=0
        await defaultPermitInput.FillAsync("1");
        await defaultQueueInput.FillAsync("0");
        await _page.GetByTestId("btn-save").ClickAsync();
        // Confirm modal appears; click Apply to submit changes
        var applyBtn = _page.GetByTestId("btn-apply");
        try { await applyBtn.WaitForAsync(new() { Timeout = 5000 }); } catch { /* modal may be instant */ }
        if (await applyBtn.CountAsync() > 0)
            await applyBtn.ClickAsync();
        await _page.WaitForTimeoutAsync(500);

        // Drive concurrent traffic to /ratelimit/ping
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var url = baseUrl.TrimEnd('/') + "/ratelimit/ping";
        var tasks = Enumerable.Range(0, 6).Select(_ => http.GetAsync(url)).ToArray();
        await Task.WhenAll(tasks);
        var statuses = tasks.Select(t => t.Result.StatusCode).ToArray();
        var ok = statuses.Count(s => s == HttpStatusCode.OK);
        var tooMany = statuses.Count(s => (int)s == 429);
        (ok >= 1).Should().BeTrue("at least one OK should pass before hitting the window limit");
        (tooMany >= 1).Should().BeTrue("at least one request should be rate-limited with 429 after tightening");

        // Revert defaults using the UI
        await defaultPermitInput.FillAsync((originalPermit > 0 ? originalPermit : 10).ToString());
        await defaultQueueInput.FillAsync((originalQueue >= 0 ? originalQueue : 0).ToString());
        await _page.GetByTestId("btn-save").ClickAsync();
        applyBtn = _page.GetByTestId("btn-apply");
        try { await applyBtn.WaitForAsync(new() { Timeout = 5000 }); } catch { }
        if (await applyBtn.CountAsync() > 0)
            await applyBtn.ClickAsync();
        await _page.WaitForTimeoutAsync(250);
    }
    public async Task DisposeAsync()
    {
        await _page!.CloseAsync();
        await _ctx!.CloseAsync();
        await _browser!.CloseAsync();
        _pw?.Dispose();
    }

    [Fact(DisplayName = "Admin UI: edit rate limits and verify via Gateway API")]
    public async Task AdminUi_EditRateLimits_Then_Verify()
    {
        var baseUrl = BaseUrl();

        // Ensure Identity discovery is reachable before we attempt OIDC redirects
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady)
        {
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail("Identity discovery not reachable; cannot perform UI admin test.");
            return; // skip gracefully when identity isn't up
        }

        // First hit the dashboard root to trigger OIDC and establish session, then navigate to admin page.
        var dashboardRoot = $"{baseUrl}/dashboard";
        await _page!.GotoAsync(
            dashboardRoot,
            new() { WaitUntil = WaitUntilState.DOMContentLoaded }
        );
        await EnsureLoggedInAsync();

        // Now navigate to the admin page.
        var adminUrl = $"{baseUrl}/dashboard/admin/rate-limits";
        await _page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // If we landed back on the login page for any reason, perform login and try once more.
        if (await IsOnLoginPageAsync(_page))
        {
            await EnsureLoggedInAsync();
            await _page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        // Emit current URL and capture a quick screenshot to aid debugging if needed
        try
        {
            Console.WriteLine($"[E2E] After admin nav, URL={_page.Url}");
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await _page.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, "pw-admin-after-nav.png"), FullPage = true }
            );
        }
        catch { }

        // Wait for inputs directly or error alert instead of clicking Load
    var defaultPermitInput = _page.GetByTestId("input-default-permit");
    var defaultQueueInput = _page.GetByTestId("input-default-queue");
        try
        {
            await defaultPermitInput.First.WaitForAsync(new() { Timeout = 30000 });
            await defaultQueueInput.First.WaitForAsync(new() { Timeout = 30000 });
        }
        catch
        {
            var loadError = await _page.QuerySelectorAsync(".alert.alert-danger");
            if (loadError is not null)
            {
                var err = (await loadError.InnerTextAsync()) ?? string.Empty;
                var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
                if (require)
                    Assert.Fail($"Admin UI load failed: {err}");
                return; // In dev-less environments, skip gracefully
            }
            await DumpPageAsync(_page, "pw-admin-load-input-timeout");
            throw;
        }

        // Read current value, increment by 1, Save
        var text = await defaultPermitInput.InputValueAsync();
        if (!int.TryParse(text, out var original))
            original = 10; // default fallback
        var newValue = original + 1;
        await defaultPermitInput.FillAsync(newValue.ToString());
        await _page.GetByTestId("btn-save").ClickAsync();
        var applyBtn2 = _page.GetByTestId("btn-apply");
        try { await applyBtn2.WaitForAsync(new() { Timeout = 5000 }); } catch { }
        if (await applyBtn2.CountAsync() > 0)
            await applyBtn2.ClickAsync();
        // Give the Blazor Server round-trip a brief moment to complete
        await _page.WaitForTimeoutAsync(500);

        // If page shows an error alert (e.g., 401/403 in non-Dev), treat as environment not configured → early return
        var alert = await _page.QuerySelectorAsync(".alert.alert-danger");
        if (alert is not null)
        {
            var errText = (await alert.InnerTextAsync()) ?? string.Empty;
            // If CI strictly requires this path, set E2E_REQUIRE_ADMIN_TOKEN=1 and ensure backchannel auth is configured.
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail($"Admin UI save failed: {errText}");
            return;
        }

        // Verify via Gateway admin API GET (retry briefly to allow propagation)
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var matched = await WaitForPermitAsync(
            http,
            baseUrl,
            newValue,
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        );
        if (!matched)
        {
            // Fetch one last time for a helpful assertion message
            var last = await GetGatewayRateLimitsAsync(http, baseUrl, CancellationToken.None);
            last.Should().NotBeNull();
            last!.Defaults.PermitLimit.Should().Be(newValue);
        }

        // Revert to original to keep subsequent runs clean
        await defaultPermitInput.FillAsync(original.ToString());
        await _page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        try
        {
            var applyBtn3 = _page.GetByTestId("btn-apply");
            await applyBtn3.WaitForAsync(new() { Timeout = 5000 });
            await applyBtn3.ClickAsync();
        }
        catch { }
    }

    [Fact(DisplayName = "Rate limit effect: /ratelimit/ping returns 429 after tightening" )]
    public async Task RateLimitPing_Should_Reflect_Tightened_Defaults()
    {
        var baseUrl = BaseUrl();
        await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));

        // Read current defaults via admin API (unauth or with token)
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var current = await GetGatewayRateLimitsAsync(http, baseUrl, CancellationToken.None);
        if (current is null)
        {
            // Try with token if admin API requires auth in this environment
            var token = await TryGetAnyAccessTokenAsync(http, baseUrl, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            current = await GetGatewayRateLimitsAsync(http, baseUrl, CancellationToken.None);
        }
        if (current is null)
        {
            // If we cannot reach the admin API in this environment and strict mode isn't set, skip.
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail("Admin API not reachable for rate-limit test.");
            return;
        }

    var originalPermit = Math.Max(3, current.Defaults.PermitLimit);
    var originalQueue = current.Defaults.QueueLimit;
    var newPermit = 2; // intentionally tight to force 429s quickly
    current.Defaults.PermitLimit = newPermit;
    current.Defaults.QueueLimit = 0; // disable queuing so excess requests are rejected immediately

        // Apply via admin API
        var csrf = Environment.GetEnvironmentVariable("DASHBOARD_CSRF");
        if (!string.IsNullOrWhiteSpace(csrf))
        {
            if (http.DefaultRequestHeaders.Contains("X-Tansu-Csrf"))
                http.DefaultRequestHeaders.Remove("X-Tansu-Csrf");
            http.DefaultRequestHeaders.Add("X-Tansu-Csrf", csrf);
        }

    var payload = JsonContent.Create(current);
        var post = await http.PostAsync(baseUrl.TrimEnd('/') + "/admin/api/rate-limits", payload);
        if (!post.IsSuccessStatusCode)
        {
            // Try with token
            var token = await TryGetAnyAccessTokenAsync(http, baseUrl, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(token))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                post = await http.PostAsync(baseUrl.TrimEnd('/') + "/admin/api/rate-limits", payload);
            }
        }
        if (!post.IsSuccessStatusCode)
        {
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail($"Failed to apply tightened limits: {(int)post.StatusCode} {post.ReasonPhrase}");
            return;
        }

        // Wait until admin API reflects the new defaults before probing /ratelimit/ping
        var applied = await WaitForPermitAsync(http, baseUrl, newPermit, TimeSpan.FromSeconds(5), CancellationToken.None);
        if (!applied)
        {
            // Best-effort final read for diagnostics
            var last = await GetGatewayRateLimitsAsync(http, baseUrl, CancellationToken.None);
            last.Should().NotBeNull();
            last!.Defaults.PermitLimit.Should().Be(newPermit);
        }

        // Drive /ratelimit/ping traffic to exceed the default permit quickly
        var ok = 0;
        var tooMany = 0;
        for (int i = 0; i < 6; i++)
        {
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/ratelimit/ping");
            if (resp.StatusCode == HttpStatusCode.OK) ok++;
            else if ((int)resp.StatusCode == 429) tooMany++;
            await Task.Delay(50);
        }
        // Expect at least one 429 if limits were applied
        (ok >= 1).Should().BeTrue("at least one OK should pass before hitting the window limit");
        (tooMany >= 1).Should().BeTrue("at least one request should be rate-limited with 429");

        // Revert defaults back to original to avoid affecting other tests
        current.Defaults.PermitLimit = originalPermit;
        current.Defaults.QueueLimit = originalQueue;
        payload = JsonContent.Create(current);
        try { await http.PostAsync(baseUrl.TrimEnd('/') + "/admin/api/rate-limits", payload); } catch { }
    }

    private async Task<bool> WaitForPermitAsync(
        HttpClient http,
        string baseUrl,
        int expected,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        // Try without auth first
        while (DateTime.UtcNow < deadline)
        {
            var dto = await GetGatewayRateLimitsAsync(http, baseUrl, ct);
            if (dto?.Defaults.PermitLimit == expected)
                return true;
            await Task.Delay(250, ct);
        }

        // Try with token if still not matched
        var token = await TryGetAnyAccessTokenAsync(http, baseUrl, ct);
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token
            );
            var deadline2 = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline2)
            {
                var dto = await GetGatewayRateLimitsAsync(http, baseUrl, ct);
                if (dto?.Defaults.PermitLimit == expected)
                    return true;
                await Task.Delay(250, ct);
            }
        }

        return false;
    } // End of Method WaitForPermitAsync

    private async Task EnsureLoggedInAsync()
    {
        // Preferred flow: trigger OIDC challenge by navigating directly to the admin page.
        // This ensures correlation/nonce cookies are established and Identity will redirect back
        // to the dashboard via the normal /signin-oidc round-trip.
        var page = _page!;
        var adminPath = "/dashboard/admin/rate-limits";
        var adminUrl = BaseUrl().TrimEnd('/') + adminPath;
        await page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Prepare selectors
        var emailByLabel = page.GetByLabel("Email", new() { Exact = false });
        var passwordByLabel = page.GetByLabel("Password", new() { Exact = false });
        var emailLocator = page.Locator(
            "input[name='Input.Email'], input#Input_Email, input[type='email'], input[name='Email'], input#Email, input#email"
        );
        var passwordLocator = page.Locator(
            "input[name='Input.Password'], input#Input_Password, input[type='password'], input[name='Password'], input#Password, input#password"
        );

        // If we're not yet authenticated, we should be on the Identity login page now.
        // Wait for the email input to be visible; if already authenticated, the admin page should load.
        var onLogin =
            await TryWaitAsync(emailByLabel, 10000) || await TryWaitAsync(emailLocator, 10000);
        if (!onLogin)
        {
            // Not on login — either already authenticated or still navigating. If the admin page loads,
            // the URL will match adminPath shortly; wait briefly for that state.
            try
            {
                await page.WaitForURLAsync("**" + adminPath + "*", new() { Timeout = 10000 });
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                return;
            }
            catch
            {
                // Diagnostics to understand current state
                try
                {
                    var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                    Directory.CreateDirectory(outDir);
                    await page.ScreenshotAsync(
                        new()
                        {
                            Path = Path.Combine(outDir, "pw-admin-login-missing.png"),
                            FullPage = true
                        }
                    );
                    var html = await page.ContentAsync();
                    await File.WriteAllTextAsync(
                        Path.Combine(outDir, "pw-admin-login-missing.html"),
                        html
                    );
                }
                catch { }
                throw new Xunit.Sdk.XunitException(
                    $"Expected login or admin page, but neither detected. URL={page.Url}"
                );
            }
        }

        // Fill credentials (dev defaults)
        if (await emailByLabel.CountAsync() > 0)
            await emailByLabel.FillAsync("admin@tansu.local");
        else
            await emailLocator.FillAsync("admin@tansu.local");
        if (await passwordByLabel.CountAsync() > 0)
            await passwordByLabel.FillAsync("Passw0rd!");
        else
            await passwordLocator.FillAsync("Passw0rd!");

        // Submit
        var submitButton = page.GetByRole(AriaRole.Button, new() { Name = "Log in" });
        if (await submitButton.CountAsync() > 0)
            await submitButton.ClickAsync();
        else
            await page.Locator("button[type='submit'], input[type='submit']").First.ClickAsync();

        // Wait for the login redirect round-trips to settle. Instead of requiring a specific URL,
        // wait for network to go idle, then we'll navigate explicitly to the admin page if needed.
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 30000 });
        }
        catch
        {
            // Diagnostics to understand current state
            try
            {
                var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                Directory.CreateDirectory(outDir);
                await page.ScreenshotAsync(
                    new()
                    {
                        Path = Path.Combine(outDir, "pw-admin-wait-networkidle-timeout.png"),
                        FullPage = true
                    }
                );
                var html = await page.ContentAsync();
                await File.WriteAllTextAsync(
                    Path.Combine(outDir, "pw-admin-wait-networkidle-timeout.html"),
                    html
                );
            }
            catch { }
            // Continue regardless; we'll attempt navigation below.
        }

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // If we're not at the admin path yet, navigate there now (session should be established)
        if (!page.Url.Contains(adminPath, StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
    }

    private static async Task<bool> TryWaitAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    } // End of Method TryWaitAsync

    private static async Task<bool> IsOnLoginPageAsync(IPage page)
    {
        try
        {
            var url = page.Url ?? string.Empty;
            if (url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
                return true;
            // Heuristic: presence of a form with action to Account/Login or a heading/button named "Log in"
            var form = await page.QuerySelectorAsync("form[action*='Account/Login']");
            if (form is not null)
                return true;
            var heading = page.GetByRole(AriaRole.Heading, new() { Name = "Log in" });
            if (await heading.CountAsync() > 0)
                return true;
        }
        catch { }
        return false;
    } // End of Method IsOnLoginPageAsync

    private static async Task<bool> TryWaitSelectorAsync(IPage page, string selector, int timeoutMs)
    {
        try
        {
            await page.WaitForSelectorAsync(selector, new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    } // End of Method TryWaitSelectorAsync

    private static async Task DumpPageAsync(IPage page, string prefix)
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, $"{prefix}.png"), FullPage = true });
            var html = await page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{prefix}.html"), html);
        }
        catch { }
    } // End of Method DumpPageAsync

    private static async Task<bool> WaitForIdentityAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var baseTrim = baseUrl.TrimEnd('/');
        var urls = new[]
        {
            baseTrim + "/identity/.well-known/openid-configuration",
            baseTrim + "/.well-known/openid-configuration"
        };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var u in urls)
                {
                    using var resp = await http.GetAsync(u);
                    if (resp.IsSuccessStatusCode)
                        return true;
                }
            }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

    private static async Task<RateLimitConfigDto?> GetGatewayRateLimitsAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct
    )
    {
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/admin/api/rate-limits", ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<RateLimitConfigDto>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryGetAnyAccessTokenAsync(
        HttpClient http,
        string baseUrl,
        CancellationToken ct
    )
    {
        var tokenUrl = $"{baseUrl}/identity/connect/token";
        // Password grant with full scopes
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

        // Client credentials with admin.full
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

    // Minimal DTOs matching Gateway admin API
    public sealed record RateLimitConfigDto
    {
        public int WindowSeconds { get; set; } // End of Property WindowSeconds
        public RateLimitDefaults Defaults { get; set; } = new(); // End of Property Defaults
        public Dictionary<string, RateLimitRouteOverride> Routes { get; set; } = new(); // End of Property Routes
    } // End of Class RateLimitConfigDto

    public sealed record RateLimitDefaults
    {
        public int PermitLimit { get; set; } // End of Property PermitLimit
        public int QueueLimit { get; set; } // End of Property QueueLimit
    } // End of Class RateLimitDefaults

    public sealed record RateLimitRouteOverride
    {
        public int? PermitLimit { get; set; } // End of Property PermitLimit
        public int? QueueLimit { get; set; } // End of Property QueueLimit
    } // End of Class RateLimitRouteOverride
} // End of Class AdminRateLimitsUiE2E
