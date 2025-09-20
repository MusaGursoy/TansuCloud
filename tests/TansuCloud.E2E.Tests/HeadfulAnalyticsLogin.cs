// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace TansuCloud.E2E.Tests;

[Collection("Global")]
public class HeadfulAnalyticsLogin : IAsyncLifetime
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private IPage? _page;

    private static string BaseUrl()
    {
        var env = System.Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.TrimEnd('/');
        return "http://127.0.0.1:8080";
    }

    public async Task InitializeAsync()
    {
        _pw = await Microsoft.Playwright.Playwright.CreateAsync();
        // Headful (non-headless) + a visible viewport
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = false, SlowMo = 50 });
        _ctx = await _browser.NewContextAsync(
            new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 1280, Height = 800 }
            }
        );
        _page = await _ctx.NewPageAsync();

        // Attach lightweight network logging for diagnostics (headful only scenario)
        try
        {
            var outDir = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(),
                "test-results"
            );
            System.IO.Directory.CreateDirectory(outDir);
            _page.Request += (_, req) =>
            {
                try
                {
                    var line = $"[Headful][REQ] {req.Method} {req.Url}";
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(outDir, "headful-network.log"),
                        line + System.Environment.NewLine
                    );
                }
                catch (Exception ex)
                {
                    TestArtifacts.PersistArtifactError("Headful_RequestHandler", ex);
                }
            };
            _page.Response += (_, resp) =>
            {
                try
                {
                    var line =
                        $"[Headful][RES] {(int)resp.Status} {resp.Request.Method} {resp.Url}";
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(outDir, "headful-network.log"),
                        line + System.Environment.NewLine
                    );
                }
                catch (Exception ex)
                {
                    TestArtifacts.PersistArtifactError("Headful_ResponseHandler", ex);
                }
            };
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Headful_AttachNetworkLogging", ex);
        }
    }

    public async Task DisposeAsync()
    {
        // Give a short delay to observe the final state if running locally
        await _page!.WaitForTimeoutAsync(1000);
        await _page!.CloseAsync();
        await _ctx!.CloseAsync();
        await _browser!.CloseAsync();
        _pw?.Dispose();
    }

    [Fact(DisplayName = "Headful: login and open analytics (metrics) page")]
    public async Task Headful_Login_And_Open_Metrics()
    {
        var baseUrl = BaseUrl();

        // Navigate to Dashboard root (gateway adds /dashboard)
        await _page!.GotoAsync($"{baseUrl}/dashboard");

        // Perform login with seeded dev admin account
        var emailSelector = "input[name='Input.Email'], #Input_Email, input[type=email]";
        var passwordSelector =
            "input[name='Input.Password'], #Input_Password, input[type=password]";
        await _page.FillAsync(emailSelector, "admin@tansu.local");
        await _page.FillAsync(passwordSelector, "Passw0rd!");
        var specificSubmit = await _page.QuerySelectorAsync("#login-submit");
        if (specificSubmit is not null)
        {
            try
            {
                await specificSubmit.ClickAsync();
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("Headful_Login_ClickSpecificSubmit", ex);
            }
        }
        else
        {
            try
            {
                await _page.ClickAsync(
                    "form#account button[type=submit], form#account input[type=submit], button#login-submit"
                );
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("Headful_Login_ClickFallbackSubmit", ex);
            }
        }

        // After submitting the login form, explicitly wait for the OIDC callback or dashboard URL.
        // Clicking can trigger background navigations; wait here to avoid Playwright click-timeout errors.
        try
        {
            // First wait for signin-oidc (authorization callback) if it happens
            await _page!.WaitForURLAsync("**/signin-oidc*", new() { Timeout = 60000 });
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Headful_WaitForSigninOidc", ex);
        }
        try
        {
            // Then wait for the dashboard to become visible (longer window)
            await _page!.WaitForURLAsync("**/dashboard*", new() { Timeout = 60000 });
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Headful_WaitForDashboard", ex);
            // If still not navigated, take a screenshot for diagnostics and let later assertions fail with context
            try
            {
                var outDir = System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(),
                    "test-results"
                );
                System.IO.Directory.CreateDirectory(outDir);
                await _page!.ScreenshotAsync(
                    new Microsoft.Playwright.PageScreenshotOptions
                    {
                        Path = System.IO.Path.Combine(outDir, "headful-login-post-click.png"),
                        FullPage = true
                    }
                );
            }
            catch (Exception ex2)
            {
                TestArtifacts.PersistArtifactError("Headful_Login_PostClick_Screenshot", ex2);
            }
        }

        // Wait for Dashboard to report readiness via gateway before navigating.
        // This reduces flakiness where the gateway briefly returns 502 while the app finishes startup.
        await WaitForHealthReadyAsync(baseUrl);

        // Navigate to Admin → Metrics (analytics) page with enhanced retry (transient 5xx, slow load)
        try
        {
            await NavigateWithRetriesAsync(
                $"{baseUrl}/dashboard/admin/metrics",
                attempts: 5,
                perTryTimeoutMs: 30000
            );
        }
        catch (Exception ex)
        {
            try
            {
                var outDir = System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(),
                    "test-results"
                );
                System.IO.Directory.CreateDirectory(outDir);
                await _page!.ScreenshotAsync(
                    new Microsoft.Playwright.PageScreenshotOptions
                    {
                        Path = System.IO.Path.Combine(outDir, "headful-final-goto-fail.png"),
                        FullPage = true
                    }
                );
                var html = await _page.ContentAsync();
                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(outDir, "headful-final-goto-fail.html"),
                    html
                );
                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(outDir, "headful-final-goto-fail.err.txt"),
                    ex.ToString()
                );
            }
            catch { }
            throw;
        }

        // Assert the Metrics heading is visible
        var heading = await _page.Locator("h1").First.InnerTextAsync();
        heading.Should().Contain("Metrics");

        // Wait a little to let charts load
        await _page.WaitForTimeoutAsync(1500);
    }

    private async Task WaitForHealthReadyAsync(string baseUrl, int timeoutMs = 15000)
    {
        using var client = new HttpClient();
        client.Timeout = System.TimeSpan.FromSeconds(2);
        var healthUrl = baseUrl.TrimEnd('/') + "/health/ready";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var res = await client.GetAsync(healthUrl);
                if (res.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // swallow and retry until timeout
            }
            await Task.Delay(500);
        }
        // If health didn't become ready in time, proceed and let Playwright capture the failure.
    }

    private async Task<IResponse?> NavigateWithRetriesAsync(
        string url,
        int attempts = 3,
        int perTryTimeoutMs = 15000
    )
    {
        if (_page is null)
            throw new System.InvalidOperationException("Page is not initialized");

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            IResponse? resp = null;
            try
            {
                var start = System.Diagnostics.Stopwatch.StartNew();
                resp = await _page.GotoAsync(
                    url,
                    new PageGotoOptions
                    {
                        Timeout = perTryTimeoutMs,
                        WaitUntil = WaitUntilState.Load
                    }
                );
                var elapsed = start.ElapsedMilliseconds;
                try
                {
                    var outDir = System.IO.Path.Combine(
                        System.IO.Directory.GetCurrentDirectory(),
                        "test-results"
                    );
                    System.IO.Directory.CreateDirectory(outDir);
                    var statusInfo = resp != null ? ((int)resp.Status).ToString() : "no-response";
                    await System.IO.File.AppendAllTextAsync(
                        System.IO.Path.Combine(outDir, "headful-goto-attempts.log"),
                        $"attempt={attempt} status={statusInfo} elapsedMs={elapsed}\n"
                    );
                }
                catch { }
            }
            catch (PlaywrightException pwEx)
            {
                // Playwright may throw on navigation issues; capture then decide to retry
                var outDir = System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(),
                    "test-results"
                );
                try
                {
                    System.IO.Directory.CreateDirectory(outDir);
                }
                catch { }
                try
                {
                    await _page.ScreenshotAsync(
                        new Microsoft.Playwright.PageScreenshotOptions
                        {
                            Path = System.IO.Path.Combine(
                                outDir,
                                $"headful-goto-attempt-{attempt}-exception.png"
                            ),
                            FullPage = true
                        }
                    );
                    var html = await _page.ContentAsync();
                    await System.IO.File.WriteAllTextAsync(
                        System.IO.Path.Combine(
                            outDir,
                            $"headful-goto-attempt-{attempt}-exception.html"
                        ),
                        html
                    );
                    await System.IO.File.WriteAllTextAsync(
                        System.IO.Path.Combine(
                            outDir,
                            $"headful-goto-attempt-{attempt}-exception.txt"
                        ),
                        pwEx.ToString()
                    );
                    await System.IO.File.AppendAllTextAsync(
                        System.IO.Path.Combine(outDir, "headful-goto-attempts.log"),
                        $"attempt={attempt} exception={pwEx.GetType().Name} msg={pwEx.Message}\n"
                    );
                }
                catch { }
            }

            // If we got a response, check status. Treat 5xx as transient and retry.
            if (resp != null)
            {
                try
                {
                    var status = resp.Status;
                    if (status >= 500)
                    {
                        // transient server error, wait and retry
                        try
                        {
                            var outDir = System.IO.Path.Combine(
                                System.IO.Directory.GetCurrentDirectory(),
                                "test-results"
                            );
                            System.IO.Directory.CreateDirectory(outDir);
                            await System.IO.File.AppendAllTextAsync(
                                System.IO.Path.Combine(outDir, "headful-goto-attempts.log"),
                                $"attempt={attempt} transientStatus={status}\n"
                            );
                            // capture snapshot for this failing attempt
                            await _page.ScreenshotAsync(
                                new Microsoft.Playwright.PageScreenshotOptions
                                {
                                    Path = System.IO.Path.Combine(
                                        outDir,
                                        $"headful-goto-attempt-{attempt}-status-{(int)status}.png"
                                    ),
                                    FullPage = true
                                }
                            );
                            var html = await _page.ContentAsync();
                            await System.IO.File.WriteAllTextAsync(
                                System.IO.Path.Combine(
                                    outDir,
                                    $"headful-goto-attempt-{attempt}-status-{(int)status}.html"
                                ),
                                html
                            );
                        }
                        catch { }
                        await Task.Delay(500);
                        continue;
                    }
                    // successful (non-5xx) response — return it
                    return resp;
                }
                catch
                {
                    // ignore and possibly retry
                }
            }

            // If no valid non-5xx response yet, small delay before retry
            await Task.Delay(500);
        }

        throw new System.TimeoutException(
            $"Failed to navigate to {url} after {attempts} attempts due to persistent 5xx or navigation failures."
        );
    }
}
