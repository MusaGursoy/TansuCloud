// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TansuCloud.Telemetry.Admin;

namespace TansuCloud.Telemetry.UnitTests;

public sealed class TelemetryEnvelopeExportFormatterTests
{
    [Fact]
    public void CreateJson_ShouldSerializeEnvelopeDetails()
    {
        var itemTimestamp = new DateTime(2025, 10, 1, 7, 30, 0, DateTimeKind.Utc);
        var detail = new TelemetryEnvelopeDetail(
            Guid.NewGuid(),
            new DateTime(2025, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            "host-A",
            "Production",
            "gateway",
            "Warning",
            60,
            500,
            3,
            true,
            false,
            new[]
            {
                new TelemetryItemView(
                    1,
                    "log",
                    itemTimestamp,
                    "Warning",
                    "Gateway latency high",
                    "template",
                    null,
                    "gateway",
                    "Production",
                    null,
                    "corr-1",
                    "trace-1",
                    "span-1",
                    "Tansu.Gateway",
                    null,
                    1,
                    "{\"duration\":123}"
                )
            }
        );

        var payload = TelemetryEnvelopeExportFormatter.CreateJson(new[] { detail });
        var json = Encoding.UTF8.GetString(payload);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(1);

        var envelopeElement = root[0];
        envelopeElement.GetProperty("service").GetString().Should().Be("gateway");
        envelopeElement.GetProperty("items").GetArrayLength().Should().Be(1);
        envelopeElement.GetProperty("items")[0]
            .GetProperty("timestampUtc")
            .GetDateTime()
            .Should()
            .Be(itemTimestamp);
    } // End of Method CreateJson_ShouldSerializeEnvelopeDetails

    [Fact]
    public void CreateCsv_ShouldEscapeFieldsAndIncludeEventWindow()
    {
        var firstEvent = new DateTime(2025, 10, 1, 7, 0, 0, DateTimeKind.Utc);
        var lastEvent = new DateTime(2025, 10, 1, 7, 30, 0, DateTimeKind.Utc);
        var detail = new TelemetryEnvelopeDetail(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new DateTime(2025, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            "host,01",
            "Prod\"West",
            "gateway",
            "Error",
            60,
            500,
            2,
            false,
            true,
            new[]
            {
                new TelemetryItemView(
                    1,
                    "log",
                    firstEvent,
                    "Error",
                    "First",
                    "template",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    null
                ),
                new TelemetryItemView(
                    2,
                    "log",
                    lastEvent,
                    "Error",
                    "Second",
                    "template",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    null
                )
            }
        );

        var payload = TelemetryEnvelopeExportFormatter.CreateCsv(new[] { detail });
        var csv = Encoding.UTF8.GetString(payload);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].TrimEnd('\r').Should().Be(
            "EnvelopeId,ReceivedAtUtc,Service,Environment,Host,SeverityThreshold,ItemCount,Acknowledged,Archived,FirstEventUtc,LastEventUtc"
        );

        var dataLine = lines[1].TrimEnd('\r');
    dataLine.Should().Contain("\"host,01\"");
    dataLine.Should().Contain("\"Prod\"\"West\"");
    dataLine.Should().Contain(",false,true,");
        dataLine.Should().Contain("2025-10-01 07:00:00 UTC");
        dataLine.Should().Contain("2025-10-01 07:30:00 UTC");
    } // End of Method CreateCsv_ShouldEscapeFieldsAndIncludeEventWindow
} // End of Class TelemetryEnvelopeExportFormatterTests
