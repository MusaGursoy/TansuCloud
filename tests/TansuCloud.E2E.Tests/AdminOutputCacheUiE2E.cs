// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminOutputCacheUiE2E : IAsyncLifetime
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

    [Fact(DisplayName = "Admin UI: OutputCache edit TTL values and save")]
    public async Task AdminUi_OutputCache_EditAndSave()
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
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/output-cache";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await EnsureLoggedInAsync();
        await _page.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Wait for inputs to appear (component auto-loads on init)
        var defaultTtlInput = _page.GetByTestId("input-default-ttl");
        var staticTtlInput = _page.GetByTestId("input-static-ttl");
        try
        {
            await defaultTtlInput.First.WaitForAsync(new() { Timeout = 30000 });
            await staticTtlInput.First.WaitForAsync(new() { Timeout = 30000 });
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
            await DumpPageAsync(_page, "pw-outputcache-inputs-timeout");
            throw;
        }

        await defaultTtlInput.WaitForAsync();
        await staticTtlInput.WaitForAsync();

        // Snapshot originals
        var originalDefaultStr = await defaultTtlInput.InputValueAsync();
        var originalStaticStr = await staticTtlInput.InputValueAsync();
        int.TryParse(originalDefaultStr, out var originalDefault);
        int.TryParse(originalStaticStr, out var originalStatic);

        // Change values (e.g., Default=10, Static=600)
        var newDefault = 10;
        var newStatic = 600;
        await defaultTtlInput.FillAsync(newDefault.ToString());
        await staticTtlInput.FillAsync(newStatic.ToString());

        // Click save
        await _page.GetByTestId("btn-save").ClickAsync();

        // Confirm modal appears
        var confirmBtn = _page.GetByTestId("btn-confirm-save");
        try
        {
            await confirmBtn.WaitForAsync(new() { Timeout = 5000 });
        }
        catch
        {
            await DumpPageAsync(_page, "pw-outputcache-confirm-timeout");
            throw;
        }
        await confirmBtn.ClickAsync();
        
        // Wait for dialog to close completely (MudBlazor + Blazor rendering cycle)
        await _page.WaitForTimeoutAsync(1000);

        // Verify success message
        var success = await _page.QuerySelectorAsync(".alert.alert-success");
        success.Should().NotBeNull("Save should show success alert");

        // Reload and verify new values are persisted
        await _page.WaitForTimeoutAsync(500); // Extra wait for dialog to fully close
        await _page.GetByTestId("btn-load").ClickAsync(new() { Force = true });
        await _page.WaitForTimeoutAsync(500);

        var reloadedDefault = await defaultTtlInput.InputValueAsync();
        var reloadedStatic = await staticTtlInput.InputValueAsync();
        reloadedDefault.Should().Be(newDefault.ToString());
        reloadedStatic.Should().Be(newStatic.ToString());

        // Revert to originals
        await defaultTtlInput.FillAsync(originalDefault.ToString());
        await staticTtlInput.FillAsync(originalStatic.ToString());
        await _page.GetByTestId("btn-save").ClickAsync();
        await _page.GetByTestId("btn-confirm-save").ClickAsync();
        
        // Wait for dialog to close completely (MudBlazor + Blazor rendering cycle)
        await _page.WaitForTimeoutAsync(1000);

        // Verify revert success
        var revertSuccess = await _page.QuerySelectorAsync(".alert.alert-success");
        revertSuccess.Should().NotBeNull("Revert should show success alert");
    } // End of Method AdminUi_OutputCache_EditAndSave

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
        var isLoggedIn = await _page!.QuerySelectorAsync("[data-testid='nav-overview']") is not null;
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
            await DumpPageAsync(_page, "pw-outputcache-login-timeout");
            throw;
        }

        // Fill credentials (dev-only)
        await emailInput.FillAsync("admin@tansu.local");
        await _page.Locator("input[name='Input.Password']").First.FillAsync("Passw0rd!");
        await _page.Locator("button[type='submit']").First.ClickAsync();

        // Wait for redirect
        await _page.WaitForURLAsync(
            url => url.Contains("/dashboard"),
            new() { Timeout = 10000 }
        );
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
} // End of Class AdminOutputCacheUiE2E
