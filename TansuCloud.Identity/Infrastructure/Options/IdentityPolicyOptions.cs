// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Identity.Infrastructure.Options;

public sealed class IdentityPolicyOptions
{
    public const string SectionName = "IdentityPolicies";

    // MFA/Password
    public bool RequireMfa { get; set; } = false;
    public bool AllowPasswordless { get; set; } = false;

    // Token lifetimes
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(60);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    // JWKS rotation
    public TimeSpan JwksRotationPeriod { get; set; } = TimeSpan.FromDays(30);
} // End of Class IdentityPolicyOptions
