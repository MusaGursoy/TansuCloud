// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace TansuCloud.Storage.Services;

public sealed record MultipartInitiateResult(string UploadId);

public sealed record UploadedPart(int PartNumber, string ETag, long Length);
public sealed record PartInfo(int PartNumber, long Length);

public interface IMultipartStorage
{
    Task<MultipartInitiateResult> InitiateAsync(string bucket, string key, CancellationToken ct);
    Task<UploadedPart> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken ct
    );
    Task<IReadOnlyList<PartInfo>> GetPartsAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct
    );
    Task<string> CompleteAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken ct
    );
    Task AbortAsync(string bucket, string key, string uploadId, CancellationToken ct);
}

internal sealed class FilesystemMultipartStorage(
    IOptions<StorageOptions> options,
    IWebHostEnvironment env,
    ITenantContext tenant
) : IMultipartStorage
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
        var normalized = key.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(BucketPath(bucket), normalized);
    }

    private string UploadRoot(string bucket, string key, string uploadId) =>
        Path.Combine(ObjectPath(bucket, key) + $".multipart.{uploadId}");

    public Task<MultipartInitiateResult> InitiateAsync(
        string bucket,
        string key,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ObjectPath(bucket, key))!);
        var uploadId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        Directory.CreateDirectory(UploadRoot(bucket, key, uploadId));
        return Task.FromResult(new MultipartInitiateResult(uploadId));
    }

    public async Task<UploadedPart> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken ct
    )
    {
        var root = UploadRoot(bucket, key, uploadId);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Upload not found");
        var partPath = Path.Combine(root, $"part-{partNumber:000000}");
        await using (var fs = File.Create(partPath))
        {
            await content.CopyToAsync(fs, ct);
        }
        var bytes = await File.ReadAllBytesAsync(partPath, ct);
        var etag = StorageEtags.ComputeWeak(bytes);
        return new UploadedPart(partNumber, etag, bytes.LongLength);
    }

    public Task<IReadOnlyList<PartInfo>> GetPartsAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct
    )
    {
        var root = UploadRoot(bucket, key, uploadId);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Upload not found");
        var list = new List<PartInfo>();
        foreach (var file in Directory.EnumerateFiles(root, "part-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            // part-000001
            var numStr = name.Substring("part-".Length);
            if (int.TryParse(numStr, out var partNumber))
            {
                var len = new FileInfo(file).Length;
                list.Add(new PartInfo(partNumber, len));
            }
        }
        return Task.FromResult<IReadOnlyList<PartInfo>>(list);
    }

    public async Task<string> CompleteAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken ct
    )
    {
        var root = UploadRoot(bucket, key, uploadId);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Upload not found");
        // Concatenate parts in numeric order of provided list
        var ordered = partNumbers.OrderBy(n => n).ToArray();
        var finalPath = ObjectPath(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await using (var outFs = File.Create(finalPath))
        {
            foreach (var n in ordered)
            {
                var partPath = Path.Combine(root, $"part-{n:000000}");
                if (!File.Exists(partPath))
                    throw new FileNotFoundException($"Missing part {n}");
                await using var inFs = File.OpenRead(partPath);
                await inFs.CopyToAsync(outFs, ct);
            }
        }
        // compute weak ETag of final file
        var finalBytes = await File.ReadAllBytesAsync(finalPath, ct);
        var etag = StorageEtags.ComputeWeak(finalBytes);
        // cleanup parts directory
        Directory.Delete(root, recursive: true);
        return etag;
    }

    public Task AbortAsync(string bucket, string key, string uploadId, CancellationToken ct)
    {
        var root = UploadRoot(bucket, key, uploadId);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }
} // End of Class FilesystemMultipartStorage
