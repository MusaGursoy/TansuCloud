// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Linq;
using TansuCloud.Telemetry.Data.Entities;

namespace TansuCloud.Telemetry.Admin;

/// <summary>
/// Provides mapping helpers between data entities and admin DTOs.
/// </summary>
internal static class TelemetryAdminMapper
{
    public static TelemetryEnvelopeSummary ToSummary(TelemetryEnvelopeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new TelemetryEnvelopeSummary(
            entity.Id,
            entity.ReceivedAtUtc,
            entity.Host,
            entity.Environment,
            entity.Service,
            entity.SeverityThreshold,
            entity.ItemCount,
            entity.AcknowledgedAtUtc.HasValue,
            entity.DeletedAtUtc.HasValue
        );
    } // End of Method ToSummary

    public static TelemetryEnvelopeDetail ToDetail(TelemetryEnvelopeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var items = entity.Items.Select(ToItemView).ToArray();

        return new TelemetryEnvelopeDetail(
            entity.Id,
            entity.ReceivedAtUtc,
            entity.Host,
            entity.Environment,
            entity.Service,
            entity.SeverityThreshold,
            entity.WindowMinutes,
            entity.MaxItems,
            entity.ItemCount,
            entity.AcknowledgedAtUtc.HasValue,
            entity.DeletedAtUtc.HasValue,
            items
        );
    } // End of Method ToDetail

    private static TelemetryItemView ToItemView(TelemetryItemEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new TelemetryItemView(
            entity.Id,
            entity.Kind,
            entity.TimestampUtc,
            entity.Level,
            entity.Message,
            entity.TemplateHash,
            entity.Exception,
            entity.Service,
            entity.Environment,
            entity.TenantHash,
            entity.CorrelationId,
            entity.TraceId,
            entity.SpanId,
            entity.Category,
            entity.EventId,
            entity.Count,
            entity.PropertiesJson
        );
    } // End of Method ToItemView
} // End of Class TelemetryAdminMapper
