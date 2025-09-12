// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using TansuCloud.Storage.Controllers;
using TansuCloud.Storage.Services;

public sealed class ObjectsControllerTests
{
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
        return new ObjectsController(
            storage.Object,
            tenant.Object,
            presign.Object,
            quotas.Object,
            av.Object,
            logger
        );
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
        storage
            .Setup(s => s.GetObjectRangeAsync(bucket, key, 10, 20, default))
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
        var ctrlCtx = new ControllerContext { HttpContext = httpCtx };
        controller.ControllerContext = ctrlCtx;

        var result = await controller.Put(bucket, key, default);
        var cr = Assert.IsType<CreatedResult>(result);
        cr.StatusCode.Should().Be(StatusCodes.Status201Created);
        av.Verify(a => a.ScanObjectAsync(bucket, key, default), Times.Once);
    }
} // End of Class ObjectsControllerTests
