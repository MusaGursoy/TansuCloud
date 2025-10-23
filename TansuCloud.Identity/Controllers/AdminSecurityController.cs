// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Data.Entities;
using TansuCloud.Identity.Infrastructure.Security;

namespace TansuCloud.Identity.Controllers;

[ApiController]
[Route("admin/security/events")]
[Authorize(
    Roles = "Admin",
    AuthenticationSchemes = AuthenticationSchemeConstants.AdminCookieAndBearer
)]
public sealed class AdminSecurityController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SecurityEvent>>> List(
        [FromQuery] string? tenantId = null,
        [FromQuery] string? userId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100
    )
    {
        var q = db.SecurityEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(tenantId))
            q = q.Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(userId))
            q = q.Where(x => x.UserId == userId);
        var items = await q.OrderByDescending(x => x.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync();
        return Ok(items);
    }
}
