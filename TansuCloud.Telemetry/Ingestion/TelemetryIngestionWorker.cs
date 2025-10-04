// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Ingestion.Models;
using TansuCloud.Telemetry.Metrics;

namespace TansuCloud.Telemetry.Ingestion;

/// <summary>
/// Background service that drains the telemetry ingestion queue and writes payloads to the database.
/// </summary>
public sealed class TelemetryIngestionWorker : BackgroundService
{
    private readonly ITelemetryIngestionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelemetryMetrics _metrics;
    private readonly ILogger<TelemetryIngestionWorker> _logger;

    public TelemetryIngestionWorker(
        ITelemetryIngestionQueue queue,
        IServiceScopeFactory scopeFactory,
        TelemetryMetrics metrics,
        ILogger<TelemetryIngestionWorker> logger
    )
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    } // End of Constructor TelemetryIngestionWorker

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry ingestion worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                await PersistAsync(workItem, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception while persisting telemetry payload");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telemetry ingestion worker stopping");
    } // End of Method ExecuteAsync

    private async Task PersistAsync(TelemetryWorkItem workItem, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<TelemetryRepository>();
        await repository.PersistAsync(workItem.Envelope, cancellationToken).ConfigureAwait(false);
        _metrics.RecordPersistedItems(workItem.Envelope.ItemCount);
    } // End of Method PersistAsync
} // End of Class TelemetryIngestionWorker
