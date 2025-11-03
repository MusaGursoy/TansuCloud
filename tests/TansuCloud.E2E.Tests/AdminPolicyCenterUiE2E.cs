// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminPolicyCenterUiE2E : IAsyncLifetime
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
    } // End of Method InitializeAsync

    [Fact(DisplayName = "Admin UI: PolicyCenter page renders and shows policy list")]
    public async Task AdminUi_PolicyCenter_PageRenders()
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

        // Navigate to Policy Center page
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/policy-center";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Policy Center')", new() { Timeout = 10_000 });

        // Verify create button exists (MudBlazor button with data-testid)
        var createButton = await _page.QuerySelectorAsync("[data-testid='btn-create-policy']");
        createButton.Should().NotBeNull("create policy button should be visible");

        // Verify filter controls exist (MudSelect with data-testid)
        var typeFilter = await _page.QuerySelectorAsync("[data-testid='filter-type']");
        typeFilter.Should().NotBeNull("filter dropdown should exist");
    } // End of Method AdminUi_PolicyCenter_PageRenders

    [Fact(DisplayName = "Admin UI: PolicyCenter navigation link works")]
    public async Task AdminUi_PolicyCenter_NavigationWorks()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady)
        {
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail("Identity discovery not reachable; cannot perform UI admin test.");
            return;
        }

        // Navigate to admin index
        var adminIndexUrl = baseUrl.TrimEnd('/') + "/dashboard/admin";
        await _page!.GotoAsync(adminIndexUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for navigation
        await _page.WaitForSelectorAsync("[data-testid='nav-policy-center']", new() { Timeout = 10_000 });

        // Click the navigation link
        var navLink = await _page.QuerySelectorAsync("[data-testid='nav-policy-center']");
        navLink.Should().NotBeNull("policy center nav link should exist");
        await navLink!.ClickAsync();

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Policy Center')", new() { Timeout = 10_000 });

        // Verify we're on the right page
        var url = _page.Url;
        url.Should().Contain("/dashboard/admin/policy-center", "should navigate to policy center page");
    } // End of Method AdminUi_PolicyCenter_NavigationWorks

    [Fact(DisplayName = "Admin UI: PolicyCenter can display enforcement mode legend")]
    public async Task AdminUi_PolicyCenter_DisplaysModeLegend()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady)
        {
            var require = Environment.GetEnvironmentVariable("E2E_REQUIRE_ADMIN_TOKEN") == "1";
            if (require)
                Assert.Fail("Identity discovery not reachable; cannot perform UI admin test.");
            return;
        }

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/policy-center";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Policy Center')", new() { Timeout = 10_000 });

        // Verify legend section exists
        var legendHeading = await _page.QuerySelectorAsync("h5:text('Enforcement Modes:')");
        legendHeading.Should().NotBeNull("enforcement modes legend should be visible");

        // Verify mode descriptions exist
        var shadowText = await _page.QuerySelectorAsync("text='Shadow'");
        shadowText.Should().NotBeNull("shadow mode description should exist");

        var auditText = await _page.QuerySelectorAsync("text='Audit Only'");
        auditText.Should().NotBeNull("audit only mode description should exist");

        var enforceText = await _page.QuerySelectorAsync("text='Enforce'");
        enforceText.Should().NotBeNull("enforce mode description should exist");
    } // End of Method AdminUi_PolicyCenter_DisplaysModeLegend

    private async Task<bool> WaitForIdentityAsync(string baseUrl, TimeSpan timeout)
    {
        try
        {
            var cts = new CancellationTokenSource(timeout);
            var discoveryUrl = baseUrl.TrimEnd('/')
                + "/identity/.well-known/openid-configuration";

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var resp = await _pw!.APIRequest.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                    var apiResp = await resp.GetAsync(discoveryUrl);
                    if (apiResp.Ok)
                    {
                        await resp.DisposeAsync();
                        return true;
                    }
                    await resp.DisposeAsync();
                }
                catch
                {
                    // ignored
                }

                await Task.Delay(500, cts.Token);
            }

            return false;
        }
        catch
        {
            return false;
        }
    } // End of Method WaitForIdentityAsync

    private async Task EnsureSignedInAsync(string baseUrl)
    {
        var currentUrl = _page!.Url;

        // If we're on the login page, sign in
        if (currentUrl.Contains("/Account/Login") || currentUrl.Contains("/connect/authorize"))
        {
            // Wait for email input
            await _page.WaitForSelectorAsync("input[type='email'], input[name='Input.Email']");

            var emailInput = await _page.QuerySelectorAsync("input[type='email']");
            if (emailInput == null)
                emailInput = await _page.QuerySelectorAsync("input[name='Input.Email']");

            emailInput.Should().NotBeNull("email input should exist");

            await emailInput!.FillAsync("admin@tansu.local");

            var passwordInput = await _page.QuerySelectorAsync("input[type='password']");
            if (passwordInput == null)
                passwordInput = await _page.QuerySelectorAsync("input[name='Input.Password']");

            passwordInput.Should().NotBeNull("password input should exist");
            await passwordInput!.FillAsync("Passw0rd!");

            var submitButton = await _page.QuerySelectorAsync("button[type='submit']");
            submitButton.Should().NotBeNull("submit button should exist");
            await submitButton!.ClickAsync();

            // Wait for redirect back to admin page
            await _page.WaitForURLAsync(
                url => url.Contains("/dashboard/admin"),
                new() { Timeout = 15_000 }
            );

            // Additional wait for page to stabilize
            await Task.Delay(1000);
        }
    } // End of Method EnsureSignedInAsync

    public async Task DisposeAsync()
    {
        if (_page != null)
            await _page.CloseAsync();
        if (_ctx != null)
            await _ctx.CloseAsync();
        if (_browser != null)
            await _browser.CloseAsync();
        _pw?.Dispose();
    } // End of Method DisposeAsync
} // End of Class AdminPolicyCenterUiE2E
