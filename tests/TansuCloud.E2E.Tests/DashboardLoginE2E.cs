// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Playwright;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class DashboardLoginE2E : IAsyncLifetime
{
    private static string GetGatewayBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var uri = new Uri(env);
                // Normalize localhost/::1 to IPv4 loopback for Kestrel bindings
                var host =
                    (
                        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || uri.Host == "::1"
                    )
                        ? "127.0.0.1"
                        : uri.Host;
                var builder = new UriBuilder(uri) { Host = host };
                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                // Fallback to raw value trimmed
                return env.TrimEnd('/');
            }
        }
        // Prefer IPv4 loopback to avoid cases where localhost resolves to ::1 but Kestrel is bound to IPv4 only
        return "http://127.0.0.1:8080";
    }

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // Use Chromium for stability; headed=false for CI speed
        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    // Disable system proxy which may break localhost/loopback in some environments
                    "--no-proxy-server",
                    // Force direct connection and bypass any env/system proxies
                    "--proxy-server=direct://",
                    "--proxy-bypass-list=*",
                    // Force DNS mapping for common dev hostnames to IPv4 loopback for stability
                    "--host-resolver-rules=MAP localhost 127.0.0.1,MAP gateway 127.0.0.1,MAP host.docker.internal 127.0.0.1,EXCLUDE nothing"
                }
            }
        );
        _context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true }
        );
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _page!.CloseAsync();
        await _context!.CloseAsync();
        await _browser!.CloseAsync();
        _playwright?.Dispose();
    }

    [Fact(DisplayName = "Dashboard login via Gateway establishes Blazor circuit and assets load")]
    public async Task Dashboard_Login_And_Circuit_Works()
    {
        var page = _page!;
        // Preflight: ensure services are up to avoid false negatives
        var api = await _playwright!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { IgnoreHTTPSErrors = true }
        );
        async Task<bool> ReachableAsync(string url, bool okOnly)
        {
            try
            {
                var res = await api.GetAsync(url, new() { MaxRedirects = 0 });
                if (okOnly)
                    return res.Status == 200;
                // For readiness, accept 2xx/3xx, and also auth challenges (401/403)
                return (res.Status is >= 200 and < 400) || res.Status is 401 or 403;
            }
            catch
            {
                return false;
            }
        }
        async Task WaitUntilAsync(Func<Task<bool>> probe, int timeoutMs = 45000, int stepMs = 1000)
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromMilliseconds(timeoutMs))
            {
                if (await probe())
                {
                    return;
                }
                await Task.Delay(stepMs);
            }
            throw new TimeoutException("Readiness probe timed out");
        }

        // Gateway root: try HTTPS then HTTP; accept 2xx/3xx as ready
        var baseUrl = GetGatewayBaseUrl();
        await WaitUntilAsync(async () => await ReachableAsync($"{baseUrl}/", okOnly: false));
        // Identity discovery: require 200; try HTTPS then HTTP via gateway
        await WaitUntilAsync(async () =>
        {
            var disco = await ReachableAsync(
                $"{baseUrl}/identity/.well-known/openid-configuration",
                okOnly: true
            );
            return disco;
        });
        // Ensure the dashboard route itself is reachable (accept 2xx/3xx for initial redirect to Identity)
        // Ensure the dashboard route itself is reachable (accept 2xx/3xx or 401/403 for initial challenge/redirect to Identity)
        await WaitUntilAsync(
            async () => await ReachableAsync($"{baseUrl}/dashboard", okOnly: false)
        );
        // Note: don't preflight-check Blazor framework asset here; it may 404 before first app access.

        // Browser warm-up is unnecessary here; readiness was already probed via API client.

        // Collect diagnostics as early as possible
        var responses = new List<IResponse>();
        page.Response += (_, r) => responses.Add(r);
        var messages = new List<string>();
        page.Console += (_, msg) =>
        {
            if (!string.IsNullOrWhiteSpace(msg.Text))
                messages.Add(msg.Text);
        };
        var wsConnected = false;
        page.WebSocket += (_, ws) =>
        {
            try
            {
                if (ws.Url.Contains("/_blazor", StringComparison.OrdinalIgnoreCase))
                {
                    wsConnected = true;
                }
            }
            catch
            { /* ignore */
            }
        };

        // 1) Navigate to the dashboard via the Gateway with resilient host fallbacks
        async Task NavigateWithFallbackAsync(string urlBase)
        {
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in new[] { "127.0.0.1", "localhost", "127.0.0.1" })
            {
                var candidate = urlBase;
                try
                {
                    var uri = new Uri(urlBase);
                    var builder = new UriBuilder(uri) { Host = host };
                    candidate = builder.Uri.ToString().TrimEnd('/');
                }
                catch
                { /* leave as-is */
                }

                if (!tried.Add(candidate))
                    continue;

                try
                {
                    await page.GotoAsync(
                        $"{candidate}/dashboard",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
                    );
                    // Success
                    baseUrl = candidate; // use for the rest of the test
                    return;
                }
                catch (Microsoft.Playwright.PlaywrightException ex)
                {
                    var msg = ex.Message ?? string.Empty;
                    if (!msg.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.OrdinalIgnoreCase))
                    {
                        throw; // different failure; bubble up
                    }
                    // else try next host
                }
            }
            throw new Microsoft.Playwright.PlaywrightException(
                $"Failed to navigate to {urlBase}/dashboard via all host fallbacks (localhost/127.0.0.1)"
            );
        }

        await NavigateWithFallbackAsync(baseUrl);

        // If redirected to Identity/Authorize or Login, perform login
        var emailSelector = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passwordSelector =
            "input[name='Input.Password'], #Input_Password, input[type=password]";

        // Wait briefly for either the login form or the dashboard heading
        var loginFormAppeared = false;
        try
        {
            await page.WaitForSelectorAsync(
                emailSelector,
                new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 12000
                }
            );
            loginFormAppeared = true;
        }
        catch
        {
            /* ignore */
        }

        var onLoginUrl =
            page.Url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase)
            || page.Url.Contains("/identity/", StringComparison.OrdinalIgnoreCase);
        var loginFormVisible = false;
        try
        {
            await page.Locator("form#account").WaitForAsync(new() { Timeout = 5000 });
            loginFormVisible = true;
        }
        catch
        {
            /* ignore */
        }
        if (loginFormAppeared || onLoginUrl || loginFormVisible)
        {
            // If we're on a login URL but can't find the form, switch to the canonical /identity base path
            if (
                (onLoginUrl || loginFormVisible)
                && await page.QuerySelectorAsync(emailSelector) is null
            )
            {
                try
                {
                    var uri = new Uri(page.Url);
                    if (
                        uri.AbsolutePath.StartsWith(
                            "/Identity/Account/Login",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        var rebuilt = $"{baseUrl}/identity/Identity/Account/Login" + uri.Query;
                        await page.GotoAsync(
                            rebuilt,
                            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
                        );
                    }
                }
                catch
                { /* ignore */
                }
            }

            if (await page.QuerySelectorAsync(emailSelector) is not null)
            {
                await page.FillAsync(emailSelector, "admin@tansu.local");
                await page.FillAsync(passwordSelector, "Passw0rd!");

                // Prefer the specific login-submit button to avoid clicking the external provider button
                var specificSubmit = await page.QuerySelectorAsync("#login-submit");
                if (specificSubmit is not null)
                {
                    await specificSubmit.ClickAsync();
                }
                else
                {
                    var submitSelector =
                        "form#account button[type=submit], form#account input[type=submit], button#login-submit";
                    await page.ClickAsync(submitSelector);
                }
                // Also press Enter in the password field as a fallback to submit the form
                try
                {
                    await page.PressAsync(passwordSelector, "Enter");
                }
                catch
                {
                    /* ignore */
                }
                // First, allow an intermediate OIDC callback URL if it appears
                try
                {
                    await page.WaitForURLAsync("**/signin-oidc*", new() { Timeout = 15000 });
                }
                catch
                {
                    // ignore: sometimes we land directly on /dashboard
                }
                // Regardless of redirect outcome, navigate to the dashboard explicitly
                await page.GotoAsync(
                    $"{baseUrl}/dashboard",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
                );
            }
        }

        // Debug: log the current URL
        Console.WriteLine($"Current URL: {page.Url}");

        // 2) Assert Dashboard content is visible
        // Prefer role-based locator for h1, then assert text
        var heading = page.GetByRole(AriaRole.Heading, new() { Level = 1 });
        try
        {
            await heading.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            // Retry once with a normalized trailing slash
            await page.GotoAsync(
                $"{baseUrl}/dashboard/",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
            );
            await heading.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        }
        var h1 = await heading.InnerTextAsync();
        if (!h1.Contains("Hello, world!", StringComparison.OrdinalIgnoreCase))
        {
            // Dump last 15 console messages and relevant network responses for diagnostics before failing
            Console.WriteLine("[Diagnostics] Unexpected H1 content: '" + h1 + "'");
            try
            {
                var consoleDump = await page.EvaluateAsync<string>(
                    @"() => window.__tansuConsoleMessages ? window.__tansuConsoleMessages.join('\n') : ''"
                );
                if (!string.IsNullOrWhiteSpace(consoleDump))
                {
                    Console.WriteLine("[Diagnostics] Browser console messages:\n" + consoleDump);
                }
            }
            catch { }
            try
            {
                var html = await page.ContentAsync();
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "test-artifacts");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "dashboard-login-failure.html");
                File.WriteAllText(path, html);
                Console.WriteLine("[Diagnostics] Saved failure HTML to " + path);
            }
            catch
            { /* ignore */
            }
        }
        h1.Should().Contain("Hello, world!");

        // 3) Assert hashed CSS and framework script loaded successfully
        // Trigger a small interaction to ensure circuit creates WS and assets load
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ReloadAsync(
            new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
        );

        responses
            .Should()
            .Contain(r => r.Url.Contains("/app.") && r.Url.EndsWith(".css") && r.Status == 200);
        responses
            .Should()
            .Contain(r =>
                r.Url.Contains("/TansuCloud.Dashboard")
                && r.Url.EndsWith(".styles.css")
                && r.Status == 200
            );
        responses
            .Should()
            .Contain(r => r.Url.EndsWith("/_framework/blazor.web.js") && r.Status == 200);
        (
            wsConnected
            || messages.Any(m =>
                m.Contains("WebSocket connected", StringComparison.OrdinalIgnoreCase)
            )
        )
            .Should()
            .BeTrue("Blazor Server should establish a WebSocket through the Gateway");

        // Log a summary of responses for diagnosis in CI logs
        foreach (var r in responses.TakeLast(10))
        {
            Console.WriteLine($"HTTP {r.Status} {r.Url}");
        }
    }

    [Fact(DisplayName = "Protected backends require auth when called via Gateway")]
    public async Task Protected_Routes_Require_Auth()
    {
        var page = _page!;
        // Unauthenticated requests to /db and /storage should return 401 (gateway guard)
        var api = await _playwright!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { IgnoreHTTPSErrors = true }
        );
        var db = await api.GetAsync($"{GetGatewayBaseUrl()}/db/health");
        db.Status.Should().Be(401);
        var storage = await api.GetAsync($"{GetGatewayBaseUrl()}/storage/health");
        storage.Status.Should().Be(401);
    }
}
