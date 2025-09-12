// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TansuCloud.Storage.Services;

namespace TansuCloud.Storage.Hosting;

internal sealed class MultipartCleanupService(
    ILogger<MultipartCleanupService> logger,
    IOptions<StorageOptions> options,
    IWebHostEnvironment env
) : BackgroundService
{
    private readonly StorageOptions _opts = options.Value;
    private readonly string _root = options.Value.RootPath ?? Path.Combine(env.ContentRootPath, "_storage");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.MultipartCleanupEnabled)
        {
            logger.LogInformation("Multipart cleanup disabled via configuration");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _opts.MultipartCleanupIntervalMinutes));
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _opts.MultipartCleanupTtlMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep(ttl, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Multipart cleanup sweep failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void Sweep(TimeSpan ttl, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return;

        var cutoff = DateTimeOffset.UtcNow - ttl;
        int deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            // Multipart temp directories are created as: <objectPath>.multipart.<uploadId>
            if (!dir.Contains(".multipart.", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var info = new DirectoryInfo(dir);
                var lastWrite = info.LastWriteTimeUtc;
                if (lastWrite < cutoff.UtcDateTime)
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (deleted > 0)
            logger.LogInformation("Multipart cleanup removed {Count} abandoned uploads", deleted);
    }
} // End of Class MultipartCleanupService
