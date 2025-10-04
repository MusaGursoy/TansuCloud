// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Formats telemetry envelope exports to CSV and JSON payloads.
/// </summary>
internal static class TelemetryEnvelopeExportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static byte[] CreateJson(IReadOnlyList<TelemetryEnvelopeDetail> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        return JsonSerializer.SerializeToUtf8Bytes(envelopes, JsonOptions);
    } // End of Method CreateJson

    public static byte[] CreateCsv(IReadOnlyList<TelemetryEnvelopeDetail> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        var builder = new StringBuilder();
        builder.AppendLine(
            "EnvelopeId,ReceivedAtUtc,Service,Environment,Host,SeverityThreshold,ItemCount,Acknowledged,Archived,FirstEventUtc,LastEventUtc"
        );

        foreach (var envelope in envelopes)
        {
            var firstEvent =
                envelope.Items.Count > 0
                    ? envelope.Items.Min(i => i.TimestampUtc)
                    : (DateTime?)null;
            var lastEvent =
                envelope.Items.Count > 0
                    ? envelope.Items.Max(i => i.TimestampUtc)
                    : (DateTime?)null;

            AppendCsvRow(
                builder,
                new[]
                {
                    envelope.Id.ToString(),
                    FormatUtc(envelope.ReceivedAtUtc),
                    envelope.Service,
                    envelope.Environment,
                    envelope.Host,
                    envelope.SeverityThreshold,
                    envelope.ItemCount.ToString(CultureInfo.InvariantCulture),
                    envelope.IsAcknowledged ? "true" : "false",
                    envelope.IsDeleted ? "true" : "false",
                    firstEvent is null ? string.Empty : FormatUtc(firstEvent.Value),
                    lastEvent is null ? string.Empty : FormatUtc(lastEvent.Value)
                }
            );
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    } // End of Method CreateCsv

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                builder.Append(',');
            }

            AppendEscapedField(builder, field ?? string.Empty);
            first = false;
        }

        builder.AppendLine();
    } // End of Method AppendCsvRow

    private static void AppendEscapedField(StringBuilder builder, string value)
    {
        var needsQuotes =
            value.Contains('"', StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        builder.Append('"');
        builder.Append(escaped);
        builder.Append('"');
    } // End of Method AppendEscapedField

    private static string FormatUtc(DateTime timestampUtc) =>
        timestampUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); // End of Method FormatUtc
} // End of Class TelemetryEnvelopeExportFormatter
