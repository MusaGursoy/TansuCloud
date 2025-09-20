// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.Logging;

public sealed class BufferedLoggerProvider : ILoggerProvider
{
    private readonly ILogBuffer _buffer;
    private readonly IOptionsMonitor<LoggingReportOptions> _options;
    private readonly string _serviceName;
    private readonly string _env;

    public BufferedLoggerProvider(ILogBuffer buffer, IOptionsMonitor<LoggingReportOptions> options)
    {
        _buffer = buffer;
        _options = options;
        _serviceName = "tansu.dashboard";
        _env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    } // End of Constructor BufferedLoggerProvider

    public ILogger CreateLogger(string categoryName) =>
        new BufferedLogger(categoryName, _buffer, _options, _serviceName, _env); // End of Method CreateLogger

    public void Dispose() { } // End of Method Dispose

    private sealed class BufferedLogger : ILogger
    {
        private readonly string _category;
        private readonly ILogBuffer _buffer;
        private readonly IOptionsMonitor<LoggingReportOptions> _options;
        private readonly string _serviceName;
        private readonly string _env;

        public BufferedLogger(
            string category,
            ILogBuffer buffer,
            IOptionsMonitor<LoggingReportOptions> options,
            string serviceName,
            string env
        )
        {
            _category = category;
            _buffer = buffer;
            _options = options;
            _serviceName = serviceName;
            _env = env;
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
                EnvironmentName = _env
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
    } // End of Class BufferedLogger

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    } // End of Class NullScope
} // End of Class BufferedLoggerProvider
