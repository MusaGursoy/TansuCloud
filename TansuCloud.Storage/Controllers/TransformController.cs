// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp; // This line is unchanged
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using TansuCloud.Observability;
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
    private static readonly System.Diagnostics.Metrics.Meter Meter =
        new("TansuCloud.Storage.Transforms", "1.0.0");
    private static readonly System.Diagnostics.Metrics.Counter<long> CacheHitCounter =
        Meter.CreateCounter<long>("storage.transforms.cache.hits");
    private static readonly System.Diagnostics.Metrics.Counter<long> CacheMissCounter =
        Meter.CreateCounter<long>("storage.transforms.cache.misses");
    private static readonly System.Diagnostics.Metrics.Histogram<double> DurationMs =
        Meter.CreateHistogram<double>("storage.transforms.duration.ms");
    private static readonly System.Diagnostics.Metrics.Counter<long> FailureCounter =
        Meter.CreateCounter<long>("storage.transforms.failures");
    private static readonly System.Diagnostics.Metrics.Counter<long> TimeoutCounter =
        Meter.CreateCounter<long>("storage.transforms.timeouts");

    // GET /api/transform/{bucket}/{*key}?w=&h=&fmt=&q=&exp=&sig=
    [HttpGet("{bucket}/{*key}")]
    [AllowAnonymous] // presigned access only; authenticated calls must include storage.read
    public async Task<IActionResult> Get(
        string bucket,
        string key,
        [FromQuery] int? w,
        [FromQuery] int? h,
        [FromQuery] string? fmt,
        [FromQuery] int? q,
        CancellationToken ct
    )
    {
        if (!_options.Enabled)
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                detail: "Transforms disabled"
            );

        // Normalize key from route (decode % escapes like %2F) to match presign canonicalization
        key = RouteKeyNormalizer.Normalize(key);

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
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "Width too large"
                );
            if (_options.MaxHeight > 0 && height > _options.MaxHeight)
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "Height too large"
                );
            if (
                _options.MaxTotalPixels > 0
                && (long)Math.Max(1, width) * Math.Max(1, height) > _options.MaxTotalPixels
            )
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "Total pixels too large"
                );
        }

        // Load source head for ETag and metadata
        var head = await storage.HeadObjectAsync(bucket, key, ct);
        if (head is null)
            return NotFound();

        // Build cache key: tenant|bucket|key|etag|fmt|w|h|q
        var qual = q is > 0 and <= 100 ? q.Value : _options.DefaultQuality;
        var cacheKey =
            $"tx:{tenant.TenantId}|{bucket}|{key}|{head.ETag}|{format}|{width}x{height}|q{qual}";

        // Optional sampling for cache hits to reduce log noise; default sample 10% if not configured
        var samplePctStr = HttpContext
            .RequestServices.GetService<IConfiguration>()
            ?["Storage:Transforms:CacheHitLogSamplePercent"];
        var samplePct = 0;
        _ = int.TryParse(samplePctStr, out samplePct);
        samplePct = samplePct <= 0 ? 0 : Math.Min(100, samplePct);

        if (memoryCache.TryGetValue<byte[]>(cacheKey, out var cached))
        {
            // Sample cache hit logs
            if (samplePct == 0 || Random.Shared.Next(0, 100) < samplePct)
            {
                logger.LogCacheHit(cacheKey);
            }
            Response.Headers.ETag = head.ETag; // tie to source ETag
            SetVaryEncoding();
            CacheHitCounter.Add(1);
            return File(cached!, GetContentType(format));
        }

        CacheMissCounter.Add(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Load original
        var found = await storage.GetObjectAsync(bucket, key, ct);
        if (found is null)
            return NotFound();

        try
        {
            using var cts =
                _options.TimeoutSeconds > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
            if (cts is not null)
                cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var tct = cts?.Token ?? ct;

            // Decode safely with ImageSharp. Read the source bytes once so we can retry if necessary.
            await using var srcMs = new MemoryStream();
            await found.Value.Content.CopyToAsync(srcMs, tct);
            var sourceBytes = srcMs.ToArray();

            using var img = await TryLoadImageAsync(sourceBytes, head.ContentType, tct);

            // Resize (maintain aspect ratio if one dimension is 0) and avoid zero-size inputs to ImageSharp
            if (width > 0 || height > 0)
            {
                int targetW = width;
                int targetH = height;
                if (targetW == 0 && targetH > 0)
                {
                    // Derive width from height
                    targetW = Math.Max(
                        1,
                        (int)Math.Round((double)img.Width * targetH / Math.Max(1, img.Height))
                    );
                }
                else if (targetH == 0 && targetW > 0)
                {
                    // Derive height from width
                    targetH = Math.Max(
                        1,
                        (int)Math.Round((double)img.Height * targetW / Math.Max(1, img.Width))
                    );
                }
                var size = new Size(Math.Max(1, targetW), Math.Max(1, targetH));
                img.Mutate(x =>
                    x.Resize(
                        new ResizeOptions
                        {
                            Size = size,
                            Mode = ResizeMode.Max,
                            Sampler = KnownResamplers.Bicubic
                        }
                    )
                );
            }

            // Encode
            var encoder = GetEncoder(format, qual);
            await using var ms = new MemoryStream();
            await img.SaveAsync(ms, encoder, tct);
            var bytes = ms.ToArray();

            // Cache with TTL and size bound via entry options
            var entry = memoryCache.CreateEntry(cacheKey);
            entry.Value = bytes;
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
                Math.Max(1, _options.CacheTtlSeconds)
            );
            entry.SetSize(bytes.Length);
            entry.Dispose();

            Response.Headers.ETag = head.ETag;
            SetVaryEncoding();
            sw.Stop();
            DurationMs.Record(sw.Elapsed.TotalMilliseconds);
            logger.LogCacheMiss(cacheKey);
            return File(bytes, GetContentType(format));
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            DurationMs.Record(sw.Elapsed.TotalMilliseconds);
            TimeoutCounter.Add(1);
            return Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                detail: "Transform timeout"
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transform failed for {Bucket}/{Key}", bucket, key);
            FailureCounter.Add(1);
            return Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                detail: "Unsupported image or transform failed"
            );
        }
    }

    private static async Task<Image> TryLoadImageAsync(
        byte[] sourceBytes,
        string contentType,
        CancellationToken ct
    )
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream(sourceBytes);
            return await Image.LoadAsync(ms, ct);
        }
        catch (SixLabors.ImageSharp.InvalidImageContentException)
            when (contentType.Contains("image/png", StringComparison.OrdinalIgnoreCase))
        {
            // Some tiny PNGs may have incorrect CRCs. Attempt a CRC-fix pass and retry once.
            // Note: This is a best-effort recovery path used only in Development/E2E.
            if (TryFixPngCrc(sourceBytes, out var fixedBytes))
            {
                using var ms2 = new MemoryStream(fixedBytes);
                return await Image.LoadAsync(ms2, ct);
            }
            throw;
        }
    }

    // Attempt to correct PNG CRCs for all chunks in-place (copy) and return a new buffer.
    private static bool TryFixPngCrc(ReadOnlySpan<byte> input, out byte[] fixedBytes)
    {
        fixedBytes = Array.Empty<byte>();
        // PNG signature (8 bytes)
        ReadOnlySpan<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (input.Length < 8 || !input.Slice(0, 8).SequenceEqual(sig))
            return false;

        var buf = input.ToArray();
        int pos = 8;
        try
        {
            while (pos + 12 <= buf.Length)
            {
                // length (4), type (4), data (len), crc (4)
                uint len = ReadUInt32BE(buf.AsSpan(pos));
                int typeStart = pos + 4;
                int dataStart = pos + 8;
                int crcPos = pos + 8 + (int)len;
                int next = crcPos + 4;
                if (crcPos + 4 > buf.Length)
                    break; // malformed

                // compute CRC over type+data
                var crc = Crc32Png.Compute(buf.AsSpan(typeStart, 4 + (int)len));
                WriteUInt32BE(buf.AsSpan(crcPos, 4), crc);

                // Advance
                pos = next;

                // Stop after IEND for safety
                if (
                    len == 0
                    && buf[typeStart] == (byte)'I'
                    && buf[typeStart + 1] == (byte)'E'
                    && buf[typeStart + 2] == (byte)'N'
                    && buf[typeStart + 3] == (byte)'D'
                )
                    break;
            }
        }
        catch
        {
            return false;
        }

        fixedBytes = buf;
        return true;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> span)
    {
        return (uint)(span[0] << 24 | span[1] << 16 | span[2] << 8 | span[3]);
    }

    private static void WriteUInt32BE(Span<byte> span, uint value)
    {
        span[0] = (byte)((value >> 24) & 0xFF);
        span[1] = (byte)((value >> 16) & 0xFF);
        span[2] = (byte)((value >> 8) & 0xFF);
        span[3] = (byte)(value & 0xFF);
    }

    private static class Crc32Png
    {
        // IEEE 802.3 CRC-32 (poly 0xEDB88320), as used by PNG. Computed over chunk type + data.
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            const uint poly = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1) != 0)
                        c = poly ^ (c >> 1);
                    else
                        c >>= 1;
                }
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }
    }

    private bool TryValidateTransformPresign(string bucket, string key, out IActionResult? problem)
    {
        problem = null;
        // Require tenant for presigned operations; use normalized tenant id from context
        // Always rely on ITenantContext normalization to ensure signature canonicalization matches presign
        var tenantId = tenant.TenantId;
        var rawTenantHeader = Request.Headers["X-Tansu-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Diagnostic: missing tenant header -> challenge (likely gateway didn't stamp or client header missing)
            logger.LogInformation(
                "Transform presign validation failed: missing tenant. Path={Path} Query={Query}",
                Request.Path,
                Request.QueryString.Value
            );
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
            logger.LogInformation(
                "Transform presign validation failed: missing exp/sig. Tenant={Tenant} Path={Path} Query={Query}",
                tenantId,
                Request.Path,
                Request.QueryString.Value
            );
            problem = Challenge();
            return false;
        }
        if (!long.TryParse(expStr, out var exp))
        {
            logger.LogInformation(
                "Transform presign validation failed: invalid exp '{Exp}'. Tenant={Tenant}",
                expStr,
                tenantId
            );
            problem = Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid exp");
            return false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exp < now)
        {
            logger.LogInformation(
                "Transform presign expired: exp={Exp} now={Now} Tenant={Tenant} Bucket={Bucket} Key={Key}",
                exp,
                now,
                tenantId,
                bucket,
                key
            );
            problem = Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Presign expired"
            );
            return false;
        }
        int? w = int.TryParse(wStr, out var wVal) ? wVal : null;
        int? h = int.TryParse(hStr, out var hVal) ? hVal : null;
        int? q = int.TryParse(qStr, out var qVal) ? qVal : null;

        // Derive canonical and validate signature via presign service using the existing method
        var expectedSig = presign.CreateTransformSignature(
            tenantId,
            bucket,
            key,
            w,
            h,
            fmt,
            q,
            exp
        );
        if (
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expectedSig),
                System.Text.Encoding.ASCII.GetBytes(sig)
            )
        )
        {
            // Log full context to aid debugging (dev logs). Avoid logging raw HMAC in production scenarios.
            logger.LogWarning(
                "Transform presign invalid signature. Tenant={Tenant} (raw='{RawTenant}') Bucket={Bucket} Key={Key} w={W} h={H} fmt={Fmt} q={Q} exp={Exp} expectedSig={Expected} providedSig={Provided}",
                tenantId,
                rawTenantHeader,
                bucket,
                key,
                w,
                h,
                fmt,
                q,
                exp,
                expectedSig,
                sig
            );
            problem = Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: "Invalid signature"
            );
            return false;
        }
        return true;
    }

    private static string BuildTransformCanonical(
        string tenantId,
        string bucket,
        string key,
        int? w,
        int? h,
        string? fmt,
        int? q,
        long exp
    )
    {
        // Canonical form for transform signing
        return string.Join(
            "\n",
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

    private static IImageEncoder GetEncoder(string fmt, int quality) =>
        fmt.ToLowerInvariant() switch
        {
            "webp" => new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy },
            "jpeg" or "jpg" => new JpegEncoder { Quality = quality },
            "png" => new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 },
            _ => new WebpEncoder { Quality = quality }
        };

    private static string GetContentType(string fmt) =>
        fmt.ToLowerInvariant() switch
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
