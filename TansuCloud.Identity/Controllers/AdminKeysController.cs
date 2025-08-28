// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TansuCloud.Identity.Infrastructure.Options;
using TansuCloud.Identity.Infrastructure.Security;

namespace TansuCloud.Identity.Controllers;

[ApiController]
[Route("admin/keys")] 
[Authorize(Roles = "Admin")]
public sealed class AdminKeysController(
    IOptions<IdentityPolicyOptions> options,
    IKeyRotationCoordinator rotation
) : ControllerBase
{
    [HttpGet("policies")]
    public ActionResult<IdentityPolicyOptions> GetPolicies() => Ok(options.Value);

    [HttpPost("rotate-now")]
    public async Task<ActionResult> RotateNow(CancellationToken ct)
    {
        await rotation.TriggerAsync(ct);
        return Accepted(new { message = "Rotation triggered (stub)." });
    }
}
