// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;

namespace TansuCloud.Dashboard.Observability.Logging;

/// <summary>
/// Bounded FIFO buffer for log records. Oldest entries are dropped when capacity is exceeded.
/// </summary>
public sealed class InMemoryLogBuffer : ILogBuffer
{
    private readonly ConcurrentQueue<LogRecord> _queue = new();
    private readonly int _capacity;

    public InMemoryLogBuffer(int capacity)
    {
        _capacity = Math.Max(100, capacity);
        Capacity = _capacity;
    } // End of Constructor InMemoryLogBuffer

    public int Capacity { get; } // End of Property Capacity

    public int Count => _queue.Count; // End of Property Count

    public void Add(LogRecord record)
    {
        _queue.Enqueue(record);

        // Trim overflow if any
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
            // drop oldest
        }
    } // End of Method Add

    public IReadOnlyList<LogRecord> Snapshot()
    {
        return _queue.ToArray();
    } // End of Method Snapshot

    public IReadOnlyList<LogRecord> PeekBatch(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<LogRecord>();
        var arr = _queue.ToArray();
        if (arr.Length == 0) return Array.Empty<LogRecord>();
        var take = Math.Min(maxCount, arr.Length);
        var list = new List<LogRecord>(take);
        for (var i = 0; i < take; i++) list.Add(arr[i]);
        return list;
    } // End of Method DequeueBatch

    public int RemoveBatch(int count)
    {
        if (count <= 0) return 0;
        var removed = 0;
        for (var i = 0; i < count && _queue.TryDequeue(out _); i++)
        {
            removed++;
        }
        return removed;
    } // End of Method RemoveBatch

    public int Clear()
    {
        var removed = 0;
        while (_queue.TryDequeue(out _))
        {
            removed++;
        }
        return removed;
    } // End of Method Clear
} // End of Class InMemoryLogBuffer
