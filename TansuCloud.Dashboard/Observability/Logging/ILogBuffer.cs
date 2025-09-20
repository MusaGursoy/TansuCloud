// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.Logging;

/// <summary>
/// Thread-safe bounded in-memory buffer for recent log records.
/// </summary>
public interface ILogBuffer
{
    int Capacity { get; }
    int Count { get; }

    void Add(LogRecord record);

    /// <summary>
    /// Snapshot current records (ordered oldest..newest) without clearing.
    /// </summary>
    IReadOnlyList<LogRecord> Snapshot();

    /// <summary>
    /// Peek up to maxCount records from the head without removing.
    /// </summary>
    IReadOnlyList<LogRecord> PeekBatch(int maxCount);

    /// <summary>
    /// Remove up to count records from the head (commit after successful send).
    /// Returns the number actually removed.
    /// </summary>
    int RemoveBatch(int count);

    /// <summary>
    /// Remove all records from the buffer; returns number cleared.
    /// </summary>
    int Clear();
} // End of Interface ILogBuffer
