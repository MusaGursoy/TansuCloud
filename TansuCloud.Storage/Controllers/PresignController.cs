// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/presign")]
public sealed class PresignController(IPresignService presign, ITenantContext tenant)
    : ControllerBase
{
    public sealed record PresignRequest(
        string Method,
        string Bucket,
        string Key,
        int? ExpirySeconds,
        long? MaxBytes,
        string? ContentType
    );

    [HttpPost]
    [Authorize(Policy = "storage.write")]
    public IActionResult Create([FromBody] PresignRequest req)
    {
        var method = req.Method?.ToUpperInvariant();
        if (method is not ("GET" or "PUT"))
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Method must be GET or PUT"
            );
        var exp =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            + (req.ExpirySeconds is > 0 ? req.ExpirySeconds.Value : 15 * 60);
        var sig = presign.CreateSignature(
            tenant.TenantId,
            method!,
            req.Bucket,
            req.Key,
            exp,
            req.MaxBytes,
            req.ContentType
        );
        var query = new QueryString().Add("exp", exp.ToString()).Add("sig", sig);
        if (req.MaxBytes is not null)
            query = query.Add("max", req.MaxBytes.Value.ToString());
        if (!string.IsNullOrEmpty(req.ContentType))
            query = query.Add("ct", req.ContentType);
        var url =
            $"/storage/api/objects/{Uri.EscapeDataString(req.Bucket)}/{Uri.EscapeDataString(req.Key)}{query}";
        return Ok(new { url, expires = exp });
    }
} // End of Class PresignController
