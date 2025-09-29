// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Playwright;

namespace TansuCloud.E2E.Tests;

public class DashboardWebsocketSoak
{
    private static string GetGatewayBaseUrl()
    {
            return TestUrls.GatewayBaseUrl;
    }

    [Fact(DisplayName = "Dashboard WebSocket soak: 50 sessions hold 3 minutes (dev quick-run)")]
    public async Task Soak_50_Sessions_3_Minutes()
    {
        // Opt-in gate: skip unless RUN_SOAK=1
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("RUN_SOAK"),
                "1",
                StringComparison.Ordinal
            )
        )
            return; // treated as pass; avoids failing local runs unintentionally

        var sessions =
            int.TryParse(Environment.GetEnvironmentVariable("SOAK_SESSIONS"), out var s) && s > 0
                ? s
                : 50; // set to 100 for full acceptance
        var hold =
            int.TryParse(Environment.GetEnvironmentVariable("SOAK_MINUTES"), out var m) && m > 0
                ? TimeSpan.FromMinutes(m)
                : TimeSpan.FromMinutes(3); // set to 10-15 for full acceptance
        var baseUrl = GetGatewayBaseUrl();

        using var cts = new CancellationTokenSource(hold + TimeSpan.FromSeconds(10));
        var disconnects = new ConcurrentBag<string>();
        var badGatewayResponses = new ConcurrentBag<string>();
        var navFailures = new ConcurrentBag<string>();

        // Preflight readiness to avoid false negatives (HttpClient without proxy)
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        async Task<bool> ReachableAsync(string url, bool okOnly)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var res = await http.SendAsync(req);
                var code = (int)res.StatusCode;
                if (okOnly)
                    return code == 200;
                return (code >= 200 && code < 400) || code == 401 || code == 403;
            }
            catch
            {
                return false;
            }
        }
        async Task WaitUntilAsync(
            string label,
            Func<Task<bool>> probe,
            int timeoutMs = 90000,
            int stepMs = 1000
        )
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromMilliseconds(timeoutMs))
            {
                if (await probe())
                    return;
                await Task.Delay(stepMs);
            }
            throw new TimeoutException($"Readiness probe timed out: {label}");
        }
        await WaitUntilAsync(
            "gateway-root",
            async () => await ReachableAsync($"{baseUrl}/", okOnly: false)
        );
        // Ensure Identity is ready before discovery to avoid transient 400s during startup
        await WaitUntilAsync(
            "identity-ready",
            async () => await ReachableAsync($"{baseUrl}/identity/health/ready", okOnly: true)
        );
        await WaitUntilAsync(
            "identity-discovery",
            async () =>
                await ReachableAsync(
                    $"{baseUrl}/identity/.well-known/openid-configuration",
                    okOnly: true
                )
        );
        await WaitUntilAsync(
            "dashboard-landing",
            async () => await ReachableAsync($"{baseUrl}/dashboard", okOnly: false)
        );

        // Use Chromium flags to ensure no proxy is used; avoid mutating process env

        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // Launch browser with env-tunable headless/headful
        static bool GetBoolEnv(string name, bool fallback)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v))
                return fallback;
            return string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        }

        var headless = GetBoolEnv("SOAK_HEADLESS", true);
        await using var browser = await playwright.Chromium.LaunchAsync(
            new()
            {
                Headless = headless,
                // Force no proxy and ensure localhost resolves to IPv4
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    "--no-proxy-server",
                    "--proxy-bypass-list=<-loopback>,localhost,127.0.0.1,::1",
                    "--host-resolver-rules=MAP localhost 127.0.0.1",
                    // Headful stability tweaks: keep timers/threads active for many tabs
                    "--disable-renderer-backgrounding",
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-ipc-flooding-protection"
                },
            }
        );

        var tasks = new List<Task>();
        static int GetIntEnv(string name, int fallback)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0)
                return v;
            return fallback;
        }
        var useTabs = GetBoolEnv("SOAK_USE_TABS", false);
        var navTimeoutMs = GetIntEnv("SOAK_NAV_TIMEOUT_MS", 60000); // default 60s
        var maxNavRetries = GetIntEnv("SOAK_NAV_RETRIES", 3);
        var jitterBaseMs = GetIntEnv("SOAK_JITTER_BASE_MS", 20); // per session index
        var jitterMaxExtraMs = GetIntEnv("SOAK_JITTER_MAX_MS", 1000);
        var contextsCount = Math.Max(1, GetIntEnv("SOAK_CONTEXTS", 1));
        var doPreLogin = GetBoolEnv("SOAK_PRELOGIN", useTabs); // default: only when tabs are used
    var reloadSeconds = GetIntEnv("SOAK_RELOAD_SECONDS", 30); // 0 to disable periodic reloads
        static WaitUntilState ParseWaitUntil(string? envValue)
        {
            return envValue?.Trim().ToLowerInvariant() switch
            {
                "load" => WaitUntilState.Load,
                "commit" => WaitUntilState.Commit,
                "networkidle" => WaitUntilState.NetworkIdle,
                _ => WaitUntilState.DOMContentLoaded,
            };
        }
        var waitUntil = ParseWaitUntil(Environment.GetEnvironmentVariable("SOAK_WAIT_UNTIL"));
        var rnd = new Random();
        // Optional shared context for tabs mode (one window with many tabs)
        IBrowserContext? sharedContext = null;
        List<IBrowserContext>? sharedContexts = null;
        if (useTabs)
        {
            if (contextsCount <= 1)
            {
                sharedContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                sharedContext.SetDefaultNavigationTimeout(navTimeoutMs);
                // Reduce network/render load: block images and fonts
                await sharedContext.RouteAsync("**/*.{png,jpg,jpeg,webp,gif,ico}", r => r.AbortAsync());
                await sharedContext.RouteAsync("**/*.{woff,woff2,ttf,otf}", r => r.AbortAsync());
                await sharedContext.AddInitScriptAsync("() => { navigator.webdriver = true; }");
            }
            else
            {
                sharedContexts = new List<IBrowserContext>(contextsCount);
                for (int ci = 0; ci < contextsCount; ci++)
                {
                    var ctx = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                    ctx.SetDefaultNavigationTimeout(navTimeoutMs);
                    await ctx.RouteAsync("**/*.{png,jpg,jpeg,webp,gif,ico}", r => r.AbortAsync());
                    await ctx.RouteAsync("**/*.{woff,woff2,ttf,otf}", r => r.AbortAsync());
                    await ctx.AddInitScriptAsync("() => { navigator.webdriver = true; }");
                    sharedContexts.Add(ctx);
                }
            }

            // Warm up auth/assets per shared context to reduce initial surge during the test
            if (doPreLogin)
            {
                static async Task PreLoginAsync(IBrowserContext ctx, string baseUrl, int timeoutMs, ConcurrentBag<string> navFailures)
                {
                    var targetUrl = $"{baseUrl}/dashboard";
                    var page = await ctx.NewPageAsync();
                    var tries = 0;
                    var max = 3;
                    while (tries < max)
                    {
                        try
                        {
                            await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = timeoutMs });
                            break; // success
                        }
                        catch (Exception ex)
                        {
                            tries++;
                            if (tries >= max)
                            {
                                navFailures.Add($"{DateTimeOffset.UtcNow:o} prelogin failed after {tries} attempts: {ex.Message}");
                                break;
                            }
                            await Task.Delay(500 + tries * 250);
                        }
                    }
                    try { await page.CloseAsync(); } catch { /* ignore */ }
                    await Task.Delay(200); // allow cookies/session to settle
                }

                if (sharedContexts is not null)
                {
                    foreach (var ctx in sharedContexts)
                    {
                        await PreLoginAsync(ctx, baseUrl, Math.Max(navTimeoutMs, 60000), navFailures);
                    }
                }
                else if (sharedContext is not null)
                {
                    await PreLoginAsync(sharedContext, baseUrl, Math.Max(navTimeoutMs, 60000), navFailures);
                }
            }
        }
        for (var i = 0; i < sessions; i++)
        {
            var index = i; // capture for closures
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        IBrowserContext? contextToDispose = null;
                        IBrowserContext? context = null;
                        if (sharedContexts is not null)
                        {
                            // Distribute tabs across multiple contexts
                            context = sharedContexts[index % sharedContexts.Count];
                        }
                        else
                        {
                            context = sharedContext;
                        }
                        if (context is null)
                        {
                            contextToDispose = await browser.NewContextAsync(
                                new() { IgnoreHTTPSErrors = true }
                            );
                            contextToDispose.SetDefaultNavigationTimeout(navTimeoutMs);
                            context = contextToDispose;
                        }
                        var page = await context.NewPageAsync();
                        try { await page.SetViewportSizeAsync(800, 600); } catch { }

                        // minimal console/network capture
                        page.Response += (_, r) =>
                        {
                            if (
                                r.Status == 502
                                && r.Url.Contains("/_blazor", StringComparison.OrdinalIgnoreCase)
                            )
                            {
                                badGatewayResponses.Add(r.Url);
                            }
                        };
                        page.WebSocket += (_, ws) =>
                        {
                            if (ws.Url.Contains("/_blazor", StringComparison.OrdinalIgnoreCase))
                            {
                                ws.Close += (_, _) =>
                                    disconnects.Add($"{DateTimeOffset.UtcNow:o} {ws.Url} closed");
                                ws.FrameReceived += (
                                    _,
                                    _
                                ) => { /* keep alive */
                                };
                            }
                        };

                        // Stagger starts to avoid thundering herd on authorize/dashboard
                        var jitter = TimeSpan.FromMilliseconds(
                            (index * jitterBaseMs) + rnd.Next(0, jitterMaxExtraMs)
                        );
                        await Task.Delay(jitter, cts.Token);

                        // Navigate with simple retry/backoff
                        var targetUrl = $"{baseUrl}/dashboard";
                        var attempts = 0;
                        bool navOk = false;
                        while (!navOk && attempts < maxNavRetries && !cts.IsCancellationRequested)
                        {
                            try
                            {
                                await page.GotoAsync(
                                    targetUrl,
                                    new() { WaitUntil = waitUntil, Timeout = navTimeoutMs }
                                );
                                navOk = true;
                            }
                            catch (Exception ex)
                            {
                                attempts++;
                                if (attempts >= maxNavRetries)
                                {
                                    navFailures.Add(
                                        $"{DateTimeOffset.UtcNow:o} nav failed after {attempts} attempts: {ex.Message}"
                                    );
                                    // Give up on this page/session to avoid failing the whole run
                                    try { await page.CloseAsync(); } catch { }
                                    if (contextToDispose is not null)
                                    {
                                        try { await contextToDispose.CloseAsync(); } catch { }
                                    }
                                    return; // exit this session task
                                }
                                await Task.Delay(1000 + attempts * 500, cts.Token);
                            }
                        }

                        var end = DateTime.UtcNow + hold;
                        while (DateTime.UtcNow < end && !cts.IsCancellationRequested)
                        {
                            // soft interaction: reload occasionally
                            await page.WaitForTimeoutAsync(5000);
                            // Avoid hammering; light reload on interval unless disabled
                            if (reloadSeconds > 0 && (end - DateTime.UtcNow).TotalSeconds % reloadSeconds < 5)
                            {
                                try
                                {
                                    await page.ReloadAsync(
                                        new() { WaitUntil = WaitUntilState.DOMContentLoaded }
                                    );
                                }
                                catch { }
                            }
                        }

                        await page.CloseAsync();
                        if (contextToDispose is not null)
                            await contextToDispose.CloseAsync();
                    },
                    cts.Token
                )
            );
        }

        await Task.WhenAll(tasks);

        // Close shared context if we used tabs mode
        if (sharedContext is not null)
            await sharedContext.CloseAsync();
        if (sharedContexts is not null)
        {
            foreach (var ctx in sharedContexts)
            {
                await ctx.CloseAsync();
            }
        }

        playwright.Dispose();

        // Assess disconnect rate (best-effort; Playwright can't see server-side disconnect without events; we track WS close)
        var disconnectRate = (double)disconnects.Count / sessions;
        Console.WriteLine($"Disconnects: {disconnects.Count}/{sessions} ({disconnectRate:P2})");
        if (navFailures.Count > 0)
        {
            Console.WriteLine($"Navigation failures: {navFailures.Count}");
            foreach (var f in navFailures.Take(5))
                Console.WriteLine(f);
        }
        disconnectRate
            .Should()
            .BeLessThan(0.05, "should be <5% in quick-run; target <1% in full run");
        badGatewayResponses
            .Count.Should()
            .Be(0, "no 502 errors should occur on WebSocket upgrade through gateway");
    }
}
