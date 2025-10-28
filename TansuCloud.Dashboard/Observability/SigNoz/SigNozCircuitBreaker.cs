// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Circuit breaker for SigNoz API calls to prevent cascading failures.
/// Opens circuit after 3 consecutive failures, closes after 1 minute of cooldown.
/// </summary>
public sealed class SigNozCircuitBreaker
{
    private readonly ILogger<SigNozCircuitBreaker> _logger;
    private readonly object _lock = new();
    
    private int _consecutiveFailures;
    private DateTime? _circuitOpenedAt;
    private const int FailureThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(1);

    public SigNozCircuitBreaker(ILogger<SigNozCircuitBreaker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Check if the circuit is currently open (API calls should be blocked).
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_circuitOpenedAt == null)
                    return false;

                var elapsed = DateTime.UtcNow - _circuitOpenedAt.Value;
                if (elapsed >= CircuitOpenDuration)
                {
                    // Cooldown period elapsed, close the circuit
                    _logger.LogInformation(
                        "Circuit breaker closing after {DurationSeconds}s cooldown. Consecutive failures reset.",
                        elapsed.TotalSeconds
                    );
                    
                    _circuitOpenedAt = null;
                    _consecutiveFailures = 0;
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Record a successful API call, resetting the failure counter.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation(
                    "SigNoz API call succeeded after {FailureCount} previous failure(s). Resetting circuit breaker.",
                    _consecutiveFailures
                );
                _consecutiveFailures = 0;
            }
            
            _circuitOpenedAt = null;
        }
    }

    /// <summary>
    /// Record a failed API call, incrementing the failure counter.
    /// Opens the circuit if threshold is reached.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            
            if (_consecutiveFailures >= FailureThreshold && _circuitOpenedAt == null)
            {
                _circuitOpenedAt = DateTime.UtcNow;
                
                _logger.LogWarning(
                    "SigNoz circuit breaker OPENED after {FailureCount} consecutive failures. " +
                    "API calls will be blocked for {CooldownSeconds}s. Cached data will be used if available.",
                    _consecutiveFailures,
                    CircuitOpenDuration.TotalSeconds
                );
            }
            else if (_circuitOpenedAt == null)
            {
                _logger.LogWarning(
                    "SigNoz API call failed ({FailureCount}/{Threshold}). Circuit breaker will open if failures continue.",
                    _consecutiveFailures,
                    FailureThreshold
                );
            }
        }
    }

    /// <summary>
    /// Get the circuit breaker state for observability/diagnostics.
    /// </summary>
    public CircuitBreakerState GetState()
    {
        lock (_lock)
        {
            if (_circuitOpenedAt == null)
            {
                return new CircuitBreakerState(
                    IsOpen: false,
                    ConsecutiveFailures: _consecutiveFailures,
                    OpenedAt: null,
                    RemainingCooldownSeconds: 0
                );
            }

            var elapsed = DateTime.UtcNow - _circuitOpenedAt.Value;
            var remainingSeconds = Math.Max(0, (CircuitOpenDuration - elapsed).TotalSeconds);

            return new CircuitBreakerState(
                IsOpen: true,
                ConsecutiveFailures: _consecutiveFailures,
                OpenedAt: _circuitOpenedAt.Value,
                RemainingCooldownSeconds: remainingSeconds
            );
        }
    }
} // End of Class SigNozCircuitBreaker

/// <summary>
/// Circuit breaker state snapshot for diagnostics.
/// </summary>
public sealed record CircuitBreakerState(
    bool IsOpen,
    int ConsecutiveFailures,
    DateTime? OpenedAt,
    double RemainingCooldownSeconds
); // End of Record CircuitBreakerState
