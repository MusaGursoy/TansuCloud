// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using OpenTelemetry.Exporter;

namespace TansuCloud.Observability;

/// <summary>
/// Emits lightweight diagnostics (EventSource + metrics) about the configured OTLP exporter.
/// This avoids ILogger dependencies at startup while still surfacing useful information for operators.
/// </summary>
internal static class OtlpExporterDiagnostics
{
    private static readonly Meter Meter =
        new(
            "tansu.otel.exporter",
            typeof(OtlpExporterDiagnostics).Assembly.GetName().Version?.ToString()
        );

    // Exposed as observable gauges so values are queryable via metrics backends.
    private static int _retryMaxAttempts;
    private static int _retryInitialBackoffMs;
    private static int _retryMaxBackoffMs;
    private static double _retryBackoffMultiplier;
    private static int _timeoutMs;

    private static readonly ObservableGauge<int> RetryMaxAttemptsGauge =
        Meter.CreateObservableGauge(
            name: "tansu.otel.exporter.retry_max_attempts",
            observeValue: () => new Measurement<int>(_retryMaxAttempts),
            unit: "attempts",
            description: "Max retry attempts configured for OTLP gRPC exporter"
        );

    private static readonly ObservableGauge<int> RetryInitialBackoffGauge =
        Meter.CreateObservableGauge(
            name: "tansu.otel.exporter.retry_initial_backoff_ms",
            observeValue: () => new Measurement<int>(_retryInitialBackoffMs),
            unit: "ms",
            description: "Initial backoff in milliseconds for OTLP gRPC retries"
        );

    private static readonly ObservableGauge<int> RetryMaxBackoffGauge = Meter.CreateObservableGauge(
        name: "tansu.otel.exporter.retry_max_backoff_ms",
        observeValue: () => new Measurement<int>(_retryMaxBackoffMs),
        unit: "ms",
        description: "Maximum backoff in milliseconds for OTLP gRPC retries"
    );

    private static readonly ObservableGauge<double> RetryBackoffMultiplierGauge =
        Meter.CreateObservableGauge(
            name: "tansu.otel.exporter.retry_backoff_multiplier",
            observeValue: () => new Measurement<double>(_retryBackoffMultiplier),
            unit: "x",
            description: "Exponential backoff multiplier for OTLP gRPC retries"
        );

    private static readonly ObservableGauge<int> ExporterTimeoutGauge = Meter.CreateObservableGauge(
        name: "tansu.otel.exporter.timeout_ms",
        observeValue: () => new Measurement<int>(_timeoutMs),
        unit: "ms",
        description: "Exporter timeout in milliseconds"
    );

    internal static void RecordConfigured(
        Uri? endpoint,
        OtlpExportProtocol protocol,
        int timeoutMilliseconds
    )
    {
        _timeoutMs = timeoutMilliseconds;
        OtlpEventSource.Log.Configured(
            endpoint?.ToString() ?? string.Empty,
            protocol.ToString(),
            timeoutMilliseconds
        );
    } // End of Method RecordConfigured

    internal static void RecordRetryPolicy(
        int maxAttempts,
        int initialBackoffMs,
        int maxBackoffMs,
        double backoffMultiplier
    )
    {
        _retryMaxAttempts = maxAttempts;
        _retryInitialBackoffMs = initialBackoffMs;
        _retryMaxBackoffMs = maxBackoffMs;
        _retryBackoffMultiplier = backoffMultiplier;

        OtlpEventSource.Log.RetryPolicySet(
            maxAttempts,
            initialBackoffMs,
            maxBackoffMs,
            backoffMultiplier
        );
    } // End of Method RecordRetryPolicy

    [EventSource(Name = "TansuCloud-Observability-OTLP")]
    private sealed class OtlpEventSource : EventSource
    {
        public static readonly OtlpEventSource Log = new();

        private OtlpEventSource() { }

        [Event(
            1,
            Level = EventLevel.Informational,
            Message = "OTLP exporter configured: Endpoint={0}, Protocol={1}, TimeoutMs={2}"
        )]
        public void Configured(string endpoint, string protocol, int timeoutMs) =>
            WriteEvent(1, endpoint, protocol, timeoutMs);

        [Event(
            2,
            Level = EventLevel.Informational,
            Message = "OTLP retry policy set: MaxAttempts={0}, InitialBackoffMs={1}, MaxBackoffMs={2}, BackoffMultiplier={3}"
        )]
        public void RetryPolicySet(
            int maxAttempts,
            int initialBackoffMs,
            int maxBackoffMs,
            double backoffMultiplier
        ) => WriteEvent(2, maxAttempts, initialBackoffMs, maxBackoffMs, backoffMultiplier);
    } // End of Class OtlpEventSource
} // End of Class OtlpExporterDiagnostics
