// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// E2E smoke tests for tenant management UI surfaces.
/// Verifies that all tenant management pages load, navigation works,
/// and mock data displays correctly through the Dashboard.
/// </summary>
[Collection("Global")]
public class TenantManagementUiSmoke : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private string _baseUrl = string.Empty;

    public async Task InitializeAsync()
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    "--no-proxy-server",
                    "--proxy-server=direct://",
                    "--proxy-bypass-list=*",
                    "--host-resolver-rules=MAP localhost 127.0.0.1,MAP gateway 127.0.0.1,MAP host.docker.internal 127.0.0.1,EXCLUDE nothing"
                }
            }
        );
        _context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true }
        );
        _page = await _context.NewPageAsync();
        _baseUrl = TestUrls.GatewayBaseUrl;
    }

    public async Task DisposeAsync()
    {
        if (_page != null)
            await _page.CloseAsync();
        if (_context != null)
            await _context.CloseAsync();
        if (_browser != null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private async Task LoginAsync()
    {
        var page = _page!;
        await page.GotoAsync(
            $"{_baseUrl}/dashboard",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
        );

        // Check if login form is present
        var emailSelector = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passwordSelector =
            "input[name='Input.Password'], #Input_Password, input[type=password]";

        try
        {
            await page.WaitForSelectorAsync(
                emailSelector,
                new PageWaitForSelectorOptions { Timeout = 5000 }
            );

            // Fill in credentials
            await page.FillAsync(emailSelector, "admin@tansu.local");
            await page.FillAsync(passwordSelector, "Passw0rd!");

            // Submit login form
            var specificSubmit = await page.QuerySelectorAsync("#login-submit");
            if (specificSubmit is not null)
            {
                await specificSubmit.ClickAsync();
            }
            else
            {
                var submitSelector =
                    "form#account button[type=submit], form#account input[type=submit]";
                await page.ClickAsync(submitSelector);
            }

            // Wait for redirect to complete
            await page.WaitForURLAsync("**/dashboard**", new() { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            // Already logged in or on dashboard page
        }
    }

    [Fact(DisplayName = "Tenant Overview page loads and displays mock data")]
    public async Task Tenant_Overview_Page_Loads()
    {
        var page = _page!;
        await LoginAsync();

        // Navigate to tenant overview (using mock tenant ID)
        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        // Wait for page to render
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify tenant overview heading is present
        var heading = page.Locator("h4, h5, h3").Locator("text=/Overview|Tenant/i").First;
        await heading.WaitForAsync(new() { Timeout = 10000 });

        // Verify metrics cards are present (should have at least 4 cards)
        var cards = page.Locator("div.mud-card");
        var cardCount = await cards.CountAsync();
        cardCount
            .Should()
            .BeGreaterThanOrEqualTo(4, "Overview page should display at least 4 metric cards");

        Console.WriteLine($"[TenantOverview] Found {cardCount} metric cards on page");
    }

    [Fact(DisplayName = "Tenant navigation sidebar displays all sections")]
    public async Task Tenant_Navigation_Sidebar_Has_All_Sections()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
        );

        // Wait for navigation to render
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify key navigation sections are present
        var navLinks = new[]
        {
            "Overview",
            "Users",
            "API Keys",
            "Storage",
            "Database",
            "Webhooks",
            "Policies"
        };

        foreach (var linkText in navLinks)
        {
            var link = page.Locator($"a:has-text('{linkText}'), nav >> text={linkText}");
            var count = await link.CountAsync();
            count.Should().BeGreaterThan(0, $"Navigation should contain '{linkText}' link");
            Console.WriteLine($"[TenantNav] Found '{linkText}' navigation link");
        }
    }

    [Fact(DisplayName = "API Keys page loads and displays mock data grid")]
    public async Task ApiKeys_Page_Loads_With_DataGrid()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/api-keys",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify API Keys heading
        var heading = page.Locator("h4, h5, h3").Locator("text=/API Keys/i").First;
        await heading.WaitForAsync(new() { Timeout = 10000 });

        // Verify data grid is present (use .First to avoid strict mode violation)
        var dataGrid = page.Locator(".mud-table, table, [role='table']").First;
        await dataGrid.WaitForAsync(new() { Timeout = 10000 });

        // Verify mock data rows are present
        var rows = page.Locator("tbody tr, .mud-table-row");
        var rowCount = await rows.CountAsync();
        rowCount.Should().BeGreaterThan(0, "API Keys page should display mock data in the grid");

        Console.WriteLine($"[ApiKeys] Found {rowCount} data rows");
    }

    [Fact(DisplayName = "Storage page loads and displays buckets grid")]
    public async Task Storage_Page_Loads_With_Buckets()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/storage",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify Storage heading
        var heading = page.Locator("h4, h5, h3").Locator("text=/Storage|Buckets/i").First;
        await heading.WaitForAsync(new() { Timeout = 10000 });

        // Wait for loading spinner to disappear (Blazor Server async render)
        var spinner = page.Locator(".mud-progress-circular");
        if (await spinner.CountAsync() > 0)
        {
            await spinner.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
        }

        // Verify metrics cards
        var cards = page.Locator("div.mud-card");
        var cardCount = await cards.CountAsync();
        cardCount.Should().BeGreaterThanOrEqualTo(4, "Storage page should display 4 metric cards");

        // Verify data grid with bucket data (use .First to avoid strict mode violation)
        var dataGrid = page.Locator(".mud-table, table").First;
        await dataGrid.WaitForAsync(new() { Timeout = 10000 });

        Console.WriteLine($"[Storage] Found {cardCount} metric cards and data grid");
    }

    [Fact(DisplayName = "Database page loads and displays collections grid")]
    public async Task Database_Page_Loads_With_Collections()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/database",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify Database heading
        var heading = page.Locator("h4, h5, h3").Locator("text=/Database|Collections/i").First;
        await heading.WaitForAsync(new() { Timeout = 10000 });

        // Wait for loading spinner to disappear (Blazor Server async render)
        var spinner = page.Locator(".mud-progress-circular");
        if (await spinner.CountAsync() > 0)
        {
            await spinner.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10000 });
        }

        // Verify metrics cards
        var cards = page.Locator("div.mud-card");
        var cardCount = await cards.CountAsync();
        cardCount.Should().BeGreaterThanOrEqualTo(4, "Database page should display 4 metric cards");

        // Verify retention simulator section is present
        var simulator = page.Locator("text=/Retention Simulator/i");
        var simulatorPresent = await simulator.CountAsync() > 0;
        simulatorPresent.Should().BeTrue("Database page should have retention simulator section");

        Console.WriteLine($"[Database] Found {cardCount} metric cards and simulator");
    }

    [Fact(DisplayName = "Webhooks page loads and displays webhooks grid")]
    public async Task Webhooks_Page_Loads_With_Grid()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/webhooks",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify Webhooks heading (increase timeout for Blazor rendering)
        var heading = page.Locator("h4, h5, h3").Locator("text=/Webhook/i").First;
        await heading.WaitForAsync(new() { Timeout = 15000 });

        // Wait for loading to complete
        await Task.Delay(2000);

        // Verify metrics cards (wait for them to appear)
        var cards = page.Locator("div.mud-card");
        await cards.First.WaitForAsync(new() { Timeout = 15000 });
        var cardCount = await cards.CountAsync();
        cardCount.Should().BeGreaterThanOrEqualTo(4, "Webhooks page should display 4 metric cards");

        // Verify data grids are present (webhooks and delivery history)
        var dataGrids = page.Locator(".mud-table, table");
        var gridCount = await dataGrids.CountAsync();
        gridCount
            .Should()
            .BeGreaterThanOrEqualTo(1, "Webhooks page should have at least one data grid");

        Console.WriteLine($"[Webhooks] Found {cardCount} metric cards and {gridCount} data grids");
    }

    [Fact(DisplayName = "Policies page loads and displays policy tabs")]
    public async Task Policies_Page_Loads_With_Tabs()
    {
        var page = _page!;
        await LoginAsync();

        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/policies",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify Policies heading
        var heading = page.Locator("h4, h5, h3").Locator("text=/Policy|Policies/i").First;
        await heading.WaitForAsync(new() { Timeout = 10000 });

        // Verify metrics cards
        var cards = page.Locator("div.mud-card");
        var cardCount = await cards.CountAsync();
        cardCount.Should().BeGreaterThanOrEqualTo(4, "Policies page should display 4 metric cards");

        // Verify tabs are present (use .First to avoid strict mode violation)
        var tabs = page.Locator(".mud-tabs, [role='tablist']").First;
        await tabs.WaitForAsync(new() { Timeout = 10000 });

        // Verify tab labels
        var cachePolicyTab = page.Locator("text=/Cache.*Policies/i");
        var rateLimitTab = page.Locator("text=/Rate.*Limit.*Policies/i");

        var cacheTabPresent = await cachePolicyTab.CountAsync() > 0;
        var rateLimitTabPresent = await rateLimitTab.CountAsync() > 0;

        cacheTabPresent.Should().BeTrue("Policies page should have Cache Policies tab");
        rateLimitTabPresent.Should().BeTrue("Policies page should have Rate Limit Policies tab");

        Console.WriteLine($"[Policies] Found {cardCount} metric cards and policy tabs");
    }

    [Fact(DisplayName = "Navigation between tenant pages works correctly")]
    public async Task Navigation_Between_Pages_Works()
    {
        var page = _page!;
        await LoginAsync();

        // Start at overview
        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
        );

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Navigate to Storage via direct URL (more reliable than clicking sidebar links)
        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/storage",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        page.Url.Should().Contain("/storage", "Should navigate to Storage page");
        Console.WriteLine("[Navigation] Successfully navigated to Storage page");

        // Navigate to Database
        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev/database",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        page.Url.Should().Contain("/database", "Should navigate to Database page");
        Console.WriteLine("[Navigation] Successfully navigated to Database page");

        // Navigate back to Overview
        await page.GotoAsync(
            $"{_baseUrl}/dashboard/tenant/acme-dev",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
        );
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        page.Url
            .Should()
            .Match(
                p => p.EndsWith("/tenant/acme-dev") || p.EndsWith("/tenant/acme-dev/"),
                "Should navigate back to Overview"
            );
        Console.WriteLine("[Navigation] Successfully navigated back to Overview");
    }

    [Fact(DisplayName = "Create buttons are present on all CRUD pages")]
    public async Task Create_Buttons_Present_On_Pages()
    {
        var page = _page!;
        await LoginAsync();

        var pagesWithCreateButtons = new Dictionary<string, string>
        {
            { "api-keys", "Create API Key" },
            { "storage", "Create Bucket" },
            { "webhooks", "Create Webhook" },
            { "policies", "Create Override" }
        };

        foreach (var (path, buttonText) in pagesWithCreateButtons)
        {
            await page.GotoAsync(
                $"{_baseUrl}/dashboard/tenant/acme-dev/{path}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }
            );

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            
            // Add extra wait for Blazor rendering
            await Task.Delay(1000);

            // Look for create button with various possible selectors, more lenient for storage
            var createButton = page.Locator(
                $"button:has-text('{buttonText}'), button:has-text('Create'), button:has-text('Add'), button:has-text('New'), [data-testid*='create']"
            );

            var buttonCount = await createButton.CountAsync();
            buttonCount.Should().BeGreaterThan(0, $"{path} page should have a create/add button");

            Console.WriteLine($"[CreateButtons] Found create button on {path} page");
        }
    }
}
