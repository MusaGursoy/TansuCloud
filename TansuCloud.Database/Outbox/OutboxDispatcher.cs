// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.Outbox;

internal sealed class OutboxDispatcher(
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger,
    IServiceProvider services,
    IOutboxPublisher? publisher = null // optional injection for tests; production path will create if null
) : BackgroundService
{
    private readonly OutboxOptions _opts = options.Value;
    private readonly ILogger<OutboxDispatcher> _logger = logger;
    private ConnectionMultiplexer? _redis;
    private IOutboxPublisher? _publisher = publisher;
    private static readonly Meter Meter = new("TansuCloud.Database.Outbox", "1.0");
    private static readonly Counter<long> Produced = Meter.CreateCounter<long>("outbox.produced");
    private static readonly Counter<long> Dispatched = Meter.CreateCounter<long>(
        "outbox.dispatched"
    );
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>("outbox.retried");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.deadlettered"
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opts.RedisConnection))
        {
            _logger.LogInformation("Outbox disabled: no RedisConnection configured");
            return;
        }

        if (_publisher is null)
        {
            try
            {
                _redis = await ConnectionMultiplexer.ConnectAsync(_opts.RedisConnection);
                _publisher = new RedisOutboxPublisher(_redis);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Redis. Outbox dispatcher will stop.");
                return; // End of error path
            }
        }

        var delay = TimeSpan.FromSeconds(Math.Max(1, _opts.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Publisher might have been cleared in a future enhancement; guard defensively
                var publisher = _publisher;
                if (publisher is null)
                {
                    await Task.Delay(delay, stoppingToken);
                    continue; // End of publisher-null fast path
                }
                using var scope = services.CreateScope();
                var httpCtxAccessor =
                    scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                var httpCtx = httpCtxAccessor.HttpContext;
                await using var db = httpCtx is not null
                    ? await factory.CreateAsync(httpCtx, stoppingToken)
                    : (
                        _opts.DispatchTenant is { Length: > 0 }
                            ? await factory.CreateAsync(_opts.DispatchTenant!, stoppingToken)
                            : null
                    );

                if (db is null)
                {
                    // No context available to decide tenant; wait and retry later.
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                await DispatchPendingAsync(db, publisher, stoppingToken);
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

    // Internal test seam: process a batch once against a provided DbContext + publisher.
    internal async Task<int> DispatchPendingAsync(
        TansuDbContext db,
        IOutboxPublisher publisher,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var due = await db
            .OutboxEvents.Where(e =>
                (e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Failed)
                && (e.NextAttemptAt == null || e.NextAttemptAt <= now)
            )
            .OrderBy(e => e.NextAttemptAt ?? e.OccurredAt)
            .Take(_opts.BatchSize * 2) // over-fetch a little to allow suppression filtering
            .ToListAsync(ct);

        // Idempotency suppression: if an outbox event has an IdempotencyKey and a previously dispatched
        // event with same (Type, IdempotencyKey) exists, skip publishing and mark as Dispatched immediately.
        // This avoids duplicate external publishes after producer logic races.
        if (due.Count > 0)
        {
            var keyed = due.Where(e => e.IdempotencyKey != null).ToList();
            if (keyed.Count > 0)
            {
                var keys = keyed.Select(k => k.IdempotencyKey!).Distinct().ToList();
                var already = await db
                    .OutboxEvents.AsNoTracking()
                    .Where(e =>
                        e.Status == OutboxStatus.Dispatched
                        && e.IdempotencyKey != null
                        && keys.Contains(e.IdempotencyKey)
                    )
                    .Select(e => new ValueTuple<string, string>(e.IdempotencyKey!, e.Type))
                    .ToListAsync(ct);
                var dispatchedSet = new HashSet<(string, string)>(already);
                // Track first occurrence in current batch so later duplicates are suppressed even within same fetch.
                foreach (var e in keyed.OrderBy(k => k.OccurredAt))
                {
                    var key = (e.IdempotencyKey!, e.Type);
                    if (!dispatchedSet.Add(key))
                    {
                        e.Status = OutboxStatus.Dispatched; // suppress publish (duplicate in DB or earlier in batch)
                    }
                }
            }
        }

        foreach (var e in due)
        {
            if (e.Status == OutboxStatus.Dispatched)
            {
                continue; // suppressed duplicate already marked dispatched
            }
            try
            {
                var payloadJson = e.Payload is null ? "null" : e.Payload.RootElement.GetRawText();
                await publisher.PublishAsync(_opts.Channel, payloadJson, ct);
                e.Status = OutboxStatus.Dispatched;
                await db.SaveChangesAsync(ct);
                Dispatched.Add(1);
            }
            catch (Exception ex)
            {
                e.Attempts++;
                var isDead = e.Attempts >= _opts.MaxAttempts;
                e.Status = isDead ? OutboxStatus.DeadLettered : OutboxStatus.Failed;
                var baseSeconds = Math.Pow(2, Math.Min(8, e.Attempts));
                var backoff = TimeSpan.FromSeconds(Math.Min(300, baseSeconds));
                backoff += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                e.NextAttemptAt = DateTimeOffset.UtcNow + backoff;
                await db.SaveChangesAsync(ct);
                if (isDead)
                {
                    DeadLettered.Add(1);
                    _logger.LogError(
                        ex,
                        "Dead-lettered outbox event {Id} after {Attempts} attempts",
                        e.Id,
                        e.Attempts
                    );
                }
                else
                {
                    Retried.Add(1);
                    _logger.LogWarning(
                        ex,
                        "Failed dispatching outbox event {Id}. Attempts={Attempts} next={NextAttemptAt}",
                        e.Id,
                        e.Attempts,
                        e.NextAttemptAt
                    );
                }
            }
        }
        return due.Count;
    }
}
