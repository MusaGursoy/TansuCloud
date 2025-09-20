// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Concurrent;

namespace TansuCloud.Gateway.Services;

public interface IRateLimitRuntime
{
    int WindowSeconds { get; }
    RateLimitDefaults Defaults { get; }
    IReadOnlyDictionary<string, RateLimitRouteOverride> Routes { get; }

    (int permitLimit, int queueLimit, int windowSeconds) Resolve(string routePrefix);
    RateLimitConfigDto GetSnapshot();
    void Apply(RateLimitConfigDto dto);
} // End of Interface IRateLimitRuntime

public sealed class RateLimitRuntime : IRateLimitRuntime
{
    private readonly object _gate = new();
    private int _windowSeconds;
    private RateLimitDefaults _defaults;
    private Dictionary<string, RateLimitRouteOverride> _routes;
    private int _version = 1; // Incremented on each Apply() to invalidate existing limiter partitions

    public RateLimitRuntime(
        int windowSeconds,
        RateLimitDefaults defaults,
        Dictionary<string, RateLimitRouteOverride> routes
    )
    {
        _windowSeconds = Math.Max(1, windowSeconds);
        _defaults = new RateLimitDefaults
        {
            PermitLimit = Math.Max(0, defaults.PermitLimit),
            QueueLimit = Math.Max(0, defaults.QueueLimit)
        };
        _routes = routes.ToDictionary(
            kv => kv.Key,
            kv => Sanitize(kv.Value),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public int WindowSeconds
    {
        get
        {
            lock (_gate)
            {
                return _windowSeconds;
            }
        }
    }
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    } // End of Property Version
    public RateLimitDefaults Defaults
    {
        get
        {
            lock (_gate)
            {
                return _defaults with { };
            }
        }
    }
    public IReadOnlyDictionary<string, RateLimitRouteOverride> Routes
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, RateLimitRouteOverride>(
                    _routes,
                    StringComparer.OrdinalIgnoreCase
                );
            }
        }
    }

    public (int permitLimit, int queueLimit, int windowSeconds) Resolve(string routePrefix)
    {
        lock (_gate)
        {
            if (
                !string.IsNullOrWhiteSpace(routePrefix)
                && _routes.TryGetValue(routePrefix, out var r)
            )
            {
                return (
                    r.PermitLimit ?? _defaults.PermitLimit,
                    r.QueueLimit ?? _defaults.QueueLimit,
                    _windowSeconds
                );
            }
            return (_defaults.PermitLimit, _defaults.QueueLimit, _windowSeconds);
        }
    } // End of Method Resolve

    public RateLimitConfigDto GetSnapshot()
    {
        lock (_gate)
        {
            return new RateLimitConfigDto
            {
                Version = _version,
                WindowSeconds = _windowSeconds,
                Defaults = _defaults with { },
                Routes = _routes.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value with { },
                    StringComparer.OrdinalIgnoreCase
                )
            };
        }
    } // End of Method GetSnapshot

    public void Apply(RateLimitConfigDto dto)
    {
        if (dto is null)
            throw new ArgumentNullException(nameof(dto));
        lock (_gate)
        {
            var win = Math.Max(1, dto.WindowSeconds);
            var defs =
                dto.Defaults ?? new RateLimitDefaults { PermitLimit = 100, QueueLimit = 100 };
            var routes =
                dto.Routes
                ?? new Dictionary<string, RateLimitRouteOverride>(StringComparer.OrdinalIgnoreCase);

            // sanitize
            var cleanDefaults = new RateLimitDefaults
            {
                PermitLimit = Math.Max(0, defs.PermitLimit),
                QueueLimit = Math.Max(0, defs.QueueLimit)
            };

            var cleanRoutes = new Dictionary<string, RateLimitRouteOverride>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var kv in routes)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;
                cleanRoutes[kv.Key] = Sanitize(kv.Value);
            }

            _windowSeconds = win;
            _defaults = cleanDefaults;
            _routes = cleanRoutes;
            // Invalidate existing limiter partitions by bumping the version.
            // The partition key composed in Program.cs includes this Version value.
            unchecked { _version++; }
        }
    } // End of Method Apply

    private static RateLimitRouteOverride Sanitize(RateLimitRouteOverride r)
    {
        var copy = r with { };
        if (copy.PermitLimit.HasValue)
            copy = copy with { PermitLimit = Math.Max(0, copy.PermitLimit.Value) };
        if (copy.QueueLimit.HasValue)
            copy = copy with { QueueLimit = Math.Max(0, copy.QueueLimit.Value) };
        return copy;
    } // End of Method Sanitize
} // End of Class RateLimitRuntime

public sealed record RateLimitConfigDto
{
    public int Version { get; set; }
    public int WindowSeconds { get; set; }
    public RateLimitDefaults? Defaults { get; set; }
    public Dictionary<string, RateLimitRouteOverride>? Routes { get; set; }
} // End of Class RateLimitConfigDto

public sealed record RateLimitDefaults
{
    public int PermitLimit { get; set; }
    public int QueueLimit { get; set; }
} // End of Class RateLimitDefaults

public sealed record RateLimitRouteOverride
{
    public int? PermitLimit { get; set; }
    public int? QueueLimit { get; set; }
} // End of Class RateLimitRouteOverride
