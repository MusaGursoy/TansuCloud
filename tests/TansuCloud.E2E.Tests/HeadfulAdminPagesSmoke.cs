// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class HeadfulAdminPagesSmoke : IAsyncLifetime
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;

    private static string BaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.TrimEnd('/');
        return "http://127.0.0.1:8080";
    }

    public async Task InitializeAsync()
    {
        _pw = await Microsoft.Playwright.Playwright.CreateAsync();
        // Headful for demonstration; slower but captures visible interactions
        _browser = await _pw.Chromium.LaunchAsync(
            new()
            {
                Headless = false,
                SlowMo = 50,
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    "--no-proxy-server",
                    "--proxy-server=direct://",
                    "--proxy-bypass-list=*",
                    // Force DNS mapping to avoid loopback resolution differences
                    "--host-resolver-rules=MAP localhost 127.0.0.1,MAP gateway 127.0.0.1,MAP host.docker.internal 127.0.0.1,EXCLUDE nothing"
                }
            }
        );
        _ctx = await _browser.NewContextAsync(
            new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 1280, Height = 900 }
            }
        );
        _page = await _ctx.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_page is not null)
            await _page.CloseAsync();
        if (_ctx is not null)
            await _ctx.CloseAsync();
        if (_browser is not null)
            await _browser.CloseAsync();
        _pw?.Dispose();
    }

    [Fact(DisplayName = "Headful: admin overview, domains, routes screenshots")]
    public async Task Headful_Admin_Pages_Screenshots()
    {
        var baseUrl = BaseUrl();
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
        Directory.CreateDirectory(outDir);

        // 0) Wait for gateway/identity readiness with quick probes and dashboard health
        await WaitForReadyAsync(baseUrl);
        await ProbeDashboardHealthAsync(baseUrl);

        // 1) Navigate directly to Identity login first to establish the app cookie, then go to dashboard
        await NavigateWithRetriesAsync(
            baseUrl + "/identity/Identity/Account/Login",
            attempts: 3,
            perTryTimeoutMs: 30000
        );
        await FillAndSubmitLoginAsync(baseUrl);
        await NavigateWithRetriesAsync(baseUrl + "/dashboard", attempts: 3, perTryTimeoutMs: 30000);
        // If not authenticated yet, try one more navigation to dashboard root
        if (!_page!.Url.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
        {
            await NavigateWithRetriesAsync(
                baseUrl + "/dashboard",
                attempts: 2,
                perTryTimeoutMs: 30000
            );
        }

        // Ensure we land on admin overview and capture
        try
        {
            await NavigateWithRetriesAsync(
                baseUrl + "/dashboard/admin",
                attempts: 4,
                perTryTimeoutMs: 40000
            );
            // Wait for the admin nav to be visible which indicates Blazor circuit is up and user is authenticated
            await _page.GetByTestId("nav-overview").WaitForAsync(new() { Timeout = 30000 });
            await _page.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, "admin-overview.png"), FullPage = true }
            );
        }
        catch
        {
            await DumpPageAsync("admin-overview-fail");
            throw;
        }

        // 2) Navigate to Domains & TLS and capture
        try
        {
            await _page.GetByTestId("nav-domains").ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await _page.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, "admin-domains.png"), FullPage = true }
            );
        }
        catch
        {
            await DumpPageAsync("admin-domains-fail");
            throw;
        }

        // 3) Navigate to Routes and capture (also click "Check health" if visible)
        try
        {
            await _page.GetByTestId("nav-routes").ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            // Try health check to populate the panel; ignore failures in non-dev envs
            try
            {
                var checkBtn = await _page.QuerySelectorAsync("button:has-text('Check health')");
                if (checkBtn is not null)
                {
                    await checkBtn.ClickAsync();
                    await _page.WaitForTimeoutAsync(1000);
                }
            }
            catch { }
            await _page.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, "admin-routes.png"), FullPage = true }
            );
        }
        catch
        {
            await DumpPageAsync("admin-routes-fail");
            throw;
        }

        // Basic sanity: we should still be authenticated under /dashboard
        _page.Url.Should().Contain("/dashboard");
    }

    private async Task WaitForReadyAsync(string baseUrl)
    {
        // Poll gateway root and identity discovery briefly
        var api = await _pw!.APIRequest.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r1 = await api.GetAsync(baseUrl + "/");
                var r2 = await api.GetAsync(baseUrl + "/identity/.well-known/openid-configuration");
                if ((int)r1.Status is >= 200 and < 600 && (int)r2.Status is >= 200 and < 600)
                    return;
            }
            catch { }
            await Task.Delay(500);
        }
    }

    private async Task ProbeDashboardHealthAsync(string baseUrl)
    {
        try
        {
            var api = await _pw!.APIRequest.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            await api.GetAsync(baseUrl + "/dashboard/health/ready");
        }
        catch { }
    }

    private async Task FillAndSubmitLoginAsync(string baseUrl)
    {
        var page = _page!;
        // If we already have a dashboard session, return early
        if (page.Url.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
            return;

        // Detect login URL and canonicalize to /identity base if necessary
        var url = page.Url;
        try
        {
            var uri = new Uri(url);
            if (
                uri.AbsolutePath.StartsWith(
                    "/Identity/Account/Login",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var rebuilt = baseUrl.TrimEnd('/') + "/identity/Identity/Account/Login" + uri.Query;
                await page.GotoAsync(
                    rebuilt,
                    new() { WaitUntil = WaitUntilState.Commit, Timeout = 20000 }
                );
            }
        }
        catch { }

        // Wait for login form (labels or input names); if not present, we're likely already logged in
        var emailSel = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passSel = "input[name='Input.Password'], #Input_Password, input[type=password]";
        try
        {
            await page.WaitForSelectorAsync(
                emailSel,
                new() { Timeout = 12000, State = WaitForSelectorState.Visible }
            );
        }
        catch
        {
            return;
        }

        // Fill credentials
        try
        {
            await page.FillAsync(emailSel, "admin@tansu.local");
        }
        catch { }
        try
        {
            await page.FillAsync(passSel, "Passw0rd!");
        }
        catch { }

        // Prefer dedicated submit button if present, else generic
        try
        {
            var specificSubmit = await page.QuerySelectorAsync("#login-submit");
            if (specificSubmit is not null)
                await specificSubmit.ClickAsync();
            else
                await page.Locator(
                    "form#account button[type='submit'], form#account input[type='submit'], button[type='submit']"
                )
                    .First.ClickAsync();
        }
        catch { }

        // Try to observe the OIDC callback, then land on dashboard explicitly
        try
        {
            await page.WaitForURLAsync("**/signin-oidc*", new() { Timeout = 15000 });
        }
        catch { }
        try
        {
            await page.GotoAsync(
                baseUrl + "/dashboard",
                new() { WaitUntil = WaitUntilState.Commit, Timeout = 20000 }
            );
        }
        catch { }
    }

    private async Task NavigateWithRetriesAsync(
        string url,
        int attempts = 3,
        int perTryTimeoutMs = 15000
    )
    {
        Exception? last = null;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                await _page!.GotoAsync(
                    url,
                    new()
                    {
                        // Try a quick commit wait; we'll validate state below regardless
                        WaitUntil = WaitUntilState.Commit,
                        Timeout = perTryTimeoutMs
                    }
                );
            }
            catch (Exception ex)
            {
                last = ex;
                // Navigation may timeout while still landing on an intermediate login/authorize page.
                // We'll validate DOM state below before deciding to retry.
            }

            // Success conditions without requiring navigation event to complete
            try
            {
                var page = _page!;
                // If login form fields are present, we've reached Identity; treat as success
                var hasLogin =
                    await page.QuerySelectorAsync(
                        "input[name='Input.Email'], #Input_Email, input[type=email]"
                    )
                        is not null;
                var hasAdminNav = await page.GetByTestId("nav-overview").CountAsync() > 0;
                var urlNow = page.Url ?? string.Empty;
                if (
                    hasLogin
                    || hasAdminNav
                    || urlNow.Contains("/identity", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return;
                }
            }
            catch { }

            try
            {
                await DumpPageAsync($"nav-retry-{i}");
            }
            catch { }
            await _page!.WaitForTimeoutAsync(500);
        }
        if (last is not null)
            throw last;
    }

    private async Task DumpPageAsync(string tag)
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await _page!.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, $"{tag}.png"), FullPage = true }
            );
            var html = await _page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{tag}.html"), html);
        }
        catch { }
    }
} // End of Class HeadfulAdminPagesSmoke
