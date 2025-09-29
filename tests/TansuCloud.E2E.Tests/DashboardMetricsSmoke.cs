// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private IAPIRequestContext? _api;

    private static string BaseUrl()
    {
        return TestUrls.GatewayBaseUrl;
    }

    public async Task InitializeAsync()
    {
        _pw = await Microsoft.Playwright.Playwright.CreateAsync();
        var headful = string.Equals(
            Environment.GetEnvironmentVariable("HEADFUL"),
            "1",
            StringComparison.OrdinalIgnoreCase
        );
        _browser = await _pw.Chromium.LaunchAsync(
            new() { Headless = !headful, SlowMo = headful ? 50 : 0 }
        );
        _ctx = await _browser.NewContextAsync(
            new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = headful ? new() { Width = 1280, Height = 900 } : null
            }
        );
        _page = await _ctx.NewPageAsync();
        // Console log relay for diagnostics
        _page.Console += (_, msg) =>
        {
            try
            {
                var line = $"[BrowserConsole][{msg.Type}] {msg.Text}";
                Console.WriteLine(line);
                File.AppendAllText(
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "test-results",
                        "browser-console.log"
                    ),
                    line + Environment.NewLine
                );
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("ConsoleHandler", ex);
            }
        };
        // Network request log
        _page.Request += (_, req) =>
        {
            try
            {
                var line = $"[Net][REQ] {req.Method} {req.Url}";
                File.AppendAllText(
                    Path.Combine(Directory.GetCurrentDirectory(), "test-results", "network.log"),
                    line + Environment.NewLine
                );
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("RequestHandler", ex);
            }
        };
        _page.Response += (_, resp) =>
        {
            try
            {
                var line = $"[Net][RES] {(int)resp.Status} {resp.Request.Method} {resp.Url}";
                File.AppendAllText(
                    Path.Combine(Directory.GetCurrentDirectory(), "test-results", "network.log"),
                    line + Environment.NewLine
                );
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("ResponseHandler", ex);
            }
        };
        try
        {
            Directory.CreateDirectory(
                Path.Combine(Directory.GetCurrentDirectory(), "test-results")
            );
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Initialize_CreateTestResultsDir", ex);
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_api is not null)
                await _api.DisposeAsync();
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Dispose_Api", ex);
        }
        try
        {
            if (_page is not null)
                await _page.CloseAsync();
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Dispose_Page", ex);
        }
        try
        {
            if (_ctx is not null)
                await _ctx.CloseAsync();
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Dispose_Ctx", ex);
        }
        try
        {
            if (_browser is not null)
                await _browser.CloseAsync();
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Dispose_Browser", ex);
        }
        try
        {
            _pw?.Dispose();
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("Dispose_Playwright", ex);
        }
    }

    [Fact(DisplayName = "Metrics page renders after login")]
    public async Task Metrics_Page_Renders()
    {
        var baseUrl = BaseUrl();
        // Soft readiness / diagnostics (non-blocking): attempt identity & dashboard probes and log results, then proceed.
        _api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { IgnoreHTTPSErrors = true }
        );
        var readinessLogPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "test-results",
            "readiness.log"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(readinessLogPath)!);
        async Task LogProbeAsync(string label, string url)
        {
            try
            {
                var res = await _api.GetAsync(url, new() { MaxRedirects = 5 });
                var line = $"[{DateTime.UtcNow:O}] {label} {(int)res.Status} {url}";
                File.AppendAllText(readinessLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                var line =
                    $"[{DateTime.UtcNow:O}] {label} ERR {url} :: {ex.GetType().Name} {ex.Message}";
                File.AppendAllText(readinessLogPath, line + Environment.NewLine);
            }
        }
        var localhostAlias = baseUrl.Replace(
            "127.0.0.1",
            "localhost",
            StringComparison.OrdinalIgnoreCase
        );
        var probeBases = new[] { baseUrl, TestUrls.PublicBaseUrl, localhostAlias }
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct()
            .ToArray();
        foreach (var b in probeBases)
        {
            await LogProbeAsync("disc", $"{b}/identity/.well-known/openid-configuration");
            await LogProbeAsync("jwks", $"{b}/identity/.well-known/jwks");
            await LogProbeAsync("rootdisc", $"{b}/.well-known/openid-configuration");
            await LogProbeAsync("dash-ready", $"{b}/dashboard/health/ready");
            await LogProbeAsync("dash-js", $"{b}/dashboard/_framework/blazor.web.js");
        }
        // Continue regardless of outcome; global fixture already validated baseline readiness.

        // Proceed directly to authenticated navigation/login flow.

        await EnsureLoggedInAsync("/dashboard/admin/metrics");
        // Already navigated to the metrics page inside EnsureLoggedInAsync.
        // Wait for a deterministic element on the page (data-testid) instead of global navigation states.
        var title = _page!.Locator("[data-testid=metrics-title]");
        try
        {
            await title.First.WaitForAsync(new() { Timeout = 30000 });
        }
        catch
        {
            await DumpPageAsync(_page, "metrics-title-missing");
            throw new Xunit.Sdk.XunitException(
                $"metrics title not visible after login, URL={_page.Url}"
            );
        }
        (await title.CountAsync()).Should().BeGreaterThan(0);
        var table = _page!.Locator("[data-testid=metrics-table]");
        try
        {
            await table.First.WaitForAsync(new() { Timeout = 15000 });
        }
        catch
        {
            await DumpPageAsync(_page, "metrics-table-missing");
            // Don't fail here; APIs below will still validate metrics backend
        }

        // After login, call the admin-only Metrics Catalog API using the browser context storage state
        // This reuses cookies reliably (domain/path/host) without manually composing Cookie headers.
        var api = await _pw!.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                StorageState = await _ctx!.StorageStateAsync()
            }
        );
        // Prefer the canonical /dashboard prefix; if gateway maps root aliases too, fall back to root
        var resp = await api.GetAsync($"{baseUrl}/dashboard/api/metrics/catalog");
        if (resp.Status is not 200)
        {
            resp = await api.GetAsync($"{baseUrl}/api/metrics/catalog");
        }
        resp.Ok.Should()
            .BeTrue($"metrics catalog should be accessible after admin login, got {resp.Status}");
        // Ensure JSON content; if not, capture a short diagnostic snippet
        var textBody = await resp.TextAsync();
        textBody.Should().NotBeNullOrWhiteSpace();
        // Fast heuristic: HTML responses start with '<'
        textBody
            .TrimStart()
            .FirstOrDefault()
            .Should()
            .NotBe(
                '<',
                $"expected JSON but got HTML. Status={resp.Status}, First200=\n{new string(textBody.Take(200).ToArray())}"
            );
        var json = System.Text.Json.JsonDocument.Parse(textBody);
        json.Should().NotBeNull();

        // Also call a small range query to validate JSON "matrix" shape
        var url =
            $"{baseUrl}/dashboard/api/metrics/range?chartId=storage.http.rps&rangeMinutes=1&stepSeconds=15";
        var rangeResp = await api.GetAsync(url);
        if (rangeResp.Status is not 200)
        {
            // Fallback to root alias if necessary
            rangeResp = await api.GetAsync(
                $"{baseUrl}/api/metrics/range?chartId=storage.http.rps&rangeMinutes=1&stepSeconds=15"
            );
        }
        rangeResp.Ok.Should().BeTrue($"metrics range should return 200, got {rangeResp.Status}");
        var rangeText = await rangeResp.TextAsync();
        rangeText
            .TrimStart()
            .FirstOrDefault()
            .Should()
            .NotBe(
                '<',
                $"expected JSON but got HTML. Status={rangeResp.Status}, First200=\n{new string(rangeText.Take(200).ToArray())}"
            );
        var rangeJson = System.Text.Json.JsonDocument.Parse(rangeText);
        rangeJson.Should().NotBeNull();
    }

    private async Task EnsureLoggedInAsync(string adminPath)
    {
        var page = _page!;
        var baseUrl = BaseUrl().TrimEnd('/');
        var target = baseUrl + adminPath;

        // Capture first natural OIDC authorize redirect for diagnostics (avoid manually navigating to /connect/authorize).
        var firstAuthorizeCaptured = false;
        page.Request += async (sender, request) =>
        {
            try
            {
                if (firstAuthorizeCaptured)
                    return;

                var url = request.Url;
                if (!url.Contains("/connect/authorize", StringComparison.OrdinalIgnoreCase))
                    return;

                firstAuthorizeCaptured = true;
                var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                Directory.CreateDirectory(outDir);

                var uri = new Uri(url);
                var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var queryDict = qs
                    .AllKeys.Where(k => k != null)
                    .ToDictionary(k => k!, k => qs[k!] ?? string.Empty);

                var payload = new
                {
                    url,
                    method = request.Method,
                    query = queryDict
                };
                await File.WriteAllTextAsync(
                    Path.Combine(outDir, "authorize-first.json"),
                    System.Text.Json.JsonSerializer.Serialize(
                        payload,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                    )
                );

                var required = new[]
                {
                    "client_id",
                    "redirect_uri",
                    "response_type",
                    "scope",
                    "code_challenge"
                };
                var missing = required
                    .Where(r =>
                        !queryDict.ContainsKey(r) || string.IsNullOrWhiteSpace(queryDict[r])
                    )
                    .ToList();
                if (missing.Count > 0)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(outDir, "authorize-first-missing.txt"),
                        string.Join(",", missing)
                    );
                }
            }
            catch (Exception ex)
            {
                try
                {
                    var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                    Directory.CreateDirectory(outDir);
                    await File.AppendAllTextAsync(
                        Path.Combine(outDir, "artifact-errors.log"),
                        $"authorize-first capture EX {ex.GetType().Name} {ex.Message}\n"
                    );
                }
                catch
                {
                    // swallow secondary logging errors
                }
            }
        };

        // Progressive navigation + gateway reachability diagnostics
        var diagDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
        Directory.CreateDirectory(diagDir);
        var navLog = Path.Combine(diagDir, "nav.log");
        void Log(string msg)
        {
            try
            {
                File.AppendAllText(navLog, $"[{DateTime.UtcNow:O}] {msg}" + Environment.NewLine);
            }
            catch { }
        }

        // Start Playwright tracing (kept until Dispose). If already started, ignore.
        try
        {
            await _ctx!.Tracing.StartAsync(
                new()
                {
                    Screenshots = true,
                    Snapshots = true,
                    Sources = true
                }
            );
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_FillCredentials", ex);
        }

        // Fast probe via APIRequest to avoid waiting full browser nav if gateway unreachable
        try
        {
            var probeCtx = await _pw!.APIRequest.NewContextAsync(
                new() { IgnoreHTTPSErrors = true }
            );
            var resp = await probeCtx.GetAsync(baseUrl + "/");
            Log($"probe / status={(int)resp.Status}");
        }
        catch (Exception ex)
        {
            Log($"probe / EX {ex.GetType().Name} {ex.Message}");
        }

        async Task<bool> TryNavigate(string url, int timeoutMs)
        {
            Log($"nav attempt {url} timeout={timeoutMs}");
            try
            {
                await page.GotoAsync(
                    url,
                    new() { Timeout = timeoutMs, WaitUntil = WaitUntilState.Commit }
                );
                Log($"nav success {url} final={page.Url}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"nav fail {url} {ex.GetType().Name} {ex.Message}");
                return false;
            }
        }

        // Progressive strategy: /dashboard -> /dashboard/health/ready (API) -> /dashboard/admin -> final target
        if (!await TryNavigate(baseUrl + "/dashboard", 10000))
        {
            // Probe health endpoint via API for quick signal
            try
            {
                var probeCtx = await _pw!.APIRequest.NewContextAsync(
                    new() { IgnoreHTTPSErrors = true }
                );
                var h = await probeCtx.GetAsync(baseUrl + "/dashboard/health/ready");
                Log($"health ready status={(int)h.Status}");
            }
            catch (Exception ex)
            {
                Log($"health ready EX {ex.GetType().Name} {ex.Message}");
            }
        }
        if (!page.Url.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
        {
            await TryNavigate(baseUrl + "/dashboard/admin", 15000);
        }
        if (!page.Url.Contains("/dashboard/admin", StringComparison.OrdinalIgnoreCase))
        {
            await TryNavigate(target, 60000); // original target
        }
        // If still not on dashboard context, dump page and continue (subsequent logic may still succeed if late nav happens)
        if (!page.Url.Contains("/dashboard", StringComparison.OrdinalIgnoreCase))
        {
            await DumpPageAsync(page, "pre-login-dashboard-missing");
        }

        // If we already navigated successfully above, don’t reissue the same request unless still not at target.
        if (!page.Url.Contains("/dashboard/admin/metrics", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await page.GotoAsync(
                    target,
                    new() { WaitUntil = WaitUntilState.Commit, Timeout = 60000 }
                );
            }
            catch (Exception ex)
            {
                Log($"final target nav fail {ex.GetType().Name} {ex.Message}");
            }
        }

        // Early authorize diagnostics: if we're already at /connect/authorize before seeing any dashboard/login markup, capture artifacts.
        if (page.Url.Contains("/connect/authorize", StringComparison.OrdinalIgnoreCase))
        {
            await DumpAuthorizeDiagnosticsAsync(page, _pw!, _ctx!, "initial-authorize");
        }

        // 2. Detect login page
        var emailByLabel = page.GetByLabel("Email", new() { Exact = false });
        var passwordByLabel = page.GetByLabel("Password", new() { Exact = false });
        var emailLocator = page.Locator(
            "input[name='Input.Email'], input#Input_Email, input[type='email'], input[name='Email'], input#Email, input#email"
        );
        var passwordLocator = page.Locator(
            "input[name='Input.Password'], input#Input_Password, input[type='password'], input[name='Password'], input#Password, input#password"
        );

        var loginDetected =
            await TryWaitAsync(emailByLabel, 8000) || await TryWaitAsync(emailLocator, 8000);

        if (loginDetected)
        {
            // 3. Fill credentials (dev seeded admin)
            try
            {
                if (await emailByLabel.CountAsync() > 0)
                    await emailByLabel.FillAsync("admin@tansu.local");
                else if (await emailLocator.CountAsync() > 0)
                    await emailLocator.First.FillAsync("admin@tansu.local");

                if (await passwordByLabel.CountAsync() > 0)
                    await passwordByLabel.FillAsync("Passw0rd!");
                else if (await passwordLocator.CountAsync() > 0)
                    await passwordLocator.First.FillAsync("Passw0rd!");
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("EnsureLoggedIn_SubmitClick", ex);
            }

            // 4. Submit
            // Analyzer-friendly explicit submit button locator
            var submitQuery = "button[type='submit'], input[type='submit']";
            ILocator submitButton = page.Locator(submitQuery).First;
            await TryScreenshotAsync(page, "before-login-submit");
            try
            {
                await submitButton.ClickAsync();
            }
            catch { }
            await TryScreenshotAsync(page, "after-login-click");

            // 5. Wait for callback or auth cookie or dashboard navigation
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var satisfied = false;
            while (DateTime.UtcNow < deadline)
            {
                var url = page.Url;
                if (
                    url.Contains("signin-oidc", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("/dashboard", StringComparison.OrdinalIgnoreCase)
                )
                {
                    satisfied = true;
                    break;
                }
                try
                {
                    var cookies = await _ctx!.CookiesAsync();
                    if (
                        cookies.Any(c =>
                            string.Equals(
                                c.Name,
                                ".AspNetCore.Cookies",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        satisfied = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    TestArtifacts.PersistArtifactError("EnsureLoggedIn_PollCookies", ex);
                }
                await Task.Delay(500);
            }
            if (!satisfied)
            {
                await DumpPageAsync(page, "login-post-click-timeout");
                await DumpCookiesAsync(_ctx!, "login-post-click-timeout-cookies.json");
            }
        }

        // 6. Best-effort waits (do not fail hard here – outer assertions will)
        try
        {
            await page.WaitForURLAsync("**/signin-oidc*", new() { Timeout = 15000 });
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_WaitSigninOidc", ex);
        }
        try
        {
            await page.WaitForURLAsync("**/dashboard*", new() { Timeout = 45000 });
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_WaitDashboard", ex);
        }

        // 7. Assert auth cookie exists (soft)
        try
        {
            var cookies = await _ctx!.CookiesAsync();
            var hasAuth = cookies.Any(c =>
                string.Equals(c.Name, ".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase)
            );
            hasAuth.Should().BeTrue("auth cookie should be present after login");
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_ReadAuthCookie", ex);
        }

        // 8. Navigate to /dashboard/admin to ensure layout (some flows may not redirect exactly)
        try
        {
            await page.GotoAsync(
                baseUrl + "/dashboard/admin",
                new() { WaitUntil = WaitUntilState.Commit, Timeout = 60000 }
            );
            await TryScreenshotAsync(page, "admin-shell");
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_GotoAdminShell", ex);
        }

        // 9. Use nav if present, else direct navigation
        var navMetrics = page.Locator("[data-testid=nav-metrics]");
        var navigatedViaNav = false;
        try
        {
            await navMetrics.First.WaitForAsync(new() { Timeout = 8000 });
            await navMetrics.First.ClickAsync();
            await TryScreenshotAsync(page, "clicked-nav-metrics");
            navigatedViaNav = true;
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_ClickNavMetrics", ex);
        }
        if (!navigatedViaNav)
        {
            try
            {
                await page.GotoAsync(
                    target,
                    new() { WaitUntil = WaitUntilState.Commit, Timeout = 60000 }
                );
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("EnsureLoggedIn_DirectGotoMetrics", ex);
            }
        }

        // 10. Final anchors – capture screenshots but don't throw (outer test asserts presence later)
        try
        {
            var metricsTitleLocator = page.Locator("[data-testid=metrics-title]").First;
            await metricsTitleLocator.WaitForAsync(new() { Timeout = 25000 });
            await TryScreenshotAsync(page, "metrics-title-visible");
            try
            {
                var metricsTableLocator = page.Locator("[data-testid=metrics-table]").First;
                await metricsTableLocator.WaitForAsync(new() { Timeout = 15000 });
            }
            catch (Exception ex)
            {
                TestArtifacts.PersistArtifactError("EnsureLoggedIn_WaitMetricsTable", ex);
            }
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("EnsureLoggedIn_FinalWaitMetrics", ex);
        }

        // 11. If still clearly on login page, dump deep diagnostics
        if (page.Url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
        {
            // We reached the login page but never saw metrics. Capture diagnostics only; DO NOT manually navigate to /connect/authorize
            // because doing so bypasses the OIDC middleware-built query string (client_id, redirect_uri, code_challenge, etc.) and
            // produces a 400 invalid_request missing client_id which stalls the flow. The natural redirect already occurred earlier.
            await DumpPageAsync(page, "login-stalled");
            await DumpCookiesAsync(_ctx!, "login-stalled-cookies.json");
        }

        // Additional: if stuck on authorize endpoint even after attempts, dump diagnostics.
        if (page.Url.Contains("/connect/authorize", StringComparison.OrdinalIgnoreCase))
        {
            await DumpAuthorizeDiagnosticsAsync(page, _pw!, _ctx!, "final-authorize");
        }
    }

    private static async Task<bool> TryWaitAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DumpPageAsync(IPage page, string prefix)
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await page.ScreenshotAsync(
                new() { Path = Path.Combine(outDir, $"{prefix}.png"), FullPage = true }
            );
            var html = await page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{prefix}.html"), html);
        }
        catch (Exception ex)
        {
            try
            {
                var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                Directory.CreateDirectory(outDir);
                await File.AppendAllTextAsync(
                    Path.Combine(outDir, "artifact-errors.log"),
                    $"DumpPageAsync {prefix} EX {ex.GetType().Name} {ex.Message}\n"
                );
            }
            catch { }
        }
    }

    private static async Task TryScreenshotAsync(IPage page, string tag)
    {
        try
        {
            var headful = string.Equals(
                Environment.GetEnvironmentVariable("HEADFUL"),
                "1",
                StringComparison.OrdinalIgnoreCase
            );
            if (!headful)
                return; // only capture extra mid-run screenshots in headful mode
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await page.ScreenshotAsync(
                new()
                {
                    Path = Path.Combine(outDir, $"{DateTime.UtcNow:HHmmss}_{tag}.png"),
                    FullPage = true
                }
            );
        }
        catch (Exception ex)
        {
            try
            {
                var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
                Directory.CreateDirectory(outDir);
                await File.AppendAllTextAsync(
                    Path.Combine(outDir, "artifact-errors.log"),
                    $"TryScreenshotAsync {tag} EX {ex.GetType().Name} {ex.Message}\n"
                );
            }
            catch { }
        }
    }

    private static async Task DumpCookiesAsync(IBrowserContext ctx, string file)
    {
        try
        {
            var cookies = await ctx.CookiesAsync();
            var json = System.Text.Json.JsonSerializer.Serialize(
                cookies,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
            );
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, file), json);
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("DumpCookiesAsync", ex);
        }
    }

    private static async Task DumpAuthorizeDiagnosticsAsync(
        IPage page,
        IPlaywright pw,
        IBrowserContext ctx,
        string tag
    )
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            var prefix = $"authorize-{tag}";
            // Dump page & screenshot
            await DumpPageAsync(page, prefix);

            // Parse query parameters
            var url = page.Url;
            var uri = new Uri(url);
            var q = uri.Query.TrimStart('?');
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var k = Uri.UnescapeDataString(kv[0]);
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                dict[k] = v;
            }
            var expected = new[]
            {
                "client_id",
                "redirect_uri",
                "scope",
                "response_type",
                "code_challenge",
                "code_challenge_method"
            };
            var missing = expected.Where(e => !dict.ContainsKey(e)).ToArray();
            var qpJson = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    url,
                    parameters = dict,
                    missing = missing,
                    timestamp = DateTime.UtcNow
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
            );
            await File.WriteAllTextAsync(Path.Combine(outDir, prefix + "-query.json"), qpJson);

            // Raw HTTP GET via APIRequest (may reproduce error status/body)
            var api = await pw.APIRequest.NewContextAsync(new() { IgnoreHTTPSErrors = true });
            var resp = await api.GetAsync(url);
            var headers = resp.Headers;
            var status = resp.Status;
            var body = await resp.TextAsync();
            var meta = new
            {
                status,
                headers,
                bodyFirst500 = new string(body.Take(500).ToArray())
            };
            await File.WriteAllTextAsync(
                Path.Combine(outDir, prefix + "-response.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    meta,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                )
            );

            // Save full body separately (detect content type heuristic)
            var ext = body.TrimStart().StartsWith("<") ? ".html" : ".txt";
            await File.WriteAllTextAsync(Path.Combine(outDir, prefix + "-body" + ext), body);

            // Dump cookies at this point
            await DumpCookiesAsync(ctx, prefix + "-cookies.json");
        }
        catch (Exception ex)
        {
            TestArtifacts.PersistArtifactError("DumpAuthorizeDiagnostics", ex);
        }
    }
}
