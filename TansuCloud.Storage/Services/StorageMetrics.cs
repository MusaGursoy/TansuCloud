// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;

namespace TansuCloud.Storage.Services;

internal static class StorageMetrics
{
    public static readonly Meter Meter = new("tansu.storage", typeof(StorageMetrics).Assembly.GetName().Version?.ToString());

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        name: "tansu_storage_requests_total",
        unit: "requests",
        description: "Total storage requests"
    );

    public static readonly Counter<long> IngressBytes = Meter.CreateCounter<long>(
        name: "tansu_storage_ingress_bytes_total",
        unit: "bytes",
        description: "Total ingress (upload) bytes"
    );

    public static readonly Counter<long> EgressBytes = Meter.CreateCounter<long>(
        name: "tansu_storage_egress_bytes_total",
        unit: "bytes",
        description: "Total egress (download) bytes"
    );

    public static readonly Counter<long> Responses = Meter.CreateCounter<long>(
        name: "tansu_storage_responses_total",
        unit: "responses",
        description: "Total responses by status class"
    );

    public static readonly Histogram<double> RequestDurationMs = Meter.CreateHistogram<double>(
        name: "tansu_storage_request_duration_ms",
        unit: "ms",
        description: "Request duration in milliseconds"
    );
}
