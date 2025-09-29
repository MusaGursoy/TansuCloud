// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TansuCloud.Observability.Auditing;
using TansuCloud.Storage.Controllers;
using TansuCloud.Storage.Services;

public sealed class ObjectsControllerTests
{
    private sealed class FakeVersions : ITenantCacheVersion
    {
        private readonly Dictionary<string, int> _versions = new();

        public int Get(string tenantId)
        {
            if (!_versions.TryGetValue(tenantId, out var v))
            {
                v = 0;
                _versions[tenantId] = v;
            }
            return v;
        }

        public int Increment(string tenantId)
        {
            var v = Get(tenantId) + 1;
            _versions[tenantId] = v;
            return v;
        }
    }

    private static ObjectsController Create(
        out Mock<IObjectStorage> storage,
        out Mock<ITenantContext> tenant,
        out Mock<IPresignService> presign,
        out Mock<IQuotaService> quotas,
        out Mock<IAntivirusScanner> av
    )
    {
        storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        tenant = new Mock<ITenantContext>(MockBehavior.Strict);
        presign = new Mock<IPresignService>(MockBehavior.Strict);
        quotas = new Mock<IQuotaService>(MockBehavior.Strict);
        av = new Mock<IAntivirusScanner>(MockBehavior.Strict);
        tenant.Setup(t => t.TenantId).Returns("tenant-ut");
        var logger = Mock.Of<ILogger<ObjectsController>>();
        var versions = new Mock<ITenantCacheVersion>();
        versions.Setup(v => v.Get(It.IsAny<string>())).Returns(0);
        var audit = new Mock<IAuditLogger>(MockBehavior.Loose);
        audit.Setup(a => a.TryEnqueue(It.IsAny<AuditEvent>())).Returns(true);
        var controller = new ObjectsController(
            storage.Object,
            tenant.Object,
            presign.Object,
            quotas.Object,
            av.Object,
            logger,
            versions.Object,
            audit.Object,
            cache: null
        );

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        httpCtx.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("scope", "storage.read storage.write"),
                    new Claim("aud", "tansu.storage")
                },
                authenticationType: "unit-test"
            )
        );
        controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return controller;
    }

    [Fact]
    public async Task List_Uses_HybridCache_With_TTL_And_Version()
    {
        // Arrange: controller with an in-memory HybridCache (no Redis) and fake version source
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var tenant = new Mock<ITenantContext>(MockBehavior.Strict);
        var presign = new Mock<IPresignService>(MockBehavior.Strict);
        var quotas = new Mock<IQuotaService>(MockBehavior.Strict);
        var av = new Mock<IAntivirusScanner>(MockBehavior.Strict);
        var logger = Mock.Of<ILogger<ObjectsController>>();
        var versions = new FakeVersions();
        tenant.Setup(t => t.TenantId).Returns("tenant-ut");

        var services = new ServiceCollection();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var bucket = "b";
        var prefix = "p";
        var listItems =
            new List<ObjectInfo>
            {
                new ObjectInfo(
                    bucket,
                    "p/k.txt",
                    1,
                    "text/plain",
                    "W\"e\"",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            } as IReadOnlyList<ObjectInfo>;
        storage.Setup(s => s.ListObjectsAsync(bucket, prefix, default)).ReturnsAsync(listItems);

        var controller = new ObjectsController(
            storage.Object,
            tenant.Object,
            presign.Object,
            quotas.Object,
            av.Object,
            logger,
            versions,
            audit: new Mock<IAuditLogger>(MockBehavior.Loose) { }.Object,
            cache
        );

        var httpCtx = new DefaultHttpContext();
        httpCtx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("scope", "storage.read") },
                authenticationType: "test"
            )
        );
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        // Act: first call should hit storage (miss) and populate cache
        var result1 = await controller.List(bucket, prefix, default);
        result1.Should().BeOfType<OkObjectResult>();
        storage.Verify(s => s.ListObjectsAsync(bucket, prefix, default), Times.Once);

        // Act: second call should be served from cache (no additional storage call)
        var result2 = await controller.List(bucket, prefix, default);
        result2.Should().BeOfType<OkObjectResult>();
        storage.Verify(s => s.ListObjectsAsync(bucket, prefix, default), Times.Once);

        // Invalidate via version bump and verify storage is called again
        versions.Increment("tenant-ut");
        var result3 = await controller.List(bucket, prefix, default);
        result3.Should().BeOfType<OkObjectResult>();
        storage.Verify(s => s.ListObjectsAsync(bucket, prefix, default), Times.Exactly(2));
    }

    [Fact]
    public async Task Head_Uses_HybridCache_And_Invalidates_On_Version()
    {
        // Arrange
        var storage = new Mock<IObjectStorage>(MockBehavior.Strict);
        var tenant = new Mock<ITenantContext>(MockBehavior.Strict);
        var presign = new Mock<IPresignService>(MockBehavior.Strict);
        var quotas = new Mock<IQuotaService>(MockBehavior.Strict);
        var av = new Mock<IAntivirusScanner>(MockBehavior.Strict);
        var logger = Mock.Of<ILogger<ObjectsController>>();
        var versions = new FakeVersions();
        tenant.Setup(t => t.TenantId).Returns("tenant-ut");

        var services = new ServiceCollection();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var bucket = "b";
        var key = "k.txt";
        var meta = new ObjectInfo(
            bucket,
            key,
            3,
            "text/plain",
            "W\"e1\"",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>()
        );
        storage.Setup(s => s.HeadObjectAsync(bucket, key, default)).ReturnsAsync(meta);

        var controller = new ObjectsController(
            storage.Object,
            tenant.Object,
            presign.Object,
            quotas.Object,
            av.Object,
            logger,
            versions,
            audit: new Mock<IAuditLogger>(MockBehavior.Loose) { }.Object,
            cache
        );

        var httpCtx = new DefaultHttpContext();
        httpCtx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("scope", "storage.read") },
                authenticationType: "test"
            )
        );
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        // Act: first head → miss
        var r1 = await controller.Head(bucket, key, default);
        r1.Should().BeOfType<OkResult>();
        storage.Verify(s => s.HeadObjectAsync(bucket, key, default), Times.Once);

        // Act: second head → hit
        var r2 = await controller.Head(bucket, key, default);
        r2.Should().BeOfType<OkResult>();
        storage.Verify(s => s.HeadObjectAsync(bucket, key, default), Times.Once);

        // Invalidate and ensure call goes to storage again
        versions.Increment("tenant-ut");
        var r3 = await controller.Head(bucket, key, default);
        r3.Should().BeOfType<OkResult>();
        storage.Verify(s => s.HeadObjectAsync(bucket, key, default), Times.Exactly(2));
    }

    [Fact]
    public async Task Get_Range_Valid_Returns_206_And_Headers()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "rb";
        var key = "rk.txt";
        var etag = "W\"r1\"";
        var len = 10L;
        // Head reports total length 10 and content-type
        storage
            .Setup(s => s.HeadObjectAsync(bucket, key, default))
            .ReturnsAsync(
                new ObjectInfo(
                    bucket,
                    key,
                    len,
                    "text/plain",
                    etag,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            );
        // Return a 0-3 range stream
        var bytes = new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' };
        storage
            .Setup(s => s.GetObjectRangeAsync(bucket, key, 0, 3, default))
            .ReturnsAsync(
                (
                    new ObjectInfo(
                        bucket,
                        key,
                        len,
                        "text/plain",
                        etag,
                        DateTimeOffset.UtcNow,
                        new Dictionary<string, string>()
                    ),
                    new MemoryStream(bytes) as Stream
                )
            );
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Get;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        httpCtx.Request.Headers["Range"] = "bytes=0-3";
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var action = await controller.Get(bucket, key, default);
        httpCtx.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        httpCtx.Response.Headers["ETag"].ToString().Should().Be(etag);
        httpCtx.Response.Headers["Accept-Ranges"].ToString().Should().Be("bytes");
        httpCtx.Response.Headers["Content-Range"].ToString().Should().Be($"bytes 0-3/{len}");
        var fs = Assert.IsType<FileStreamResult>(action);
        fs.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Get_Range_Invalid_Returns_416()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "rb2";
        var key = "rk2.txt";
        var etag = "W\"r2\"";
        var len = 5L;
        storage
            .Setup(s => s.HeadObjectAsync(bucket, key, default))
            .ReturnsAsync(
                new ObjectInfo(
                    bucket,
                    key,
                    len,
                    "text/plain",
                    etag,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            );
        // Simulate storage rejecting out-of-range with null
        // Controller clamps end to head.Length - 1 (=4). Set up mock accordingly to return null (out of range)
        storage
            .Setup(s => s.GetObjectRangeAsync(bucket, key, 10, 4, default))
            .ReturnsAsync(((ObjectInfo, Stream)?)null);
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Get;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
    httpCtx.Request.Headers["Range"] = "bytes=10-20"; // invalid given length=5
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var action = await controller.Get(bucket, key, default);
        var pr = Assert.IsType<ObjectResult>(action);
        pr.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
    }

    [Fact]
    public async Task Put_QuotaExceeded_Returns_ProblemDetails_With_Extensions()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "qb";
        var key = "qk.txt";
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);
        var eval = new QuotaEvaluation(
            Exceeded: true,
            Reason: "MaxTotalBytes",
            MaxTotalBytes: 100,
            MaxObjectSizeBytes: 50,
            MaxObjectCount: 10,
            CurrentTotalBytes: 80,
            CurrentObjectCount: 3,
            IncomingBytes: 30
        );
        quotas.Setup(q => q.EvaluateAsync(It.IsAny<long>(), default)).ReturnsAsync(eval);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Put;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        httpCtx.Request.ContentType = "text/plain";
        httpCtx.Request.ContentLength = 30;
        httpCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 30)));
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Put(bucket, key, default);
        var pr = Assert.IsType<ObjectResult>(result);
        pr.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        var pd = Assert.IsType<ProblemDetails>(pr.Value);
        pd.Title.Should().Be("Quota exceeded");
        pd.Detail.Should().Be("MaxTotalBytes");
        pd.Extensions["maxTotalBytes"].Should().Be(100L);
        pd.Extensions["maxObjectSizeBytes"].Should().Be(50L);
        pd.Extensions["maxObjectCount"].Should().Be(10L);
        pd.Extensions["currentTotalBytes"].Should().Be(80L);
        pd.Extensions["currentObjectCount"].Should().Be(3L);
        pd.Extensions["incomingBytes"].Should().Be(30L);
    }

    [Fact]
    public async Task Get_IfNoneMatch_Returns_304_On_Match()
    {
        var controller = Create(
            out var storage,
            out var tenant,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "b";
        var key = "k.txt";
        var etag = "W\"abc\"";
        storage
            .Setup(s => s.HeadObjectAsync(bucket, key, default))
            .ReturnsAsync(
                new ObjectInfo(
                    bucket,
                    key,
                    3,
                    "text/plain",
                    etag,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            );

        // No auth path uses presign; for unit simplicity stub presign validation to true when called
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Get;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        httpCtx.Request.Headers["If-None-Match"] = etag;
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var user = new System.Security.Claims.ClaimsPrincipal(); // unauthenticated -> presign path
        httpCtx.User = user;
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Get(bucket, key, default);
        var obj = Assert.IsType<StatusCodeResult>(result);
        obj.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    [Fact]
    public async Task Get_IfMatch_Returns_412_On_Mismatch()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "b2";
        var key = "k2.txt";
        var etag = "W\"xyz\"";
        storage
            .Setup(s => s.HeadObjectAsync(bucket, key, default))
            .ReturnsAsync(
                new ObjectInfo(
                    bucket,
                    key,
                    1,
                    "text/plain",
                    etag,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            );

        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Get;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        httpCtx.Request.Headers["If-Match"] = "\"strong\""; // mismatch
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Get(bucket, key, default);
        var obj = Assert.IsType<StatusCodeResult>(result);
        obj.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task Put_Presign_ContentType_Mismatch_Returns_415_And_Logs()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "b3";
        var key = "k3.txt";
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);
        quotas
            .Setup(q => q.EvaluateAsync(It.IsAny<long>(), default))
            .ReturnsAsync(new QuotaEvaluation(false, null, 0, 0, 0, 0, 0, 0));

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Put;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s&ct=text/plain");
        httpCtx.Request.ContentType = "application/json; charset=utf-8"; // mismatch to text/plain
        httpCtx.Request.ContentLength = 2;
        httpCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Put(bucket, key, default);
        var pr = Assert.IsType<ObjectResult>(result);
        pr.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        // Ensure storage wasn't called
        storage.Verify(
            s =>
                s.PutObjectAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<IDictionary<string, string>>(),
                    default
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Put_Invokes_Antivirus_Scan_After_Save()
    {
        var controller = Create(
            out var storage,
            out _,
            out var presign,
            out var quotas,
            out var av
        );
        var bucket = "b4";
        var key = "k4.txt";
        presign
            .Setup(p =>
                p.Validate(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    bucket,
                    key,
                    It.IsAny<long>(),
                    It.IsAny<long?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string>()
                )
            )
            .Returns(true);
        quotas
            .Setup(q => q.EvaluateAsync(It.IsAny<long>(), default))
            .ReturnsAsync(new QuotaEvaluation(false, null, 0, 0, 0, 0, 0, 0));
        storage
            .Setup(s =>
                s.PutObjectAsync(
                    bucket,
                    key,
                    It.IsAny<Stream>(),
                    "text/plain",
                    It.IsAny<IDictionary<string, string>>(),
                    default
                )
            )
            .Returns(Task.CompletedTask);
        storage
            .Setup(s => s.HeadObjectAsync(bucket, key, default))
            .ReturnsAsync(
                new ObjectInfo(
                    bucket,
                    key,
                    2,
                    "text/plain",
                    "W\"etag\"",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()
                )
            );
        av.Setup(a => a.ScanObjectAsync(bucket, key, default)).ReturnsAsync(true);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Method = HttpMethods.Put;
        httpCtx.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        httpCtx.Request.ContentType = "text/plain";
        httpCtx.Request.ContentLength = 2;
        httpCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("hi"));
        httpCtx.Request.Headers["X-Tansu-Tenant"] = "tenant-ut";
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Put(bucket, key, default);
        var cr = Assert.IsType<CreatedResult>(result);
        cr.StatusCode.Should().Be(StatusCodes.Status201Created);
        av.Verify(a => a.ScanObjectAsync(bucket, key, default), Times.Once);
    }
} // End of Class ObjectsControllerTests
