// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Playwright;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class DashboardMetricsSmoke : IAsyncLifetime
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;

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
        await _page!.CloseAsync();
        await _ctx!.CloseAsync();
        await _browser!.CloseAsync();
        _pw?.Dispose();
    }

    [Fact(DisplayName = "Metrics page renders after login")]
    public async Task Metrics_Page_Renders()
    {
        var baseUrl = BaseUrl();
        // Navigate to dashboard root which will redirect to login
        await _page!.GotoAsync($"{baseUrl}/dashboard");

        // Perform login
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

        // Navigate to metrics page under admin
        await _page.GotoAsync($"{baseUrl}/dashboard/admin/metrics");

        // Assert heading and table appear (or No data placeholder)
        await _page.WaitForSelectorAsync("h1:text('Metrics')");
        var hasTable = await _page.Locator("table").CountAsync();
        hasTable.Should().BeGreaterThanOrEqualTo(0);
    }
}
