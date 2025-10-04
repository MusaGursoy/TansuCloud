// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace TansuCloud.E2E.Tests;

public sealed class TelemetryServiceE2E
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    [Fact(DisplayName = "Telemetry: readiness endpoint reports healthy status")]
    public async Task Telemetry_Service_Is_Ready()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = CreateClient();

        var response = await WaitForTelemetryAsync(client, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync(cts.Token);
        payload.Should().NotBeNullOrWhiteSpace();

        using var document = JsonDocument.Parse(payload);
        TryGetPropertyCaseInsensitive(document.RootElement, "status", out var statusElement)
            .Should()
            .BeTrue("the readiness payload should include a status field");
        statusElement.GetString().Should().Be("Healthy");
    }

    [Fact(DisplayName = "Telemetry: ingestion persists envelopes visible via admin API")]
    public async Task Telemetry_Ingestion_Roundtrip_Admin_List()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var client = CreateClient();

        await WaitForTelemetryAsync(client, cts.Token);

        var baseUrl = TestUrls.TelemetryBaseUrl;
        var ingestionKey = ResolveEnvironmentValue(
            "TELEMETRY__INGESTION__APIKEY",
            "dev-telemetry-api-key-1234567890"
        );
        var adminKey = ResolveEnvironmentValue(
            "TELEMETRY__ADMIN__APIKEY",
            "dev-telemetry-admin-key-9876543210"
        );

        ingestionKey.Should().NotBeNullOrWhiteSpace();
        adminKey.Should().NotBeNullOrWhiteSpace();

        var serviceName = $"e2e-telemetry-{Guid.NewGuid():N}";
        var hostName = $"{Environment.MachineName.ToLowerInvariant()}-telemetry";
        var now = DateTimeOffset.UtcNow;

        var report = new
        {
            host = hostName,
            environment = "Development",
            service = serviceName,
            severityThreshold = "Warning",
            windowMinutes = 5,
            maxItems = 200,
            items = Enumerable
                .Range(0, 3)
                .Select(index => new
                {
                    kind = "log",
                    timestamp = now.AddSeconds(-index).ToString("O"),
                    level = index % 2 == 0 ? "Warning" : "Error",
                    message = $"E2E telemetry event {index}",
                    templateHash = $"template-{index}",
                    exception = (string?)null,
                    service = serviceName,
                    environment = "Development",
                    tenantHash = (string?)null,
                    correlationId = Guid.NewGuid().ToString("N"),
                    traceId = ActivityTraceId.CreateRandom().ToString(),
                    spanId = ActivitySpanId.CreateRandom().ToString(),
                    category = "TansuCloud.E2E",
                    eventId = 10_000 + index,
                    count = 1,
                    properties = new { machine = Environment.MachineName, iteration = index }
                })
                .ToArray()
        };

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/logs/report"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestionKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(report, SerializerOptions),
                Encoding.UTF8,
                "application/json"
            );

            using var response = await client.SendAsync(request, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        Guid? envelopeId = null;
        JsonElement matchedEnvelope = default;

        for (var attempt = 0; attempt < 40 && envelopeId is null; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);

            using var listRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/api/admin/envelopes?Service={Uri.EscapeDataString(serviceName)}&IncludeAcknowledged=true&IncludeDeleted=true&PageSize=50"
            );
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

            using var listResponse = await client.SendAsync(listRequest, cts.Token);
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var listPayload = await listResponse.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(listPayload);
            if (
                !TryGetPropertyCaseInsensitive(
                    doc.RootElement,
                    "envelopes",
                    out var envelopesElement
                )
            )
            {
                continue;
            }

            foreach (var envelope in envelopesElement.EnumerateArray())
            {
                if (!TryGetPropertyCaseInsensitive(envelope, "service", out var serviceElement))
                {
                    continue;
                }

                if (
                    !string.Equals(
                        serviceElement.GetString(),
                        serviceName,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                matchedEnvelope = envelope;

                if (
                    TryGetPropertyCaseInsensitive(envelope, "id", out var idElement)
                    && Guid.TryParse(idElement.GetString(), out var parsed)
                )
                {
                    envelopeId = parsed;
                    break;
                }
            }
        }

        envelopeId
            .HasValue.Should()
            .BeTrue("the telemetry envelope should be visible via the admin API");

        if (envelopeId is null)
        {
            return;
        }

        using (
            var detailRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/api/admin/envelopes/{envelopeId:guid}"
            )
        )
        {
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
            using var detailResponse = await client.SendAsync(detailRequest, cts.Token);
            detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var detailPayload = await detailResponse.Content.ReadAsStringAsync(cts.Token);
            using var detailDoc = JsonDocument.Parse(detailPayload);
            TryGetPropertyCaseInsensitive(detailDoc.RootElement, "items", out var itemsElement)
                .Should()
                .BeTrue("the envelope detail should include log items");
            itemsElement.ValueKind.Should().Be(JsonValueKind.Array);
            itemsElement.GetArrayLength().Should().BeGreaterThan(0);

            if (TryGetPropertyCaseInsensitive(detailDoc.RootElement, "host", out var hostElement))
            {
                hostElement.GetString().Should().Be(hostName);
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static async Task<HttpResponseMessage> WaitForTelemetryAsync(
        HttpClient client,
        CancellationToken ct
    )
    {
        var baseUrl = TestUrls.TelemetryBaseUrl;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var response = await client.GetAsync($"{baseUrl}/health/ready", ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return response;
                }

                response.Dispose();
            }
            catch
            {
                // Swallow and retry.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        throw new TimeoutException("Telemetry service is not reporting ready state.");
    }

    private static string ResolveEnvironmentValue(string key, string fallback)
    {
        TestEnvironment.EnsureInitialized();
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return fallback;
    }

    private static bool TryGetPropertyCaseInsensitive(
        JsonElement element,
        string name,
        out JsonElement value
    )
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
