// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Observability.Auditing;
using TansuCloud.Storage.Controllers;
using TansuCloud.Storage.Services;

public sealed class MultipartControllerTests
{
    private static MultipartController Create(
        out Mock<IMultipartStorage> mp,
        out Mock<ITenantContext> tenant,
        out Mock<IPresignService> presign,
        out Mock<IQuotaService> quotas,
        long minPartSize = 5,
        long maxPartSize = 0
    )
    {
        mp = new Mock<IMultipartStorage>(MockBehavior.Strict);
        tenant = new Mock<ITenantContext>(MockBehavior.Strict);
        presign = new Mock<IPresignService>(MockBehavior.Strict);
        quotas = new Mock<IQuotaService>(MockBehavior.Strict);
        tenant.Setup(t => t.TenantId).Returns("tenant-ut");
        var opts = Options.Create(
            new StorageOptions
            {
                MultipartMinPartSizeBytes = minPartSize,
                MultipartMaxPartSizeBytes = maxPartSize
            }
        );
        var logger = Mock.Of<ILogger<MultipartController>>();
        var audit = new Mock<IAuditLogger>(MockBehavior.Loose);
        audit.Setup(a => a.TryEnqueue(It.IsAny<AuditEvent>())).Returns(true);
        return new MultipartController(
            mp.Object,
            tenant.Object,
            presign.Object,
            opts,
            quotas.Object,
            audit.Object
        );
    }

    [Fact]
    public async Task Complete_Returns_400_When_Missing_Uploaded_Part()
    {
        var controller = Create(out var mp, out _, out var presign, out var quotas);
        var bucket = "b";
        var key = "k";
        var uploadId = "u";
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
        // Parts info contains only part 1
        mp.Setup(m => m.GetPartsAsync(bucket, key, uploadId, default))
            .ReturnsAsync(new[] { new PartInfo(1, 10) });

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var res = await controller.Complete(
            bucket,
            key,
            uploadId,
            new MultipartController.CompleteRequest(new List<int> { 1, 2 }),
            default
        );
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Assert.Contains("Missing uploaded part 2", pr.Value?.ToString());
    }

    [Fact]
    public async Task Complete_Returns_400_When_NonLast_Part_Below_MinSize()
    {
        var controller = Create(out var mp, out _, out var presign, out var quotas, minPartSize: 5);
        var bucket = "b2";
        var key = "k2";
        var uploadId = "u2";
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
        // part 1 too small (len 3), part 2 ok; part 1 is non-last
        mp.Setup(m => m.GetPartsAsync(bucket, key, uploadId, default))
            .ReturnsAsync(new[] { new PartInfo(1, 3), new PartInfo(2, 10) });

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var res = await controller.Complete(
            bucket,
            key,
            uploadId,
            new MultipartController.CompleteRequest(new List<int> { 1, 2 }),
            default
        );
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Assert.Contains("below minimum size", pr.Value?.ToString());
    }

    [Fact]
    public async Task Complete_Returns_400_For_Duplicate_Part_Numbers()
    {
        var controller = Create(out var mp, out _, out var presign, out var quotas);
        var bucket = "bdup";
        var key = "kdup";
        var uploadId = "udup";
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
        // parts 1 and 2 exist
        mp.Setup(m => m.GetPartsAsync(bucket, key, uploadId, default))
            .ReturnsAsync(new[] { new PartInfo(1, 10), new PartInfo(2, 10) });

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        // Duplicate part number 2 provided
        var res = await controller.Complete(
            bucket,
            key,
            uploadId,
            new MultipartController.CompleteRequest(new List<int> { 1, 2, 2 }),
            default
        );
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Assert.Contains("Duplicate part number", pr.Value?.ToString());
    }

    [Fact]
    public async Task UploadPart_Returns_403_For_Invalid_Presign_Signature()
    {
        var controller = Create(out var mp, out _, out var presign, out var quotas);
        var bucket = "b3";
        var key = "k3";
        var uploadId = "u3";
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
            .Returns(false);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Put;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=bad");
        http.Request.ContentLength = 10;
        http.Request.Body = new MemoryStream(new byte[10]);
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var res = await controller.UploadPart(bucket, key, partNumber: 1, uploadId, default);
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UploadPart_Returns_413_When_Exceeds_MaxPartSize()
    {
        // Set max to 5 bytes
        var controller = Create(
            out var mp,
            out _,
            out var presign,
            out var quotas,
            minPartSize: 1,
            maxPartSize: 5
        );
        var bucket = "bmax";
        var key = "kmax";
        var uploadId = "umax";
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

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Put;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        http.Request.ContentLength = 10; // exceeds max 5
        http.Request.Body = new MemoryStream(new byte[10]);
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var res = await controller.UploadPart(bucket, key, partNumber: 1, uploadId, default);
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Complete_Returns_413_When_LastPart_Exceeds_Max()
    {
        // We configure max=100 bytes; the last part will be 10_000 bytes
        var controller = Create(
            out var mp,
            out _,
            out var presign,
            out var quotas,
            minPartSize: 1,
            maxPartSize: 100
        );
        var bucket = "bmax2";
        var key = "kmax2";
        var uploadId = "umax2";
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

        // parts info: part 1 small, part 2 very large (simulate oversize)
        mp.Setup(m => m.GetPartsAsync(bucket, key, uploadId, default))
            .ReturnsAsync(new[] { new PartInfo(1, 10), new PartInfo(2, 10_000) });

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.QueryString = new QueryString("?exp=9999999999&sig=s");
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var res = await controller.Complete(
            bucket,
            key,
            uploadId,
            new MultipartController.CompleteRequest(new List<int> { 1, 2 }),
            default
        );
        var pr = Assert.IsType<ObjectResult>(res);
        pr.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }
}
