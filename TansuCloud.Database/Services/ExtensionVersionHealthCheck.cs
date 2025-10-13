// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TansuCloud.Database.Services;

/// <summary>
/// Health check that reports PostgreSQL extension versions across all tenant databases.
/// Provides visibility into extension status after container upgrades.
/// </summary>
public sealed class ExtensionVersionHealthCheck : IHealthCheck
{
    private readonly ExtensionVersionService _extensionService;
    private readonly ILogger<ExtensionVersionHealthCheck> _logger;

    public ExtensionVersionHealthCheck(
        ExtensionVersionService extensionService,
        ILogger<ExtensionVersionHealthCheck> logger
    )
    {
        _extensionService = extensionService;
        _logger = logger;
    } // End of Constructor ExtensionVersionHealthCheck

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var versions = await _extensionService.GetExtensionVersionsAsync(cancellationToken);

            if (versions.Count == 0)
            {
                return HealthCheckResult.Degraded(
                    "No tenant databases found with tracked extensions",
                    data: new Dictionary<string, object> { ["databases"] = 0 }
                );
            }

            // Build summary data for health check response
            var data = new Dictionary<string, object>
            {
                ["databases"] = versions.Count,
                ["extensions"] = versions
                    .SelectMany(db => db.Value.Keys)
                    .Distinct()
                    .Order()
                    .ToArray()
            };

            // Check for version consistency across databases
            var allCitusVersions = versions
                .Where(db => db.Value.ContainsKey("citus"))
                .Select(db => db.Value["citus"])
                .Distinct()
                .ToArray();

            var allVectorVersions = versions
                .Where(db => db.Value.ContainsKey("vector"))
                .Select(db => db.Value["vector"])
                .Distinct()
                .ToArray();

            if (allCitusVersions.Length > 1 || allVectorVersions.Length > 1)
            {
                data["warning"] = "Version mismatch detected across databases";
                if (allCitusVersions.Length > 1)
                {
                    data["citus_versions"] = allCitusVersions;
                }
                if (allVectorVersions.Length > 1)
                {
                    data["vector_versions"] = allVectorVersions;
                }

                return HealthCheckResult.Degraded(
                    "Extension version mismatch detected across tenant databases",
                    data: data
                );
            }

            // All versions consistent
            if (allCitusVersions.Length == 1)
            {
                data["citus_version"] = allCitusVersions[0];
            }
            if (allVectorVersions.Length == 1)
            {
                data["vector_version"] = allVectorVersions[0];
            }

            return HealthCheckResult.Healthy(
                $"All {versions.Count} tenant database(s) have consistent extension versions",
                data: data
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check extension versions");
            return HealthCheckResult.Unhealthy(
                "Failed to retrieve extension versions",
                ex,
                data: new Dictionary<string, object> { ["error"] = ex.Message }
            );
        }
    } // End of Method CheckHealthAsync
} // End of Class ExtensionVersionHealthCheck
