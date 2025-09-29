// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Observability;
using TansuCloud.Storage.Controllers;
using TansuCloud.Storage.Services;
using Xunit;

public class TransformCacheLoggingTests
{
    [Fact]
    public async Task CacheHit_Sampled_By_Percent_When_Configured()
    {
        var logger = new TestLogger<TransformController>();
        var (controller, cache, conf, storage, tenant) = CreateController(logger, samplePercent: 100);

        // Arrange a cache entry
        var key = "a/b";
        var head = new ObjectInfo(
            Bucket: "buck",
            Key: key,
            Length: 3,
            ContentType: "image/webp",
            ETag: "etag1",
            LastModified: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string>()
        );
        storage
            .Setup(s => s.HeadObjectAsync("buck", key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(head);

        // Put a cached transform
        var cacheKeyPrefix = $"tx:{tenant.Object.TenantId}|buck|{key}|{head.ETag}|webp|0x0|q75";
        cache.Set(cacheKeyPrefix, new byte[] { 1, 2, 3 }, new MemoryCacheEntryOptions());

        // Act
        var res = await controller.Get(
            "buck",
            key,
            null,
            null,
            "webp",
            null,
            CancellationToken.None
        );

        res.Should().BeOfType<FileContentResult>();
        logger.Events.Should().Contain(e => e.Id == LogEvents.StorageCacheHit.Id);
    }

    [Fact]
    public async Task CacheMiss_Always_Logged()
    {
        var logger = new TestLogger<TransformController>();
        var (controller, _, _, storage, _) = CreateController(logger, samplePercent: 0);
        var head = new ObjectInfo(
            Bucket: "buck",
            Key: "a/b",
            Length: 3,
            ContentType: "image/webp",
            ETag: "etag1",
            LastModified: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string>()
        );
        storage
            .Setup(s => s.HeadObjectAsync("buck", "a/b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(head);
        storage
            .Setup(s => s.GetObjectAsync("buck", "a/b", It.IsAny<CancellationToken>()))
            .ReturnsAsync((head, (Stream)new MemoryStream(new byte[] { 1, 2, 3 })));

        var res = await controller.Get(
            "buck",
            "a/b",
            null,
            null,
            "webp",
            null,
            CancellationToken.None
        );
        // May be 415/timeout depending on encoder, but we only assert logging of miss attempt occurs prior to transform
        logger.Events.Should().Contain(e => e.Id == LogEvents.StorageCacheMiss.Id);
    }

    private static (
        TransformController controller,
        IMemoryCache cache,
        IConfiguration conf,
        Mock<IObjectStorage> storage,
        Mock<ITenantContext> tenant
    ) CreateController(ILogger<TransformController> logger, int samplePercent)
    {
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var tenant = new Mock<ITenantContext>(MockBehavior.Strict);
        tenant.SetupGet(t => t.TenantId).Returns("acme-dev");
        var cache = new MemoryCache(new MemoryCacheOptions());
        var presign = new Mock<IPresignService>(MockBehavior.Strict);
        presign
            .Setup(p =>
                p.CreateTransformSignature(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>()
                )
            )
            .Returns("sig");
        var opts = Options.Create(
            new StorageOptions
            {
                Transforms = new TransformOptions
                {
                    Enabled = true,
                    AllowedFormats = new[] { "webp" },
                    DefaultQuality = 75,
                    CacheTtlSeconds = 60
                }
            }
        );
        var confDict = new Dictionary<string, string?>
        {
            ["Storage:Transforms:CacheHitLogSamplePercent"] = samplePercent.ToString()
        };
        var conf = new ConfigurationBuilder().AddInMemoryCollection(confDict!).Build();

        var controller = new TransformController(
            storage.Object,
            tenant.Object,
            cache,
            presign.Object,
            opts,
            logger
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IConfiguration>(conf)
                    .BuildServiceProvider()
            }
        };
        return (controller, cache, conf, storage, tenant);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(int Id, string Message)> Events { get; } = new();

    IDisposable ILogger.BeginScope<TState>(TState state) => new Dummy();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Events.Add((eventId.Id, formatter(state, exception)));

        private sealed class Dummy : IDisposable
        {
            public void Dispose() { }
        }
    }
}
