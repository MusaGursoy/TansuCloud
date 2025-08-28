// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Identity.Infrastructure.Options;
using TansuCloud.Identity.Infrastructure.Keys;
using TansuCloud.Identity.Data;

namespace TansuCloud.Identity.Infrastructure.Security;

public interface IKeyRotationCoordinator
{
    Task TriggerAsync(CancellationToken ct = default);
}

internal sealed class JwksRotationService : BackgroundService, IKeyRotationCoordinator
{
    private readonly IOptions<IdentityPolicyOptions> _options;
    private readonly ILogger<JwksRotationService> _logger;
    private readonly Channel<bool> _channel = Channel.CreateUnbounded<bool>();
    private readonly IServiceProvider _sp;

    public JwksRotationService(
        IOptions<IdentityPolicyOptions> options,
        ILogger<JwksRotationService> logger,
        IServiceProvider sp
    )
    {
        _options = options;
        _logger = logger;
        _sp = sp;
    }

    public Task TriggerAsync(CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(true, ct).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = _options.Value.JwksRotationPeriod;
        if (period <= TimeSpan.Zero)
            period = TimeSpan.FromDays(30);

        // Placeholder loop: log rotation intent on schedule or trigger.
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "[JWKS] Rotation check (stub). Next scheduled in {Period}.",
                period
            );

            var delayTask = Task.Delay(period, stoppingToken);
            var triggerTask = _channel.Reader.ReadAsync(stoppingToken).AsTask();
            var completed = await Task.WhenAny(delayTask, triggerTask);

            if (completed == triggerTask)
            {
                _logger.LogInformation("[JWKS] Immediate rotation trigger received.");
            }

            // Perform rotation on either schedule or trigger
            using var scope = _sp.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            try
            {
                await store.RotateAsync(gracePeriod: TimeSpan.FromDays(7), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JWKS] Rotation failed");
            }
            // else: scheduled tick already logged above; loop continues
        }
    }
}
