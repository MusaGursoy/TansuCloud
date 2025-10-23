// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TansuCloud.Identity.Data.Entities;
using TansuCloud.Identity.Infrastructure.Keys;

namespace TansuCloud.Identity.Controllers;

[ApiController]
public sealed class JwksController(IKeyStore store) : ControllerBase
{
    // Serve JWKS at both the standard endpoint and the OpenIddict-advertised location
    [HttpGet(".well-known/openid-configuration/jwks")]
    [HttpGet(".well-known/jwks")]
    public async Task<IActionResult> GetJwks(CancellationToken ct)
    {
        var keys = await store.GetAllAsync(ct);
        // Only include public parameters
        var jwks = new
        {
            keys = keys.Select(k =>
            {
                using var doc = JsonDocument.Parse(k.Json);
                var root = doc.RootElement;
                return new
                {
                    kty = root.GetProperty("kty").GetString(),
                    kid = root.GetProperty("kid").GetString(),
                    use = root.GetProperty("use").GetString(),
                    alg = root.GetProperty("alg").GetString(),
                    n = root.GetProperty("n").GetString(),
                    e = root.GetProperty("e").GetString()
                };
            })
        };
        return Ok(jwks);
    }
}
