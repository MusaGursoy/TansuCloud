// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TansuCloud.Dashboard.Logging
{
    /// <summary>
    /// In-memory ring buffer logger provider capturing logs at or above a configured minimum log level.
    /// Intended for short-term diagnostics and periodic reporting, not as a durable store.
    /// </summary>
    public sealed class InMemoryLogBufferProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, InMemoryLogger> _loggers = new();
        private readonly InMemoryLogBuffer _buffer;
        private readonly LogLevel _minLevel;

        public InMemoryLogBufferProvider(int capacity, LogLevel minLevel)
        {
            _buffer = new InMemoryLogBuffer(capacity);
            _minLevel = minLevel;
        }

        public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new InMemoryLogger(name, _buffer, _minLevel));

        public void Dispose() => _loggers.Clear();

        public InMemoryLogBuffer Buffer => _buffer;

        public sealed class InMemoryLogBuffer
        {
            private readonly object _gate = new();
            private readonly LogEntry[] _entries;
            private int _nextIndex = 0;
            private int _count = 0;

            public InMemoryLogBuffer(int capacity)
            {
                if (capacity < 1)
                    capacity = 1000;
                _entries = new LogEntry[capacity];
            }

            public int Capacity => _entries.Length;

            public int Count
            {
                get { lock (_gate) return _count; }
            }

            public void Add(LogEntry entry)
            {
                lock (_gate)
                {
                    _entries[_nextIndex] = entry;
                    _nextIndex = (_nextIndex + 1) % _entries.Length;
                    if (_count < _entries.Length) _count++;
                }
            }

            public IReadOnlyList<LogEntry> Snapshot(int max = int.MaxValue)
            {
                lock (_gate)
                {
                    var count = Math.Min(_count, max);
                    var list = new List<LogEntry>(count);
                    var start = (_count == _entries.Length) ? _nextIndex : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var idx = (start + i) % _entries.Length;
                        list.Add(_entries[idx]);
                    }
                    return list;
                }
            }

            public int DrainTo(ICollection<LogEntry> destination, int max)
            {
                lock (_gate)
                {
                    var count = Math.Min(_count, Math.Min(max, _entries.Length));
                    var start = (_count == _entries.Length) ? _nextIndex : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var idx = (start + i) % _entries.Length;
                        destination.Add(_entries[idx]);
                    }
                    // After draining, keep entries but reset counters so we don't resend.
                    _nextIndex = 0;
                    _count = 0;
                    return count;
                }
            }

            public void Clear()
            {
                lock (_gate)
                {
                    Array.Clear(_entries, 0, _entries.Length);
                    _nextIndex = 0;
                    _count = 0;
                }
            }
        }

        public readonly record struct LogEntry(
            DateTimeOffset Timestamp,
            LogLevel Level,
            string Category,
            EventId EventId,
            string Message,
            Exception? Exception,
            IReadOnlyDictionary<string, object?> State
        );

        private sealed class InMemoryLogger : ILogger
        {
            private readonly string _category;
            private readonly InMemoryLogBuffer _buffer;
            private readonly LogLevel _minLevel;

            public InMemoryLogger(string category, InMemoryLogBuffer buffer, LogLevel minLevel)
            {
                _category = category;
                _buffer = buffer;
                _minLevel = minLevel;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var msg = formatter(state, exception);
                var dict = StateToDictionary(state);
                _buffer.Add(new LogEntry(DateTimeOffset.UtcNow, logLevel, _category, eventId, msg, exception, dict));
            }

            private static IReadOnlyDictionary<string, object?> StateToDictionary<TState>(TState state)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in list)
                    {
                        dict[kv.Key] = kv.Value;
                    }
                    return dict;
                }
                return new Dictionary<string, object?> { ["state"] = state };
            }
        }
    } // End of Class InMemoryLogBufferProvider
} // End of Namespace TansuCloud.Dashboard.Logging
