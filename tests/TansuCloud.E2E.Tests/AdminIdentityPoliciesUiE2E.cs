// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class AdminIdentityPoliciesUiE2E : IAsyncLifetime
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

    [Fact(DisplayName = "Admin UI: IdentityPolicies page displays current configuration")]
    public async Task AdminUi_IdentityPolicies_DisplaysConfiguration()
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
        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/identity-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Identity Policies')", new() { Timeout = 10_000 });

        // Verify password policy section exists
        var passwordHeader = await _page.QuerySelectorAsync("text='Password Policy'");
        passwordHeader.Should().NotBeNull("password policy section should be visible");

        // Verify lockout section exists
        var lockoutHeader = await _page.QuerySelectorAsync("text='Account Lockout'");
        lockoutHeader.Should().NotBeNull("lockout section should be visible");

        // Verify token lifetimes section exists
        var tokenHeader = await _page.QuerySelectorAsync("text='Token Lifetimes'");
        tokenHeader.Should().NotBeNull("token lifetimes section should be visible");

        // Verify password length input exists and has a value
        var passwordLengthInput = await _page.QuerySelectorAsync("#passwordLength");
        passwordLengthInput.Should().NotBeNull("password length input should exist");
        var lengthValue = await passwordLengthInput!.InputValueAsync();
        lengthValue.Should().NotBeNullOrEmpty("password length should have a value");

        // Verify edit button is present
        var editButton = await _page.QuerySelectorAsync("button:text('Edit Policies')");
        editButton.Should().NotBeNull("edit button should be visible");
    } // End of Method AdminUi_IdentityPolicies_DisplaysConfiguration

    [Fact(DisplayName = "Admin UI: IdentityPolicies shows not-implemented message on save")]
    public async Task AdminUi_IdentityPolicies_ShowsNotImplementedMessage()
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

        var adminUrl = baseUrl.TrimEnd('/') + "/dashboard/admin/identity-policies";
        await _page!.GotoAsync(adminUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await EnsureSignedInAsync(baseUrl);

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Identity Policies')", new() { Timeout = 10_000 });

        // Click edit button
        var editButton = await _page.QuerySelectorAsync("button:text('Edit Policies')");
        editButton.Should().NotBeNull("edit button should exist");
        await editButton!.ClickAsync();

        // Wait for edit mode to activate
        await Task.Delay(500);

        // Verify save button appears
        var saveButton = await _page.QuerySelectorAsync("button:text('Save Changes')");
        saveButton.Should().NotBeNull("save button should appear in edit mode");

        // Click save
        await saveButton!.ClickAsync();

        // Wait for error message
        await Task.Delay(1000);

        // Verify not-implemented error message appears
        var errorAlert = await _page.QuerySelectorAsync(".alert-danger");
        errorAlert.Should().NotBeNull("error alert should appear");

        var errorText = await errorAlert!.TextContentAsync();
        errorText.Should().Contain("require service restart", "error message should mention restart requirement");
    } // End of Method AdminUi_IdentityPolicies_ShowsNotImplementedMessage

    [Fact(DisplayName = "Admin UI: IdentityPolicies navigation link works")]
    public async Task AdminUi_IdentityPolicies_NavigationWorks()
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
        await _page.WaitForSelectorAsync("[data-testid='nav-identity-policies']", new() { Timeout = 10_000 });

        // Click the navigation link
        var navLink = await _page.QuerySelectorAsync("[data-testid='nav-identity-policies']");
        navLink.Should().NotBeNull("identity policies nav link should exist");
        await navLink!.ClickAsync();

        // Wait for page to load
        await _page.WaitForSelectorAsync("h2:text('Identity Policies')", new() { Timeout = 10_000 });

        // Verify we're on the right page
        var url = _page.Url;
        url.Should().Contain("/dashboard/admin/identity-policies", "should navigate to identity policies page");
    } // End of Method AdminUi_IdentityPolicies_NavigationWorks

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
} // End of Class AdminIdentityPoliciesUiE2E
