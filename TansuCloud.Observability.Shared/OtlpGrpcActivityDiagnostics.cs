// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TansuCloud.Observability;

/// <summary>
/// Dev-only lightweight Activity listener for gRPC client calls.
/// Filters to OTLP Export calls and emits a failure counter when non-OK status is observed.
/// Controlled via config: OpenTelemetry:Otlp:Diagnostics:EnableGrpcActivityListener=true
/// </summary>
internal static class OtlpGrpcActivityDiagnostics
{
    private static int _enabled;
    private static ActivityListener? _listener;

    private static readonly Meter Meter =
        new(
            "tansu.otel.exporter",
            typeof(OtlpGrpcActivityDiagnostics).Assembly.GetName().Version?.ToString()
        );
    private static readonly Counter<long> GrpcFailures = Meter.CreateCounter<long>(
        name: "tansu.otel.exporter.grpc_failures",
        unit: "count",
        description: "Number of failed OTLP gRPC export attempts observed via Activity events"
    );

    // Known OTLP service names (traces/metrics/logs) used to identify exporter calls
    private static readonly ConcurrentDictionary<string, byte> OtlpServiceNames =
        new(
            new[]
            {
                new KeyValuePair<string, byte>(
                    "opentelemetry.proto.collector.trace.v1.TraceService",
                    0
                ),
                new KeyValuePair<string, byte>(
                    "opentelemetry.proto.collector.metrics.v1.MetricsService",
                    0
                ),
                new KeyValuePair<string, byte>(
                    "opentelemetry.proto.collector.logs.v1.LogsService",
                    0
                ),
            }
        );

    internal static void TryEnable()
    {
        if (Interlocked.Exchange(ref _enabled, 1) == 1)
        {
            return; // already enabled
        }

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Grpc.Net.Client",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(listener);
        _listener = listener;
    } // End of Method TryEnable

    private static void OnActivityStopped(Activity activity)
    {
        if (activity is null)
        {
            return;
        }

        // Identify OTLP export RPCs by rpc.service + rpc.method tags
        var service = activity.GetTagItem("rpc.service") as string;
        if (service is null || !OtlpServiceNames.ContainsKey(service))
        {
            return;
        }

        var method = activity.GetTagItem("rpc.method") as string ?? "(unknown)";
        var grpcStatus = activity.GetTagItem("grpc.status_code")?.ToString() ?? string.Empty;

        // Treat non-OK activity status or explicit grpc.status_code != 0 (OK) as a failure
        var failed =
            activity.Status != ActivityStatusCode.Ok
            || (
                grpcStatus.Length > 0
                && !string.Equals(grpcStatus, "OK", StringComparison.OrdinalIgnoreCase)
                && grpcStatus != "0"
            );
        if (!failed)
        {
            return;
        }

        GrpcFailures.Add(
            1,
            new KeyValuePair<string, object?>("rpc.service", service),
            new KeyValuePair<string, object?>("rpc.method", method),
            new KeyValuePair<string, object?>("grpc.status_code", grpcStatus)
        );
    } // End of Method OnActivityStopped
} // End of Class OtlpGrpcActivityDiagnostics
