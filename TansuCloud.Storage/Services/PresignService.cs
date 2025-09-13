// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TansuCloud.Storage.Services;

public interface IPresignService
{
    string CreateSignature(
        string tenantId,
        string method,
        string bucket,
        string key,
        long expiresUnix,
        long? maxBytes,
        string? contentType
    );

    bool Validate(
        string tenantId,
        string method,
        string bucket,
        string key,
        long expiresUnix,
        long? maxBytes,
        string? contentType,
        string signature
    );

    string CreateTransformSignature(
        string tenantId,
        string bucket,
        string key,
        int? width,
        int? height,
        string? format,
        int? quality,
        long expiresUnix
    );
}

internal sealed class PresignService(IOptions<StorageOptions> options) : IPresignService
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.PresignSecret ?? "");

    public string CreateSignature(
        string tenantId,
        string method,
        string bucket,
        string key,
        long expiresUnix,
        long? maxBytes,
        string? contentType
    )
    {
        var canonical = BuildCanonical(
            tenantId,
            method,
            bucket,
            key,
            expiresUnix,
            maxBytes,
            contentType
        );
        return ComputeHmacHex(_key, canonical);
    }

    public bool Validate(
        string tenantId,
        string method,
        string bucket,
        string key,
        long expiresUnix,
        long? maxBytes,
        string? contentType,
        string signature
    )
    {
        if (_key.Length == 0)
            return false; // not configured
        var canonical = BuildCanonical(
            tenantId,
            method,
            bucket,
            key,
            expiresUnix,
            maxBytes,
            contentType
        );
        var actual = ComputeHmacHex(_key, canonical);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(signature)
        );
    }

    public string CreateTransformSignature(
        string tenantId,
        string bucket,
        string key,
        int? width,
        int? height,
        string? format,
        int? quality,
        long expiresUnix
    )
    {
        var canonical = BuildTransformCanonical(
            tenantId,
            bucket,
            key,
            width,
            height,
            format,
            quality,
            expiresUnix
        );
        return ComputeHmacHex(_key, canonical);
    }

    private static string BuildCanonical(
        string tenantId,
        string method,
        string bucket,
        string key,
        long expiresUnix,
        long? maxBytes,
        string? contentType
    )
    {
        return string.Join(
            "\n",
            tenantId,
            method.ToUpperInvariant(),
            bucket,
            key,
            expiresUnix.ToString(),
            maxBytes?.ToString() ?? string.Empty,
            contentType ?? string.Empty
        );
    }

    private static string ComputeHmacHex(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        var bytes = Encoding.UTF8.GetBytes(data);
        var hash = hmac.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string BuildTransformCanonical(
        string tenantId,
        string bucket,
        string key,
        int? width,
        int? height,
        string? format,
        int? quality,
        long expiresUnix
    )
    {
        return string.Join(
            "\n",
            tenantId,
            "TRANSFORM",
            bucket,
            key,
            width?.ToString() ?? string.Empty,
            height?.ToString() ?? string.Empty,
            format ?? string.Empty,
            quality?.ToString() ?? string.Empty,
            expiresUnix.ToString()
        );
    }
} // End of Class PresignService
