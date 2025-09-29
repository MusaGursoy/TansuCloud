// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class AdminAccessE2E : IAsyncLifetime
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
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = true });
        _ctx = await _browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        _page = await _ctx.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_page is not null)
                await _page.CloseAsync();
        }
        catch { }
        try
        {
            if (_ctx is not null)
                await _ctx.CloseAsync();
        }
        catch { }
        try
        {
            if (_browser is not null)
                await _browser.CloseAsync();
        }
        catch { }
        try
        {
            _pw?.Dispose();
        }
        catch { }
    }

    [Fact(DisplayName = "Admin access: unauthenticated is redirected; admin sees sidebar")]
    public async Task AdminAccess_SignInRedirect_And_AdminSeesNav()
    {
        var baseUrl = BaseUrl();
        var adminUrl = baseUrl + "/dashboard/admin";

        // Probe Identity discovery to avoid flakiness
        var api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { IgnoreHTTPSErrors = true }
        );
        async Task<bool> ReachableAsync(string url)
        {
            try
            {
                var r = await api.GetAsync(url, new() { MaxRedirects = 0 });
                return r.Status is >= 200 and < 400 || r.Status is 401 or 302 or 303;
            }
            catch
            {
                return false;
            }
        }
        async Task WaitUntilAsync(Func<Task<bool>> cond, int timeout = 45000)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < timeout)
            {
                if (await cond())
                    return;
                await Task.Delay(1000);
            }
            throw new TimeoutException("Identity readiness timed out");
        }
        await WaitUntilAsync(
            async () => await ReachableAsync(baseUrl + "/identity/.well-known/openid-configuration")
        );

        // 1) Unauthenticated: navigate to /dashboard/admin and expect login challenge/redirect
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // Debug: print current URL and a short snippet to understand what page we are on
        try
        {
            await _page.WaitForTimeoutAsync(500);
            Console.WriteLine($"[DEBUG] After Goto(admin): URL={_page.Url}");
            var html = await _page.ContentAsync();
            var snippet = html.Length > 600 ? html.Substring(0, 600) : html;
            Console.WriteLine("[DEBUG] HTML snippet:" + Environment.NewLine + snippet);
        }
        catch { }
        // Preferred signal: URL should change to the Identity login page under /Identity/Account/Login
        var onLogin = false;
        try
        {
            await _page.WaitForURLAsync("**/Identity/Account/Login*", new() { Timeout = 20000 });
            onLogin = true;
        }
        catch
        {
            // Fallback heuristic: look for common login page elements
            var email = _page.GetByLabel("Email", new() { Exact = false });
            var password = _page.GetByLabel("Password", new() { Exact = false });
            var loginButton = _page.GetByRole(AriaRole.Button, new() { Name = "Log in" });
            onLogin = await TryWaitAsync(email, 10000)
                || await TryWaitAsync(password, 10000)
                || await TryWaitAsync(loginButton, 10000);
        }
        onLogin
            .Should()
            .BeTrue("unauthenticated navigation to admin should be challenged with a sign-in page");

        // 2) Login as admin and verify sidebar via data-testids
        await FillAndSubmitLoginAsync();
        // After login, ensure we land back on /dashboard/admin
        await _page.WaitForURLAsync("**/dashboard/admin*", new() { Timeout = 30000 });
        // Wait for one of the sidebar items to be visible
        await _page.GetByTestId("nav-rate-limits").WaitForAsync(new() { Timeout = 30000 });
        // Validate a few nav items exist
        (await _page.GetByTestId("nav-overview").CountAsync())
            .Should()
            .BeGreaterThan(0);
        (await _page.GetByTestId("nav-providers").CountAsync()).Should().BeGreaterThan(0);
        (await _page.GetByTestId("nav-routes").CountAsync()).Should().BeGreaterThan(0);
        (await _page.GetByTestId("nav-rate-limits").CountAsync()).Should().BeGreaterThan(0);
        (await _page.GetByTestId("nav-logs").CountAsync()).Should().BeGreaterThan(0);
    }

    private static async Task<bool> TryWaitAsync(ILocator loc, int timeoutMs)
    {
        try
        {
            await loc.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task FillAndSubmitLoginAsync()
    {
        var page = _page!;
        var emailByLabel = page.GetByLabel("Email", new() { Exact = false });
        var passwordByLabel = page.GetByLabel("Password", new() { Exact = false });
        var emailLocator = page.Locator(
            "input[name='Input.Email'], input#Input_Email, input[type='email'], input[name='Email'], input#Email, input#email"
        );
        var passwordLocator = page.Locator(
            "input[name='Input.Password'], input#Input_Password, input[type='password'], input[name='Password'], input#Password, input#password"
        );

        if (await emailByLabel.CountAsync() > 0)
            await emailByLabel.FillAsync("admin@tansu.local");
        else
            await emailLocator.FillAsync("admin@tansu.local");
        if (await passwordByLabel.CountAsync() > 0)
            await passwordByLabel.FillAsync("Passw0rd!");
        else
            await passwordLocator.FillAsync("Passw0rd!");

        var submitButton = page.GetByRole(AriaRole.Button, new() { Name = "Log in" });
        if (await submitButton.CountAsync() > 0)
            await submitButton.ClickAsync();
        else
            await page.Locator("button[type='submit'], input[type='submit']").First.ClickAsync();
    }
}
