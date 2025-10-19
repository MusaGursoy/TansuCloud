// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using System.Net.Http.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminCacheRatePoliciesUiE2E : IAsyncLifetime
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

    public async Task DisposeAsync()
    {
        // Clean up test policies created by this test class
        await Infrastructure.PolicyCleanupHelper.CleanupAllTestPoliciesAsync(BaseUrl());
        
        if (_page is not null) await _page.CloseAsync();
        if (_ctx is not null) await _ctx.CloseAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    } // End of Method DisposeAsync

    private async Task<bool> WaitForIdentityAsync(string baseUrl, TimeSpan timeout)
    {
        var disc = $"{baseUrl.TrimEnd('/')}/.well-known/openid-configuration";
        var cts = new CancellationTokenSource(timeout);
        using var client = new HttpClient();
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync(disc, cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
            await Task.Delay(500, cts.Token);
        }
        return false;
    } // End of Method WaitForIdentityAsync

    private async Task EnsureSignedInAsync(string baseUrl)
    {
        var currentUrl = _page!.Url;
        if (currentUrl.Contains("/connect/authorize") || currentUrl.Contains("/Account/Login"))
        {
            await _page.WaitForSelectorAsync("input[name='Input.Email']", new() { Timeout = 10_000 });
            await _page.FillAsync("input[name='Input.Email']", "admin@tansu.local");
            await _page.FillAsync("input[name='Input.Password']", "Passw0rd!");
            await _page.ClickAsync("button[type='submit']");
            await _page.WaitForURLAsync($"{baseUrl}/**", new() { Timeout = 10_000 });
        }
    } // End of Method EnsureSignedInAsync

    [Fact(DisplayName = "Admin UI: Cache & Rate Policies page renders")]
    public async Task AdminUi_CacheRatePolicies_PageRenders()
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

        // Navigate to Cache & Rate Policies page
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/cache-rate-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Cache & Rate Limit Policies')", new() { Timeout = 10_000 });

        // Verify tabs exist
        var cacheTab = await _page.QuerySelectorAsync("button:has-text('Cache Policies')");
        cacheTab.Should().NotBeNull("cache policies tab should be visible");

        var rateLimitTab = await _page.QuerySelectorAsync("button:has-text('Rate Limit Policies')");
        rateLimitTab.Should().NotBeNull("rate limit policies tab should be visible");

        // Verify create button exists
        var createButton = await _page.QuerySelectorAsync("button:text('Create Policy')");
        createButton.Should().NotBeNull("create policy button should be visible");
    } // End of Method AdminUi_CacheRatePolicies_PageRenders

    [Fact(DisplayName = "Admin UI: Create Cache Policy workflow")]
    public async Task AdminUi_CacheRatePolicies_CreateCachePolicy()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady) return;

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/cache-rate-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureSignedInAsync(baseUrl);

        // Ensure we're on Cache Policies tab - use Locator API for dynamic Blazor re-renders
        await _page.Locator("button:has-text('Cache Policies')").ClickAsync(new() { Timeout = 10_000 });
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Click Create Policy button - use Locator API
        await _page.Locator("button:text('Create Policy')").ClickAsync(new() { Timeout = 5_000 });

        // Wait for modal to appear
        await _page.WaitForSelectorAsync(".modal.show", new() { Timeout = 5_000 });

        // Fill out cache policy form
        var policyName = $"e2e-cache-{Guid.NewGuid().ToString()[..8]}";
        
        // Fill Policy ID - wait for it to be visible first
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").FillAsync(policyName);
        
        // Select Cache Policy type (should be default 3, but ensure it's selected)
        await _page.Locator(".modal.show select").First.SelectOptionAsync(new[] { "3" });
        
        // Select Enforcement Mode (second select) - REQUIRED FIELD
        await _page.Locator(".modal.show select").Nth(1).SelectOptionAsync(new[] { "2" }); // Enforce mode
        
        // Wait for Cache configuration section to appear, then fill TTL
        await Task.Delay(500); // Give Blazor time to render conditional fields
        await _page.Locator(".modal.show input[type='number']").First.FillAsync("120");

        // VaryByQuery - wait for it to be visible
        await _page.Locator(".modal.show input[placeholder*='query']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await _page.Locator(".modal.show input[placeholder*='query']").FillAsync("page,category");

        // Save policy - scope to modal to avoid clicking wrong button
        await _page.Locator(".modal.show button:text('Save')").ClickAsync(new() { Timeout = 5_000 });

        // Wait for modal to close and policy to appear in list
        await _page.WaitForSelectorAsync(".modal.show", new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
        await Task.Delay(1000); // Allow time for policy to be created

        // Verify policy appears in list
        var policyCard = await _page.QuerySelectorAsync($"text={policyName}");
        policyCard.Should().NotBeNull($"policy {policyName} should appear in list");

        // Clean up: Delete the policy
        var deleteButtons = await _page.QuerySelectorAllAsync("button:has-text('Delete')");
        if (deleteButtons.Count > 0)
        {
            // Find the delete button for our policy (in the same card)
            foreach (var btn in deleteButtons)
            {
                var parent = await btn.EvaluateAsync<string>("el => el.closest('.card')?.innerText || ''");
                if (parent.Contains(policyName))
                {
                    await btn.ClickAsync();
                    // Confirm deletion in any confirmation modal/dialog if needed
                    await Task.Delay(500);
                    break;
                }
            }
        }
    } // End of Method AdminUi_CacheRatePolicies_CreateCachePolicy

    [Fact(DisplayName = "Admin UI: Create Rate Limit Policy workflow")]
    public async Task AdminUi_CacheRatePolicies_CreateRateLimitPolicy()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady) return;

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/cache-rate-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureSignedInAsync(baseUrl);

        // Switch to Rate Limit Policies tab - use Locator API
        await _page.Locator("button:has-text('Rate Limit Policies')").ClickAsync(new() { Timeout = 10_000 });
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Click Create Policy button - use Locator API
        await _page.Locator("button:text('Create Policy')").ClickAsync(new() { Timeout = 5_000 });

        // Wait for modal to appear
        await _page.WaitForSelectorAsync(".modal.show", new() { Timeout = 5_000 });

        // Fill out rate limit policy form
        var policyName = $"e2e-ratelimit-{Guid.NewGuid().ToString()[..8]}";
        
        // Fill Policy ID - wait for it to be visible first
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").FillAsync(policyName);
        
        // Select Rate Limit Policy type
        await _page.Locator(".modal.show select").First.SelectOptionAsync(new[] { "4" });
        
        // Select Enforcement Mode (second select) - REQUIRED FIELD
        await _page.Locator(".modal.show select").Nth(1).SelectOptionAsync(new[] { "2" }); // Enforce mode
        
        // Wait for rate limit fields to appear, then fill number inputs
        await Task.Delay(500); // Give Blazor time to render conditional fields
        
        // Fill Time Window (first number input)
        await _page.Locator(".modal.show input[type='number']").First.FillAsync("30");
        
        // Fill Permit Limit (second number input)
        await _page.Locator(".modal.show input[type='number']").Nth(1).FillAsync("50");

        // Wait for partition strategy select to appear (third select)
        await _page.Locator(".modal.show select").Nth(2).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        
        // Select partition strategy (PerIp)
        await _page.Locator(".modal.show select").Nth(2).SelectOptionAsync(new[] { "PerIp" });

        // Save policy - scope to modal
        await _page.Locator(".modal.show button:text('Save')").ClickAsync(new() { Timeout = 5_000 });

        // Wait for modal to close and policy to appear in list
        await _page.WaitForSelectorAsync(".modal.show", new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
        await Task.Delay(1000); // Allow time for policy to be created

        // Verify policy appears in list
        var policyCard = await _page.QuerySelectorAsync($"text={policyName}");
        policyCard.Should().NotBeNull($"policy {policyName} should appear in list");

        // Clean up: Delete the policy
        var deleteButtons = await _page.QuerySelectorAllAsync("button:has-text('Delete')");
        if (deleteButtons.Count > 0)
        {
            foreach (var btn in deleteButtons)
            {
                var parent = await btn.EvaluateAsync<string>("el => el.closest('.card')?.innerText || ''");
                if (parent.Contains(policyName))
                {
                    await btn.ClickAsync();
                    await Task.Delay(500);
                    break;
                }
            }
        }
    } // End of Method AdminUi_CacheRatePolicies_CreateRateLimitPolicy

    [Fact(DisplayName = "Admin UI: Cache Policy simulator works")]
    public async Task AdminUi_CacheRatePolicies_SimulatorWorks()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady) return;

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/cache-rate-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureSignedInAsync(baseUrl);

        // Ensure we're on Cache Policies tab - use Locator API
        await _page.Locator("button:has-text('Cache Policies')").ClickAsync(new() { Timeout = 10_000 });
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Create a test policy first - use Locator API
        await _page.Locator("button:text('Create Policy')").ClickAsync(new() { Timeout = 5_000 });
        await _page.WaitForSelectorAsync(".modal.show", new() { Timeout = 5_000 });

        var policyName = $"e2e-sim-{Guid.NewGuid().ToString()[..8]}";
        
        // Fill Policy ID - wait for it to be visible first
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await _page.Locator(".modal.show input[placeholder*='cache-api-responses']").FillAsync(policyName);
        
        // Select Cache Policy type (default is 3, but ensure it's selected)
        await _page.Locator(".modal.show select").First.SelectOptionAsync(new[] { "3" });
        
        // Select Enforcement Mode (second select) - REQUIRED FIELD
        await _page.Locator(".modal.show select").Nth(1).SelectOptionAsync(new[] { "2" }); // Enforce mode
        
        // Wait for Cache configuration section to appear, then fill TTL
        await Task.Delay(500); // Give Blazor time to render conditional fields
        await _page.Locator(".modal.show input[type='number']").First.FillAsync("300");
        
        // Wait for VaryByQuery input to appear
        await _page.Locator(".modal.show input[placeholder*='query']").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await _page.Locator(".modal.show input[placeholder*='query']").FillAsync("page");

        // Save policy - scope to modal
        await _page.Locator(".modal.show button:text('Save')").ClickAsync(new() { Timeout = 5_000 });
        await _page.WaitForSelectorAsync(".modal.show", new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
        await Task.Delay(1000);

        // Find and click Test button for the policy
        var testButtons = await _page.QuerySelectorAllAsync("button:has-text('Test')");
        if (testButtons.Count > 0)
        {
            // Click the last Test button (most recent policy)
            await testButtons[^1].ClickAsync();
            
            // Wait for Blazor to render the simulator modal (Blazor SignalR update takes time)
            await Task.Delay(2000); // Blazor needs time to render modal via SignalR
            
            // Verify simulator heading is visible (simpler selector works better for Blazor-rendered content)
            var simulatorHeading = await _page.QuerySelectorAsync("h5:has-text('Policy Simulator:')");
            simulatorHeading.Should().NotBeNull("simulator modal should be visible with heading");

            // Verify URL input field is present
            var urlInput = await _page.QuerySelectorAsync("input[placeholder*='/api/data']");
            urlInput.Should().NotBeNull("simulator URL input should be visible");
            
            // Verify Run Simulation button is present
            var runButton = await _page.QuerySelectorAsync("button:has-text('Run Simulation')");
            runButton.Should().NotBeNull("Run Simulation button should be visible");
            
            // Note: Actually running the simulation and verifying results would require
            // the simulator backend API to be fully implemented. For now, we verify
            // that the simulator UI opens correctly with all expected elements.
        }

        // Clean up: Delete the policy
        var deleteButtons = await _page.QuerySelectorAllAsync("button:has-text('Delete')");
        if (deleteButtons.Count > 0)
        {
            foreach (var btn in deleteButtons)
            {
                var parent = await btn.EvaluateAsync<string>("el => el.closest('.card')?.innerText || ''");
                if (parent.Contains(policyName))
                {
                    await btn.ClickAsync();
                    await Task.Delay(500);
                    break;
                }
            }
        }
    } // End of Method AdminUi_CacheRatePolicies_SimulatorWorks

    [Fact(DisplayName = "Admin UI: Navigation link to Cache & Rate Policies works")]
    public async Task AdminUi_CacheRatePolicies_NavigationWorks()
    {
        var baseUrl = BaseUrl();
        var idReady = await WaitForIdentityAsync(baseUrl, TimeSpan.FromSeconds(30));
        if (!idReady) return;

        // Navigate to admin index
        var adminIndexUrl = baseUrl.TrimEnd('/') + "/dashboard/admin";
        await _page!.GotoAsync(adminIndexUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureSignedInAsync(baseUrl);

        // Wait for admin layout to load
        await _page.WaitForSelectorAsync("nav", new() { Timeout = 10_000 });

        // Click navigation link - use Locator API
        await _page.Locator("a[href*='cache-rate-policies']").ClickAsync();
        await Task.Delay(1000);

        // Verify we're on the correct page
        var pageHeading = await _page.WaitForSelectorAsync("h2:text('Cache & Rate Limit Policies')", new() { Timeout = 10_000 });
        pageHeading.Should().NotBeNull("should navigate to cache & rate policies page");
    } // End of Method AdminUi_CacheRatePolicies_NavigationWorks
} // End of Class AdminCacheRatePoliciesUiE2E
