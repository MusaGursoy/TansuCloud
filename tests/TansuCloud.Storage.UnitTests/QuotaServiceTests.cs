// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Storage.Services;

public class QuotaServiceTests
{
    private static IQuotaService Create(long maxTotal = 0, long maxObj = 0, long maxCount = 0)
    {
        var env = Mock.Of<IWebHostEnvironment>(e =>
            e.ContentRootPath == System.IO.Path.GetTempPath()
        );
        var opts = Options.Create(
            new StorageOptions
            {
                Quotas = new QuotaLimits
                {
                    MaxTotalBytes = maxTotal,
                    MaxObjectSizeBytes = maxObj,
                    MaxObjectCount = maxCount
                }
            }
        );
        var tenant = Mock.Of<ITenantContext>(t => t.TenantId == "tenant-test");
        return new FilesystemQuotaService(env, opts, tenant);
    }

    [Fact]
    public async Task Evaluate_Respects_MaxObjectSize()
    {
        var svc = Create(maxObj: 10);
        var eval = await svc.EvaluateAsync(11, default);
        eval.Exceeded.Should().BeTrue();
        eval.Reason.Should().Be("MaxObjectSizeBytes");
    }

    [Fact]
    public async Task Evaluate_Respects_MaxObjectCount()
    {
        // Arrange: 0 means unlimited; use 1 to allow a single object currently present, then adding 1 more should exceed
        var env = Mock.Of<IWebHostEnvironment>(e =>
            e.ContentRootPath == System.IO.Path.GetTempPath()
        );
        var opts = Options.Create(
            new StorageOptions { Quotas = new QuotaLimits { MaxObjectCount = 1 } }
        );
        var tenantId = "tenant-test";
        var tenant = Mock.Of<ITenantContext>(t => t.TenantId == tenantId);
        var svc = new FilesystemQuotaService(env, opts, tenant);

        // Create tenant directory and one existing object file to simulate current usage
        var root = System.IO.Path.Combine(env.ContentRootPath, "_storage", tenantId);
        Directory.CreateDirectory(root);
        var existing = System.IO.Path.Combine(root, "existing.bin");
        await File.WriteAllBytesAsync(existing, new byte[] { 1, 2, 3 });

        try
        {
            var eval = await svc.EvaluateAsync(1, default);
            eval.Exceeded.Should().BeTrue();
            eval.Reason.Should().Be("MaxObjectCount");
        }
        finally
        {
            // Cleanup
            try
            {
                File.Delete(existing);
            }
            catch { }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Evaluate_Respects_MaxTotalBytes()
    {
        var svc = Create(maxTotal: 1);
        var eval = await svc.EvaluateAsync(2, default);
        eval.Exceeded.Should().BeTrue();
        eval.Reason.Should().Be("MaxTotalBytes");
    }
}
