// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Net.Http.Headers;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/objects")]
public sealed class ObjectsController(
    IObjectStorage storage,
    ITenantContext tenant,
    IPresignService presign,
    IQuotaService quotas,
    IAntivirusScanner av,
    ILogger<ObjectsController> logger,
    ITenantCacheVersion versions,
    Microsoft.Extensions.Caching.Hybrid.HybridCache? cache = null
) : ControllerBase
{
    private string CacheKey(params string[] parts)
    {
        var v = versions.Get(tenant.TenantId);
        return $"t:{tenant.TenantId}:v{v}:storage:" + string.Join(':', parts);
    } // End of Method CacheKey

    // GET /api/objects?bucket=...&prefix=...
    [HttpGet]
    [Authorize(Policy = "storage.read")]
    public async Task<IActionResult> List(
        [FromQuery] string bucket,
        [FromQuery] string? prefix,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "bucket required");
        // Cached listing (tenant/version-aware)
        var listKey = CacheKey("list", bucket, prefix ?? string.Empty);
        if (cache is not null)
        {
            StorageMetrics.CacheAttempts.Add(
                1,
                new System.Collections.Generic.KeyValuePair<string, object?>("op", "list")
            );
            var miss = false;
            var cached = await cache.GetOrCreateAsync(
                listKey,
                async token =>
                {
                    // miss path (factory executed)
                    miss = true;
                    StorageMetrics.CacheMisses.Add(
                        1,
                        new System.Collections.Generic.KeyValuePair<string, object?>("op", "list")
                    );
                    var list = await storage.ListObjectsAsync(bucket, prefix, ct);
                    var dto = list.Select(o => new
                        {
                            o.Key,
                            o.ETag,
                            o.Length,
                            o.ContentType,
                            lastModified = o.LastModified
                        })
                        .ToArray();
                    return dto as object;
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) }
            );
            if (!miss)
                StorageMetrics.CacheHits.Add(
                    1,
                    new System.Collections.Generic.KeyValuePair<string, object?>("op", "list")
                );
            return Ok(cached);
        }

        var list = await storage.ListObjectsAsync(bucket, prefix, ct);
        var dto2 = list.Select(o => new
        {
            o.Key,
            o.ETag,
            o.Length,
            o.ContentType,
            lastModified = o.LastModified
        });
        return Ok(dto2);
    }

    // PUT /api/objects/{bucket}/{*key}
    [HttpPut("{bucket}/{*key}")]
    [AllowAnonymous] // supports presigned anonymous PUT; authenticated calls must include storage.write
    public async Task<IActionResult> Put(string bucket, string key, CancellationToken ct)
    {
        // Normalize key from route (decode % escapes like %2F)
        key = RouteKeyNormalizer.Normalize(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "PUT"));
        // support presigned anonymous PUT via query when no auth context
        long? presignMax = null;
        string? presignCt = null;
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("PUT", bucket, key, out var problem))
                return problem!;
            // capture presigned max if provided
            if (long.TryParse(Request.Query["max"].ToString(), out var maxParsed))
                presignMax = maxParsed;
            var ctQ = Request.Query["ct"].ToString();
            if (!string.IsNullOrWhiteSpace(ctQ))
                presignCt = ctQ;
        }
        // else: authenticated requests are authorized by API-level policies
        else
        {
            // Enforce write scope for authenticated direct PUTs
            var hasWrite =
                User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" storage.write ")
                )
                || User.Claims.Any(c =>
                    c.Type == "scope" && ($" {c.Value} ").Contains(" admin.full ")
                );
            if (!hasWrite)
                return Forbid();
        }
        if (!Request.ContentLength.HasValue)
            return Problem(
                statusCode: StatusCodes.Status411LengthRequired,
                detail: "Content-Length required"
            );
        // presign max enforcement if provided
        if (presignMax.HasValue && Request.ContentLength!.Value > presignMax.Value)
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: "Presigned max exceeded"
            );

        // presigned content-type enforcement if provided
        var contentType = Request.ContentType ?? "application/octet-stream";
        // Compare only the media type portion (ignore parameters like charset)
        static string NormalizeMediaType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            if (MediaTypeHeaderValue.TryParse(value, out var parsed) && parsed.MediaType.HasValue)
                return parsed.MediaType.Value.ToString();
            var semi = value.IndexOf(';');
            return (semi >= 0 ? value[..semi] : value).Trim();
        }
        if (presignCt is not null)
        {
            var reqType = NormalizeMediaType(contentType);
            var expType = NormalizeMediaType(presignCt);
            if (!string.Equals(reqType, expType, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Presigned content-type mismatch for {Bucket}/{Key}: expected {Expected}, got {Actual}",
                    bucket,
                    key,
                    expType,
                    reqType
                );
                return Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    detail: "Content-Type mismatch"
                );
            }
        }

        // detailed quota check
        var eval = await quotas.EvaluateAsync(Request.ContentLength!.Value, ct);
        if (eval.Exceeded)
        {
            logger.LogWarning(
                "Quota exceeded for tenant {Tenant} on PUT {Bucket}/{Key}: {Reason}. Incoming={Incoming} CurrentBytes={CurrentBytes} MaxTotal={MaxTotal}",
                tenant.TenantId,
                bucket,
                key,
                eval.Reason,
                eval.IncomingBytes,
                eval.CurrentTotalBytes,
                eval.MaxTotalBytes
            );
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
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
        await storage.PutObjectAsync(bucket, key, Request.Body, contentType, null, ct);
        var head = await storage.HeadObjectAsync(bucket, key, ct);
        if (Request.ContentLength is long len)
            StorageMetrics.IngressBytes.Add(
                len,
                new("tenant", tenant.TenantId),
                new("bucket", bucket)
            );
        // optional AV scan (no-op currently)
        _ = av.ScanObjectAsync(bucket, key, ct);
        Response.Headers.ETag = head?.ETag;
        try
        {
            _ = versions.Increment(tenant.TenantId);
        }
        catch { }
        return Created(
            $"/api/objects/{bucket}/{key}",
            new
            {
                bucket,
                key,
                etag = head?.ETag,
                length = head?.Length,
                contentType = head?.ContentType
            }
        );
    }

    // HEAD /api/objects/{bucket}/{*key}
    [HttpHead("{bucket}/{*key}")]
    [Authorize(Policy = "storage.read")]
    public async Task<IActionResult> Head(string bucket, string key, CancellationToken ct)
    {
        // Normalize key from route (decode % escapes like %2F)
        key = RouteKeyNormalizer.Normalize(key);
        // Try cache for HEAD metadata
        var headKey = CacheKey("head", bucket, key);
        if (cache is not null)
        {
            StorageMetrics.CacheAttempts.Add(
                1,
                new System.Collections.Generic.KeyValuePair<string, object?>("op", "head")
            );
            var miss = false;
            var cached = await cache.GetOrCreateAsync(
                headKey,
                async token =>
                {
                    miss = true;
                    StorageMetrics.CacheMisses.Add(
                        1,
                        new System.Collections.Generic.KeyValuePair<string, object?>("op", "head")
                    );
                    return await storage.HeadObjectAsync(bucket, key, ct);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) }
            );
            if (cached is null)
                return NotFound();
            if (!miss)
                StorageMetrics.CacheHits.Add(
                    1,
                    new System.Collections.Generic.KeyValuePair<string, object?>("op", "head")
                );
            Response.Headers.ETag = cached.ETag;
            Response.Headers["Last-Modified"] = cached.LastModified.ToString("R");
            Response.Headers["Content-Length"] = cached.Length.ToString();
            Response.Headers["Content-Type"] = cached.ContentType;
            return Ok();
        }

        var meta = await storage.HeadObjectAsync(bucket, key, ct);
        if (meta is null)
            return NotFound();
        Response.Headers.ETag = meta.ETag;
        Response.Headers["Last-Modified"] = meta.LastModified.ToString("R");
        Response.Headers["Content-Length"] = meta.Length.ToString();
        Response.Headers["Content-Type"] = meta.ContentType;
        return Ok();
    }

    // GET /api/objects/{bucket}/{*key}
    [HttpGet("{bucket}/{*key}")]
    [AllowAnonymous] // supports presigned anonymous GET; authenticated calls must include storage.read
    public async Task<IActionResult> Get(string bucket, string key, CancellationToken ct)
    {
        // Normalize key from route (decode % escapes like %2F)
        key = RouteKeyNormalizer.Normalize(key);
        StorageMetrics.Requests.Add(1, new("tenant", tenant.TenantId), new("op", "GET"));
        // support presigned anonymous GET via query when no auth context
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            if (!TryValidatePresign("GET", bucket, key, out var problem))
                return problem!;
        }
        // else: authenticated requests are authorized by API-level policies
        var range = Request.Headers["Range"].ToString();
        var head = await storage.HeadObjectAsync(bucket, key, ct);
        if (head is null)
            return NotFound();

        // Task 14: Ensure caches vary by encoding regardless of whether compression applies
        const string varyHeader = "Vary";
        const string acceptEncoding = "Accept-Encoding";
        if (Response.Headers.TryGetValue(varyHeader, out var varyVal))
        {
            if (!varyVal.ToString().Contains(acceptEncoding, StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers.Append(varyHeader, acceptEncoding);
            }
        }
        else
        {
            Response.Headers[varyHeader] = acceptEncoding;
        }

        // Conditional requests
        if (Request.Headers.TryGetValue("If-None-Match", out var inm))
        {
            // Accept both strong and weak forms; weak comparison is allowed for If-None-Match
            var inmRaw = inm.ToString();
            try
            {
                if (
                    System.Net.Http.Headers.EntityTagHeaderValue.TryParse(
                        head.ETag,
                        out var current
                    )
                )
                {
                    // If multiple values are provided, any match triggers 304
                    var candidates = inmRaw.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    foreach (var v in candidates)
                    {
                        if (
                            System.Net.Http.Headers.EntityTagHeaderValue.TryParse(v, out var reqTag)
                        )
                        {
                            if (string.Equals(reqTag.Tag, current.Tag, StringComparison.Ordinal))
                            {
                                Response.Headers.ETag = head.ETag;
                                return StatusCode(StatusCodes.Status304NotModified);
                            }
                        }
                        else if (string.Equals(v, head.ETag, StringComparison.Ordinal))
                        {
                            Response.Headers.ETag = head.ETag;
                            return StatusCode(StatusCodes.Status304NotModified);
                        }
                    }
                }
                else if (string.Equals(inmRaw, head.ETag, StringComparison.Ordinal))
                {
                    Response.Headers.ETag = head.ETag;
                    return StatusCode(StatusCodes.Status304NotModified);
                }
            }
            catch { }
        }
        if (Request.Headers.TryGetValue("If-Match", out var im) && im.ToString() != head.ETag)
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        if (!string.IsNullOrEmpty(range))
        {
            // format: bytes=start-end
            if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                // Ignore invalid unit per test expectation and return full content
                var foundFull = await storage.GetObjectAsync(bucket, key, ct);
                if (foundFull is null)
                    return NotFound();
                Response.Headers.ETag = foundFull.Value.Info.ETag;
                StorageMetrics.EgressBytes.Add(
                    foundFull.Value.Info.Length,
                    new("tenant", tenant.TenantId),
                    new("bucket", bucket)
                );
                return new FileStreamResult(
                    foundFull.Value.Content,
                    foundFull.Value.Info.ContentType
                );
            }
            var parts = range[6..].Split('-', 2);
            if (!long.TryParse(parts[0], out var start))
                return Problem(
                    statusCode: StatusCodes.Status416RangeNotSatisfiable,
                    detail: "Invalid range start"
                );
            long end = head.Length - 1;
            if (parts.Length == 2 && long.TryParse(parts[1], out var parsedEnd))
                end = parsedEnd;
            // Clamp end to last byte
            if (end >= head.Length)
                end = head.Length - 1;
            // Handle zero-length object: if start == 0 and end becomes -1, return 200 OK empty body
            if (head.Length == 0)
            {
                var foundFull = await storage.GetObjectAsync(bucket, key, ct);
                if (foundFull is null)
                    return NotFound();
                Response.Headers.ETag = foundFull.Value.Info.ETag;
                return new FileStreamResult(
                    foundFull.Value.Content,
                    foundFull.Value.Info.ContentType
                );
            }
            var ranged = await storage.GetObjectRangeAsync(bucket, key, start, end, ct);
            if (ranged is null)
                return Problem(
                    statusCode: StatusCodes.Status416RangeNotSatisfiable,
                    detail: "Out of range"
                );
            Response.Headers.ETag = head.ETag;
            Response.Headers["Accept-Ranges"] = "bytes";
            Response.Headers["Content-Range"] = $"bytes {start}-{end}/{head.Length}";
            Response.StatusCode = StatusCodes.Status206PartialContent;
            var length = (end - start + 1);
            if (length > 0)
                StorageMetrics.EgressBytes.Add(
                    length,
                    new("tenant", tenant.TenantId),
                    new("bucket", bucket)
                );
            return new FileStreamResult(ranged.Value.Content, head.ContentType);
        }

        var found = await storage.GetObjectAsync(bucket, key, ct);
        if (found is null)
            return NotFound();
        Response.Headers.ETag = found.Value.Info.ETag;
        StorageMetrics.EgressBytes.Add(
            found.Value.Info.Length,
            new("tenant", tenant.TenantId),
            new("bucket", bucket)
        );
        return new FileStreamResult(found.Value.Content, found.Value.Info.ContentType);
    }

    // DELETE /api/objects/{bucket}/{*key}
    [HttpDelete("{bucket}/{*key}")]
    [Authorize(Policy = "storage.write")]
    public async Task<IActionResult> Delete(string bucket, string key, CancellationToken ct)
    {
        // Normalize key from route (decode % escapes like %2F)
        key = RouteKeyNormalizer.Normalize(key);
        var ok = await storage.DeleteObjectAsync(bucket, key, ct);
        if (ok)
        {
            try
            {
                _ = versions.Increment(tenant.TenantId);
            }
            catch { }
            return NoContent();
        }
        return NotFound();
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
            logger.LogWarning(
                "Presign expired for {Bucket}/{Key}: exp={Exp}, now={Now}",
                bucket,
                key,
                exp,
                now
            );
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
            logger.LogWarning(
                "Invalid presign signature for {Bucket}/{Key}. tenant={Tenant} method={Method} exp={Exp} max={Max} ct={Ct} sig.len={SigLen}",
                bucket,
                key,
                tenantId,
                method,
                exp,
                max,
                string.IsNullOrEmpty(ctStr) ? null : ctStr,
                sig?.Length ?? 0
            );
            problem = Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Invalid signature"
            );
            return false;
        }
        return true;
    }
} // End of Class ObjectsController
