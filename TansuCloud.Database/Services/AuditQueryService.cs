// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Database.Services;

public interface IAuditQueryService
{
    Task<QueryResult> QueryAsync(AuditQuery input, CancellationToken ct);
}

public sealed record AuditQuery(
    DateTimeOffset startUtc,
    DateTimeOffset endUtc,
    int pageSize,
    string? tenantId = null,
    string? subject = null,
    string? category = null,
    string? action = null,
    string? service = null,
    string? outcome = null,
    string? correlationId = null,
    string? pageToken = null,
    bool impersonationOnly = false
);

public sealed record AuditItem(
    Guid Id,
    DateTimeOffset WhenUtc,
    string Service,
    string Environment,
    string Version,
    string TenantId,
    string Subject,
    string Action,
    string Category,
    string RouteTemplate,
    string CorrelationId,
    string TraceId,
    string SpanId,
    string? ClientIpHash,
    string? UserAgent,
    string? Outcome,
    string? ReasonCode,
    JsonDocument? Details,
    string? ImpersonatedBy,
    string? SourceHost
);

public sealed record QueryResult(IReadOnlyList<AuditItem> Items, string? NextPageToken);

internal sealed class AuditQueryService(IOptions<AuditOptions> options) : IAuditQueryService
{
    private readonly AuditOptions _opts = options.Value;

    public async Task<QueryResult> QueryAsync(AuditQuery q, CancellationToken ct)
    {
        if (q.pageSize <= 0 || q.pageSize > 200)
            throw new ArgumentOutOfRangeException(nameof(q.pageSize), "pageSize must be 1..200");
        if (q.endUtc <= q.startUtc)
            throw new ArgumentException("endUtc must be greater than startUtc");

        // Keyset pagination token format: base64("whenTicks:id")
        long? afterTicks = null; Guid? afterId = null;
        if (!string.IsNullOrWhiteSpace(q.pageToken))
        {
            try
            {
                var bytes = Convert.FromBase64String(q.pageToken);
                var s = Encoding.UTF8.GetString(bytes);
                var parts = s.Split(':');
                if (parts.Length == 2 && long.TryParse(parts[0], out var t) && Guid.TryParse(parts[1], out var gid))
                {
                    afterTicks = t; afterId = gid;
                }
            }
            catch { }
        }

        var sql = new StringBuilder();
        sql.Append($"SELECT id, when_utc, service, environment, version, tenant_id, subject, action, category, route_template, correlation_id, trace_id, span_id, client_ip_hash, user_agent, outcome, reason_code, details, impersonated_by, source_host FROM {_opts.Table} WHERE when_utc >= @start AND when_utc <= @end");
        if (!string.IsNullOrWhiteSpace(q.tenantId)) sql.Append(" AND tenant_id = @tenant");
        if (!string.IsNullOrWhiteSpace(q.subject)) sql.Append(" AND subject = @subject");
        if (!string.IsNullOrWhiteSpace(q.category)) sql.Append(" AND category = @category");
        if (!string.IsNullOrWhiteSpace(q.action)) sql.Append(" AND action = @action");
        if (!string.IsNullOrWhiteSpace(q.service)) sql.Append(" AND service = @service");
        if (!string.IsNullOrWhiteSpace(q.outcome)) sql.Append(" AND outcome = @outcome");
        if (!string.IsNullOrWhiteSpace(q.correlationId)) sql.Append(" AND correlation_id = @corr");
        if (q.impersonationOnly) sql.Append(" AND impersonated_by IS NOT NULL");
        if (afterTicks.HasValue && afterId.HasValue)
        {
            // keyset: order by when desc, id desc; continue strictly after the last seen row
            sql.Append(" AND ( (extract(epoch from when_utc)*10000000)::bigint < @afterTicks OR ( (extract(epoch from when_utc)*10000000)::bigint = @afterTicks AND id < @afterId ) )");
        }
        sql.Append(" ORDER BY when_utc DESC, id DESC LIMIT @take");

        var items = new List<AuditItem>(q.pageSize);
        await using var conn = new NpgsqlConnection(_opts.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
        // Parameters
        cmd.Parameters.AddWithValue("@start", q.startUtc);
        cmd.Parameters.AddWithValue("@end", q.endUtc);
        cmd.Parameters.AddWithValue("@take", q.pageSize + 1); // fetch one extra for next-token detection
        if (!string.IsNullOrWhiteSpace(q.tenantId)) cmd.Parameters.AddWithValue("@tenant", q.tenantId!);
        if (!string.IsNullOrWhiteSpace(q.subject)) cmd.Parameters.AddWithValue("@subject", q.subject!);
        if (!string.IsNullOrWhiteSpace(q.category)) cmd.Parameters.AddWithValue("@category", q.category!);
        if (!string.IsNullOrWhiteSpace(q.action)) cmd.Parameters.AddWithValue("@action", q.action!);
        if (!string.IsNullOrWhiteSpace(q.service)) cmd.Parameters.AddWithValue("@service", q.service!);
        if (!string.IsNullOrWhiteSpace(q.outcome)) cmd.Parameters.AddWithValue("@outcome", q.outcome!);
        if (!string.IsNullOrWhiteSpace(q.correlationId)) cmd.Parameters.AddWithValue("@corr", q.correlationId!);
        if (afterTicks.HasValue && afterId.HasValue)
        {
            cmd.Parameters.AddWithValue("@afterTicks", afterTicks!.Value);
            cmd.Parameters.AddWithValue("@afterId", afterId!.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var whenUtc = reader.GetFieldValue<DateTimeOffset>(1);
            var service = reader.GetString(2);
            var environment = reader.GetString(3);
            var version = reader.GetString(4);
            var tenant = reader.GetString(5);
            var subject = reader.GetString(6);
            var action = reader.GetString(7);
            var category = reader.GetString(8);
            var route = reader.GetString(9);
            var corr = reader.GetString(10);
            var trace = reader.GetString(11);
            var span = reader.GetString(12);
            var ip = reader.IsDBNull(13) ? null : reader.GetString(13);
            var ua = reader.IsDBNull(14) ? null : reader.GetString(14);
            var outcome = reader.IsDBNull(15) ? null : reader.GetString(15);
            var reason = reader.IsDBNull(16) ? null : reader.GetString(16);
            JsonDocument? details = null;
            if (!reader.IsDBNull(17))
            {
                // details stored as jsonb; read as text and parse
                var json = reader.GetString(17);
                details = JsonDocument.Parse(json);
            }
            var impBy = reader.IsDBNull(18) ? null : reader.GetString(18);
            var srcHost = reader.IsDBNull(19) ? null : reader.GetString(19);
            items.Add(new AuditItem(id, whenUtc, service, environment, version, tenant, subject, action, category, route, corr, trace, span, ip, ua, outcome, reason, details, impBy, srcHost));
        }

        string? nextToken = null;
        if (items.Count > q.pageSize)
        {
            var last = items[q.pageSize - 1];
            var ticks = last.WhenUtc.UtcDateTime.Ticks;
            nextToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}:{last.Id}"));
            // trim to pageSize
            items.RemoveRange(q.pageSize, items.Count - q.pageSize);
        }

        return new QueryResult(items, nextToken);
    } // End of Method QueryAsync
} // End of Class AuditQueryService
