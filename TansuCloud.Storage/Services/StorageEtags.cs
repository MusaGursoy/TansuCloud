// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text;

namespace TansuCloud.Storage.Services;

public static class StorageEtags
{
    public static string ComputeWeak(byte[] input)
    {
        using var hasher = SHA256.Create();
        var hash = hasher.ComputeHash(input);
        var b64 = Convert.ToBase64String(hash);
        return $"W/\"{b64}\"";
    }

    public static string ComputeWeak(ReadOnlySpan<byte> input)
    {
        using var hasher = SHA256.Create();
        Span<byte> buf = stackalloc byte[32];
        if (!hasher.TryComputeHash(input, buf, out var written))
        {
            var arr = input.ToArray();
            return ComputeWeak(arr);
        }
        var b64 = Convert.ToBase64String(buf[..written]);
        return $"W/\"{b64}\"";
    }

    public static string ComputeWeak(string s) => ComputeWeak(Encoding.UTF8.GetBytes(s));
} // End of Class StorageEtags
