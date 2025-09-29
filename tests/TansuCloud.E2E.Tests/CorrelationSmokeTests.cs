// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace TansuCloud.E2E.Tests;

public sealed class CorrelationSmokeTests
{
    private readonly ITestOutputHelper _output;
    public CorrelationSmokeTests(ITestOutputHelper output) => _output = output; // End of Constructor CorrelationSmokeTests

    [Fact]
    public async Task Provision_Request_Echoes_Correlation_And_Optional_LogsContainId()
    {
        // This is a light smoke: it asserts the correlation echo header always.
        // If local dev services are running (dev: up) and writing gateway-out.log/database-out.log,
        // it also verifies the correlation ID appears in at least one of them.

        var baseUrl = TestUrls.GatewayBaseUrl;

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var tenantId = $"obs-smoke-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var corrId = $"obs-corr-{Guid.NewGuid():N}";

        using var req = new HttpRequestMessage(HttpMethod.Post, "/db/api/provisioning/tenants");
        req.Headers.TryAddWithoutValidation("X-Provision-Key", "letmein");
        req.Headers.TryAddWithoutValidation("X-Correlation-ID", corrId);
        var body = JsonSerializer.Serialize(new { tenantId, displayName = $"Obs Smoke {tenantId}" });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        // Assert the echo header is present (provided by RequestEnrichmentMiddleware)
        resp.Headers.TryGetValues("X-Correlation-ID", out var echoedValues).Should().BeTrue();
        // Some stacks may add the header twice with the same value. Accept duplicates as long as all equal our corrId.
        echoedValues!.Should().Contain(corrId);
        echoedValues!.Should().OnlyContain(v => v == corrId);
        _output.WriteLine($"Echoed X-Correlation-ID matched: {corrId}");

        // Try to locate repo root (contains TansuCloud.sln), then check dev log files.
        var root = FindRepoRoot();
        if (root is null)
        {
            _output.WriteLine("Repo root not found from AppContext.BaseDirectory; skipping log scan.");
            return; // header echo assertion already validated correlation
        }

        var candidates = new[]
        {
            Path.Combine(root, "gateway-out.log"),
            Path.Combine(root, "database-out.log"),
        };

        var foundInAny = false;
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    _output.WriteLine($"Log not found: {path}");
                    continue;
                }
                var text = await File.ReadAllTextAsync(path);
                if (text.Contains(corrId, StringComparison.OrdinalIgnoreCase))
                {
                    foundInAny = true;
                    _output.WriteLine($"Correlation ID found in: {path}");
                }
                else
                {
                    _output.WriteLine($"Correlation ID not present in: {path}");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Error reading {path}: {ex.Message}");
            }
        }

        // Only assert on logs if at least one candidate exists AND is fresh (recently written),
        // to avoid flakiness when running in containers where logs are not written to host files.
        var recentThreshold = TimeSpan.FromMinutes(10);
        var anyCandidateFresh = candidates
            .Where(File.Exists)
            .Any(p => (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)) < recentThreshold);
        if (anyCandidateFresh)
        {
            foundInAny.Should().BeTrue("when local dev services write local log files, at least one should include the correlation id");
        }
    } // End of Method Provision_Request_Echoes_Correlation_And_Optional_LogsContainId

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var sln = Path.Combine(dir.FullName, "TansuCloud.sln");
            if (File.Exists(sln)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    } // End of Method FindRepoRoot
} // End of Class CorrelationSmokeTests
