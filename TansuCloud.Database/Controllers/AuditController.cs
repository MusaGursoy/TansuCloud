// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Database.Security;
using TansuCloud.Database.Services;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Database.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuditController(
    IAuditQueryService svc,
    ILogger<AuditController> logger,
    IAuditLogger audit
) : ControllerBase
{
    private readonly IAuditQueryService _svc = svc;
    private readonly ILogger<AuditController> _logger = logger;
    private readonly IAuditLogger _audit = audit;

    public sealed record QueryRequest(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int? pageSize,
        string? tenantId,
        string? subject,
        string? category,
        string? action,
        string? service,
        string? outcome,
        string? correlationId,
        string? pageToken,
        bool? impersonationOnly
    );

    [HttpGet]
    [Authorize("db.read")]
    public async Task<ActionResult> Get([FromQuery] QueryRequest req, CancellationToken ct)
    {
        if (req.endUtc == default || req.startUtc == default)
            return Problem(
                title: "startUtc and endUtc are required",
                statusCode: StatusCodes.Status400BadRequest
            );
        if (req.endUtc <= req.startUtc)
            return Problem(
                title: "endUtc must be greater than startUtc",
                statusCode: StatusCodes.Status400BadRequest
            );
        var pageSize = Math.Clamp(req.pageSize ?? 100, 1, 200);

        // RBAC scoping: admin.full can query all tenants; otherwise restrict to explicit tenant from header/claims
        string? effectiveTenant = req.tenantId;
        var isAdmin = User.HasScope("admin.full");
        if (!isAdmin)
        {
            // Derive tenant from header if provided; otherwise reject when tenantId is missing
            var headerTenant = Request.Headers["X-Tansu-Tenant"].ToString();
            if (string.IsNullOrWhiteSpace(effectiveTenant))
                effectiveTenant = headerTenant;
            if (string.IsNullOrWhiteSpace(effectiveTenant))
                return Problem(
                    title: "tenantId is required for non-admin requests",
                    statusCode: StatusCodes.Status400BadRequest
                );
        }

        var query = new AuditQuery(
            req.startUtc,
            req.endUtc,
            pageSize,
            effectiveTenant,
            req.subject,
            req.category,
            req.action,
            req.service,
            req.outcome,
            req.correlationId,
            req.pageToken,
            req.impersonationOnly ?? false
        );

        var result = await _svc.QueryAsync(query, ct);
        return Ok(result);
    } // End of Method Get

    // CSV export — gated to admin.full; enforces a strict upper row limit
    [HttpGet("export/csv")]
    [Authorize] // explicit admin check below
    public async Task<IActionResult> ExportCsv(
        [FromQuery] QueryRequest req,
        [FromQuery] int? limit,
        CancellationToken ct
    )
    {
        if (!User.HasScope("admin.full"))
        {
            _audit.TryEnqueue(
                new AuditEvent
                {
                    Category = "Admin",
                    Action = "AuditExport",
                    Outcome = "Forbidden",
                    ReasonCode = "NotAdmin",
                }
            );
            return Problem(
                title: "Admin scope required (admin.full)",
                statusCode: StatusCodes.Status403Forbidden
            );
        }

        var take = Math.Clamp(limit ?? 10_000, 1, 10_000);
        var items = await CollectAsync(req, take, ct);

        // Build CSV
        var sb = new StringBuilder();
        sb.AppendLine(
            "WhenUtc,TenantId,Subject,Category,Action,Service,Outcome,ReasonCode,CorrelationId,TraceId,SpanId,RouteTemplate,Environment,Version,ClientIpHash,UserAgent,ImpersonatedBy,SourceHost,Details"
        );
        foreach (var it in items)
        {
            // Escape CSV fields with quotes and replace quotes with double quotes per RFC 4180
            static string Csv(string? v) =>
                string.IsNullOrEmpty(v) ? string.Empty : ($"\"{v.Replace("\"", "\"\"")}\"");
            var details = it.Details?.RootElement.GetRawText();
            sb.Append(Csv(it.WhenUtc.ToString("o")))
                .Append(',')
                .Append(Csv(it.TenantId))
                .Append(',')
                .Append(Csv(it.Subject))
                .Append(',')
                .Append(Csv(it.Category))
                .Append(',')
                .Append(Csv(it.Action))
                .Append(',')
                .Append(Csv(it.Service))
                .Append(',')
                .Append(Csv(it.Outcome))
                .Append(',')
                .Append(Csv(it.ReasonCode))
                .Append(',')
                .Append(Csv(it.CorrelationId))
                .Append(',')
                .Append(Csv(it.TraceId))
                .Append(',')
                .Append(Csv(it.SpanId))
                .Append(',')
                .Append(Csv(it.RouteTemplate))
                .Append(',')
                .Append(Csv(it.Environment))
                .Append(',')
                .Append(Csv(it.Version))
                .Append(',')
                .Append(Csv(it.ClientIpHash))
                .Append(',')
                .Append(Csv(it.UserAgent))
                .Append(',')
                .Append(Csv(it.ImpersonatedBy))
                .Append(',')
                .Append(Csv(it.SourceHost))
                .Append(',')
                .Append(Csv(details))
                .AppendLine();
        }

        var fileName = $"audit-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.csv";
        Response.Headers["X-Export-Limit"] = take.ToString();
        Response.Headers["X-Export-Count"] = items.Count.ToString();

        // Audit export action (allowlisted filter summary and count)
        _audit.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Admin",
                Action = "AuditExport",
                Outcome = "Success"
            },
            new
            {
                kind = "csv",
                count = items.Count,
                req.startUtc,
                req.endUtc,
                req.tenantId,
                req.subject,
                req.category,
                req.action,
                req.service,
                req.outcome,
                req.correlationId,
                req.impersonationOnly
            },
            new[]
            {
                "kind",
                "count",
                "startUtc",
                "endUtc",
                "tenantId",
                "subject",
                "category",
                "action",
                "service",
                "outcome",
                "correlationId",
                "impersonationOnly"
            }
        );

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
    } // End of Method ExportCsv

    // JSON export — gated to admin.full; returns an array of items (PII already redacted at write-time)
    [HttpGet("export/json")]
    [Authorize]
    public async Task<IActionResult> ExportJson(
        [FromQuery] QueryRequest req,
        [FromQuery] int? limit,
        CancellationToken ct
    )
    {
        if (!User.HasScope("admin.full"))
        {
            _audit.TryEnqueue(
                new AuditEvent
                {
                    Category = "Admin",
                    Action = "AuditExport",
                    Outcome = "Forbidden",
                    ReasonCode = "NotAdmin",
                }
            );
            return Problem(
                title: "Admin scope required (admin.full)",
                statusCode: StatusCodes.Status403Forbidden
            );
        }

        var take = Math.Clamp(limit ?? 10_000, 1, 10_000);
        var items = await CollectAsync(req, take, ct);

        var payload = items.Select(i => new
        {
            i.Id,
            i.WhenUtc,
            i.Service,
            i.Environment,
            i.Version,
            i.TenantId,
            i.Subject,
            i.Action,
            i.Category,
            i.RouteTemplate,
            i.CorrelationId,
            i.TraceId,
            i.SpanId,
            i.ClientIpHash,
            i.UserAgent,
            i.Outcome,
            i.ReasonCode,
            Details = i.Details?.RootElement, // serialize as JSON
            i.ImpersonatedBy,
            i.SourceHost
        });

        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = false }
        );

        var fileName = $"audit-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json";
        Response.Headers["X-Export-Limit"] = take.ToString();
        Response.Headers["X-Export-Count"] = items.Count.ToString();

        _audit.TryEnqueueRedacted(
            new AuditEvent
            {
                Category = "Admin",
                Action = "AuditExport",
                Outcome = "Success"
            },
            new
            {
                kind = "json",
                count = items.Count,
                req.startUtc,
                req.endUtc,
                req.tenantId,
                req.subject,
                req.category,
                req.action,
                req.service,
                req.outcome,
                req.correlationId,
                req.impersonationOnly
            },
            new[]
            {
                "kind",
                "count",
                "startUtc",
                "endUtc",
                "tenantId",
                "subject",
                "category",
                "action",
                "service",
                "outcome",
                "correlationId",
                "impersonationOnly"
            }
        );

        return File(Encoding.UTF8.GetBytes(json), "application/json", fileName);
    } // End of Method ExportJson

    private async Task<List<AuditItem>> CollectAsync(
        QueryRequest req,
        int take,
        CancellationToken ct
    )
    {
        if (req.endUtc == default || req.startUtc == default)
            throw new ArgumentException("startUtc and endUtc are required");
        if (req.endUtc <= req.startUtc)
            throw new ArgumentException("endUtc must be greater than startUtc");

        // RBAC scoping: admin.full can export all tenants; otherwise restrict to explicit tenant from header/claims
        string? effectiveTenant = req.tenantId;
        var isAdmin = User.HasScope("admin.full");
        if (!isAdmin)
        {
            var headerTenant = Request.Headers["X-Tansu-Tenant"].ToString();
            if (string.IsNullOrWhiteSpace(effectiveTenant))
                effectiveTenant = headerTenant;
            if (string.IsNullOrWhiteSpace(effectiveTenant))
                throw new InvalidOperationException("tenantId is required for non-admin requests");
        }

        var items = new List<AuditItem>(Math.Min(take, 2048));
        string? token = req.pageToken;
        var remaining = take;
        while (remaining > 0)
        {
            var page = Math.Min(200, remaining);
            var q = new AuditQuery(
                req.startUtc,
                req.endUtc,
                page,
                effectiveTenant,
                req.subject,
                req.category,
                req.action,
                req.service,
                req.outcome,
                req.correlationId,
                token,
                req.impersonationOnly ?? false
            );
            var result = await _svc.QueryAsync(q, ct);
            if (result.Items.Count == 0)
                break;
            items.AddRange(result.Items);
            token = result.NextPageToken;
            if (string.IsNullOrWhiteSpace(token))
                break;
            remaining -= result.Items.Count;
        }
        return items;
    } // End of Method CollectAsync
} // End of Class AuditController
