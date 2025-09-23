// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TansuCloud.Observability.Auditing;

/// <summary>
/// Enriches and enqueues audit events; a hosted background writer persists to Postgres.
/// Never blocks the request path; on backpressure may drop (configurable) with metrics.
/// </summary>
internal sealed class HttpAuditLogger(
    IOptions<AuditOptions> options,
    IHttpContextAccessor http,
    ILogger<HttpAuditLogger> logger,
    IHostEnvironment env
) : IAuditLogger
{
    private static readonly Meter Meter = new("TansuCloud.Audit");
    private static readonly Counter<long> DroppedCounter = Meter.CreateCounter<long>(
        "audit_dropped"
    );
    private static readonly Counter<long> EnqueuedCounter = Meter.CreateCounter<long>(
        "audit_enqueued"
    );
    private static readonly ObservableGauge<int> BacklogGauge = Meter.CreateObservableGauge(
        "audit_backlog",
        () =>
        {
            var count = 0;
            if (_channel.IsValueCreated)
            {
                try { count = _channel.Value.Reader.Count; } catch { count = 0; }
            }
            return new[] { new Measurement<int>(count) };
        }
    );

    private readonly AuditOptions _opts = options.Value;
    private readonly IHttpContextAccessor _http = http;
    private readonly ILogger<HttpAuditLogger> _logger = logger;
    private readonly IHostEnvironment _env = env;

    // Bounded channel shared via static factory; writer reads from it
    private static readonly Lazy<Channel<AuditEvent>> _channel =
        new(
            () =>
                Channel.CreateBounded<AuditEvent>(
                    new BoundedChannelOptions(capacity: 10_000)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = true,
                        SingleWriter = false
                    }
                )
        );

    internal static ChannelReader<AuditEvent> Reader => _channel.Value.Reader;

    public bool TryEnqueue(AuditEvent evt)
    {
        var ctx = _http.HttpContext;
        if (ctx != null)
        {
            evt = EnrichFromHttpContext(evt, ctx);
        }
        // Enforce details size limit
        evt = TruncateDetailsIfNeeded(evt, _opts.MaxDetailsBytes);
        // Ensure idempotency key exists for natural dedupe
        if (string.IsNullOrWhiteSpace(evt.IdempotencyKey))
        {
            evt = new AuditEvent
            {
                Id = evt.Id,
                WhenUtc = evt.WhenUtc,
                Service = evt.Service,
                Environment = evt.Environment,
                Version = evt.Version,
                TenantId = evt.TenantId,
                Subject = evt.Subject,
                Action = evt.Action,
                Category = evt.Category,
                RouteTemplate = evt.RouteTemplate,
                CorrelationId = evt.CorrelationId,
                TraceId = evt.TraceId,
                SpanId = evt.SpanId,
                ClientIpHash = evt.ClientIpHash,
                UserAgent = evt.UserAgent,
                Outcome = evt.Outcome,
                ReasonCode = evt.ReasonCode,
                Details = evt.Details,
                ImpersonatedBy = evt.ImpersonatedBy,
                SourceHost = evt.SourceHost,
                UniqueKey = evt.UniqueKey,
                IdempotencyKey = AuditKey.Compute(evt)
            };
        }

        bool written;
        if (_opts.FullDropEnabled)
        {
            written = _channel.Value.Writer.TryWrite(evt);
        }
        else
        {
            written =
                _channel.Value.Writer.WaitToWriteAsync(default).AsTask().GetAwaiter().GetResult()
                && _channel.Value.Writer.TryWrite(evt);
        }
        if (written)
            EnqueuedCounter.Add(1);
        else
            DroppedCounter.Add(1);
        return written;
    } // End of Method TryEnqueue

    private AuditEvent EnrichFromHttpContext(AuditEvent e, HttpContext ctx)
    {
        var activity = System.Diagnostics.Activity.Current;
        var routeBase = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : string.Empty;
        var template = routeBase; // best-effort; templates can be supplied by caller
        var corr = ctx.Response.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrWhiteSpace(corr))
        {
            corr = ctx.Request.Headers["X-Correlation-ID"].ToString();
        }
        string? clientHash = null;
        if (!string.IsNullOrWhiteSpace(_opts.ClientIpHashSalt))
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                clientHash = HmacSha256Hex(_opts.ClientIpHashSalt!, ip);
            }
        }

        var ua = ctx.Request.Headers["User-Agent"].ToString();
        if (ua.Length > 128)
            ua = ua.Substring(0, 128);
        var tenant = ctx.Request.Headers["X-Tansu-Tenant"].ToString() ?? string.Empty;

        return new AuditEvent
        {
            Id = e.Id,
            WhenUtc = e.WhenUtc,
            Service = string.IsNullOrWhiteSpace(e.Service)
                ? _env.ApplicationName ?? "unknown"
                : e.Service,
            Environment = string.IsNullOrWhiteSpace(e.Environment)
                ? (_env.EnvironmentName ?? "")
                : e.Environment,
            Version = e.Version,
            TenantId = string.IsNullOrWhiteSpace(e.TenantId) ? tenant : e.TenantId,
            Subject = e.Subject,
            Action = e.Action,
            Category = e.Category,
            RouteTemplate = string.IsNullOrWhiteSpace(e.RouteTemplate) ? template : e.RouteTemplate,
            CorrelationId = string.IsNullOrWhiteSpace(e.CorrelationId) ? corr : e.CorrelationId,
            TraceId = string.IsNullOrWhiteSpace(e.TraceId)
                ? activity?.TraceId.ToString() ?? string.Empty
                : e.TraceId,
            SpanId = string.IsNullOrWhiteSpace(e.SpanId)
                ? activity?.SpanId.ToString() ?? string.Empty
                : e.SpanId,
            ClientIpHash = e.ClientIpHash ?? clientHash,
            UserAgent = e.UserAgent ?? ua,
            Outcome = e.Outcome,
            ReasonCode = e.ReasonCode,
            Details = e.Details,
            ImpersonatedBy = e.ImpersonatedBy,
            SourceHost = e.SourceHost,
            IdempotencyKey = e.IdempotencyKey,
            UniqueKey = e.UniqueKey
        };
    } // End of Method EnrichFromHttpContext

    private static AuditEvent TruncateDetailsIfNeeded(AuditEvent e, int maxBytes)
    {
        if (e.Details == null || maxBytes <= 0)
            return e;
        try
        {
            var raw = e.Details.RootElement.GetRawText();
            var bytes = Encoding.UTF8.GetBytes(raw);
            if (bytes.Length <= maxBytes)
                return e;
            // Truncate to max bytes and add a marker field
            var truncated = Encoding.UTF8.GetString(bytes, 0, maxBytes);
            var json =
                $"{{\"truncated\":true,\"len\":{bytes.Length},\"preview\":{truncated.AsJsonStringLiteral()} }}";
            var doc = JsonDocument.Parse(json);
            return new AuditEvent
            {
                Id = e.Id,
                WhenUtc = e.WhenUtc,
                Service = e.Service,
                Environment = e.Environment,
                Version = e.Version,
                TenantId = e.TenantId,
                Subject = e.Subject,
                Action = e.Action,
                Category = e.Category,
                RouteTemplate = e.RouteTemplate,
                CorrelationId = e.CorrelationId,
                TraceId = e.TraceId,
                SpanId = e.SpanId,
                ClientIpHash = e.ClientIpHash,
                UserAgent = e.UserAgent,
                Outcome = e.Outcome,
                ReasonCode = e.ReasonCode,
                Details = doc,
                ImpersonatedBy = e.ImpersonatedBy,
                SourceHost = e.SourceHost,
                IdempotencyKey = e.IdempotencyKey,
                UniqueKey = e.UniqueKey
            };
        }
        catch
        {
            return e;
        }
    } // End of Method TruncateDetailsIfNeeded

    private static string HmacSha256Hex(string key, string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    } // End of Method HmacSha256Hex
} // End of Class HttpAuditLogger

internal static class JsonStringExtensions
{
    public static string AsJsonStringLiteral(this string s) => JsonSerializer.Serialize(s);
} // End of Class JsonStringExtensions

/// <summary>
/// Hosted background writer: creates table if needed; batches inserts to Postgres.
/// </summary>
internal sealed class AuditBackgroundWriter(
    IOptions<AuditOptions> options,
    ILogger<AuditBackgroundWriter> logger,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private static readonly Meter Meter = new("TansuCloud.Audit");
    private static readonly Counter<long> WriteFailures = Meter.CreateCounter<long>(
        "audit_write_failures"
    );
    private readonly AuditOptions _opts = options.Value;
    private readonly ILogger<AuditBackgroundWriter> _logger = logger;
    // Touch lifetime to avoid unused parameter warning and to ensure service binds to app lifetime.
    private readonly IHostApplicationLifetime _lifetime = lifetime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureTableAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit writer failed to ensure table");
        }

        var reader = HttpAuditLogger.Reader;
        var batch = new List<AuditEvent>(256);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await reader.WaitToReadAsync(stoppingToken))
                {
                    while (reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        if (batch.Count >= 256)
                            break;
                    }
                    if (batch.Count > 0)
                    {
                        await WriteBatchAsync(batch, stoppingToken);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit batch write failed");
                try { WriteFailures.Add(batch.Count); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    } // End of Method ExecuteAsync

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_opts.ConnectionString);
        await conn.OpenAsync(ct);
        var sql =
            $@"
CREATE TABLE IF NOT EXISTS {_opts.Table} (
    id uuid PRIMARY KEY,
    when_utc timestamptz NOT NULL,
    service text NOT NULL,
    environment text NOT NULL,
    version text NOT NULL,
    tenant_id text NOT NULL,
    subject text NOT NULL,
    action text NOT NULL,
    category text NOT NULL,
    route_template text NOT NULL,
    correlation_id text NOT NULL,
    trace_id text NOT NULL,
    span_id text NOT NULL,
    client_ip_hash text NULL,
    user_agent text NULL,
    outcome text NULL,
    reason_code text NULL,
    details jsonb NULL,
    impersonated_by text NULL,
    source_host text NULL,
    idempotency_key text NULL,
    unique_key text NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_when_desc ON {_opts.Table} (when_utc DESC);
CREATE INDEX IF NOT EXISTS ix_audit_tenant ON {_opts.Table} (tenant_id);
CREATE INDEX IF NOT EXISTS ix_audit_category ON {_opts.Table} (category);
CREATE INDEX IF NOT EXISTS ix_audit_action ON {_opts.Table} (action);
CREATE INDEX IF NOT EXISTS ix_audit_service ON {_opts.Table} (service);
CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_idem ON {_opts.Table} (idempotency_key) WHERE idempotency_key IS NOT NULL;
";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    } // End of Method EnsureTableAsync

    private async Task WriteBatchAsync(List<AuditEvent> batch, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_opts.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var insert =
            $@"INSERT INTO {_opts.Table} (
            id, when_utc, service, environment, version, tenant_id, subject, action, category, route_template, correlation_id, trace_id, span_id,
            client_ip_hash, user_agent, outcome, reason_code, details, impersonated_by, source_host, idempotency_key, unique_key
        ) VALUES (
            @id, @when_utc, @service, @environment, @version, @tenant_id, @subject, @action, @category, @route_template, @correlation_id, @trace_id, @span_id,
            @client_ip_hash, @user_agent, @outcome, @reason_code, @details, @impersonated_by, @source_host, @idempotency_key, @unique_key
        ) ON CONFLICT DO NOTHING;";
        foreach (var e in batch)
        {
            await using var cmd = new NpgsqlCommand(insert, conn, tx);
            cmd.Parameters.AddWithValue("@id", e.Id);
            cmd.Parameters.AddWithValue("@when_utc", e.WhenUtc);
            cmd.Parameters.AddWithValue("@service", e.Service);
            cmd.Parameters.AddWithValue("@environment", e.Environment);
            cmd.Parameters.AddWithValue("@version", e.Version);
            cmd.Parameters.AddWithValue("@tenant_id", e.TenantId);
            cmd.Parameters.AddWithValue("@subject", e.Subject);
            cmd.Parameters.AddWithValue("@action", e.Action);
            cmd.Parameters.AddWithValue("@category", e.Category);
            cmd.Parameters.AddWithValue("@route_template", e.RouteTemplate);
            cmd.Parameters.AddWithValue("@correlation_id", e.CorrelationId);
            cmd.Parameters.AddWithValue("@trace_id", e.TraceId);
            cmd.Parameters.AddWithValue("@span_id", e.SpanId);
            cmd.Parameters.AddWithValue("@client_ip_hash", (object?)e.ClientIpHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_agent", (object?)e.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@outcome", (object?)e.Outcome ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason_code", (object?)e.ReasonCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "@details",
                (object?)(e.Details?.RootElement.GetRawText()) ?? DBNull.Value
            );
            cmd.Parameters.AddWithValue(
                "@impersonated_by",
                (object?)e.ImpersonatedBy ?? DBNull.Value
            );
            cmd.Parameters.AddWithValue("@source_host", (object?)e.SourceHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "@idempotency_key",
                (object?)e.IdempotencyKey ?? DBNull.Value
            );
            cmd.Parameters.AddWithValue("@unique_key", (object?)e.UniqueKey ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    } // End of Method WriteBatchAsync
} // End of Class AuditBackgroundWriter

/// <summary>
/// Service registration extensions for the Audit SDK (Task 31 Phase 1).
/// </summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit logger and background writer and binds Audit options from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Application configuration used to bind the Audit section.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTansuAudit(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services
            .AddOptions<AuditOptions>()
            .Bind(config.GetSection(AuditOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IAuditLogger, HttpAuditLogger>();
        services.AddHostedService<AuditBackgroundWriter>();
        return services;
    } // End of Method AddTansuAudit
} // End of Class AuditServiceCollectionExtensions
