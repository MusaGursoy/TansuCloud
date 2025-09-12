// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Controllers;

[ApiController]
[Route("api/transform")]
public sealed class TransformController(
    IObjectStorage storage,
    ITenantContext tenant,
    IMemoryCache memoryCache,
    IPresignService presign,
    IOptions<StorageOptions> opts,
    ILogger<TransformController> logger
) : ControllerBase
{
    private readonly TransformOptions _options = opts.Value.Transforms;

    // GET /api/transform/{bucket}/{*key}?w=&h=&fmt=&q=&exp=&sig=
    [HttpGet("{bucket}/{*key}")]
    [AllowAnonymous] // presigned access only; authenticated calls must include storage.read
    public async Task<IActionResult> Get(string bucket, string key, [FromQuery] int? w, [FromQuery] int? h, [FromQuery] string? fmt, [FromQuery] int? q, CancellationToken ct)
    {
        if (!_options.Enabled)
            return Problem(statusCode: StatusCodes.Status404NotFound, detail: "Transforms disabled");

        // Normalize key
        key = Uri.UnescapeDataString(key);

        // Presign validation (signed GET; reuse IPresignService contract semantics)
        if (!TryValidateTransformPresign(bucket, key, out var problem))
            return problem!;

        // Enforce output format allowlist
        var format = (fmt ?? "webp").ToLowerInvariant();
        if (!_options.AllowedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid format");

        // Clamp dimensions
        var width = Math.Max(0, w ?? 0);
        var height = Math.Max(0, h ?? 0);
        if (width == 0 && height == 0)
        {
            // default: no resize – pass-through re-encode to requested fmt
        }
        else
        {
            if (_options.MaxWidth > 0 && width > _options.MaxWidth)
                return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Width too large");
            if (_options.MaxHeight > 0 && height > _options.MaxHeight)
                return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Height too large");
            if (_options.MaxTotalPixels > 0 && (long)Math.Max(1, width) * Math.Max(1, height) > _options.MaxTotalPixels)
                return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Total pixels too large");
        }

        // Load source head for ETag and metadata
        var head = await storage.HeadObjectAsync(bucket, key, ct);
        if (head is null)
            return NotFound();

        // Build cache key: tenant|bucket|key|etag|fmt|w|h|q
        var qual = q is > 0 and <= 100 ? q.Value : _options.DefaultQuality;
        var cacheKey = $"tx:{tenant.TenantId}|{bucket}|{key}|{head.ETag}|{format}|{width}x{height}|q{qual}";

        if (memoryCache.TryGetValue<byte[]>(cacheKey, out var cached))
        {
            logger.LogDebug("Transform cache hit for {CacheKey}", cacheKey);
            Response.Headers.ETag = head.ETag; // tie to source ETag
            SetVaryEncoding();
            return File(cached!, GetContentType(format));
        }

        // Load original
        var found = await storage.GetObjectAsync(bucket, key, ct);
        if (found is null)
            return NotFound();

        try
        {
            using var cts = _options.TimeoutSeconds > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            if (cts is not null)
                cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var tct = cts?.Token ?? ct;

            // Decode safely with ImageSharp
            using var img = await Image.LoadAsync(found.Value.Content, tct);

            // Resize (maintain aspect if one dimension is 0)
            if (width > 0 || height > 0)
            {
                var size = new Size(width, height);
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = size,
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Bicubic
                }));
            }

            // Encode
            var encoder = GetEncoder(format, qual);
            await using var ms = new MemoryStream();
            await img.SaveAsync(ms, encoder, tct);
            var bytes = ms.ToArray();

            // Cache with TTL and size bound via entry options
            var entry = memoryCache.CreateEntry(cacheKey);
            entry.Value = bytes;
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _options.CacheTtlSeconds));
            entry.SetSize(bytes.Length);
            entry.Dispose();

            Response.Headers.ETag = head.ETag;
            SetVaryEncoding();
            return File(bytes, GetContentType(format));
        }
        catch (OperationCanceledException)
        {
            return Problem(statusCode: StatusCodes.Status504GatewayTimeout, detail: "Transform timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transform failed for {Bucket}/{Key}", bucket, key);
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, detail: "Unsupported image or transform failed");
        }
    }

    private bool TryValidateTransformPresign(string bucket, string key, out IActionResult? problem)
    {
        problem = null;
        // Require tenant for presigned operations; prefer header, but fall back to resolved context
        var tenantId = Request.Headers["X-Tansu-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
            tenantId = tenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            problem = Challenge();
            return false;
        }

        var expStr = Request.Query["exp"].ToString();
        var sig = Request.Query["sig"].ToString();
        var wStr = Request.Query["w"].ToString();
        var hStr = Request.Query["h"].ToString();
        var fmt = Request.Query["fmt"].ToString();
        var qStr = Request.Query["q"].ToString();
        if (string.IsNullOrEmpty(expStr) || string.IsNullOrEmpty(sig))
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
            problem = Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Presign expired");
            return false;
        }
        int? w = int.TryParse(wStr, out var wVal) ? wVal : null;
        int? h = int.TryParse(hStr, out var hVal) ? hVal : null;
        int? q = int.TryParse(qStr, out var qVal) ? qVal : null;

        // Derive canonical and validate signature via presign service using the existing method
        var expectedSig = presign.CreateTransformSignature(tenantId, bucket, key, w, h, fmt, q, exp);
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expectedSig), System.Text.Encoding.ASCII.GetBytes(sig)))
        {
            problem = Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Invalid signature");
            return false;
        }
        return true;
    }

    private static string BuildTransformCanonical(string tenantId, string bucket, string key, int? w, int? h, string? fmt, int? q, long exp)
    {
        // Canonical form for transform signing
        return string.Join("\n",
            tenantId,
            "TRANSFORM",
            bucket,
            key,
            (w?.ToString() ?? string.Empty),
            (h?.ToString() ?? string.Empty),
            (fmt ?? string.Empty),
            (q?.ToString() ?? string.Empty),
            exp.ToString()
        );
    }

    private static IImageEncoder GetEncoder(string fmt, int quality)
        => fmt.ToLowerInvariant() switch
        {
            "webp" => new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy },
            "jpeg" or "jpg" => new JpegEncoder { Quality = quality },
            "png" => new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 },
            _ => new WebpEncoder { Quality = quality }
        };

    private static string GetContentType(string fmt)
        => fmt.ToLowerInvariant() switch
        {
            "webp" => "image/webp",
            "jpeg" or "jpg" => "image/jpeg",
            "png" => "image/png",
            _ => "application/octet-stream"
        };

    private void SetVaryEncoding()
    {
        const string varyHeader = "Vary";
        const string acceptEncoding = "Accept-Encoding";
        if (Response.Headers.TryGetValue(varyHeader, out var varyVal))
        {
            if (!varyVal.ToString().Contains(acceptEncoding, StringComparison.OrdinalIgnoreCase))
                Response.Headers.Append(varyHeader, acceptEncoding);
        }
        else
        {
            Response.Headers[varyHeader] = acceptEncoding;
        }
    }
} // End of Class TransformController
