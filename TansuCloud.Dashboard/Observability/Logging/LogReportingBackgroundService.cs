// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Logging
{
    /// <summary>
    /// Periodically collects recent warning/error/critical logs and reports them to the main server.
    /// Respects LogReportingOptions.Enabled and becomes a no-op when MainServerUrl is not configured.
    /// </summary>
    public sealed class LogReportingBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<LogReportingBackgroundService> _logger;
        private readonly IOptionsMonitor<LogReportingOptions> _options;
        private readonly ILogReportingRuntimeSwitch _runtime;
        private readonly ILogBuffer _buffer;
        private readonly Random _rand = new();

        public LogReportingBackgroundService(
            IServiceProvider sp,
            ILogger<LogReportingBackgroundService> logger,
            IOptionsMonitor<LogReportingOptions> options,
            ILogReportingRuntimeSwitch runtime,
            ILogBuffer buffer
        )
        {
            _sp = sp;
            _logger = logger;
            _options = options;
            _runtime = runtime;
            _buffer = buffer;
        } // End of Constructor LogReportingBackgroundService

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Stagger initial delay slightly to avoid thundering herd on restarts
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var opts = _options.CurrentValue;
                var interval = TimeSpan.FromMinutes(Math.Max(1, opts.ReportIntervalMinutes));
                try
                {
                    if (opts.Enabled && _runtime.Enabled)
                    {
                        await RunOnceAsync(opts, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Log reporting tick failed");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        } // End of Method ExecuteAsync

        private async Task RunOnceAsync(LogReportingOptions opts, CancellationToken ct)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var reporter = scope.ServiceProvider.GetRequiredService<ILogReporter>();
                // Snapshot then filter; only dequeue after a successful send to avoid loss.
                var snapshot = _buffer.Snapshot();
                if (snapshot.Count == 0)
                {
                    return; // nothing to report
                }

                var max = Math.Max(50, opts.MaxItems);
                var toSend = FilterForReporting(snapshot, opts).Take(max).ToList();
                if (toSend.Count == 0)
                {
                    return; // nothing matches policy
                }

                var env =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
                var host = Environment.MachineName;
                var service = "tansu.dashboard";

                var req = new LogReportRequest(
                    Host: host,
                    Environment: env,
                    Service: service,
                    SeverityThreshold: opts.SeverityThreshold,
                    WindowMinutes: Math.Max(1, opts.QueryWindowMinutes),
                    MaxItems: Math.Max(50, opts.MaxItems),
                    Items: toSend
                );

                // Send first; if successful, dequeue exactly the number we sent from the head.
                await reporter.ReportAsync(req, ct).ConfigureAwait(false);
                _ = _buffer.RemoveBatch(toSend.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log reporting failed");
            }
        } // End of Method RunOnceAsync

        private IEnumerable<LogItem> FilterForReporting(
            IReadOnlyList<LogRecord> snapshot,
            LogReportingOptions opts
        )
        {
            var threshold = ParseLevel(opts.SeverityThreshold);
            foreach (var r in snapshot)
            {
                var lvl = ParseLevel(r.Level);
                if (lvl < threshold)
                    continue;

                // Warnings: require allowlist or sampling
                if (lvl == 3) // Warning
                {
                    if (!IsAllowedCategory(r.Category, opts.AllowedWarningCategories))
                    {
                        if (opts.WarningSamplingPercent <= 0)
                            continue;
                        var roll = _rand.Next(0, 100);
                        if (roll >= opts.WarningSamplingPercent)
                            continue;
                    }
                }

                yield return new LogItem(
                    Timestamp: r.Timestamp.ToUniversalTime().ToString("o"),
                    Level: r.Level,
                    Message: r.Message,
                    Exception: r.Exception,
                    Service: r.ServiceName,
                    Environment: r.EnvironmentName,
                    Tenant: r.Tenant,
                    TraceId: r.TraceId,
                    SpanId: r.SpanId,
                    Properties: r.State
                );
            }
        }

        private static int ParseLevel(string? level)
        {
            // Map to numeric: Trace=0, Debug=1, Information=2, Warning=3, Error=4, Critical=5
            return level switch
            {
                "Trace" => 0,
                "Debug" => 1,
                "Information" => 2,
                "Warning" => 3,
                "Error" => 4,
                "Critical" => 5,
                _ => 2
            };
        }

        private static bool IsAllowedCategory(string? category, string[] allowPrefixes)
        {
            if (
                string.IsNullOrWhiteSpace(category)
                || allowPrefixes is null
                || allowPrefixes.Length == 0
            )
                return false;
            foreach (var p in allowPrefixes)
            {
                if (
                    !string.IsNullOrWhiteSpace(p)
                    && category.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                )
                    return true;
            }
            return false;
        }
    } // End of Class LogReportingBackgroundService
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
