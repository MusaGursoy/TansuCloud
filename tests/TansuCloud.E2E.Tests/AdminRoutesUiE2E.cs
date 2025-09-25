// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminRoutesUiE2E
{
    [Fact]
    public async Task Routes_Page_Renders_Or_Login()
    {
        // Gate UI test behind env var to avoid running by default
        var run = Environment.GetEnvironmentVariable("RUN_UI");
        if (!string.Equals(run, "1", StringComparison.Ordinal))
        {
            return; // not running UI tests in this environment
        }

        var baseUrl =
            Environment.GetEnvironmentVariable("GATEWAY_BASE_URL") ?? "http://127.0.0.1:8080";

        // Try to ensure browsers are installed (best-effort)
        try
        {
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch { }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true }
        );
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true }
        );
        var page = await context.NewPageAsync();

        // Navigate to Routes admin page (canonical under /dashboard)
        var target = baseUrl.TrimEnd('/') + "/dashboard/admin/routes";
        var response = await page.GotoAsync(
            target,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }
        );
        Assert.NotNull(response);

        // Accept either the page header (already authenticated) or the sign-in screen
        var foundRoutesHeader = false;
        var foundLogin = false;
        try
        {
            await page.Locator("h2:has-text(\"YARP Routes\")")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            foundRoutesHeader = true;
        }
        catch { }

        if (!foundRoutesHeader)
        {
            // Look for common Identity login cues
            try
            {
                // Email/password fields or a generic Sign in text
                var loginLocator = page.Locator(
                    "input[type=email], input[name=\"Input.Email\"], text=Sign in"
                );
                await loginLocator.First.WaitForAsync(
                    new LocatorWaitForOptions { Timeout = 15000 }
                );
                foundLogin = true;
            }
            catch { }
        }

        Assert.True(
            foundRoutesHeader || foundLogin,
            "Expected either Routes admin page or login screen to be visible."
        );
    }
} // End of Class AdminRoutesUiE2E
