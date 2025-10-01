// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class HealthReadinessDataSmoke
{
    [Fact(DisplayName = "Health: readiness payload exposes OTLP diagnostics and ActivityIdFormat")]
    public async Task Ready_Endpoints_Expose_Otlp_Diagnostics()
    {
        using var client = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = true,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var baseUrl = TestUrls.GatewayBaseUrl.TrimEnd('/');

        var readinessPaths = new[]
        {
            "/health/ready", // Gateway own readiness surface
            "/identity/health/ready",
            "/dashboard/health/ready",
            "/db/health/ready",
            "/storage/health/ready",
        };

        foreach (var path in readinessPaths)
        {
            using var res = await client.GetAsync(baseUrl + path);
            res.EnsureSuccessStatusCode();
            using var s = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(s);
            var root = doc.RootElement;
            root.TryGetProperty("entries", out var entries)
                .Should()
                .BeTrue("health response should have entries");

            bool found = false;
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("data", out var data))
                {
                    if (
                        data.TryGetProperty("activity.defaultIdFormat", out _)
                        && data.TryGetProperty("otlp.endpoint", out _)
                        && data.TryGetProperty("otlp.tcpReachable", out _)
                    )
                    {
                        found = true;
                        break;
                    }
                }
            }

            found
                .Should()
                .BeTrue(
                    $"{path} readiness response should include activity.defaultIdFormat, otlp.endpoint, otlp.tcpReachable"
                );
        }
    }
} // End of Class HealthReadinessDataSmoke
