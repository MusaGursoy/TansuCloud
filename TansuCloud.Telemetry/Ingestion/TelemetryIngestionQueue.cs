// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Ingestion.Models;
using TansuCloud.Telemetry.Metrics;

namespace TansuCloud.Telemetry.Ingestion;

/// <summary>
/// Channel-backed telemetry ingestion queue.
/// </summary>
public sealed class TelemetryIngestionQueue : ITelemetryIngestionQueue, IAsyncDisposable
{
    private readonly Channel<TelemetryWorkItem> _channel;
    private readonly TelemetryMetrics _metrics;
    private readonly ILogger<TelemetryIngestionQueue> _logger;
    private readonly int _capacity;
    private int _depth;

    public TelemetryIngestionQueue(
        IOptions<TelemetryIngestionOptions> options,
        TelemetryMetrics metrics,
        ILogger<TelemetryIngestionQueue> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);

        _metrics = metrics;
        _logger = logger;

        var value = options.Value;
        _capacity = value.QueueCapacity;

        var channelOptions = new BoundedChannelOptions(_capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<TelemetryWorkItem>(channelOptions);
        _metrics.RegisterQueueObserver(GetDepth);
    } // End of Constructor TelemetryIngestionQueue

    public ChannelReader<TelemetryWorkItem> Reader => _channel.Reader; // End of Property Reader

    public async ValueTask<bool> TryEnqueueAsync(TelemetryWorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (_channel.Writer.TryWrite(workItem))
        {
            var newDepth = Interlocked.Increment(ref _depth);
            _metrics.RecordQueueDepth(newDepth);
            return true;
        }

        try
        {
            await _channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
            var newDepth = Interlocked.Increment(ref _depth);
            _metrics.RecordQueueDepth(newDepth);
            return true;
        }
        catch (ChannelClosedException ex)
        {
            _logger.LogWarning(ex, "Telemetry ingestion queue is closed; dropping payload.");
            return false;
        }
    } // End of Method TryEnqueueAsync

    public async ValueTask<TelemetryWorkItem> DequeueAsync(CancellationToken cancellationToken)
    {
        var workItem = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var newDepth = Interlocked.Decrement(ref _depth);
        _metrics.RecordQueueDepth(Math.Max(newDepth, 0));
        return workItem;
    } // End of Method DequeueAsync

    public int GetDepth()
    {
        return Volatile.Read(ref _depth);
    } // End of Method GetDepth

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    } // End of Method DisposeAsync
} // End of Class TelemetryIngestionQueue
