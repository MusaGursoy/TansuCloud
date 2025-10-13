// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System.Net.Http.Json;
using System.Text.Json;

namespace TansuCloud.Database.Services;

/// <summary>
/// Client for PgCat Admin API to manage connection pools dynamically.
/// Enables zero-downtime tenant provisioning by adding pools synchronously.
/// </summary>
public class PgCatAdminClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PgCatAdminClient> _logger;
    private readonly string _adminUser;
    private readonly string _adminPassword;

    public PgCatAdminClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<PgCatAdminClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _adminUser = configuration["PGCAT_ADMIN_USER"] ?? throw new InvalidOperationException("PGCAT_ADMIN_USER not configured");
        _adminPassword = configuration["PGCAT_ADMIN_PASSWORD"] ?? throw new InvalidOperationException("PGCAT_ADMIN_PASSWORD not configured");
    }

    /// <summary>
    /// Adds a new database pool to PgCat via the Admin API.
    /// This enables immediate tenant access without waiting for config reload.
    /// </summary>
    /// <param name="database">Database name (e.g., "tansu_tenant_acme")</param>
    /// <param name="poolSize">Number of connections in the pool (default 20)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if pool was added successfully, false if it already exists</returns>
    public async Task<bool> AddPoolAsync(
        string database,
        int poolSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                database,
                pool_size = poolSize,
                // PgCat will inherit other settings from the default pool configuration
            };

            _logger.LogInformation(
                "Adding PgCat pool for database {Database} with pool size {PoolSize}",
                database,
                poolSize);

            // PgCat Admin API: POST /admin/pools
            // Note: PgCat Admin API uses basic auth
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/admin/pools")
            {
                Content = JsonContent.Create(request)
            };
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_adminUser}:{_adminPassword}"))
            );

            var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Successfully added PgCat pool for database {Database}",
                    database);
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogInformation(
                    "PgCat pool for database {Database} already exists (idempotent)",
                    database);
                return false; // Pool already exists, which is fine
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to add PgCat pool for database {Database}: {StatusCode} - {Error}",
                database,
                response.StatusCode,
                errorContent);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception while adding PgCat pool for database {Database}",
                database);
            return false;
        }
    }

    /// <summary>
    /// Removes a database pool from PgCat via the Admin API.
    /// Useful for tenant deprovisioning or cleanup.
    /// </summary>
    /// <param name="database">Database name (e.g., "tansu_tenant_acme")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if pool was removed successfully</returns>
    public async Task<bool> RemovePoolAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Removing PgCat pool for database {Database}",
                database);

            // PgCat Admin API: DELETE /admin/pools/{database}
            var requestMessage = new HttpRequestMessage(HttpMethod.Delete, $"/admin/pools/{database}");
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_adminUser}:{_adminPassword}"))
            );

            var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Successfully removed PgCat pool for database {Database}",
                    database);
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "PgCat pool for database {Database} not found (idempotent)",
                    database);
                return false; // Pool doesn't exist, which is fine
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to remove PgCat pool for database {Database}: {StatusCode} - {Error}",
                database,
                response.StatusCode,
                errorContent);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception while removing PgCat pool for database {Database}",
                database);
            return false;
        }
    }

    /// <summary>
    /// Lists all configured pools in PgCat.
    /// Useful for diagnostics and validation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of database names with configured pools</returns>
    public async Task<List<string>> ListPoolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Listing PgCat pools");

            // PgCat Admin API: GET /admin/pools
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, "/admin/pools");
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_adminUser}:{_adminPassword}"))
            );

            var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var pools = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken);
                _logger.LogDebug("Found {PoolCount} PgCat pools", pools?.Count ?? 0);
                return pools ?? new List<string>();
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to list PgCat pools: {StatusCode} - {Error}",
                response.StatusCode,
                errorContent);

            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while listing PgCat pools");
            return new List<string>();
        }
    }
} // End of Class PgCatAdminClient
