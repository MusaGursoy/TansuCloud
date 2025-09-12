// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Storage.Services;

public sealed record ObjectInfo(
    string Bucket,
    string Key,
    long Length,
    string ContentType,
    string ETag,
    DateTimeOffset LastModified,
    IReadOnlyDictionary<string, string> Metadata
);

public interface IObjectStorage
{
    // Buckets
    Task<bool> CreateBucketAsync(string bucket, CancellationToken ct);
    Task<bool> DeleteBucketAsync(string bucket, CancellationToken ct);
    Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct);
    Task<bool> BucketExistsAsync(string bucket, CancellationToken ct);

    // Objects
    Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        IDictionary<string, string>? metadata,
        CancellationToken ct
    );

    Task<(ObjectInfo Info, Stream Content)?> GetObjectAsync(
        string bucket,
        string key,
        CancellationToken ct
    );
    Task<ObjectInfo?> HeadObjectAsync(string bucket, string key, CancellationToken ct);
    Task<bool> DeleteObjectAsync(string bucket, string key, CancellationToken ct);
    Task<(ObjectInfo Info, Stream Content)?> GetObjectRangeAsync(
        string bucket,
        string key,
        long start,
        long end,
        CancellationToken ct
    );
    /// <summary>
    /// List objects in a bucket optionally filtered by a key prefix.
    /// </summary>
    Task<IReadOnlyList<ObjectInfo>> ListObjectsAsync(
        string bucket,
        string? prefix,
        CancellationToken ct
    );
} // End of Interface IObjectStorage
