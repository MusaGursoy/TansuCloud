// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Extensions.Options;
using TansuCloud.Storage.Services;

public class PresignServiceTests
{
    private static IPresignService Create(string secret = "dev-presign") =>
        new PresignService(Options.Create(new StorageOptions { PresignSecret = secret }));

    [Fact]
    public void Validate_ReturnsTrue_ForMatchingSignature()
    {
        var svc = Create();
        var tenant = "t1";
        var method = "PUT";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        long? max = 123;
        string? ct = "text/plain";
        var sig = svc.CreateSignature(tenant, method, bucket, key, exp, max, ct);
        svc.Validate(tenant, method, bucket, key, exp, max, ct, sig).Should().BeTrue();
    }

    [Theory]
    [InlineData("bad-secret")]
    public void Validate_False_WhenSecretDiffers(string otherSecret)
    {
        var tenant = "t1";
        var method = "GET";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        long? max = null;
        string? ct = null;
        var svcA = Create();
        var svcB = Create(otherSecret);
        var sig = svcA.CreateSignature(tenant, method, bucket, key, exp, max, ct);
        svcB.Validate(tenant, method, bucket, key, exp, max, ct, sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_False_OnMethodMismatch()
    {
        var svc = Create();
        var tenant = "t1";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        var sig = svc.CreateSignature(tenant, "PUT", bucket, key, exp, 10, "application/json");
        svc.Validate(tenant, "GET", bucket, key, exp, 10, "application/json", sig)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(null, 100L)]
    [InlineData(100L, null)]
    [InlineData(100L, 200L)]
    public void Validate_False_WhenMaxBytes_Differs(long? a, long? b)
    {
        var svc = Create();
        var tenant = "t1";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        var sig = svc.CreateSignature(tenant, "PUT", bucket, key, exp, a, "text/plain");
        svc.Validate(tenant, "PUT", bucket, key, exp, b, "text/plain", sig).Should().BeFalse();
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("text/plain", "text/plain; charset=utf-8")]
    public void Validate_IsStrict_OnContentType_InSignature(string ctSigned, string ctRequest)
    {
        var svc = Create();
        var tenant = "t1";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        // Signature binds to ctSigned exactly
        var sig = svc.CreateSignature(tenant, "PUT", bucket, key, exp, 100, ctSigned);
        // Validation compares canonical string; any change (including parameters) invalidates
        var shouldBeValid = ctRequest == ctSigned;
        svc.Validate(tenant, "PUT", bucket, key, exp, 100, ctRequest, sig)
            .Should()
            .Be(shouldBeValid);
    }

    [Fact]
    public void Validate_False_WhenMissingRequiredParameters()
    {
        var svc = Create();
        var tenant = "t1";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        var sig = svc.CreateSignature(tenant, "PUT", bucket, key, exp, 100, "text/plain");
        // Missing content-type param (ct) compared against a signature that included ct ⇒ should be false
        svc.Validate(tenant, "PUT", bucket, key, exp, 100, null, sig).Should().BeFalse();
        // Missing max compared against a signature that included max ⇒ should be false
        svc.Validate(tenant, "PUT", bucket, key, exp, null, "text/plain", sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_False_WhenExtraParametersProvided()
    {
        var svc = Create();
        var tenant = "t1";
        var bucket = "b";
        var key = "k";
        long exp = 1700000000;
        // Signature without optional params
        var sig = svc.CreateSignature(tenant, "GET", bucket, key, exp, null, null);
        // Providing extra params during validation should fail
        svc.Validate(tenant, "GET", bucket, key, exp, 1, null, sig).Should().BeFalse();
        svc.Validate(tenant, "GET", bucket, key, exp, null, "text/plain", sig).Should().BeFalse();
    }
}
