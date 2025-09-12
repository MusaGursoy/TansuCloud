// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Storage.Services;

/// <summary>
/// Options for the storage service (filesystem root, quotas, presign, etc.).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Root folder for the local filesystem provider. Defaults to ./_storage under content root.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>

    /// <summary>
    /// Abandoned multipart cleanup scan interval. Default: 10 minutes.
    /// </summary>
    public TimeSpan MultipartCleanupInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Multipart upload inactivity timeout. Upload roots older than this are deleted. Default: 1 hour.
    /// </summary>
    public TimeSpan MultipartInactivityTimeout { get; set; } = TimeSpan.FromHours(1);
    /// Secret used to sign presigned URLs (HMAC-SHA256). Keep out of source control.
    /// </summary>
    public string? PresignSecret { get; set; }

    /// <summary>
    /// Default expiry for presigned URLs when not specified (minutes).
    /// </summary>
    public int DefaultPresignExpiryMinutes { get; set; } = 15;

    /// <summary>
    /// Per-tenant quotas; all sizes in bytes; 0 or negative disables the limit.
    /// </summary>
    public QuotaLimits Quotas { get; set; } = new();

    /// <summary>
    /// Minimum allowed multipart part size in bytes (except for the final part). Default 5 MiB.
    /// </summary>
    public long MultipartMinPartSizeBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>
    /// Maximum allowed multipart part size in bytes for any part (including the final part).
    /// 0 or negative disables this limit. Default: disabled.
    /// </summary>
    public long MultipartMaxPartSizeBytes { get; set; } = 0;

    /// <summary>
    /// Enable background cleanup for abandoned multipart uploads.
    /// </summary>
    public bool MultipartCleanupEnabled { get; set; } = true;

    /// <summary>
    /// TTL in minutes after which a multipart temp folder is considered abandoned.
    /// </summary>
    public int MultipartCleanupTtlMinutes { get; set; } = 60; // 1 hour

    /// <summary>
    /// Sweep interval in minutes for the background cleanup.
    /// </summary>
    public int MultipartCleanupIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Response compression settings (Task 14). Controls Brotli level, HTTPS enablement, and MIME allowlist.
    /// </summary>
    public CompressionOptions Compression { get; set; } = new();

    /// <summary>
    /// Image transform options (Task 14). Controls limits, formats, and cache.
    /// </summary>
    public TransformOptions Transforms { get; set; } = new();
} // End of Class StorageOptions

public sealed class QuotaLimits
{
    public long MaxTotalBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB
    public long MaxObjectSizeBytes { get; set; } = 100L * 1024 * 1024; // 100 MB
    public long MaxObjectCount { get; set; } = 1_000_000; // large default
} // End of Class QuotaLimits

/// <summary>
/// Response compression settings for the Storage service.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// Master toggle for response compression. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Allow compression over HTTPS. Default: true (dev/e2e convenience). Review security guidance before enabling in prod.
    /// </summary>
    public bool EnableForHttps { get; set; } = true;

    /// <summary>
    /// Brotli compression level. Default: Fastest. Other values: NoCompression, Optimal, SmallestSize.
    /// </summary>
    public System.IO.Compression.CompressionLevel BrotliLevel { get; set; } = System.IO.Compression.CompressionLevel.Optimal;

    /// <summary>
    /// Allowlist of MIME types eligible for compression. Wildcards are not supported. If null/empty, a sensible default is used.
    /// </summary>
    public string[]? MimeTypes { get; set; }
} // End of Class CompressionOptions

/// <summary>
/// Settings for secure on-the-fly image transforms.
/// </summary>
public sealed class TransformOptions
{
    /// <summary>
    /// Master toggle for transform endpoint. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum width in pixels. 0 disables the check. Default: 4096.
    /// </summary>
    public int MaxWidth { get; set; } = 4096;

    /// <summary>
    /// Maximum height in pixels. 0 disables the check. Default: 4096.
    /// </summary>
    public int MaxHeight { get; set; } = 4096;

    /// <summary>
    /// Maximum total pixels (width * height). 0 disables the check. Default: 16,777,216 (4096x4096).
    /// </summary>
    public long MaxTotalPixels { get; set; } = 4096L * 4096L;

    /// <summary>
    /// Default JPEG/WebP quality if not specified. Range 1-100. Default: 75.
    /// </summary>
    public int DefaultQuality { get; set; } = 75;

    /// <summary>
    /// Allowed output formats. Default: ["webp","jpeg","png"].
    /// </summary>
    public string[] AllowedFormats { get; set; } = new[] { "webp", "jpeg", "png" };

    /// <summary>
    /// Transform execution timeout in seconds. 0 disables the timeout. Default: 10.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Cache TTL in seconds for transformed images. Default: 300 (5 min).
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of items to keep in memory cache. 0 means unlimited (not recommended). Default: 1000.
    /// </summary>
    public int CacheMaxEntries { get; set; } = 1000;
} // End of Class TransformOptions
