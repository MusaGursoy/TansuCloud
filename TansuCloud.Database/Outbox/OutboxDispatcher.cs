// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.Outbox;

internal sealed class OutboxDispatcher(
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger,
    IServiceProvider services
) : BackgroundService
{
    private readonly OutboxOptions _opts = options.Value;
    private readonly ILogger<OutboxDispatcher> _logger = logger;
    private ConnectionMultiplexer? _redis;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opts.RedisConnection))
        {
            _logger.LogInformation("Outbox disabled: no RedisConnection configured");
            return;
        }

        try
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(_opts.RedisConnection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Redis. Outbox dispatcher will stop.");
            return;
        }

        var sub = _redis.GetSubscriber();
        var delay = TimeSpan.FromSeconds(Math.Max(1, _opts.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var httpCtxAccessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                var httpCtx = httpCtxAccessor.HttpContext;
                if (httpCtx is null)
                {
                    // No request context -> skip; this dispatcher is per-process and needs a tenant context.
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
                await using var db = await factory.CreateAsync(httpCtx, stoppingToken);

                var now = DateTimeOffset.UtcNow;
                var due = await db.OutboxEvents
                    .Where(e => e.Status == OutboxStatus.Pending && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
                    .OrderBy(e => e.NextAttemptAt ?? e.OccurredAt)
                    .Take(_opts.BatchSize)
                    .ToListAsync(stoppingToken);

                foreach (var e in due)
                {
                    try
                    {
                        var payloadJson = e.Payload is null ? "null" : e.Payload.RootElement.GetRawText();
                        await sub.PublishAsync(RedisChannel.Literal(_opts.Channel), payloadJson);
                        e.Status = OutboxStatus.Dispatched;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        e.Attempts++;
                        e.Status = e.Attempts >= _opts.MaxAttempts ? OutboxStatus.DeadLettered : OutboxStatus.Failed;
                        var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(10, e.Attempts))));
                        // add jitter 0-1s
                        backoff += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                        e.NextAttemptAt = DateTimeOffset.UtcNow + backoff;
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogWarning(ex, "Failed dispatching outbox event {Id}. Status={Status} Attempts={Attempts}", e.Id, e.Status, e.Attempts);
                    }
                }
            }
            catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatcher iteration failed");
            }
            await Task.Delay(delay, stoppingToken);
        }
    }
}
