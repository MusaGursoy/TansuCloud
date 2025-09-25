// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TansuCloud.Gateway.Services;

/// <summary>
/// Runtime registry for domain-to-certificate bindings. Dev-only scaffold for Task 17 iteration 2.
/// Does not persist secrets; only stores non-sensitive metadata for display and auditing.
/// </summary>
public interface IDomainTlsRuntime
{
    IReadOnlyCollection<DomainBindingInfo> List();

    DomainBindingInfo AddOrReplace(DomainBindRequestDto request);

    DomainBindingInfo AddOrReplacePem(DomainBindPemRequestDto request);

    (DomainBindingInfo Current, DomainBindingInfo? Previous) Rotate(DomainRotateRequestDto request);

    bool Remove(string host);
} // End of Interface IDomainTlsRuntime

public sealed record DomainBindRequestDto
{
    public required string Host { get; init; } // normalized lower-case host

    // Base64 of a PFX that includes the private key. We don't store it; only validate and derive metadata.
    public required string PfxBase64 { get; init; }

    public string? PfxPassword { get; init; }
} // End of Record DomainBindRequestDto

public sealed record DomainBindPemRequestDto
{
    public required string Host { get; init; } // normalized lower-case host

    // PEM-encoded certificate (and optionally cert chain). Private key provided separately.
    public required string CertPem { get; init; }

    // PEM-encoded private key (PKCS#8 or PKCS#1).
    public required string KeyPem { get; init; }

    // Optional: additional chain PEMs concatenated
    public string? ChainPem { get; init; }
} // End of Record DomainBindPemRequestDto

public sealed record DomainRotateRequestDto
{
    public required string Host { get; init; }

    // One of the following inputs must be provided
    public string? PfxBase64 { get; init; }
    public string? PfxPassword { get; init; }

    public string? CertPem { get; init; }
    public string? KeyPem { get; init; }
    public string? ChainPem { get; init; }
} // End of Record DomainRotateRequestDto

public sealed record DomainBindingInfo
{
    public required string Host { get; init; }
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required string Thumbprint { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    public required bool HasPrivateKey { get; init; }
    public bool HostnameMatches { get; init; }

    // Chain metadata (non-secret): indicates if an intermediate chain was provided and whether it linked.
    public bool ChainProvided { get; init; }
    public bool ChainValidated { get; init; }
    public int ChainCount { get; init; }
} // End of Record DomainBindingInfo

internal sealed class DomainTlsRuntime : IDomainTlsRuntime
{
    private readonly ConcurrentDictionary<string, DomainBindingInfo> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DomainBindingInfo> List() =>
        _bindings.Values.OrderBy(b => b.Host, StringComparer.OrdinalIgnoreCase).ToArray();

    public DomainBindingInfo AddOrReplace(DomainBindRequestDto request)
    {
        var host = NormalizeHost(request.Host);
        // Validate cert
        var raw = Convert.FromBase64String(request.PfxBase64);

        // Load as X509Certificate2 to validate basics and extract metadata
        using var cert = X509CertificateLoader.LoadPkcs12(
            raw,
            request.PfxPassword,
            X509KeyStorageFlags.EphemeralKeySet
                | X509KeyStorageFlags.MachineKeySet
                | X509KeyStorageFlags.Exportable
        );

        if (!cert.HasPrivateKey)
        {
            throw new CryptographicException("Certificate must contain a private key.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < cert.NotBefore || now > cert.NotAfter)
        {
            throw new CryptographicException("Certificate is not currently valid (date range).");
        }

        var matches = MatchesHost(cert, host);

        // Optional: detect chain inside the PFX (non-secret)
        bool chainProvided = false;
        bool chainValidated = false;
        int chainCount = 0;
        try
        {
            var coll = X509CertificateLoader.LoadPkcs12Collection(
                raw,
                request.PfxPassword,
                X509KeyStorageFlags.EphemeralKeySet
                    | X509KeyStorageFlags.MachineKeySet
                    | X509KeyStorageFlags.Exportable
            );
            // Count additional certs beyond leaf
            chainCount = Math.Max(0, coll.Count - 1);
            chainProvided = chainCount > 0;
            if (chainProvided)
            {
                using var chain = new X509Chain();
                foreach (var c in coll)
                {
                    if (
                        !c.HasPrivateKey
                        && !string.Equals(
                            c.Thumbprint,
                            cert.Thumbprint,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        chain.ChainPolicy.ExtraStore.Add(c);
                    }
                }
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags =
                    X509VerificationFlags.AllowUnknownCertificateAuthority;
                chainValidated = chain.Build(cert);
            }
        }
        catch
        {
            // best-effort only
        }

        var info = new DomainBindingInfo
        {
            Host = host,
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            Thumbprint = cert.Thumbprint ?? string.Empty,
            NotBefore = cert.NotBefore,
            NotAfter = cert.NotAfter,
            HasPrivateKey = cert.HasPrivateKey,
            HostnameMatches = matches,
            ChainProvided = chainProvided,
            ChainValidated = chainValidated,
            ChainCount = chainCount
        };

        _bindings[host] = info;
        return info;
    } // End of Method AddOrReplace

    public DomainBindingInfo AddOrReplacePem(DomainBindPemRequestDto request)
    {
        var host = NormalizeHost(request.Host);
        if (string.IsNullOrWhiteSpace(request.CertPem))
            throw new ArgumentException("CertPem is required", nameof(request.CertPem));
        if (string.IsNullOrWhiteSpace(request.KeyPem))
            throw new ArgumentException("KeyPem is required", nameof(request.KeyPem));

        // Build certificate with private key from PEM
        using var cert = X509Certificate2.CreateFromPem(request.CertPem, request.KeyPem);
        using var withKey = cert;

        if (!withKey.HasPrivateKey)
        {
            throw new CryptographicException("Certificate must contain a private key.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < withKey.NotBefore || now > withKey.NotAfter)
        {
            throw new CryptographicException("Certificate is not currently valid (date range).");
        }

        var matches = MatchesHost(withKey, host);

        // Parse and validate optional chain
        bool chainProvided = false;
        bool chainValidated = false;
        int chainCount = 0;
        if (!string.IsNullOrWhiteSpace(request.ChainPem))
        {
            try
            {
                var chainCerts = ParseCertificatesFromPem(request.ChainPem);
                chainCount = chainCerts.Count;
                chainProvided = chainCount > 0;
                if (chainProvided)
                {
                    using var chain = new X509Chain();
                    foreach (var c in chainCerts)
                    {
                        chain.ChainPolicy.ExtraStore.Add(c);
                    }
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                    // Allow unknown root CA to avoid requiring system trust in dev; we only check linkage
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chainValidated = chain.Build(withKey);
                }
            }
            catch
            {
                // best-effort only
            }
        }

        var info = new DomainBindingInfo
        {
            Host = host,
            Subject = withKey.Subject,
            Issuer = withKey.Issuer,
            Thumbprint = withKey.Thumbprint ?? string.Empty,
            NotBefore = withKey.NotBefore,
            NotAfter = withKey.NotAfter,
            HasPrivateKey = withKey.HasPrivateKey,
            HostnameMatches = matches,
            ChainProvided = chainProvided,
            ChainValidated = chainValidated,
            ChainCount = chainCount
        };

        _bindings[host] = info;
        return info;
    } // End of Method AddOrReplacePem

    public (DomainBindingInfo Current, DomainBindingInfo? Previous) Rotate(
        DomainRotateRequestDto request
    )
    {
        var host = NormalizeHost(request.Host);
        _bindings.TryGetValue(host, out var prev);

        DomainBindingInfo current;
        if (!string.IsNullOrWhiteSpace(request.PfxBase64))
        {
            current = AddOrReplace(
                new DomainBindRequestDto
                {
                    Host = host,
                    PfxBase64 = request.PfxBase64!,
                    PfxPassword = request.PfxPassword
                }
            );
        }
        else if (
            !string.IsNullOrWhiteSpace(request.CertPem)
            && !string.IsNullOrWhiteSpace(request.KeyPem)
        )
        {
            current = AddOrReplacePem(
                new DomainBindPemRequestDto
                {
                    Host = host,
                    CertPem = request.CertPem!,
                    KeyPem = request.KeyPem!,
                    ChainPem = request.ChainPem
                }
            );
        }
        else
        {
            throw new ArgumentException("Either PfxBase64 or (CertPem + KeyPem) must be provided.");
        }

        return (current, prev);
    } // End of Method Rotate

    public bool Remove(string host)
    {
        host = NormalizeHost(host);
        return _bindings.TryRemove(host, out _);
    } // End of Method Remove

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host is required", nameof(host));
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    } // End of Method NormalizeHost

    private static bool MatchesHost(X509Certificate2 cert, string host)
    {
        try
        {
            // Prefer SANs
            foreach (var ext in cert.Extensions)
            {
                if (ext.Oid?.Value == "2.5.29.17") // subjectAltName
                {
                    var san = new AsnEncodedData(ext.Oid, ext.RawData).Format(true);
                    // Format(true) gives a multi-line string like DNS Name=example.com
                    var lines = san.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var idx = line.IndexOf('=');
                        if (idx > 0)
                        {
                            var value = line[(idx + 1)..].Trim();
                            if (HostMatchesPattern(host, value))
                                return true;
                        }
                    }
                }
            }

            // Fallback to CN
            var subject = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrWhiteSpace(subject) && HostMatchesPattern(host, subject))
                return true;
        }
        catch
        {
            // best-effort only
        }
        return false;
    } // End of Method MatchesHost

    private static bool HostMatchesPattern(string host, string pattern)
    {
        host = NormalizeHost(host);
        pattern = NormalizeHost(pattern);
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..]; // .example.com
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Count(c => c == '.') >= suffix.Count(c => c == '.');
        }
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    } // End of Method HostMatchesPattern

    private static List<X509Certificate2> ParseCertificatesFromPem(string pem)
    {
        var list = new List<X509Certificate2>();
        if (string.IsNullOrWhiteSpace(pem))
            return list;
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        int idx = 0;
        while (true)
        {
            var start = pem.IndexOf(begin, idx, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                break;
            var finish = pem.IndexOf(end, start, StringComparison.OrdinalIgnoreCase);
            if (finish < 0)
                break;
            var base64 = pem.Substring(start + begin.Length, finish - (start + begin.Length));
            base64 = new string(base64.Where(c => !char.IsWhiteSpace(c)).ToArray());
            try
            {
                var raw = Convert.FromBase64String(base64);
                // Use X509CertificateLoader for non-obsolete loading
                var c = X509CertificateLoader.LoadCertificate(new ReadOnlySpan<byte>(raw));
                list.Add(c);
            }
            catch
            {
                // ignore malformed blocks
            }
            idx = finish + end.Length;
        }
        return list;
    } // End of Method ParseCertificatesFromPem
} // End of Class DomainTlsRuntime
