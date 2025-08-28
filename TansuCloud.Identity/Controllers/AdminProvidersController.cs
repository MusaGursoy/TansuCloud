// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Identity.Data;
using TansuCloud.Identity.Infrastructure.External;
using TansuCloud.Identity.Infrastructure.Security;

namespace TansuCloud.Identity.Controllers;

[ApiController]
[Route("admin/providers")]
[Authorize(Roles = "Admin")]
public sealed class AdminProvidersController(AppDbContext db, ISecurityAuditLogger audit)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ExternalProviderSetting>>> List(
        [FromQuery] string? tenantId = null,
        [FromQuery] int take = 50
    )
    {
        var q = db.ExternalProviderSettings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(tenantId))
            q = q.Where(x => x.TenantId == tenantId);
        var items = await q.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();
        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ExternalProviderSetting>> Create(
        [FromBody] ExternalProviderSetting input,
        CancellationToken ct
    )
    {
        input.Id = 0;
        input.CreatedAt = DateTimeOffset.UtcNow;
        input.UpdatedAt = null;
        await db.ExternalProviderSettings.AddAsync(input, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(
            "ExternalProviderCreate",
            details: $"tenant={input.TenantId}, provider={input.Provider}"
        );
        return CreatedAtAction(nameof(GetById), new { id = input.Id }, input);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ExternalProviderSetting>> GetById(long id)
    {
        var item = await db.ExternalProviderSettings.FindAsync(id);
        if (item == null)
            return NotFound();
        return Ok(item);
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult> Update(
        long id,
        [FromBody] ExternalProviderSetting input,
        CancellationToken ct
    )
    {
        var item = await db.ExternalProviderSettings.FindAsync(id);
        if (item == null)
            return NotFound();
        item.DisplayName = input.DisplayName;
        item.Authority = input.Authority;
        item.ClientId = input.ClientId;
        item.ClientSecret = input.ClientSecret;
        item.Scopes = input.Scopes;
        item.Enabled = input.Enabled;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(
            "ExternalProviderUpdate",
            details: $"tenant={item.TenantId}, provider={item.Provider}"
        );
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    public async Task<ActionResult> Delete(long id, CancellationToken ct)
    {
        var item = await db.ExternalProviderSettings.FindAsync(id);
        if (item == null)
            return NotFound();
        db.ExternalProviderSettings.Remove(item);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(
            "ExternalProviderDelete",
            details: $"tenant={item.TenantId}, provider={item.Provider}"
        );
        return NoContent();
    }
}
