// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace TansuCloud.Storage.Services;

internal sealed class FilesystemObjectStorage(
    IWebHostEnvironment env,
    IOptions<StorageOptions> options,
    ITenantContext tenant
) : IObjectStorage
{
    private readonly string _root = EnsureRoot(
        options.Value.RootPath ?? Path.Combine(env.ContentRootPath, "_storage")
    );

    private static string EnsureRoot(string root)
    {
        Directory.CreateDirectory(root);
        return root;
    }

    private string TenantRoot => Path.Combine(_root, tenant.TenantId);

    private string BucketPath(string bucket) => Path.Combine(TenantRoot, bucket);

    private string ObjectPath(string bucket, string key)
    {
        // emulate S3-style keys using directory separators from '/'
        var normalized = key.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(BucketPath(bucket), normalized);
    }

    private string MetaPath(string bucket, string key) => ObjectPath(bucket, key) + ".meta.json";

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken ct) =>
        Task.FromResult(Directory.Exists(BucketPath(bucket)));

    public Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(TenantRoot))
            return Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        var list = Directory
            .EnumerateDirectories(TenantRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();
        return Task.FromResult((IReadOnlyList<string>)list);
    }

    public Task<bool> CreateBucketAsync(string bucket, CancellationToken ct)
    {
        Directory.CreateDirectory(BucketPath(bucket));
        return Task.FromResult(true);
    }

    public Task<bool> DeleteBucketAsync(string bucket, CancellationToken ct)
    {
        var path = BucketPath(bucket);
        if (!Directory.Exists(path))
            return Task.FromResult(true); // idempotent delete
        // Consider bucket empty if it contains no user object files. Ignore empty directories and
        // internal metadata files ("*.meta.json"). If only internal files remain, delete recursively.
        var allFiles = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
        var userFiles = allFiles.Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (userFiles.Any())
            return Task.FromResult(false);
        // Only empty directories and/or metadata files remain -> safe to delete recursively
        Directory.Delete(path, recursive: true);
        return Task.FromResult(true);
    }

    public async Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        IDictionary<string, string>? metadata,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ObjectPath(bucket, key))!);
        var path = ObjectPath(bucket, key);
        using (var fs = File.Create(path))
        {
            await content.CopyToAsync(fs, ct);
        }
        var info = await HeadObjectAsync(bucket, key, ct);
        var meta = new Dictionary<string, string>(
            metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["content-type"] = contentType,
            ["etag"] = info?.ETag ?? string.Empty,
            ["last-modified"] = (info?.LastModified ?? DateTimeOffset.UtcNow).ToString("O"),
            ["length"] = (info?.Length ?? new FileInfo(path).Length).ToString()
        };
        await File.WriteAllTextAsync(MetaPath(bucket, key), JsonSerializer.Serialize(meta), ct);
    }

    public async Task<(ObjectInfo Info, Stream Content)?> GetObjectAsync(
        string bucket,
        string key,
        CancellationToken ct
    )
    {
        var info = await HeadObjectAsync(bucket, key, ct);
        if (info is null)
            return null;
        var stream = File.OpenRead(ObjectPath(bucket, key));
        return (info, stream);
    }

    public async Task<ObjectInfo?> HeadObjectAsync(string bucket, string key, CancellationToken ct)
    {
        var path = ObjectPath(bucket, key);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var etag = StorageEtags.ComputeWeak(bytes);
        var fi = new FileInfo(path);
        var meta = await ReadMeta(bucket, key, ct);
        var contentType = meta.TryGetValue("content-type", out var ctStr)
            ? ctStr
            : "application/octet-stream";
        return new ObjectInfo(bucket, key, fi.Length, contentType, etag, fi.LastWriteTimeUtc, meta);
    }

    public async Task<bool> DeleteObjectAsync(string bucket, string key, CancellationToken ct)
    {
        var path = ObjectPath(bucket, key);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        var mp = MetaPath(bucket, key);
        if (File.Exists(mp))
            File.Delete(mp);
        await Task.CompletedTask;
        return true;
    }

    public async Task<(ObjectInfo Info, Stream Content)?> GetObjectRangeAsync(
        string bucket,
        string key,
        long start,
        long end,
        CancellationToken ct
    )
    {
        var head = await HeadObjectAsync(bucket, key, ct);
        if (head is null)
            return null;
        var length = head.Length;
        if (start < 0 || end < start || start >= length)
            return null;
        var toRead = end - start + 1;
        await using var fs = File.OpenRead(ObjectPath(bucket, key));
        fs.Seek(start, SeekOrigin.Begin);
        var ms = new MemoryStream((int)Math.Min(toRead, 1024 * 1024));
        var buffer = new byte[81920];
        long remaining = toRead;
        while (remaining > 0)
        {
            var readLen = (int)Math.Min(buffer.Length, remaining);
            var n = await fs.ReadAsync(buffer.AsMemory(0, readLen), ct);
            if (n <= 0)
                break;
            await ms.WriteAsync(buffer.AsMemory(0, n), ct);
            remaining -= n;
        }
        ms.Position = 0;
        return (
            new ObjectInfo(
                bucket,
                key,
                length,
                head.ContentType,
                head.ETag,
                head.LastModified,
                head.Metadata
            ),
            ms
        );
    }

    public async Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(
        string bucket,
        string? prefix,
        CancellationToken ct
    )
    {
        var results = new List<ObjectInfo>();
        var root = BucketPath(bucket);
        if (!Directory.Exists(root))
            return results;
        var searchRoot = root;
        var normalizePrefix = prefix?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty;
        if (!string.IsNullOrEmpty(normalizePrefix))
        {
            searchRoot = Path.Combine(root, normalizePrefix);
            if (!Directory.Exists(searchRoot))
                return results;
        }
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(root, file);
            var key = rel.Replace(Path.DirectorySeparatorChar, '/');
            var head = await HeadObjectAsync(bucket, key, ct);
            if (head is not null)
                results.Add(head);
        }
        return results;
    }

    private async Task<Dictionary<string, string>> ReadMeta(
        string bucket,
        string key,
        CancellationToken ct
    )
    {
        var metaFile = MetaPath(bucket, key);
        if (!File.Exists(metaFile))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await File.ReadAllTextAsync(metaFile, ct);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
} // End of Class FilesystemObjectStorage
