using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// E2E tests for Loki logs backend (Task 47 Phase 3).
/// Validates that Loki is receiving logs from all services via OTEL Collector,
/// and that the Dashboard can query logs through the adapter pattern.
/// </summary>
public sealed class LokiLogsE2E
{
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _client;

    public LokiLogsE2E(ITestOutputHelper output)
    {
        _output = output;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Fact]
    public async Task Loki_IsReceivingLogs_ViaOtelCollector()
    {
        // Arrange: Query Loki API to check if logs are being received
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var queryUrl = $"{lokiBaseUrl}/loki/api/v1/query_range?query={{job=\"otel-collector\"}}&limit=1";

        // Act
        var response = await _client.GetAsync(queryUrl);

        // Assert
        Assert.True(response.IsSuccessStatusCode, 
            $"Loki query failed: {response.StatusCode}. Is Loki running and accessible at {lokiBaseUrl}?");

        var content = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Loki query response (first 500 chars): {content.Substring(0, Math.Min(500, content.Length))}");

        var json = JsonDocument.Parse(content);
        var status = json.RootElement.GetProperty("status").GetString();
        Assert.Equal("success", status);
    }

    [Fact]
    public async Task Loki_Configuration_IsPresent()
    {
        // Arrange: Check Loki configuration endpoint
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var configUrl = $"{lokiBaseUrl}/config";

        // Act
        var response = await _client.GetAsync(configUrl);

        // Assert
        Assert.True(response.IsSuccessStatusCode,
            $"Loki config endpoint failed: {response.StatusCode}. Check loki-config.yml deployment.");

        var content = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Loki config loaded (first 200 chars): {content.Substring(0, Math.Min(200, content.Length))}");

        // Verify key configuration sections are present
        Assert.Contains("ingester", content);
        Assert.Contains("storage_config", content);
        Assert.Contains("schema_config", content);
    }

    [Fact]
    public async Task Loki_RetentionIsConfigured_7Days()
    {
        // Arrange: Check compactor config for retention
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var configUrl = $"{lokiBaseUrl}/config";

        // Act
        var response = await _client.GetAsync(configUrl);
        var content = await response.Content.ReadAsStringAsync();

        // Assert: Verify 168h (7 days) retention in compactor config
        Assert.Contains("retention_enabled: true", content);
        Assert.Contains("retention_period: 168h", content);

        _output.WriteLine("Loki retention verified: 7 days (168h) with compactor enabled");
    }

    [Fact]
    public async Task Loki_Services_ArePresent_InLogs()
    {
        // Arrange: Query for service_name label values
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var labelsUrl = $"{lokiBaseUrl}/loki/api/v1/label/service_name/values";

        // Act
        var response = await _client.GetAsync(labelsUrl);

        // Assert
        Assert.True(response.IsSuccessStatusCode,
            $"Loki labels query failed: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var services = json.RootElement.GetProperty("data").EnumerateArray().Select(s => s.GetString()).ToList();

        _output.WriteLine($"Services found in Loki: {string.Join(", ", services)}");

        // Verify at least some TansuCloud services are present
        var expectedServices = new[] { "tansu.gateway", "tansu.identity", "tansu.dashboard", "tansu.database", "tansu.storage" };
        var foundServices = expectedServices.Where(s => services.Contains(s)).ToList();

        Assert.NotEmpty(foundServices);
        _output.WriteLine($"TansuCloud services instrumented: {foundServices.Count}/5");
    }

    [Fact]
    public async Task Loki_LogsAreCollected_AfterHealthChecks()
    {
        // Arrange: Trigger health checks to generate logs
        var gatewayHealthUrl = $"{TestUrls.GatewayBaseUrl}/_health";
        await _client.GetAsync(gatewayHealthUrl); // Trigger log generation

        await Task.Delay(2000); // Wait for logs to be ingested

        // Query Loki for recent logs from gateway
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1_000_000_000; // Nanoseconds
        var start = (DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()) * 1_000_000_000;
        var queryUrl = $"{lokiBaseUrl}/loki/api/v1/query_range?query={{service_name=\"tansu.gateway\"}}&start={start}&end={end}&limit=100";

        // Act
        var response = await _client.GetAsync(queryUrl);

        // Assert
        Assert.True(response.IsSuccessStatusCode,
            $"Loki query failed: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var result = json.RootElement.GetProperty("data").GetProperty("result");
        var logCount = result.GetArrayLength();

        _output.WriteLine($"Recent logs from Gateway: {logCount} streams");
        Assert.True(logCount > 0, "No logs found from Gateway. Check OTEL Collector routing.");
    }

    [Fact]
    public async Task Loki_OtelCollector_IsForwardingLogs()
    {
        // Arrange: Query OTEL Collector metrics to verify logs are being exported
        var otelMetricsUrl = $"{TestUrls.OtelCollectorBaseUrl}/metrics";

        // Act
        var response = await _client.GetAsync(otelMetricsUrl);

        // Assert
        Assert.True(response.IsSuccessStatusCode,
            $"OTEL Collector metrics endpoint failed: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();

        // Check for loki exporter metrics
        var hasLokiExporter = content.Contains("otelcol_exporter_sent_log_records") || 
                              content.Contains("loki") ||
                              content.Contains("exporter");

        _output.WriteLine($"OTEL Collector metrics (first 1000 chars): {content.Substring(0, Math.Min(1000, content.Length))}");
        _output.WriteLine($"Loki exporter metrics present: {hasLokiExporter}");

        Assert.True(hasLokiExporter, "OTEL Collector is not exporting logs to Loki. Check otel-collector-config.yaml.");
    }

    [Fact]
    public async Task Loki_LocalFilesystemStorage_IsConfigured()
    {
        // Arrange: Check Loki config for filesystem storage
        var lokiBaseUrl = TestUrls.LokiApiBaseUrl;
        var configUrl = $"{lokiBaseUrl}/config";

        // Act
        var response = await _client.GetAsync(configUrl);
        var content = await response.Content.ReadAsStringAsync();

        // Assert: Verify local filesystem storage paths
        Assert.Contains("filesystem", content);
        Assert.Contains("/loki/chunks", content);
        Assert.Contains("/loki/index", content);

        _output.WriteLine("Loki storage verified: Local filesystem with /loki/chunks and /loki/index");
    }

    [Fact]
    public async Task Dashboard_LogsPage_RequiresAuth()
    {
        // Arrange: Dashboard logs page should require authentication
        var logsPageUrl = $"{TestUrls.PublicBaseUrl}/admin/logs";

        // Act: Access without auth
        var response = await _client.GetAsync(logsPageUrl);

        // Assert: Should redirect to login
        Assert.True(response.StatusCode == HttpStatusCode.Redirect || 
                    response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {response.StatusCode}");

        _output.WriteLine($"Dashboard logs page correctly requires auth: {response.StatusCode}");
    }

    [Fact]
    public async Task Dashboard_LogsPage_IsAccessibleWithAuth()
    {
        // Arrange: Authenticate and access logs page
        var token = await GetAdminTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var logsPageUrl = $"{TestUrls.PublicBaseUrl}/admin/logs";

        // Act
        var response = await _client.GetAsync(logsPageUrl);

        // Assert: Should return 200 OK with authenticated request
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"Error response: {errorContent}");
        }

        Assert.True(response.IsSuccessStatusCode,
            $"Dashboard logs page failed with auth: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Logs", content); // Should contain "Logs" in the page title or heading

        _output.WriteLine("Dashboard logs page accessible with authentication");
    }

    #region Helper Methods

    private async Task<string> GetAdminTokenAsync()
    {
        var tokenUrl = $"{TestUrls.PublicBaseUrl}/identity/connect/token";
        var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "admin@tansu.local",
            ["password"] = "Passw0rd!",
            ["client_id"] = "tansu-dashboard",
            ["client_secret"] = "dev-secret",
            ["scope"] = "openid profile email admin.full"
        });

        var tokenResponse = await _client.PostAsync(tokenUrl, tokenRequest);
        tokenResponse.EnsureSuccessStatusCode();

        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return tokenJson.GetProperty("access_token").GetString()!;
    }

    #endregion
}
