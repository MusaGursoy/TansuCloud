// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TansuCloud.Identity.Data;

namespace TansuCloud.Identity.Infrastructure.Keys;

public interface IKeyStore
{
    Task<JwkKey> GetCurrentAsync(CancellationToken ct = default);
    Task<(JwkKey current, JwkKey next)> RotateAsync(
        TimeSpan gracePeriod,
        CancellationToken ct = default
    );
    Task<IEnumerable<JwkKey>> GetAllAsync(CancellationToken ct = default);
    Task<int> CleanupRetiredAsync(CancellationToken ct = default);
}

public sealed class KeyStore(AppDbContext db, ILogger<KeyStore> logger) : IKeyStore
{
    public async Task<JwkKey> GetCurrentAsync(CancellationToken ct = default)
    {
        var cur = await db
            .JwkKeys.AsNoTracking()
            .OrderByDescending(k => k.IsCurrent)
            .ThenByDescending(k => k.Id)
            .FirstOrDefaultAsync(ct);
        if (cur is not null)
            return cur;
        var created = await CreateNewRsaAsync(isCurrent: true, ct: ct);
        logger.LogInformation("[JWKS] Created initial signing key {Kid}", created.Kid);
        return created;
    }

    public async Task<(JwkKey current, JwkKey next)> RotateAsync(
        TimeSpan gracePeriod,
        CancellationToken ct = default
    )
    {
        var current =
            await db
                .JwkKeys.Where(k => k.IsCurrent)
                .OrderByDescending(k => k.Id)
                .FirstOrDefaultAsync(ct) ?? await CreateNewRsaAsync(isCurrent: true, ct: ct);

        current.IsCurrent = false;
        current.RetireAfter = DateTimeOffset.UtcNow.Add(gracePeriod);
        var next = await CreateNewRsaAsync(isCurrent: true, ct: ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "[JWKS] Rotated keys: old {OldKid} retiring after {RetireAfter}, new {NewKid}",
            current.Kid,
            current.RetireAfter,
            next.Kid
        );
        return (current, next);
    }

    public async Task<IEnumerable<JwkKey>> GetAllAsync(CancellationToken ct = default)
    {
        return await db
            .JwkKeys.AsNoTracking()
            .OrderByDescending(k => k.IsCurrent)
            .ThenByDescending(k => k.Id)
            .ToListAsync(ct);
    }

    public async Task<int> CleanupRetiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var retired = await db
            .JwkKeys.Where(k => !k.IsCurrent && k.RetireAfter != null && k.RetireAfter < now)
            .ToListAsync(ct);
        if (retired.Count == 0)
            return 0;
        db.JwkKeys.RemoveRange(retired);
        await db.SaveChangesAsync(ct);
        return retired.Count;
    }

    private static string Base64Url(byte[] input) =>
        Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<JwkKey> CreateNewRsaAsync(bool isCurrent, CancellationToken ct)
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(includePrivateParameters: true);
        var kid = Guid.NewGuid().ToString("N");
        var jwkObj = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["kid"] = kid,
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["n"] = Base64Url(p.Modulus!),
            ["e"] = Base64Url(p.Exponent!),
            ["d"] = Base64Url(p.D!),
            ["p"] = Base64Url(p.P!),
            ["q"] = Base64Url(p.Q!),
            ["dp"] = Base64Url(p.DP!),
            ["dq"] = Base64Url(p.DQ!),
            ["qi"] = Base64Url(p.InverseQ!)
        };
        var jwkJson = JsonSerializer.Serialize(jwkObj);
        var entity = new JwkKey
        {
            Kid = kid,
            Use = "sig",
            Alg = "RS256",
            Json = jwkJson,
            IsCurrent = isCurrent
        };
        await db.JwkKeys.AddAsync(entity, ct);
        await db.SaveChangesAsync(ct);
        return entity;
    }
}
