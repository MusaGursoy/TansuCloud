// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/multipart")]
public sealed class MultipartController(
    IMultipartStorage multipart,
    ITenantContext tenant,
    IPresignService presign,
    IOptions<StorageOptions> options,
    IQuotaService quotas
) : ControllerBase
{
    // POST /api/multipart/{bucket}/{*key}/initiate
    [HttpPost("{bucket}/initiate/{*key}")]
    [AllowAnonymous] // supports presigned initiate; authenticated calls must include storage.write
    public async Task<IActionResult> Initiate(string bucket, string key, CancellationToken ct)
    {
        // Normalize key from route (decode % escapes like %2F) to ensure correct filesystem pathing
        key = Uri.UnescapeDataString(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "MP_INIT"));
        // allow anonymous via presign
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("POST", bucket, key, out var problem))
                return problem!;
        }
        else
        {
            if (
                !User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" storage.write ")
                ) || !User.Claims.Any(c => c.Type == "aud" && c.Value.Contains("tansu.storage"))
            )
            {
                return Forbid();
            }
        }
        var res = await multipart.InitiateAsync(bucket, key, ct);
        return Ok(new { res.UploadId });
    }

    // PUT /api/multipart/{bucket}/{*key}/parts/{partNumber}?uploadId=xyz
    [HttpPut("{bucket}/parts/{partNumber:int}/{*key}")]
    [AllowAnonymous] // supports presigned part upload; authenticated calls must include storage.write
    public async Task<IActionResult> UploadPart(
        string bucket,
        string key,
        int partNumber,
        [FromQuery] string uploadId,
        CancellationToken ct
    )
    {
        // Normalize key from route (decode % escapes like %2F)
        key = Uri.UnescapeDataString(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "MP_PART"));
        if (string.IsNullOrWhiteSpace(uploadId))
            return Problem(statusCode: 400, detail: "uploadId required");
        // presign for anonymous
        long? presignMax = null;
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("PUT", bucket, key, out var problem))
                return problem!;
            if (long.TryParse(Request.Query["max"].ToString(), out var maxParsed))
                presignMax = maxParsed;
        }
        else
        {
            if (
                !User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" storage.write ")
                ) || !User.Claims.Any(c => c.Type == "aud" && c.Value.Contains("tansu.storage"))
            )
            {
                return Forbid();
            }
        }
        if (!Request.ContentLength.HasValue)
            return Problem(statusCode: 411, detail: "Content-Length required");
        var min = options.Value.MultipartMinPartSizeBytes;
        var maxPart = options.Value.MultipartMaxPartSizeBytes;
        // allow short last parts at this stage; strict check can be applied on Complete with known parts
        if (Request.ContentLength.Value < min)
        {
            // tolerate if client marks last part; since we don't know here, return 400 with hint
            Response.Headers.Append(
                "X-Tansu-Hint",
                "Part too small; only final part may be smaller than minimum"
            );
        }
        // enforce max part size if configured
        if (maxPart > 0 && Request.ContentLength.Value > maxPart)
            return Problem(statusCode: 413, detail: $"Part exceeds maximum size {maxPart} bytes");
        // presign max enforcement if present
        if (presignMax.HasValue && Request.ContentLength.Value > presignMax.Value)
            return Problem(statusCode: 413, detail: "Presigned max exceeded");
        // detailed quota check on incoming size (best effort)
        var eval = await quotas.EvaluateAsync(Request.ContentLength.Value, ct);
        if (eval.Exceeded)
        {
            return Problem(
                statusCode: 413,
                title: "Quota exceeded",
                detail: eval.Reason,
                extensions: new Dictionary<string, object?>
                {
                    ["maxTotalBytes"] = eval.MaxTotalBytes,
                    ["maxObjectSizeBytes"] = eval.MaxObjectSizeBytes,
                    ["maxObjectCount"] = eval.MaxObjectCount,
                    ["currentTotalBytes"] = eval.CurrentTotalBytes,
                    ["currentObjectCount"] = eval.CurrentObjectCount,
                    ["incomingBytes"] = eval.IncomingBytes
                }
            );
        }
        var part = await multipart.UploadPartAsync(
            bucket,
            key,
            uploadId,
            partNumber,
            Request.Body,
            ct
        );
        if (Request.ContentLength is long len)
            StorageMetrics.IngressBytes.Add(
                len,
                new("tenant", tenant.TenantId),
                new("bucket", bucket)
            );
        Response.Headers.ETag = part.ETag;
        return Ok(
            new
            {
                part.PartNumber,
                part.ETag,
                part.Length
            }
        );
    }

    public sealed record CompleteRequest(List<int> Parts);

    // POST /api/multipart/{bucket}/{*key}/complete?uploadId=xyz
    [HttpPost("{bucket}/complete/{*key}")]
    [AllowAnonymous] // supports presigned completion; authenticated calls must include storage.write
    public async Task<IActionResult> Complete(
        string bucket,
        string key,
        [FromQuery] string uploadId,
        [FromBody] CompleteRequest body,
        CancellationToken ct
    )
    {
        // Normalize key from route (decode % escapes like %2F)
        key = Uri.UnescapeDataString(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "MP_COMPLETE"));
        if (string.IsNullOrWhiteSpace(uploadId))
            return Problem(statusCode: 400, detail: "uploadId required");
        if (body?.Parts is null || body.Parts.Count == 0)
            return Problem(statusCode: 400, detail: "Parts required");
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("POST", bucket, key, out var problem))
                return problem!;
        }
        else
        {
            if (
                !User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" storage.write ")
                ) || !User.Claims.Any(c => c.Type == "aud" && c.Value.Contains("tansu.storage"))
            )
            {
                return Forbid();
            }
        }
        // enforce min part size except for the last one
        var partsInfo = await multipart.GetPartsAsync(bucket, key, uploadId, ct);
        var dict = partsInfo.ToDictionary(p => p.PartNumber, p => p.Length);
        var min = options.Value.MultipartMinPartSizeBytes;
        var maxPart = options.Value.MultipartMaxPartSizeBytes;
        // Reject duplicate part numbers in the provided list
        var seen = new HashSet<int>();
        for (int i = 0; i < body.Parts.Count; i++)
        {
            var pn = body.Parts[i];
            if (!seen.Add(pn))
                return Problem(statusCode: 400, detail: $"Duplicate part number {pn}");
        }
        for (int i = 0; i < body.Parts.Count; i++)
        {
            var n = body.Parts[i];
            if (!dict.TryGetValue(n, out var len))
                return Problem(statusCode: 400, detail: $"Missing uploaded part {n}");
            var isLast = i == body.Parts.Count - 1;
            if (!isLast && len < min)
                return Problem(statusCode: 400, detail: $"Part {n} below minimum size {min}");
            if (maxPart > 0 && len > maxPart)
                return Problem(statusCode: 413, detail: $"Part {n} exceeds maximum size {maxPart} bytes");
        }

        // compute total incoming size to enforce object size/total quotas
        long totalIncoming = body.Parts.Sum(n => dict[n]);
        var eval = await quotas.EvaluateAsync(totalIncoming, ct);
        if (eval.Exceeded)
        {
            return Problem(
                statusCode: 413,
                title: "Quota exceeded",
                detail: eval.Reason,
                extensions: new Dictionary<string, object?>
                {
                    ["maxTotalBytes"] = eval.MaxTotalBytes,
                    ["maxObjectSizeBytes"] = eval.MaxObjectSizeBytes,
                    ["maxObjectCount"] = eval.MaxObjectCount,
                    ["currentTotalBytes"] = eval.CurrentTotalBytes,
                    ["currentObjectCount"] = eval.CurrentObjectCount,
                    ["incomingBytes"] = eval.IncomingBytes
                }
            );
        }
        var etag = await multipart.CompleteAsync(bucket, key, uploadId, body.Parts, ct);
        Response.Headers.ETag = etag;
        return Ok(
            new
            {
                bucket,
                key,
                etag
            }
        );
    }

    // DELETE /api/multipart/{bucket}/{*key}/abort?uploadId=xyz
    [HttpDelete("{bucket}/abort/{*key}")]
    [AllowAnonymous] // supports presigned abort; authenticated calls must include storage.write
    public async Task<IActionResult> Abort(
        string bucket,
        string key,
        [FromQuery] string uploadId,
        CancellationToken ct
    )
    {
        // Normalize key from route (decode % escapes like %2F)
        key = Uri.UnescapeDataString(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "MP_ABORT"));
        if (string.IsNullOrWhiteSpace(uploadId))
            return Problem(statusCode: 400, detail: "uploadId required");
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("DELETE", bucket, key, out var problem))
                return problem!;
        }
        else
        {
            if (
                !User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" storage.write ")
                ) || !User.Claims.Any(c => c.Type == "aud" && c.Value.Contains("tansu.storage"))
            )
            {
                return Forbid();
            }
        }
        await multipart.AbortAsync(bucket, key, uploadId, ct);
        return NoContent();
    }

    private bool TryValidatePresign(
        string method,
        string bucket,
        string key,
        out IActionResult? problem
    )
    {
        problem = null;
        var sig = Request.Query["sig"].ToString();
        var expStr = Request.Query["exp"].ToString();
        var maxStr = Request.Query["max"].ToString();
        var ctStr = Request.Query["ct"].ToString();
        // Require tenant header for presigned operations
        var tenantHeader = Request.Headers["X-Tansu-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(tenantHeader))
        {
            problem = Challenge();
            return false;
        }
        if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(expStr))
        {
            problem = Challenge();
            return false;
        }
        if (!long.TryParse(expStr, out var exp))
        {
            problem = Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid exp");
            return false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exp < now)
        {
            problem = Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Presign expired"
            );
            return false;
        }
        long? max = null;
        if (!string.IsNullOrEmpty(maxStr) && long.TryParse(maxStr, out var maxParsed))
            max = maxParsed;
        var tenantId = tenant.TenantId;
        if (
            !presign.Validate(
                tenantId,
                method,
                bucket,
                key,
                exp,
                max,
                string.IsNullOrEmpty(ctStr) ? null : ctStr,
                sig
            )
        )
        {
            problem = Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Invalid signature"
            );
            return false;
        }
        return true;
    }
} // End of Class MultipartController
