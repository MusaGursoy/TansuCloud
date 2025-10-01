// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TansuCloud.Database.EF;
using TansuCloud.Database.Services;
using TansuCloud.Observability;

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

                var tenantTag = ResolveTenantTag(httpCtx, _opts.DispatchTenant);
                await DispatchPendingAsync(db, publisher, tenantTag, stoppingToken);
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
        string? tenantTag,
        CancellationToken ct
    )
    {
        using var activity = TansuActivitySources.Background.StartActivity(
            "OutboxDispatch",
            ActivityKind.Internal
        );

        var normalizedTenant = NormalizeTenantTag(tenantTag);

        if (activity is not null)
        {
            if (!string.IsNullOrEmpty(normalizedTenant))
            {
                activity.SetTag(TelemetryConstants.Tenant, normalizedTenant);
            }

            if (!string.IsNullOrWhiteSpace(_opts.Channel))
            {
                activity.SetTag("outbox.channel", _opts.Channel);
            }

            activity.SetTag("outbox.batch.limit", _opts.BatchSize);
        }

        try
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

            if (string.IsNullOrEmpty(normalizedTenant))
            {
                var derived = NormalizeTenantTag(TryDeriveTenantFromDatabase(db));
                if (!string.IsNullOrEmpty(derived))
                {
                    normalizedTenant = derived;
                    activity?.SetTag(TelemetryConstants.Tenant, normalizedTenant);
                }
            }

            activity?.SetTag("outbox.batch.due_count", due.Count);

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

            var suppressed = 0;
            var dispatchedCount = 0;
            var retriedCount = 0;
            var deadLetteredCount = 0;

            foreach (var e in due)
            {
                if (e.Status == OutboxStatus.Dispatched)
                {
                    suppressed++;
                    continue; // suppressed duplicate already marked dispatched
                }

                var attempt = e.Attempts + 1;
                using var eventActivity = StartEventActivity(normalizedTenant, e, attempt);

                try
                {
                    _logger.LogOutboxDispatchAttempt(e.Id, attempt);
                    var payloadJson = e.Payload is null ? "null" : e.Payload.RootElement.GetRawText();
                    await publisher.PublishAsync(_opts.Channel, payloadJson, ct);
                    e.Status = OutboxStatus.Dispatched;
                    await db.SaveChangesAsync(ct);
                    Dispatched.Add(1);
                    dispatchedCount++;
                    eventActivity?.SetTag("outbox.event.status", nameof(OutboxStatus.Dispatched));
                    eventActivity?.SetStatus(ActivityStatusCode.Ok);
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

                    eventActivity?.SetTag("outbox.event.status", e.Status.ToString());
                    eventActivity?.SetTag("outbox.event.error", ex.Message);
                    eventActivity?.SetTag("outbox.event.next_attempt_at", e.NextAttemptAt?.ToString("O"));
                    eventActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    if (isDead)
                    {
                        DeadLettered.Add(1);
                        deadLetteredCount++;
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
                        retriedCount++;
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

            activity?.SetTag("outbox.events.suppressed", suppressed);
            activity?.SetTag("outbox.events.dispatched", dispatchedCount);
            activity?.SetTag("outbox.events.retried", retriedCount);
            activity?.SetTag("outbox.events.dead_lettered", deadLetteredCount);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return due.Count;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("outbox.error", ex.Message);
            throw;
        }
    }

    private static string? ResolveTenantTag(HttpContext? httpContext, string? fallbackTenant)
    {
        var headerTenant = httpContext?.Request.Headers["X-Tansu-Tenant"].ToString();
        if (!string.IsNullOrWhiteSpace(headerTenant))
        {
            return headerTenant;
        }

        return fallbackTenant;
    }

    private static string? TryDeriveTenantFromDatabase(TansuDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var name = conn?.Database;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            const string prefix = "tansu_tenant_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }

            return name;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeTenantTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 64)
        {
            trimmed = trimmed[..64];
        }

        return trimmed.ToLowerInvariant();
    }

    private static Activity? StartEventActivity(string? tenant, OutboxEvent e, int attempt)
    {
        var activity = TansuActivitySources.Background.StartActivity(
            "OutboxDispatch.Event",
            ActivityKind.Internal
        );

        if (activity is not null)
        {
            if (!string.IsNullOrEmpty(tenant))
            {
                activity.SetTag(TelemetryConstants.Tenant, tenant);
            }

            activity.SetTag("outbox.event.id", e.Id);
            activity.SetTag("outbox.event.type", e.Type);
            activity.SetTag("outbox.event.attempt", attempt);
            activity.SetTag("outbox.event.status", e.Status.ToString());
        }

        return activity;
    }
}
