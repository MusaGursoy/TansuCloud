// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using TansuCloud.Identity.Infrastructure.Keys;

namespace TansuCloud.Identity.Infrastructure.Options;

/// <summary>
/// Registers persisted RSA signing keys from IKeyStore into OpenIddictServerOptions without building a temporary service provider.
/// Ensures an initial key exists on first run.
/// </summary>
public sealed class OpenIddictSigningOptionsSetup(IServiceScopeFactory scopeFactory)
    : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        // Resolve scoped services safely inside a created scope
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        // Ensure at least one current key exists
        store.GetCurrentAsync().GetAwaiter().GetResult();

        var keys = store.GetAllAsync().GetAwaiter().GetResult();
        foreach (var k in keys)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(k.Json);
            var root = doc.RootElement;
            static byte[] FromB64Url(string? s) =>
                string.IsNullOrEmpty(s)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(
                        s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '=')
                    );
            var parms = new RSAParameters
            {
                Modulus = FromB64Url(root.GetProperty("n").GetString()),
                Exponent = FromB64Url(root.GetProperty("e").GetString()),
                D = root.TryGetProperty("d", out var dProp) ? FromB64Url(dProp.GetString()) : null,
                P = root.TryGetProperty("p", out var pProp) ? FromB64Url(pProp.GetString()) : null,
                Q = root.TryGetProperty("q", out var qProp) ? FromB64Url(qProp.GetString()) : null,
                DP = root.TryGetProperty("dp", out var dpProp)
                    ? FromB64Url(dpProp.GetString())
                    : null,
                DQ = root.TryGetProperty("dq", out var dqProp)
                    ? FromB64Url(dqProp.GetString())
                    : null,
                InverseQ = root.TryGetProperty("qi", out var qiProp)
                    ? FromB64Url(qiProp.GetString())
                    : null
            };

            var rsa = RSA.Create();
            rsa.ImportParameters(parms);
            options.SigningCredentials.Add(
                new SigningCredentials(
                    new RsaSecurityKey(rsa) { KeyId = k.Kid },
                    SecurityAlgorithms.RsaSha256
                )
            );
        }
    }
}
