// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Options;
using TansuCloud.Storage.Services;
using Xunit;

namespace TansuCloud.Storage.UnitTests;

public class TransformSignatureTests
{
    [Fact]
    public void Canonical_Consistency_Basic()
    {
        var so = new StorageOptions { PresignSecret = "secret" };
        var tenant = "t1";
        var bucket = "b";
        var key = "k/x.png";
        var w = 640;
    // Height intentionally left as 0 in canonical (via empty string)
        var fmt = "webp";
        var q = 80;
        var exp = 1234567890L;
        var canonical = string.Join(
            "\n",
            tenant,
            "TRANSFORM",
            bucket,
            key,
            w.ToString(),
            string.Empty,
            fmt,
            q.ToString(),
            exp.ToString()
        );
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(so.PresignSecret!)
        );
        var sig = BitConverter
            .ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
            .Replace("-", string.Empty)
            .ToLowerInvariant();
        Assert.Equal(64, sig.Length);
    }

    [Fact]
    public void Transform_Signature_Uses_Unescaped_Key()
    {
        var so = new StorageOptions { PresignSecret = "secret" };
        var svc = new PresignService(Options.Create(so));
        var tenant = "tansu_tenant_demo";
        var bucket = "photos";
        var key = "img/original.png"; // contains a slash
        var escapedKey = Uri.EscapeDataString(key); // "img%2Foriginal.png"
        int? w = 1;
        int? h = null;
        string? fmt = "jpeg";
        int? q = 80;
        long exp = 1_757_699_900L;

        // Signature computed over the unescaped key
        var sig = svc.CreateTransformSignature(tenant, bucket, key, w, h, fmt, q, exp);

        // Recreating the expected signature via the same method but with escaped key must differ
        var sigEscaped = svc.CreateTransformSignature(
            tenant,
            bucket,
            escapedKey,
            w,
            h,
            fmt,
            q,
            exp
        );

        Assert.Equal(64, sig.Length);
        Assert.Equal(64, sigEscaped.Length);
        Assert.NotEqual(sig, sigEscaped);

        // Validate that a controller using the normalized (unescaped) route key would accept
        var canonicalOk = string.Join(
            "\n",
            tenant,
            "TRANSFORM",
            bucket,
            key,
            (w?.ToString() ?? string.Empty),
            (h?.ToString() ?? string.Empty),
            (fmt ?? string.Empty),
            (q?.ToString() ?? string.Empty),
            exp.ToString()
        );
        var expected = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(so.PresignSecret!)
        ).ComputeHash(Encoding.UTF8.GetBytes(canonicalOk));
        var expectedHex = BitConverter.ToString(expected).Replace("-", string.Empty).ToLowerInvariant();
        Assert.Equal(expectedHex, sig);
    }
}
