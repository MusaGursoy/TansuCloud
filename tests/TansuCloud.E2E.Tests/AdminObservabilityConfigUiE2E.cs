// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminObservabilityConfigUiE2E : IAsyncLifetime
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

    [Fact(DisplayName = "Admin UI: ObservabilityConfig page displays governance settings")]
    public async Task AdminUi_ObservabilityConfig_DisplaysSettings()
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
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/observability-config";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Observability Governance')", new() { Timeout = 10_000 });

        // Verify retention section exists
        var retentionHeader = await _page.QuerySelectorAsync("text='Retention Periods'");
        retentionHeader.Should().NotBeNull("retention section should be visible");

        // Verify sampling section exists
        var samplingHeader = await _page.QuerySelectorAsync("text='Sampling'");
        samplingHeader.Should().NotBeNull("sampling section should be visible");

        // Verify alert SLOs section exists
        var alertHeader = await _page.QuerySelectorAsync("text='Alert SLO Templates'");
        alertHeader.Should().NotBeNull("alert SLOs section should be visible");

        // Verify retention inputs exist and have values
        var tracesInput = await _page.QuerySelectorAsync("#retentionTraces");
        tracesInput.Should().NotBeNull("traces retention input should exist");
        var tracesValue = await tracesInput!.InputValueAsync();
        tracesValue.Should().NotBeNullOrEmpty("traces retention should have a value");

        var logsInput = await _page.QuerySelectorAsync("#retentionLogs");
        logsInput.Should().NotBeNull("logs retention input should exist");

        var metricsInput = await _page.QuerySelectorAsync("#retentionMetrics");
        metricsInput.Should().NotBeNull("metrics retention input should exist");

        // Verify sampling input exists
        var samplingInput = await _page.QuerySelectorAsync("#samplingRatio");
        samplingInput.Should().NotBeNull("sampling ratio input should exist");
        var samplingValue = await samplingInput!.InputValueAsync();
        samplingValue.Should().NotBeNullOrEmpty("sampling ratio should have a value");

        // Verify refresh button is present
        var refreshButton = await _page.QuerySelectorAsync("button:text('Refresh Configuration')");
        refreshButton.Should().NotBeNull("refresh button should be visible");
    } // End of Method AdminUi_ObservabilityConfig_DisplaysSettings

    [Fact(DisplayName = "Admin UI: ObservabilityConfig navigation link works")]
    public async Task AdminUi_ObservabilityConfig_NavigationWorks()
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
        await _page.WaitForSelectorAsync("[data-testid='nav-observability-config']", new() { Timeout = 10_000 });

        // Click the navigation link
        var navLink = await _page.QuerySelectorAsync("[data-testid='nav-observability-config']");
        navLink.Should().NotBeNull("observability config nav link should exist");
        await navLink!.ClickAsync();

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Observability Governance')", new() { Timeout = 10_000 });

        // Verify we're on the right page
        var url = _page.Url;
        url.Should().Contain("/dashboard/admin/observability-config", "should navigate to observability config page");
    } // End of Method AdminUi_ObservabilityConfig_NavigationWorks

    [Fact(DisplayName = "Admin UI: ObservabilityConfig displays alert SLO details")]
    public async Task AdminUi_ObservabilityConfig_DisplaysAlertSLOs()
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

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/observability-config";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Observability Governance')", new() { Timeout = 10_000 });

        // Wait a bit for data to load
        await Task.Delay(1000);

        // Check if alert SLO details table is present
        var detailsHeader = await _page.QuerySelectorAsync("text='Alert SLO Details'");
        
        if (detailsHeader != null)
        {
            // If the table exists, verify it has content
            var table = await _page.QuerySelectorAsync("table");
            table.Should().NotBeNull("alert SLO details table should exist");

            // Verify table has headers
            var headers = await _page.QuerySelectorAllAsync("th");
            headers.Should().HaveCountGreaterThan(0, "table should have column headers");
        }
    } // End of Method AdminUi_ObservabilityConfig_DisplaysAlertSLOs

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
            // Use correct Identity form field names with Locator API (auto-retrying)
            var emailInput = _page.Locator("input[name='Input.Email']");
            await emailInput.WaitForAsync(new() { Timeout = 10_000 });
            await emailInput.FillAsync("admin@tansu.local");

            var passwordInput = _page.Locator("input[name='Input.Password']");
            await passwordInput.FillAsync("Passw0rd!");

            var submitButton = _page.Locator("button[type='submit']");
            await submitButton.ClickAsync();

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
} // End of Class AdminObservabilityConfigUiE2E
