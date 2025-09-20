// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using FluentAssertions;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class GatewayRateLimitRetryAfterTests
{
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    } // End of Method CreateClient

    private static string GetGatewayBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try
            {
                var uri = new Uri(env);
                var host =
                    (
                        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || uri.Host == "::1"
                    )
                        ? "127.0.0.1"
                        : uri.Host;
                var b = new UriBuilder(uri) { Host = host };
                return b.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return env.TrimEnd('/');
            }
        }
        return "http://127.0.0.1:8080";
    } // End of Method GetGatewayBaseUrl

    private static async Task WaitForGatewayAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = GetGatewayBaseUrl();
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var ping = await client.GetAsync($"{baseUrl}/health/live", ct);
                if ((int)ping.StatusCode < 500)
                    return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Gateway not ready");
    } // End of Method WaitForGatewayAsync

    [Fact(DisplayName = "Gateway returns 429 with Retry-After header when rate limited")]
    public async Task Gateway_429_RetryAfter_Present()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = CreateClient();
        await WaitForGatewayAsync(client, cts.Token);

        var baseUrl = GetGatewayBaseUrl();
        // Hit a public route (no Authorization) many times rapidly to trigger rate limiter.
        // Using health endpoint to avoid backend load/side effects.
        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 500; i++)
        {
            tasks.Add(client.GetAsync($"{baseUrl}/ratelimit/ping", cts.Token));
        }
        var results = await Task.WhenAll(tasks);

        // At least one should be 429 under fixed window limits
        results
            .Any(r => r.StatusCode == (HttpStatusCode)429)
            .Should()
            .BeTrue("rate limiter should eventually trigger");

        // And at least one 429 should include a Retry-After header
        var anyRetry = results
            .Where(r => (int)r.StatusCode == 429)
            .Any(r =>
            {
                // Prefer typed header when available
                if (r.Headers.RetryAfter?.Delta is TimeSpan delta)
                {
                    return delta.TotalSeconds >= 1; // accept any positive hint
                }
                if (r.Headers.TryGetValues("Retry-After", out var vals))
                {
                    foreach (var v in vals)
                    {
                        if (int.TryParse(v, out var seconds) && seconds >= 1)
                            return true;
                    }
                }
                if (r.Headers.TryGetValues("X-Retry-After", out var vals2))
                {
                    foreach (var v in vals2)
                    {
                        if (int.TryParse(v, out var seconds) && seconds >= 1)
                            return true;
                    }
                }
                return false;
            });
        anyRetry
            .Should()
            .BeTrue("429 responses should include Retry-After header with a positive backoff hint");
    } // End of Method Gateway_429_RetryAfter_Present
} // End of Class GatewayRateLimitRetryAfterTests
