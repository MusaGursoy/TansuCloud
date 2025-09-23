// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Observability.Auditing;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/presign")]
public sealed class PresignController(IPresignService presign, ITenantContext tenant, IAuditLogger audit)
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
        // Audit (Storage:PresignCreate)
        audit.TryEnqueueRedacted(
            new AuditEvent { Action = "PresignCreate", Category = "Storage", Outcome = "Success" },
            new { Method = method, Bucket = req.Bucket, Key = req.Key, MaxBytes = req.MaxBytes, ContentType = req.ContentType },
            new[] { "Method", "Bucket", "Key", "MaxBytes", "ContentType" }
        );
        return Ok(new { url, expires = exp });
    }

    public sealed record TransformPresignRequest(
        string Bucket,
        string Key,
        int? Width,
        int? Height,
        string? Format,
        int? Quality,
        int? ExpirySeconds
    );

    [HttpPost("transform")]
    [Authorize(Policy = "storage.write")]
    public IActionResult CreateTransform([FromBody] TransformPresignRequest req)
    {
        // Validate inputs early
        if (req.Width is < 0 || req.Height is < 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Width/Height cannot be negative");

        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (req.ExpirySeconds is > 0 ? req.ExpirySeconds.Value : 15 * 60);
        var sig = presign.CreateTransformSignature(
            tenant.TenantId,
            req.Bucket,
            req.Key,
            req.Width,
            req.Height,
            req.Format,
            req.Quality,
            exp
        );

        var query = new QueryString().Add("exp", exp.ToString()).Add("sig", sig);
        if (req.Width is not null)
            query = query.Add("w", req.Width.Value.ToString());
        if (req.Height is not null)
            query = query.Add("h", req.Height.Value.ToString());
        if (!string.IsNullOrWhiteSpace(req.Format))
            query = query.Add("fmt", req.Format!);
        if (req.Quality is not null)
            query = query.Add("q", req.Quality.Value.ToString());

        var url = $"/storage/api/transform/{Uri.EscapeDataString(req.Bucket)}/{Uri.EscapeDataString(req.Key)}{query}";
        // Audit (Storage:PresignTransform)
        audit.TryEnqueueRedacted(
            new AuditEvent { Action = "PresignTransform", Category = "Storage", Outcome = "Success" },
            new { Bucket = req.Bucket, Key = req.Key, Width = req.Width, Height = req.Height, Format = req.Format, Quality = req.Quality },
            new[] { "Bucket", "Key", "Width", "Height", "Format", "Quality" }
        );
        return Ok(new { url, expires = exp });
    }
} // End of Class PresignController
