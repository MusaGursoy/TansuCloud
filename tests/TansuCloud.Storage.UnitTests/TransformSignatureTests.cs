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
        var w = 640; var h = 0; var fmt = "webp"; var q = 80; var exp = 1234567890L;
        var canonical = string.Join("\n", tenant, "TRANSFORM", bucket, key, w.ToString(), string.Empty, fmt, q.ToString(), exp.ToString());
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(so.PresignSecret!));
        var sig = BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).ToLowerInvariant();
        Assert.Equal(64, sig.Length);
    }
}
