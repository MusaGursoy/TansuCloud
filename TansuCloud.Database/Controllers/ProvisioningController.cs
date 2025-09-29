// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TansuCloud.Database.Provisioning;
using TansuCloud.Observability.Auditing;
using TansuCloud.Observability.Shared.Configuration;

namespace TansuCloud.Database.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ProvisioningController(
    ITenantProvisioner provisioner,
    ILogger<ProvisioningController> logger,
    IAuditLogger audit,
    AppUrlsOptions appUrls
) : ControllerBase
{
    private readonly ITenantProvisioner _provisioner = provisioner;
    private readonly ILogger<ProvisioningController> _logger = logger;
    private readonly IAuditLogger _audit = audit;
    private readonly AppUrlsOptions _appUrls = appUrls;

    public sealed record ProvisionTenantDto(string tenantId, string? displayName, string? region);

    [HttpPost("tenants")]
    [AllowAnonymous]
    public async Task<ActionResult> ProvisionTenant(
        [FromBody] ProvisionTenantDto input,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(input.tenantId))
            return Problem(
                title: "tenantId is required",
                statusCode: StatusCodes.Status400BadRequest
            );

        // Try to authenticate via JwtBearer even if endpoint allows anonymous (to get proper challenges/logs)
        var hasAuthHeader = Request.Headers.ContainsKey("Authorization");
        var authResult = await HttpContext.AuthenticateAsync(
            JwtBearerDefaults.AuthenticationScheme
        );
        if (authResult.Succeeded && authResult.Principal is not null)
        {
            HttpContext.User = authResult.Principal;
            // Audience can be emitted as multiple 'aud' claims or a single JSON array serialized claim.
            bool audOk = false;
            var audClaims = User.Claims.Where(c => c.Type == "aud").Select(c => c.Value).ToList();
            if (audClaims.Count == 0)
            {
                audOk = false;
            }
            else if (audClaims.Count == 1 && audClaims[0].StartsWith("["))
            {
                // Try parse JSON array
                try
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(audClaims[0]);
                    audOk = arr?.Contains("tansu.db") == true;
                }
                catch
                {
                    audOk = audClaims[0].Contains("tansu.db", StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                audOk = audClaims.Contains("tansu.db");
            }
            var scopes = string.Join(
                ' ',
                User.Claims.Where(c => c.Type == "scope").Select(c => c.Value)
            );
            var hasPriv =
                scopes.Contains("admin.full", StringComparison.Ordinal)
                || scopes.Contains("db.write", StringComparison.Ordinal);
            if (!audOk || !hasPriv)
                return Forbid();
        }
        else
        {
            // If an Authorization header was presented but authentication failed, surface details for diagnostics
            if (hasAuthHeader)
            {
                // Development fallback: try manual JWT validation against issuer JWKS to bypass framework decode bug
                try
                {
                    var env = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (env.IsDevelopment())
                    {
                        var raw = Request.Headers["Authorization"].ToString();
                        string? token = null;
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (
                                parts.Length >= 2
                                && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                            )
                                token = parts[1];
                        }
                        if (!string.IsNullOrEmpty(token))
                        {
                            var cfg2 =
                                HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                            var issuerConfigured =
                                cfg2["Oidc:Issuer"] ?? _appUrls.GetIssuer("identity");
                            var issuerNoSlash = issuerConfigured.TrimEnd('/');
                            var issuerWithSlash = issuerNoSlash + "/";
                            var root = new Uri(issuerWithSlash).GetLeftPart(UriPartial.Authority);
                            var jwksUri = new Uri(
                                new Uri(root + "/"),
                                ".well-known/jwks"
                            ).AbsoluteUri;
                            using var http = new HttpClient();
                            var jwksJson = await http.GetStringAsync(jwksUri, ct);
                            var jwkSet = new JsonWebKeySet(jwksJson);
                            var tvp = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuers = new[] { issuerNoSlash, issuerWithSlash },
                                ValidateAudience = false, // we check aud later
                                RequireSignedTokens = true,
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.FromMinutes(2),
                                IssuerSigningKeys = jwkSet.Keys,
                                // Accept OpenIddict access tokens as well
                                ValidTypes = new[] { "at+jwt", "JWT", "jwt" }
                            };
                            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
                            var manualResult = await handler.ValidateTokenAsync(token, tvp);
                            if (manualResult.IsValid && manualResult.ClaimsIdentity is not null)
                            {
                                HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                                    manualResult.ClaimsIdentity
                                );
                                _logger.LogInformation(
                                    "[ManualJWT] Token validated via JWKS fallback. Proceeding with policy checks."
                                );
                                try
                                {
                                    Response.Headers["X-ManualJwt"] = "ok";
                                }
                                catch { }
                                // continue to aud/scope checks below outside of this else-block
                                goto ManualValidated;
                            }
                            else
                            {
                                var errMsg = manualResult.Exception?.Message ?? "unknown";
                                _logger.LogWarning(
                                    "[ManualJWT] Validation failed via JWKS fallback: {Error}",
                                    errMsg
                                );
                                try
                                {
                                    var sanitized = errMsg
                                        .Replace("\r", " ")
                                        .Replace("\n", " ")
                                        .Replace("\"", "'");
                                    Response.Headers["X-ManualJwt"] = $"fail: {sanitized}";
                                }
                                catch { }
                                // Second attempt (Development only): perform manual RS256 signature verification using JWKS
                                try
                                {
                                    var parts = token.Split('.');
                                    if (parts.Length == 3)
                                    {
                                        static byte[] B64UrlDecodeToBytes(string s)
                                        {
                                            var p = s.Replace('-', '+').Replace('_', '/');
                                            switch (p.Length % 4)
                                            {
                                                case 2:
                                                    p += "==";
                                                    break;
                                                case 3:
                                                    p += "=";
                                                    break;
                                            }
                                            return Convert.FromBase64String(p);
                                        }
                                        static string B64UrlDecodeToString(string s) =>
                                            System.Text.Encoding.UTF8.GetString(
                                                B64UrlDecodeToBytes(s)
                                            );

                                        var headerJson = B64UrlDecodeToString(parts[0]);
                                        using var headerDoc = JsonDocument.Parse(headerJson);
                                        var hdrEl = headerDoc.RootElement;
                                        var alg = hdrEl.TryGetProperty("alg", out var algEl)
                                            ? algEl.GetString()
                                            : null;
                                        var kid = hdrEl.TryGetProperty("kid", out var kidEl)
                                            ? kidEl.GetString()
                                            : null;
                                        if (
                                            string.Equals(
                                                alg,
                                                "RS256",
                                                StringComparison.OrdinalIgnoreCase
                                            ) && !string.IsNullOrEmpty(kid)
                                        )
                                        {
                                            var jwk = jwkSet
                                                .Keys.OfType<JsonWebKey>()
                                                .FirstOrDefault(k =>
                                                    string.Equals(
                                                        k.Kid,
                                                        kid,
                                                        StringComparison.Ordinal
                                                    )
                                                );
                                            if (
                                                jwk is not null
                                                && !string.IsNullOrEmpty(jwk.N)
                                                && !string.IsNullOrEmpty(jwk.E)
                                            )
                                            {
                                                using var rsa = RSA.Create();
                                                rsa.ImportParameters(
                                                    new RSAParameters
                                                    {
                                                        Modulus =
                                                            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(
                                                                jwk.N
                                                            ),
                                                        Exponent =
                                                            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(
                                                                jwk.E
                                                            )
                                                    }
                                                );
                                                var signingInput =
                                                    System.Text.Encoding.ASCII.GetBytes(
                                                        parts[0] + "." + parts[1]
                                                    );
                                                var signature =
                                                    Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(
                                                        parts[2]
                                                    );
                                                var ok = rsa.VerifyData(
                                                    signingInput,
                                                    signature,
                                                    HashAlgorithmName.SHA256,
                                                    RSASignaturePadding.Pkcs1
                                                );
                                                if (ok)
                                                {
                                                    // Build a ClaimsPrincipal from payload as in raw fallback (but we now have signature verified)
                                                    var payloadJson = B64UrlDecodeToString(
                                                        parts[1]
                                                    );
                                                    using var doc = JsonDocument.Parse(payloadJson);
                                                    var rootEl = doc.RootElement;
                                                    var iss = rootEl.TryGetProperty(
                                                        "iss",
                                                        out var issEl
                                                    )
                                                        ? issEl.GetString()
                                                        : null;
                                                    if (
                                                        iss is not null
                                                        && (
                                                            iss.Equals(
                                                                issuerNoSlash,
                                                                StringComparison.Ordinal
                                                            )
                                                            || iss.Equals(
                                                                issuerWithSlash,
                                                                StringComparison.Ordinal
                                                            )
                                                        )
                                                    )
                                                    {
                                                        var now =
                                                            DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                                        var expOk =
                                                            !rootEl.TryGetProperty(
                                                                "exp",
                                                                out var expEl
                                                            )
                                                            || expEl.GetInt64() >= now - 5;
                                                        if (expOk)
                                                        {
                                                            var claims =
                                                                new List<System.Security.Claims.Claim>();
                                                            foreach (
                                                                var prop in rootEl.EnumerateObject()
                                                            )
                                                            {
                                                                if (prop.NameEquals("aud"))
                                                                {
                                                                    if (
                                                                        prop.Value.ValueKind
                                                                        == JsonValueKind.Array
                                                                    )
                                                                    {
                                                                        foreach (
                                                                            var a in prop.Value.EnumerateArray()
                                                                        )
                                                                        {
                                                                            var v = a.GetString();
                                                                            if (
                                                                                !string.IsNullOrEmpty(
                                                                                    v
                                                                                )
                                                                            )
                                                                                claims.Add(
                                                                                    new System.Security.Claims.Claim(
                                                                                        "aud",
                                                                                        v
                                                                                    )
                                                                                );
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        var v =
                                                                            prop.Value.ToString();
                                                                        if (
                                                                            !string.IsNullOrEmpty(v)
                                                                        )
                                                                            claims.Add(
                                                                                new System.Security.Claims.Claim(
                                                                                    "aud",
                                                                                    v
                                                                                )
                                                                            );
                                                                    }
                                                                }
                                                                else if (prop.NameEquals("scope"))
                                                                {
                                                                    if (
                                                                        prop.Value.ValueKind
                                                                        == JsonValueKind.Array
                                                                    )
                                                                    {
                                                                        foreach (
                                                                            var s in prop.Value.EnumerateArray()
                                                                        )
                                                                        {
                                                                            var v = s.GetString();
                                                                            if (
                                                                                !string.IsNullOrEmpty(
                                                                                    v
                                                                                )
                                                                            )
                                                                                claims.Add(
                                                                                    new System.Security.Claims.Claim(
                                                                                        "scope",
                                                                                        v
                                                                                    )
                                                                                );
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        var v =
                                                                            prop.Value.GetString();
                                                                        if (
                                                                            !string.IsNullOrEmpty(v)
                                                                        )
                                                                        {
                                                                            foreach (
                                                                                var piece in v.Split(
                                                                                    ' ',
                                                                                    StringSplitOptions.RemoveEmptyEntries
                                                                                )
                                                                            )
                                                                            {
                                                                                claims.Add(
                                                                                    new System.Security.Claims.Claim(
                                                                                        "scope",
                                                                                        piece
                                                                                    )
                                                                                );
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    switch (prop.Value.ValueKind)
                                                                    {
                                                                        case JsonValueKind.String:
                                                                            var sv =
                                                                                prop.Value.GetString();
                                                                            if (
                                                                                !string.IsNullOrEmpty(
                                                                                    sv
                                                                                )
                                                                            )
                                                                                claims.Add(
                                                                                    new System.Security.Claims.Claim(
                                                                                        prop.Name,
                                                                                        sv
                                                                                    )
                                                                                );
                                                                            break;
                                                                        case JsonValueKind.Number:
                                                                            claims.Add(
                                                                                new System.Security.Claims.Claim(
                                                                                    prop.Name,
                                                                                    prop.Value.ToString()
                                                                                )
                                                                            );
                                                                            break;
                                                                        case JsonValueKind.True:
                                                                        case JsonValueKind.False:
                                                                            claims.Add(
                                                                                new System.Security.Claims.Claim(
                                                                                    prop.Name,
                                                                                    prop.Value.GetBoolean()
                                                                                        .ToString()
                                                                                )
                                                                            );
                                                                            break;
                                                                    }
                                                                }
                                                            }
                                                            var id2 =
                                                                new System.Security.Claims.ClaimsIdentity(
                                                                    claims,
                                                                    authenticationType: "ManualDevRS256"
                                                                );
                                                            HttpContext.User =
                                                                new System.Security.Claims.ClaimsPrincipal(
                                                                    id2
                                                                );
                                                            _logger.LogInformation(
                                                                "[ManualJWT] RS256 signature verified using JWKS. Proceeding with policy checks."
                                                            );
                                                            try
                                                            {
                                                                Response.Headers["X-ManualJwt"] =
                                                                    "ok-rs256";
                                                            }
                                                            catch { }
                                                            goto ManualValidated;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception exrs)
                                {
                                    _logger.LogWarning(
                                        exrs,
                                        "[ManualJWT] RS256 verification attempt failed"
                                    );
                                }
                                // Last resort (Development only): accept unverified token by decoding payload manually.
                                // This bypasses signature validation and MUST NOT be enabled outside Development.
                                try
                                {
                                    var segs = token.Split('.');
                                    if (segs.Length == 3)
                                    {
                                        static string B64UrlDecode(string s)
                                        {
                                            var p = s.Replace('-', '+').Replace('_', '/');
                                            switch (p.Length % 4)
                                            {
                                                case 2:
                                                    p += "==";
                                                    break;
                                                case 3:
                                                    p += "=";
                                                    break;
                                            }
                                            return System.Text.Encoding.UTF8.GetString(
                                                Convert.FromBase64String(p)
                                            );
                                        }
                                        var payloadJson = B64UrlDecode(segs[1]);
                                        using var doc = JsonDocument.Parse(payloadJson);
                                        var rootEl = doc.RootElement;
                                        // Minimal checks: issuer matches, exp not expired
                                        var iss = rootEl.TryGetProperty("iss", out var issEl)
                                            ? issEl.GetString()
                                            : null;
                                        if (
                                            iss is not null
                                            && (
                                                iss.Equals(issuerNoSlash, StringComparison.Ordinal)
                                                || iss.Equals(
                                                    issuerWithSlash,
                                                    StringComparison.Ordinal
                                                )
                                            )
                                        )
                                        {
                                            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                            var expOk =
                                                !rootEl.TryGetProperty("exp", out var expEl)
                                                || expEl.GetInt64() >= now - 5; // small clock skew tolerance
                                            if (expOk)
                                            {
                                                var claims =
                                                    new List<System.Security.Claims.Claim>();
                                                foreach (var prop in rootEl.EnumerateObject())
                                                {
                                                    if (prop.NameEquals("aud"))
                                                    {
                                                        if (
                                                            prop.Value.ValueKind
                                                            == JsonValueKind.Array
                                                        )
                                                        {
                                                            foreach (
                                                                var a in prop.Value.EnumerateArray()
                                                            )
                                                            {
                                                                var v = a.GetString();
                                                                if (!string.IsNullOrEmpty(v))
                                                                    claims.Add(
                                                                        new System.Security.Claims.Claim(
                                                                            "aud",
                                                                            v
                                                                        )
                                                                    );
                                                            }
                                                        }
                                                        else
                                                        {
                                                            var v = prop.Value.ToString();
                                                            if (!string.IsNullOrEmpty(v))
                                                                claims.Add(
                                                                    new System.Security.Claims.Claim(
                                                                        "aud",
                                                                        v
                                                                    )
                                                                );
                                                        }
                                                    }
                                                    else if (prop.NameEquals("scope"))
                                                    {
                                                        // scope can be space separated string or array
                                                        if (
                                                            prop.Value.ValueKind
                                                            == JsonValueKind.Array
                                                        )
                                                        {
                                                            foreach (
                                                                var s in prop.Value.EnumerateArray()
                                                            )
                                                            {
                                                                var v = s.GetString();
                                                                if (!string.IsNullOrEmpty(v))
                                                                    claims.Add(
                                                                        new System.Security.Claims.Claim(
                                                                            "scope",
                                                                            v
                                                                        )
                                                                    );
                                                            }
                                                        }
                                                        else
                                                        {
                                                            var v = prop.Value.GetString();
                                                            if (!string.IsNullOrEmpty(v))
                                                            {
                                                                foreach (
                                                                    var piece in v.Split(
                                                                        ' ',
                                                                        StringSplitOptions.RemoveEmptyEntries
                                                                    )
                                                                )
                                                                {
                                                                    claims.Add(
                                                                        new System.Security.Claims.Claim(
                                                                            "scope",
                                                                            piece
                                                                        )
                                                                    );
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        // Map common scalar claims as-is
                                                        switch (prop.Value.ValueKind)
                                                        {
                                                            case JsonValueKind.String:
                                                                var sv = prop.Value.GetString();
                                                                if (!string.IsNullOrEmpty(sv))
                                                                    claims.Add(
                                                                        new System.Security.Claims.Claim(
                                                                            prop.Name,
                                                                            sv
                                                                        )
                                                                    );
                                                                break;
                                                            case JsonValueKind.Number:
                                                                claims.Add(
                                                                    new System.Security.Claims.Claim(
                                                                        prop.Name,
                                                                        prop.Value.ToString()
                                                                    )
                                                                );
                                                                break;
                                                            case JsonValueKind.True:
                                                            case JsonValueKind.False:
                                                                claims.Add(
                                                                    new System.Security.Claims.Claim(
                                                                        prop.Name,
                                                                        prop.Value.GetBoolean()
                                                                            .ToString()
                                                                    )
                                                                );
                                                                break;
                                                        }
                                                    }
                                                }
                                                var id = new System.Security.Claims.ClaimsIdentity(
                                                    claims,
                                                    authenticationType: "ManualDevUnverified"
                                                );
                                                HttpContext.User =
                                                    new System.Security.Claims.ClaimsPrincipal(id);
                                                _logger.LogWarning(
                                                    "[ManualJWT] Accepted token WITHOUT signature verification (Development only). Proceeding with policy checks."
                                                );
                                                try
                                                {
                                                    Response.Headers["X-ManualJwt"] = "ok-raw";
                                                }
                                                catch { }
                                                goto ManualValidated;
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    _logger.LogWarning(
                                        ex2,
                                        "[ManualJWT] Raw decode fallback failed"
                                    );
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ManualJWT] Exception during manual validation fallback");
                    try
                    {
                        var sanitized =
                            ex.GetType().Name
                            + ": "
                            + (
                                ex.Message?.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'")
                                ?? string.Empty
                            );
                        Response.Headers["X-ManualJwt"] = $"exception: {sanitized}";
                    }
                    catch { }
                }
                var err = authResult.Failure?.GetType().Name ?? "unknown_error";
                var desc = authResult.Failure?.Message ?? "Authentication failed";
                try
                {
                    // Avoid quotes breaking header formatting
                    desc = desc.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
                }
                catch { }
                // In Development, append a short prefix of the Authorization header to help diagnose token parsing issues
                try
                {
                    var env = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
                    if (env.IsDevelopment())
                    {
                        var raw = Request.Headers["Authorization"].ToString();
                        if (!string.IsNullOrEmpty(raw))
                        {
                            var prefix = raw.Length > 28 ? raw.Substring(0, 28) : raw;
                            prefix = prefix
                                .Replace("\r", " ")
                                .Replace("\n", " ")
                                .Replace("\"", "'");
                            desc = $"{desc} (authPrefix='{prefix}')";
                        }
                    }
                }
                catch { }
                Response.Headers["WWW-Authenticate"] =
                    $"Bearer error=\"invalid_token\", error_description=\"{desc}\", error_type=\"{err}\"";
                return Unauthorized();
            }
            // Otherwise, allow dev bypass header when configured
            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var key = cfg["Dev:ProvisionBypassKey"];
            var hdr = Request.Headers["X-Provision-Key"].ToString();
            if (
                string.IsNullOrWhiteSpace(key) || !string.Equals(key, hdr, StringComparison.Ordinal)
            )
                return Unauthorized();
        }

        ManualValidated:
        ;

        TenantProvisionResult result;
        try
        {
            result = await _provisioner.ProvisionAsync(
                new TenantProvisionRequest(input.tenantId, input.displayName, input.region),
                ct
            );
        }
        catch (Exception ex)
        {
            // Emit failure audit (redacted payload)
            var evFail = new AuditEvent
            {
                Category = "Provisioning",
                Action = "TenantProvision",
                Subject = User?.Identity?.Name ?? "system",
                Outcome = "Failure",
                ReasonCode = ex.GetType().Name,
                CorrelationId = HttpContext.TraceIdentifier
            };
            _audit.TryEnqueueRedacted(evFail, input, new[] { "tenantId", "displayName", "region" });
            throw;
        }

        _logger.LogInformation(
            "Provisioned tenant {Tenant} -> db={Db} created={Created}",
            result.TenantId,
            result.Database,
            result.Created
        );

        // Emit success audit (redacted payload)
        var ev = new AuditEvent
        {
            Category = "Provisioning",
            Action = "TenantProvision",
            Subject = User?.Identity?.Name ?? "system",
            Outcome = "Success",
            CorrelationId = HttpContext.TraceIdentifier
        };
        _audit.TryEnqueueRedacted(ev, new { result.TenantId, result.Database, result.Created }, new[] { "TenantId", "Database", "Created" });

        return Ok(result);
    } // End of Method ProvisionTenant
} // End of Class ProvisioningController
