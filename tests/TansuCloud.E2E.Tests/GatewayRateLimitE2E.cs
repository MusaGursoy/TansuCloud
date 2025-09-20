// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TansuCloud.E2E.Tests
{
    public class GatewayRateLimitE2E
    {
        private static string GetGatewayBaseUrl()
        {
            var env = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL");
            if (!string.IsNullOrWhiteSpace(env))
                return env.TrimEnd('/');
            return "http://localhost:8080";
        }

        private sealed record RateLimitDefaults
        {
            public int PermitLimit { get; set; }
            public int QueueLimit { get; set; }
        }

        private sealed record RateLimitRouteOverride
        {
            public int? PermitLimit { get; set; }
            public int? QueueLimit { get; set; }
        }

        private sealed record RateLimitConfigDto
        {
            public int WindowSeconds { get; set; }
            public RateLimitDefaults? Defaults { get; set; }
            public Dictionary<string, RateLimitRouteOverride>? Routes { get; set; }
        }

        [Fact(
            DisplayName = "Rate limiter returns 429 with Retry-After header when limit is exceeded (dev)"
        )]
        public async Task RateLimiter_Returns_429_With_RetryAfter()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            var baseUrl = GetGatewayBaseUrl();

            // Ensure gateway is up
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    using var ping = await client.GetAsync($"{baseUrl}/", cts.Token);
                    if ((int)ping.StatusCode < 400)
                        break;
                }
                catch { }
                await Task.Delay(500, cts.Token);
            }

            // 1) Fetch current config
            var current = await client.GetFromJsonAsync<RateLimitConfigDto>(
                $"{baseUrl}/admin/api/rate-limits",
                cancellationToken: cts.Token
            );
            Assert.NotNull(current);

            // 2) Apply a very small window and limits (permit=1, queue=0, window=2s) for 'ratelimit' route,
            //     because the test endpoint lives under /ratelimit/* and partitions by first path segment.
            var newCfg = new RateLimitConfigDto
            {
                WindowSeconds = 2,
                Defaults = new RateLimitDefaults
                {
                    PermitLimit = current!.Defaults?.PermitLimit ?? 100,
                    QueueLimit = current!.Defaults?.QueueLimit ?? 100
                },
                Routes = new Dictionary<string, RateLimitRouteOverride>
                {
                    ["ratelimit"] = new RateLimitRouteOverride { PermitLimit = 1, QueueLimit = 0 },
                    ["dashboard"] =
                        current!.Routes is not null
                        && current.Routes.TryGetValue("dashboard", out var rDash)
                            ? rDash
                            : new RateLimitRouteOverride(),
                    ["db"] =
                        current!.Routes is not null && current.Routes.TryGetValue("db", out var rDb)
                            ? rDb
                            : new RateLimitRouteOverride(),
                    ["storage"] =
                        current!.Routes is not null
                        && current.Routes.TryGetValue("storage", out var rSt)
                            ? rSt
                            : new RateLimitRouteOverride(),
                }
            };
            using (
                var resSet = await client.PostAsJsonAsync(
                    $"{baseUrl}/admin/api/rate-limits",
                    newCfg,
                    cts.Token
                )
            )
            {
                Assert.True(
                    resSet.IsSuccessStatusCode,
                    $"Set config failed: {(int)resSet.StatusCode} {resSet.ReasonPhrase}"
                );
            }

            // 3) Hit the ping twice in quick succession; second should be 429 with Retry-After headers
            using var res1 = await client.GetAsync($"{baseUrl}/ratelimit/ping", cts.Token);
            Assert.True(res1.IsSuccessStatusCode);

            using var res2 = await client.GetAsync($"{baseUrl}/ratelimit/ping", cts.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, res2.StatusCode);
            Assert.True(
                res2.Headers.TryGetValues("Retry-After", out var retryAfterValues),
                "Retry-After header missing"
            );
            var retryAfter = retryAfterValues!.FirstOrDefault();
            Assert.Equal("2", retryAfter);
        }
    }
}
