// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;

namespace TansuCloud.Identity.Infrastructure.Runtime;

/// <summary>
/// Runtime configuration holder for Identity policies (password, lockout, token lifetimes).
/// </summary>
public interface IIdentityPoliciesRuntime
{
    IdentityPoliciesConfig GetCurrent();
    void Update(IdentityPoliciesConfig config);
} // End of Interface IIdentityPoliciesRuntime

internal sealed class IdentityPoliciesRuntime : IIdentityPoliciesRuntime
{
    private readonly ConcurrentDictionary<string, IdentityPoliciesConfig> _store = new();
    private const string Key = "current";

    public IdentityPoliciesRuntime(IdentityPoliciesConfig initial)
    {
        _store[Key] = initial;
    } // End of Constructor IdentityPoliciesRuntime

    public IdentityPoliciesConfig GetCurrent()
    {
        return _store.TryGetValue(Key, out var config) ? config : new IdentityPoliciesConfig();
    } // End of Method GetCurrent

    public void Update(IdentityPoliciesConfig config)
    {
        _store[Key] = config;
    } // End of Method Update
} // End of Class IdentityPoliciesRuntime

/// <summary>
/// Identity policies configuration model.
/// </summary>
public record IdentityPoliciesConfig
{
    // Password policy
    public int PasswordRequiredLength { get; init; } = 6;
    public bool PasswordRequireDigit { get; init; } = false;
    public bool PasswordRequireUppercase { get; init; } = false;
    public bool PasswordRequireLowercase { get; init; } = false;
    public bool PasswordRequireNonAlphanumeric { get; init; } = false;

    // Lockout policy
    public bool LockoutEnabled { get; init; } = true;
    public int LockoutMaxFailedAttempts { get; init; } = 5;
    public int LockoutDurationMinutes { get; init; } = 15;

    // Token lifetimes (in seconds)
    public int AccessTokenLifetimeSeconds { get; init; } = 3600; // 1 hour
    public int RefreshTokenLifetimeSeconds { get; init; } = 2592000; // 30 days
} // End of Class IdentityPoliciesConfig
