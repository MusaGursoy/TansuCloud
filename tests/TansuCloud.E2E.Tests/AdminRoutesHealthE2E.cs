// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminRoutesHealthE2E : IAsyncLifetime
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

    [Fact(DisplayName = "Admin UI: Routes health probe displays cluster status")]
    public async Task AdminUi_Routes_HealthProbe_DisplaysStatus()
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
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/routes";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureLoggedInAsync();
        await _page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Wait for the page to load
        var checkHealthBtn = _page.Locator("button:has-text('Check health')");
        try
        {
            await checkHealthBtn.WaitForAsync(new() { Timeout = 15000 });
        }
        catch
        {
            await DumpPageAsync(_page, "pw-routes-health-load-timeout");
            throw;
        }

        // Click the "Check health" button
        await checkHealthBtn.ClickAsync();

        // Wait for success message to appear using Playwright's built-in waiting
        var successAlert = _page.Locator(".text-success");
        await successAlert.WaitForAsync(
            new() { Timeout = 15000, State = WaitForSelectorState.Visible }
        );

        // Verify the success message text
        var successText = await successAlert.TextContentAsync();
        successText
            .Should()
            .Contain("Health checked", "Success message should confirm health check completed");

        // Verify health table appears for at least one cluster
        // We expect to see health tables for clusters like identity, dashboard, etc.
        var healthTables = await _page.QuerySelectorAllAsync("[data-testid^='health-table-']");
        healthTables.Should().NotBeEmpty("At least one cluster health table should be displayed");

        // Verify table structure - check for at least one destination row
        var firstTable = healthTables.First();
        var destNames = await firstTable.QuerySelectorAllAsync("[data-testid='dest-name']");
        destNames.Should().NotBeEmpty("Health table should contain destination rows");

        // Verify destination details are present
        var destAddresses = await firstTable.QuerySelectorAllAsync("[data-testid='dest-address']");
        destAddresses.Should().NotBeEmpty("Destination addresses should be displayed");

        var destStatuses = await firstTable.QuerySelectorAllAsync("[data-testid='dest-status']");
        destStatuses.Should().NotBeEmpty("Destination statuses should be displayed");

        var destElapsed = await firstTable.QuerySelectorAllAsync("[data-testid='dest-elapsed']");
        destElapsed.Should().NotBeEmpty("Elapsed times should be displayed");

        // Verify raw JSON is available in details element
        var detailsElement = await _page.QuerySelectorAsync("details");
        detailsElement.Should().NotBeNull("Raw JSON details should be present");

        // Expand details and verify JSON content
        await detailsElement!.ClickAsync();
        var jsonTextarea = await _page.QuerySelectorAsync("details textarea");
        jsonTextarea.Should().NotBeNull("Raw JSON textarea should be present");
        var jsonContent = await jsonTextarea!.InputValueAsync();
        jsonContent.Should().Contain("path", "JSON should contain path field");
        jsonContent.Should().Contain("clusters", "JSON should contain clusters field");
    } // End of Method AdminUi_Routes_HealthProbe_DisplaysStatus

    private async Task<bool> WaitForIdentityAsync(string baseUrl, TimeSpan timeout)
    {
        using var client = new HttpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var url = baseUrl.TrimEnd('/') + "/.well-known/openid-configuration";
        while (sw.Elapsed < timeout)
        {
            try
            {
                var resp = await client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch { }
            await Task.Delay(1000);
        }
        return false;
    } // End of Method WaitForIdentityAsync

    private async Task EnsureLoggedInAsync()
    {
        // Check if already logged in by looking for admin nav
        var isLoggedIn =
            await _page!.QuerySelectorAsync("[data-testid='nav-overview']") is not null;
        if (isLoggedIn)
            return;

        // Wait for login form (Identity uses Input.Email / Input.Password naming)
        var emailInput = _page.Locator("input[name='Input.Email']").First;
        try
        {
            await emailInput.WaitForAsync(new() { Timeout = 10000 });
        }
        catch
        {
            await DumpPageAsync(_page, "pw-routes-health-login-timeout");
            throw;
        }

        // Fill credentials (dev-only)
        await emailInput.FillAsync("admin@tansu.local");
        await _page.Locator("input[name='Input.Password']").First.FillAsync("Passw0rd!");
        await _page.Locator("button[type='submit']").First.ClickAsync();

        // Wait for redirect
        await _page.WaitForURLAsync(url => url.Contains("/dashboard"), new() { Timeout = 10000 });
    } // End of Method EnsureLoggedInAsync

    private static async Task DumpPageAsync(IPage page, string label)
    {
        try
        {
            var html = await page.ContentAsync();
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{label}-{DateTime.UtcNow.Ticks}.html"
            );
            await System.IO.File.WriteAllTextAsync(path, html);
            Console.WriteLine($"Dumped page HTML to: {path}");
        }
        catch { }
    } // End of Method DumpPageAsync

    public async Task DisposeAsync()
    {
        if (_page is not null)
            await _page.CloseAsync();
        if (_ctx is not null)
            await _ctx.CloseAsync();
        if (_browser is not null)
            await _browser.CloseAsync();
        _pw?.Dispose();
    } // End of Method DisposeAsync
} // End of Class AdminRoutesHealthE2E
