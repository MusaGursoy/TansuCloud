// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text;
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
                var success = true;
                try
                {
                    if (opts.Enabled && _runtime.Enabled)
                    {
                        success = await RunOnceAsync(opts, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Log reporting tick failed");
                    success = false;
                }

                var delay = interval;
                if (!success)
                {
                    delay += TimeSpan.FromSeconds(_rand.Next(5, 30));
                }

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        } // End of Method ExecuteAsync

        private async Task<bool> RunOnceAsync(LogReportingOptions opts, CancellationToken ct)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var reporter = scope.ServiceProvider.GetRequiredService<ILogReporter>();
                // Snapshot then filter; only dequeue after a successful send to avoid loss.
                var snapshot = _buffer.Snapshot();
                if (snapshot.Count == 0)
                {
                    return true; // nothing to report
                }

                var result = FilterForReporting(snapshot, opts);
                if (result.Items.Count == 0)
                {
                    _ = _buffer.RemoveBatch(result.SourceCount);
                    return true; // nothing matches policy
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
                    Items: result.Items
                );

                // Send first; if successful, dequeue exactly the number we sent from the head.
                await reporter.ReportAsync(req, ct).ConfigureAwait(false);
                _ = _buffer.RemoveBatch(result.SourceCount);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log reporting failed");
                return false;
            }
        } // End of Method RunOnceAsync

        private FilterResult FilterForReporting(
            IReadOnlyList<LogRecord> snapshot,
            LogReportingOptions opts
        )
        {
            var threshold = ParseLevel(opts.SeverityThreshold);
            var now = DateTimeOffset.UtcNow;
            var window = TimeSpan.FromMinutes(Math.Max(1, opts.QueryWindowMinutes));
            var maxItems = Math.Max(50, opts.MaxItems);
            var items = new List<LogItem>(Math.Min(snapshot.Count, maxItems));
            var perfAggregates = new Dictionary<string, PerfAggregate>();
            var consumed = 0;

            foreach (var r in snapshot)
            {
                if (items.Count >= maxItems)
                {
                    break;
                }

                consumed++;

                if (now - r.Timestamp > window)
                {
                    continue;
                }

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

                var kind = ResolveKind(r, lvl);
                var templateHash = ComputeTemplateHash(r);
                var tenantHash = HashTenant(r.Tenant, opts);
                var item = new LogItem(
                    Kind: kind,
                    Timestamp: r.Timestamp.ToUniversalTime().ToString("o"),
                    Level: r.Level,
                    Message: r.Message,
                    TemplateHash: templateHash,
                    Exception: r.Exception,
                    Service: r.ServiceName ?? "tansu.dashboard",
                    Environment: r.EnvironmentName ?? "Production",
                    TenantHash: tenantHash,
                    CorrelationId: r.CorrelationId,
                    TraceId: r.TraceId,
                    SpanId: r.SpanId,
                    Category: r.Category,
                    EventId: r.EventId == 0 ? null : r.EventId,
                    Count: 1,
                    Properties: r.State
                );

                if (kind == "perf_slo_breach")
                {
                    if (perfAggregates.TryGetValue(templateHash, out var agg))
                    {
                        agg.Count++;
                    }
                    else
                    {
                        perfAggregates[templateHash] = new PerfAggregate(item);
                    }
                }
                else
                {
                    items.Add(item);
                }
            }

            // Collapse perf aggregates into final payload while respecting MaxItems
            foreach (var aggregate in perfAggregates.Values)
            {
                if (items.Count >= maxItems)
                {
                    break;
                }

                items.Add(aggregate.ToLogItem());
            }

            if (items.Count > maxItems)
            {
                items = items.Take(maxItems).ToList();
            }

            return new FilterResult(items, consumed);
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

        private static string ResolveKind(LogRecord record, int levelNumeric)
        {
            if (record.EventId is >= 1500 and <= 1599)
            {
                return "perf_slo_breach";
            }

            if (record.EventId is >= 4000 and <= 4099)
            {
                return "telemetry_internal";
            }

            return levelNumeric switch
            {
                >= 5 => "critical",
                4 => "error",
                3 => "warning",
                _ => "info"
            };
        }

        private static string ComputeTemplateHash(LogRecord record)
        {
            using var sha = SHA256.Create();
            var payload = string.Join("|", record.Category, record.EventId.ToString(), record.Message);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private static string? HashTenant(string? tenant, LogReportingOptions opts)
        {
            if (string.IsNullOrWhiteSpace(tenant))
            {
                return null;
            }

            if (!opts.PseudonymizeTenants)
            {
                return tenant;
            }

            var secret = opts.TenantHashSecret;
            if (!string.IsNullOrEmpty(secret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(tenant));
                return Convert.ToHexString(bytes);
            }

            using var sha = SHA256.Create();
            var fallback = sha.ComputeHash(Encoding.UTF8.GetBytes(tenant));
            return Convert.ToHexString(fallback);
        }

        private sealed class PerfAggregate
        {
            private int _count;
            private readonly LogItem _template;

            public PerfAggregate(LogItem template)
            {
                _template = template;
                _count = template.Count;
            }

            public int Count
            {
                get => _count;
                set => _count = value;
            }

            public LogItem ToLogItem()
            {
                return _template with { Count = _count };
            }
        }

        private sealed record FilterResult(IReadOnlyList<LogItem> Items, int SourceCount);
    } // End of Class LogReportingBackgroundService
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
