// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Database.Hosting;

/// <summary>
/// Hosted service that ensures PostgreSQL extensions are updated during application startup.
/// This prevents runtime errors from version mismatches after Docker image upgrades.
/// </summary>
public sealed class ExtensionVersionHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExtensionVersionHostedService> _logger;

    public ExtensionVersionHostedService(
        IServiceProvider serviceProvider,
        ILogger<ExtensionVersionHostedService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    } // End of Constructor ExtensionVersionHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running pre-flight extension version checks...");

        try
        {
            // Create a scope to resolve scoped services
            using var scope = _serviceProvider.CreateScope();
            var extensionService =
                scope.ServiceProvider.GetRequiredService<
                    Services.ExtensionVersionService
                >();

            var count = await extensionService.EnsureExtensionVersionsAsync(cancellationToken);

            _logger.LogInformation(
                "Pre-flight checks completed successfully. Updated {Count} database(s)",
                count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-flight extension checks failed. Service may not start properly");

            // Rethrow to prevent startup if extension updates fail
            // This ensures we don't run with mismatched extension versions
            throw;
        }
    } // End of Method StartAsync

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No cleanup needed
        return Task.CompletedTask;
    } // End of Method StopAsync
} // End of Class ExtensionVersionHostedService
