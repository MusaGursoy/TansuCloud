using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TansuCloud.Dashboard.Observability.SigNoz;

/// <summary>
/// Manages JWT authentication for SigNoz API access.
/// Implements token caching and automatic refresh.
/// </summary>
public interface ISigNozAuthenticationService
{
    /// <summary>
    /// Gets a valid JWT access token, refreshing if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JWT access token, or null if authentication is disabled.</returns>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of SigNoz JWT authentication service.
/// Based on SigNoz auth flow: https://github.com/SigNoz/signoz/blob/main/tests/integration/fixtures/auth.py
/// 1. GET /api/v2/sessions/context?email={email} → Returns orgId
/// 2. POST /api/v2/sessions/email_password with {email, password, orgId} → Returns accessToken
/// 3. Use Bearer token in Authorization header for subsequent API calls
/// </summary>
public sealed class SigNozAuthenticationService : ISigNozAuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<SigNozQueryOptions> _options;
    private readonly ILogger<SigNozAuthenticationService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public SigNozAuthenticationService(
        HttpClient httpClient,
        IOptionsMonitor<SigNozQueryOptions> options,
        ILogger<SigNozAuthenticationService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        
        // If no email/password configured, authentication is disabled
        if (string.IsNullOrWhiteSpace(opts.Email) || string.IsNullOrWhiteSpace(opts.Password))
        {
            _logger.LogDebug("SigNoz authentication disabled (no credentials configured)");
            return null;
        }

        // Check if cached token is still valid (with 5-minute buffer)
        if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedToken;
        }

        // Acquire lock to prevent concurrent token refreshes
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock (another thread may have refreshed)
            if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _cachedToken;
            }

            _logger.LogInformation("Authenticating with SigNoz API (email: {Email})", opts.Email);

            // Step 1: Get orgId from sessions/context
            var contextUrl = $"{opts.ApiBaseUrl}/api/v2/sessions/context?email={Uri.EscapeDataString(opts.Email)}";
            var contextResponse = await _httpClient.GetAsync(contextUrl, cancellationToken);
            
            if (!contextResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get SigNoz session context: {StatusCode}", contextResponse.StatusCode);
                return null;
            }

            var contextJson = await contextResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (!contextJson.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("orgs", out var orgs) ||
                orgs.GetArrayLength() == 0)
            {
                _logger.LogError("Invalid session context response: no orgs found");
                return null;
            }

            var orgId = orgs[0].GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(orgId))
            {
                _logger.LogError("Invalid session context response: empty orgId");
                return null;
            }

            _logger.LogDebug("Retrieved orgId: {OrgId}", orgId);

            // Step 2: Authenticate with email/password/orgId to get access token
            var loginUrl = $"{opts.ApiBaseUrl}/api/v2/sessions/email_password";
            var loginPayload = new
            {
                email = opts.Email,
                password = opts.Password,
                orgId = orgId
            };

            var loginResponse = await _httpClient.PostAsJsonAsync(loginUrl, loginPayload, cancellationToken);
            
            if (!loginResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to authenticate with SigNoz: {StatusCode}", loginResponse.StatusCode);
                return null;
            }

            var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (!loginJson.TryGetProperty("data", out var loginData) ||
                !loginData.TryGetProperty("accessToken", out var accessTokenElement))
            {
                _logger.LogError("Invalid login response: no access token found");
                return null;
            }

            var accessToken = accessTokenElement.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogError("Invalid login response: empty access token");
                return null;
            }

            // Cache token with 1-hour expiry (default JWT expiration)
            _cachedToken = accessToken;
            _tokenExpiry = DateTime.UtcNow.AddHours(1);

            _logger.LogInformation("Successfully authenticated with SigNoz (token expires at {Expiry})", _tokenExpiry);

            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during SigNoz authentication");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
