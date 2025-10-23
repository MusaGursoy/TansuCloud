// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Data.Entities;

namespace TansuCloud.Identity.Infrastructure.Security;

public interface ISecurityAuditLogger
{
    Task LogAsync(
        string type,
        string? userId = null,
        string? actorId = null,
        string? details = null,
        CancellationToken ct = default
    );
}

internal sealed class SecurityAuditLogger(AppDbContext db, IHttpContextAccessor httpContextAccessor)
    : ISecurityAuditLogger
{
    public async Task LogAsync(
        string type,
        string? userId = null,
        string? actorId = null,
        string? details = null,
        CancellationToken ct = default
    )
    {
        var tid = httpContextAccessor.HttpContext?.Request.Headers["X-Tansu-Tenant"].ToString();
        var ev = new SecurityEvent
        {
            Type = type,
            UserId = userId,
            ActorId = actorId,
            TenantId = string.IsNullOrWhiteSpace(tid) ? null : tid,
            Details = details
        };
        await db.SecurityEvents.AddAsync(ev, ct);
        await db.SaveChangesAsync(ct);
    }
} // End of Class SecurityAuditLogger
