// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Logging;

public sealed class BufferedLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ILogBuffer _buffer;
    private readonly IOptionsMonitor<LoggingReportOptions> _options;
    private readonly string _serviceName;
    private readonly string _env;
    private IExternalScopeProvider? _scopeProvider;

    public BufferedLoggerProvider(ILogBuffer buffer, IOptionsMonitor<LoggingReportOptions> options)
    {
        _buffer = buffer;
        _options = options;
        _serviceName = "tansu.dashboard";
        _env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    } // End of Constructor BufferedLoggerProvider

    public ILogger CreateLogger(string categoryName) =>
        new BufferedLogger(categoryName, _buffer, _options, _serviceName, _env, _scopeProvider); // End of Method CreateLogger

    public void Dispose() { } // End of Method Dispose

    public void SetScopeProvider(IExternalScopeProvider? scopeProvider)
    {
        _scopeProvider = scopeProvider;
    } // End of Method SetScopeProvider

    private sealed class BufferedLogger : ILogger
    {
        private readonly string _category;
        private readonly ILogBuffer _buffer;
        private readonly IOptionsMonitor<LoggingReportOptions> _options;
        private readonly string _serviceName;
        private readonly string _env;
        private readonly IExternalScopeProvider? _scopeProvider;

        public BufferedLogger(
            string category,
            ILogBuffer buffer,
            IOptionsMonitor<LoggingReportOptions> options,
            string serviceName,
            string env,
            IExternalScopeProvider? scopeProvider
        )
        {
            _category = category;
            _buffer = buffer;
            _options = options;
            _serviceName = serviceName;
            _env = env;
            _scopeProvider = scopeProvider;
        } // End of Constructor BufferedLogger

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            var opts = _options.CurrentValue;
            return opts.Enabled && logLevel >= opts.MinimumLevel;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled || logLevel < opts.MinimumLevel)
                return;

            var span = Activity.Current;
            var record = new LogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = logLevel.ToString(),
                Category = _category,
                Message = SafeFormat(formatter, state, exception),
                Exception = exception?.ToString(),
                TraceId = span?.TraceId.ToString(),
                SpanId = span?.SpanId.ToString(),
                ServiceName = _serviceName,
                EnvironmentName = _env,
                EventId = eventId.Id,
                EventName = eventId.Name
            };

            // Try to capture structured state
            try
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
                {
                    var dict = kvps.ToDictionary(k => k.Key, v => v.Value);
                    var json = JsonSerializer.SerializeToElement(
                        dict,
                        new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = System
                                .Text
                                .Json
                                .Serialization
                                .JsonIgnoreCondition
                                .WhenWritingNull
                        }
                    );
                    record = record with { State = json };
                }
            }
            catch
            { /* ignore state serialization issues */
            }

            if (_scopeProvider is not null)
            {
                ExtractScope(_scopeProvider, ref record);
            }

            if (string.IsNullOrEmpty(record.CorrelationId) && span is not null)
            {
                record = record with { CorrelationId = span.TraceId.ToString() };
            }

            _buffer.Add(record);
        }

        private static string SafeFormat<TState>(
            Func<TState, Exception?, string> formatter,
            TState state,
            Exception? exception
        )
        {
            try
            {
                return formatter(state, exception);
            }
            catch
            {
                return exception?.Message ?? "";
            }
        }

        private static void ExtractScope(IExternalScopeProvider scopeProvider, ref LogRecord record)
        {
            string? correlationId = record.CorrelationId;
            string? tenant = record.Tenant;
            string? requestId = record.RequestId;

            scopeProvider.ForEachScope((scope, state) =>
            {
                switch (scope)
                {
                    case IEnumerable<KeyValuePair<string, object?>> kvps:
                        foreach (var kv in kvps)
                        {
                            if (kv.Value is null)
                            {
                                continue;
                            }

                            var key = kv.Key;
                            if (string.Equals(key, "CorrelationId", StringComparison.OrdinalIgnoreCase))
                            {
                                correlationId ??= kv.Value?.ToString();
                            }
                            else if (string.Equals(key, "Tenant", StringComparison.OrdinalIgnoreCase))
                            {
                                tenant ??= kv.Value?.ToString();
                            }
                            else if (string.Equals(key, "RequestId", StringComparison.OrdinalIgnoreCase))
                            {
                                requestId ??= kv.Value?.ToString();
                            }
                        }
                        break;
                    case IDisposable: // ignore nested disposables
                        break;
                    default:
                        if (scope is { })
                        {
                            var text = scope.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                correlationId ??= text;
                            }
                        }
                        break;
                }
            }, state: (object?)null);

            record = record with
            {
                CorrelationId = correlationId,
                Tenant = tenant,
                RequestId = requestId
            };
        }
    } // End of Class BufferedLogger

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    } // End of Class NullScope
} // End of Class BufferedLoggerProvider
