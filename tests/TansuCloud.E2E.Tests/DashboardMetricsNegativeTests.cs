// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class DashboardMetricsNegativeTests : IAsyncLifetime
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;
    private IAPIRequestContext? _api;

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
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = true });
        _ctx = await _browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        _page = await _ctx.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        try { if (_api is not null) await _api.DisposeAsync(); } catch { }
        await _page!.CloseAsync();
        await _ctx!.CloseAsync();
        await _browser!.CloseAsync();
        _pw?.Dispose();
    }

    [Fact(DisplayName = "Metrics API requires admin (403 when not logged in)")]
    public async Task Metrics_AdminOnly_Without_Login_Returns_403()
    {
        var baseUrl = BaseUrl();
        // Direct call without any cookies should not be authorized
        var api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { IgnoreHTTPSErrors = true }
        );
        var resp = await api.GetAsync($"{baseUrl}/dashboard/api/metrics/catalog");
        // Playwright follows redirects by default. If we ended up on the login page, it will be 200 HTML.
        if ((int)resp.Status == 200)
        {
            var contentType = resp.Headers.TryGetValue("content-type", out var ct)
                ? ct
                : string.Empty;
            var body = await resp.TextAsync();
            var url = resp.Url ?? string.Empty;
            var looksLikeLogin =
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                && (
                    url.Contains("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("/identity", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Login", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
                );
            looksLikeLogin
                .Should()
                .BeTrue(
                    "unauthenticated requests should be challenged/redirected to login, not served the API"
                );
            return; // acceptable outcome
        }

        // If not 200, it should be a proper unauthorized status
        ((int)resp.Status)
            .Should()
            .BeOneOf(new[] { 401, 403, 302 });
    }

    [Fact(DisplayName = "Metrics API rejects unknown chartId (400)")]
    public async Task Metrics_UnknownChartId_Returns_400()
    {
        var baseUrl = BaseUrl();
        // Preflight identity discovery to avoid flakiness
        _api = await _pw!.APIRequest.NewContextAsync(new APIRequestNewContextOptions { IgnoreHTTPSErrors = true });
        async Task<bool> ReachableAsync(string url)
        {
            try
            {
                var res = await _api.GetAsync(url, new() { MaxRedirects = 0 });
                return res.Status == 200;
            }
            catch { return false; }
        }
        async Task WaitUntilAsync(Func<Task<bool>> cond, int timeoutMs = 60000, int pollMs = 1000)
        {
            var start = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - start < TimeSpan.FromMilliseconds(timeoutMs))
            {
                if (await cond()) return;
                await Task.Delay(pollMs);
            }
            throw new TimeoutException("Condition not met within timeout");
        }
        await WaitUntilAsync(async () => await ReachableAsync($"{baseUrl}/identity/.well-known/openid-configuration"));
        // Login as admin using the UI flow to obtain cookies
        await _page!.GotoAsync($"{baseUrl}/dashboard");
        var emailSelector = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passwordSelector =
            "input[name='Input.Password'], #Input_Password, input[type=password]";
        await _page.FillAsync(emailSelector, "admin@tansu.local");
        await _page.FillAsync(passwordSelector, "Passw0rd!");
        var specificSubmit = await _page.QuerySelectorAsync("#login-submit");
        if (specificSubmit is not null)
            await specificSubmit.ClickAsync();
        else
            await _page.ClickAsync(
                "form#account button[type=submit], form#account input[type=submit], button#login-submit"
            );
        try { await _page.WaitForURLAsync("**/dashboard**", new() { Timeout = 30000 }); } catch { }
        await _page.WaitForSelectorAsync("nav a[href='admin/metrics']", new() { Timeout = 30000 });

        // Extract cookies into a header for API calls
        var cookies = await _ctx!.CookiesAsync(new[] { baseUrl });
        var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        var api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ExtraHTTPHeaders = new Dictionary<string, string> { ["Cookie"] = cookieHeader }
            }
        );

        var resp = await api.GetAsync(
            $"{baseUrl}/dashboard/api/metrics/range?chartId=does.not.exist&rangeMinutes=1&stepSeconds=15"
        );
        // Some gateways might expose root alias; fallback
        if ((int)resp.Status == 404)
        {
            resp = await api.GetAsync(
                $"{baseUrl}/api/metrics/range?chartId=does.not.exist&rangeMinutes=1&stepSeconds=15"
            );
        }
        ((int)resp.Status).Should().Be(400);
        var json = await resp.JsonAsync();
        json.Should().NotBeNull();
    }

    [Fact(DisplayName = "Metrics API (instant) rejects unknown chartId (400)")]
    public async Task Metrics_Instant_UnknownChartId_Returns_400()
    {
        var baseUrl = BaseUrl();
        // Preflight identity discovery to avoid flakiness
        _api = await _pw!.APIRequest.NewContextAsync(new APIRequestNewContextOptions { IgnoreHTTPSErrors = true });
        async Task<bool> ReachableAsync(string url)
        {
            try
            {
                var res = await _api.GetAsync(url, new() { MaxRedirects = 0 });
                return res.Status == 200;
            }
            catch { return false; }
        }
        async Task WaitUntilAsync(Func<Task<bool>> cond, int timeoutMs = 60000, int pollMs = 1000)
        {
            var start = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - start < TimeSpan.FromMilliseconds(timeoutMs))
            {
                if (await cond()) return;
                await Task.Delay(pollMs);
            }
            throw new TimeoutException("Condition not met within timeout");
        }
        await WaitUntilAsync(async () => await ReachableAsync($"{baseUrl}/identity/.well-known/openid-configuration"));
        // Login as admin using the UI flow to obtain cookies
        await _page!.GotoAsync($"{baseUrl}/dashboard");
        var emailSelector = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passwordSelector =
            "input[name='Input.Password'], #Input_Password, input[type=password]";
        await _page.FillAsync(emailSelector, "admin@tansu.local");
        await _page.FillAsync(passwordSelector, "Passw0rd!");
        var specificSubmit = await _page.QuerySelectorAsync("#login-submit");
        if (specificSubmit is not null)
            await specificSubmit.ClickAsync();
        else
            await _page.ClickAsync(
                "form#account button[type=submit], form#account input[type=submit], button#login-submit"
            );
        try { await _page.WaitForURLAsync("**/dashboard**", new() { Timeout = 30000 }); } catch { }
        await _page.WaitForSelectorAsync("nav a[href='admin/metrics']", new() { Timeout = 30000 });

        // Extract cookies into a header for API calls
        var cookies = await _ctx!.CookiesAsync(new[] { baseUrl });
        var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        var api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ExtraHTTPHeaders = new Dictionary<string, string> { ["Cookie"] = cookieHeader }
            }
        );

        var resp = await api.GetAsync(
            $"{baseUrl}/dashboard/api/metrics/instant?chartId=does.not.exist"
        );
        if ((int)resp.Status == 404)
        {
            resp = await api.GetAsync($"{baseUrl}/api/metrics/instant?chartId=does.not.exist");
        }
        ((int)resp.Status).Should().Be(400);
        var json = await resp.JsonAsync();
        json.Should().NotBeNull();
    }
}
