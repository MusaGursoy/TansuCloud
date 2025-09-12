// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Storage.Services;

public sealed class MultipartStorageTests
{
    private static (FilesystemMultipartStorage svc, string root, string tenantId) Create()
    {
        var env = Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == Path.GetTempPath());
        var tempRoot = Path.Combine(
            env.ContentRootPath,
            "_storage-tests-" + Guid.NewGuid().ToString("N")
        );
        var opts = Options.Create(new StorageOptions { RootPath = tempRoot });
        var tenantId = "tenant-ut" + Guid.NewGuid().ToString("N");
        var tenant = Mock.Of<ITenantContext>(t => t.TenantId == tenantId);
        var svc = new FilesystemMultipartStorage(opts, env, tenant);
        return (svc, tempRoot, tenantId);
    }

    [Fact]
    public async Task Complete_Throws_When_Missing_Part()
    {
        var (svc, root, _) = Create();
        var bucket = "b";
        var key = "k/file.bin";
        try
        {
            var init = await svc.InitiateAsync(bucket, key, default);
            // upload only part 1
            await svc.UploadPartAsync(
                bucket,
                key,
                init.UploadId,
                1,
                new MemoryStream(new byte[] { 1, 2, 3 }),
                default
            );
            // complete with part 1 and 2 -> should throw for missing part 2
            Func<Task> act = () =>
                svc.CompleteAsync(bucket, key, init.UploadId, new[] { 1, 2 }, default);
            await act.Should().ThrowAsync<FileNotFoundException>();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Complete_Concatenates_In_Numeric_Order_Ignoring_Provided_Order()
    {
        var (svc, root, _) = Create();
        var bucket = "b2";
        var key = "k2/file.bin";
        try
        {
            var init = await svc.InitiateAsync(bucket, key, default);
            var p1 = Encoding.UTF8.GetBytes("one");
            var p2 = Encoding.UTF8.GetBytes("two");
            await svc.UploadPartAsync(bucket, key, init.UploadId, 1, new MemoryStream(p1), default);
            await svc.UploadPartAsync(bucket, key, init.UploadId, 2, new MemoryStream(p2), default);

            // Pass parts out-of-order [2,1]; implementation orders them internally
            var etag = await svc.CompleteAsync(bucket, key, init.UploadId, new[] { 2, 1 }, default);

            var expected = StorageEtags.ComputeWeak(p1.Concat(p2).ToArray());
            etag.Should().Be(expected);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Abort_Removes_Upload_Temp_Directory()
    {
        var (svc, root, tenantId) = Create();
        var bucket = "b3";
        var key = "k3/file.bin";
        try
        {
            var init = await svc.InitiateAsync(bucket, key, default);
            await svc.UploadPartAsync(
                bucket,
                key,
                init.UploadId,
                1,
                new MemoryStream(new byte[] { 9 }),
                default
            );

            // Path: <root>/<tenant>/<bucket>/<key>.multipart.<uploadId>
            var sepKey = key.Replace('/', Path.DirectorySeparatorChar);
            var uploadRoot = Path.Combine(
                root!,
                tenantId,
                bucket,
                sepKey + $".multipart.{init.UploadId}"
            );
            Directory.Exists(uploadRoot).Should().BeTrue();

            await svc.AbortAsync(bucket, key, init.UploadId, default);
            Directory.Exists(uploadRoot).Should().BeFalse();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }
} // End of Class MultipartStorageTests
