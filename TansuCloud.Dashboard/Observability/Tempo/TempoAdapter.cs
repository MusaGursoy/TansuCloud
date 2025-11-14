// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using TansuCloud.Dashboard.Models;

namespace TansuCloud.Dashboard.Observability.Tempo;

/// <summary>
/// Adapter to convert Tempo API responses to Dashboard models.
/// Maps TempoTrace/TempoSpan to TraceDetail/SpanDetail for UI compatibility.
/// </summary>
public static class TempoAdapter
{
    /// <summary>
    /// Converts Tempo search result to Dashboard TraceSearchResult model.
    /// </summary>
    public static TraceSearchResult ToSearchResult(TempoTraceSearchResult tempoResult)
    {
        ArgumentNullException.ThrowIfNull(tempoResult);

        var traces = tempoResult.Traces
            .Select(ToTraceSummary)
            .ToList();

        return new TraceSearchResult
        {
            Traces = traces,
            Total = traces.Count,
            HasMore = false // Tempo doesn't provide pagination info
        };
    } // End of Method ToSearchResult

    /// <summary>
    /// Converts Tempo trace metadata to Dashboard TraceSummary model.
    /// </summary>
    public static TraceSummary ToTraceSummary(TempoTraceMetadata tempoTrace)
    {
        ArgumentNullException.ThrowIfNull(tempoTrace);

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
            tempoTrace.StartTimeUnixNano / 1_000_000
        );

        return new TraceSummary
        {
            TraceId = tempoTrace.TraceId,
            ServiceName = tempoTrace.RootServiceName,
            OperationName = tempoTrace.RootTraceName,
            DurationMs = tempoTrace.DurationMs ?? 0,
            SpanCount = 0, // Tempo search doesn't return span count
            Timestamp = timestamp,
            Status = "UNSET", // Status not available in search metadata
            ErrorMessage = null
        };
    } // End of Method ToTraceSummary

    /// <summary>
    /// Converts Tempo trace to Dashboard TraceDetail model.
    /// </summary>
    public static TraceDetail? ToTraceDetail(TempoTrace? tempoTrace)
    {
        if (tempoTrace == null || tempoTrace.Spans.Count == 0)
            return null;

        // Find root span (no parent)
        var rootSpan = tempoTrace.Spans.FirstOrDefault(s => string.IsNullOrEmpty(s.ParentSpanId));
        
        // Calculate total duration from root span or max end time
        long durationMs;
        DateTimeOffset timestamp;
        string serviceName;

        if (rootSpan != null)
        {
            durationMs = rootSpan.DurationNano / 1_000_000;
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(rootSpan.StartTimeUnixNano / 1_000_000);
            serviceName = rootSpan.ServiceName;
        }
        else
        {
            // Fallback: calculate from min start time and max end time
            var minStart = tempoTrace.Spans.Min(s => s.StartTimeUnixNano);
            var maxEnd = tempoTrace.Spans.Max(s => s.StartTimeUnixNano + s.DurationNano);
            durationMs = (maxEnd - minStart) / 1_000_000;
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(minStart / 1_000_000);
            serviceName = tempoTrace.Spans.FirstOrDefault()?.ServiceName ?? "unknown";
        }

        return new TraceDetail
        {
            TraceId = tempoTrace.TraceId,
            ServiceName = serviceName,
            DurationMs = durationMs,
            Timestamp = timestamp,
            Spans = tempoTrace.Spans.Select(ToSpanDetail).ToList()
        };
    } // End of Method ToTraceDetail

    /// <summary>
    /// Converts Tempo span to Dashboard SpanDetail model.
    /// </summary>
    public static SpanDetail ToSpanDetail(TempoSpan tempoSpan)
    {
        ArgumentNullException.ThrowIfNull(tempoSpan);

        // Parse status from string (ok/error) to uppercase status code
        var status = tempoSpan.Status?.ToUpperInvariant() switch
        {
            "OK" => "OK",
            "ERROR" => "ERROR",
            _ => "UNSET"
        };

        // Extract status message from tags if available
        string? statusMessage = null;
        if (tempoSpan.Tags.TryGetValue("error.message", out var errorMsg))
        {
            statusMessage = errorMsg;
        }
        else if (tempoSpan.Tags.TryGetValue("otel.status_description", out var otelDesc))
        {
            statusMessage = otelDesc;
        }

        // Extract span kind from tags
        var kind = "INTERNAL";
        if (tempoSpan.Tags.TryGetValue("span.kind", out var kindValue))
        {
            kind = kindValue.ToUpperInvariant();
        }

        // Convert Tempo events to SpanEvents
        var events = tempoSpan.Events
            .Select(e => new SpanEvent
            {
                Name = e.Name,
                TimestampNano = e.TimeUnixNano,
                Attributes = e.Attributes
            })
            .ToList();

        return new SpanDetail
        {
            SpanId = tempoSpan.SpanId,
            ParentSpanId = tempoSpan.ParentSpanId,
            ServiceName = tempoSpan.ServiceName,
            Name = tempoSpan.OperationName,
            Kind = kind,
            StartTimeNano = tempoSpan.StartTimeUnixNano,
            EndTimeNano = tempoSpan.StartTimeUnixNano + tempoSpan.DurationNano,
            DurationMs = tempoSpan.DurationNano / 1_000_000,
            Status = status,
            StatusMessage = statusMessage,
            Attributes = tempoSpan.Tags,
            Events = events
        };
    } // End of Method ToSpanDetail

} // End of Class TempoAdapter
