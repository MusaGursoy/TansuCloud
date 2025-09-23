// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Database.Services;

public sealed class AuditRetentionOptions
{
    public int Days { get; set; } = 180; // default retention window
    public string[] LegalHoldTenants { get; set; } = Array.Empty<string>();
    public bool RedactInsteadOfDelete { get; set; } = false; // when true, null Details and mark Outcome/ReasonCode
    public TimeSpan Schedule { get; set; } = TimeSpan.FromHours(6); // how often to run
}

internal sealed class AuditRetentionWorker(
    IOptions<AuditOptions> auditOptions,
    IOptions<AuditRetentionOptions> opts,
    ILogger<AuditRetentionWorker> logger,
    IHostApplicationLifetime lifetime,
    IAuditLogger audit,
    IAuditDbConnectionFactory connectionFactory
) : BackgroundService
{
    private readonly AuditOptions _audit = auditOptions.Value;
    private readonly AuditRetentionOptions _opts = opts.Value;
    private readonly ILogger<AuditRetentionWorker> _logger = logger;
    private readonly IHostApplicationLifetime _lifetime = lifetime; // ensure binding to app lifetime
    private readonly IAuditLogger _auditLogger = audit;
    private readonly IAuditDbConnectionFactory _connFactory = connectionFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // initial delay to avoid startup thundering herd
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch { }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit retention job failed");
            }
            try
            {
                await Task.Delay(_opts.Schedule, stoppingToken);
            }
            catch { }
        }
    } // End of Method ExecuteAsync

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Abs(_opts.Days));
        await using var conn = await _connFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);

        // Legal hold filter
        var holds = _opts.LegalHoldTenants ?? Array.Empty<string>();
        var legalHoldWhere = holds.Length > 0 ? " AND tenant_id <> ALL(@holds)" : string.Empty;

        int affected = 0;
        if (_opts.RedactInsteadOfDelete)
        {
            // Redact Details and mark outcome/reason for rows older than cutoff (not on legal hold)
            var sql =
                $@"UPDATE {_audit.Table}
SET details = NULL, outcome = COALESCE(outcome, 'Redacted'), reason_code = 'Retention'
WHERE when_utc < @cutoff{legalHoldWhere};";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p1 = cmd.CreateParameter();
            p1.ParameterName = "@cutoff";
            p1.Value = cutoff;
            cmd.Parameters.Add(p1);
            if (holds.Length > 0)
            {
                var p2 = cmd.CreateParameter();
                p2.ParameterName = "@holds";
                p2.Value = holds;
                cmd.Parameters.Add(p2);
            }
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            // Hard delete
            var sql = $@"DELETE FROM {_audit.Table} WHERE when_utc < @cutoff{legalHoldWhere};";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p1 = cmd.CreateParameter();
            p1.ParameterName = "@cutoff";
            p1.Value = cutoff;
            cmd.Parameters.Add(p1);
            if (holds.Length > 0)
            {
                var p2 = cmd.CreateParameter();
                p2.ParameterName = "@holds";
                p2.Value = holds;
                cmd.Parameters.Add(p2);
            }
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        // Emit an audit event for the retention action
        _auditLogger.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Admin",
                Action = "AuditRetention",
                Outcome = "Success"
            },
            new
            {
                cutoff,
                redacted = _opts.RedactInsteadOfDelete,
                affected,
                holds = holds.Length
            },
            new[] { "cutoff", "redacted", "affected", "holds" }
        );
    } // End of Method RunOnceAsync
} // End of Class AuditRetentionWorker

// Factory abstraction to create database connections for audit retention operations
internal interface IAuditDbConnectionFactory
{
    Task<DbConnection> CreateAsync(CancellationToken ct);
} // End of Interface IAuditDbConnectionFactory

internal sealed class NpgsqlAuditDbConnectionFactory(IOptions<AuditOptions> options)
    : IAuditDbConnectionFactory
{
    private readonly AuditOptions _opts = options.Value;

    public Task<DbConnection> CreateAsync(CancellationToken ct)
    {
        // Return a closed connection; caller will open it
        return Task.FromResult<DbConnection>(new NpgsqlConnection(_opts.ConnectionString));
    } // End of Method CreateAsync
} // End of Class NpgsqlAuditDbConnectionFactory
