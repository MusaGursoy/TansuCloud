// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Database.Services;

/// <summary>
/// Hosted service that runs pre-flight extension version checks during application startup.
/// Ensures PostgreSQL extensions are updated to match library versions before accepting traffic.
/// </summary>
public sealed class ExtensionVersionStartupService : IHostedService
{
    private readonly ILogger<ExtensionVersionStartupService> _logger;
    private readonly ExtensionVersionService _extensionService;

    public ExtensionVersionStartupService(
        ILogger<ExtensionVersionStartupService> logger,
        ExtensionVersionService extensionService
    )
    {
        _logger = logger;
        _extensionService = extensionService;
    } // End of Constructor ExtensionVersionStartupService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running pre-flight extension version checks...");
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var processedCount = await _extensionService.EnsureExtensionVersionsAsync(
                cancellationToken
            );

            var duration = DateTimeOffset.UtcNow - startTime;
            _logger.LogInformation(
                "Pre-flight checks completed successfully in {Duration}ms. Processed {Count} database(s)",
                duration.TotalMilliseconds,
                processedCount
            );
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Pre-flight extension version checks failed. Application may not function correctly."
            );

            // In production, fail startup if extensions can't be updated
            var isDevelopment = string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase
            );

            if (!isDevelopment)
            {
                throw;
            }
        }
    } // End of Method StartAsync

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    } // End of Method StopAsync
} // End of Class ExtensionVersionStartupService
