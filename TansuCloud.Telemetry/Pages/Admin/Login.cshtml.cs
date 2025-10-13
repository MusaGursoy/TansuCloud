// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Security;

namespace TansuCloud.Telemetry.Pages.Admin;

/// <summary>
/// Presents a lightweight login screen that exchanges the admin API key for a secure session cookie.
/// </summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IOptionsSnapshot<TelemetryAdminOptions> _adminOptions;

    public LoginModel(IOptionsSnapshot<TelemetryAdminOptions> adminOptions)
    {
        _adminOptions = adminOptions;
    } // End of Constructor LoginModel

    [BindProperty]
    [Display(Name = "Admin API key")]
    [Required]
    [MinLength(16)]
    public string ApiKey { get; set; } = string.Empty; // End of Property ApiKey

    /// <summary>
    /// Indicates whether the UI should display guidance about providing the admin API key.
    /// </summary>
    public bool ShowMissingKeyHint { get; private set; } // End of Property ShowMissingKeyHint

    /// <summary>
    /// Optional status message rendered above the login form when redirecting from an authentication failure.
    /// </summary>
    public string? StatusMessage { get; private set; } // End of Property StatusMessage

    /// <summary>
    /// Bootstrap alert class associated with <see cref="StatusMessage"/>.
    /// </summary>
    public string StatusMessageCssClass { get; private set; } = "alert-info"; // End of Property StatusMessageCssClass

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; } // End of Property ReturnUrl

    public IActionResult OnGet()
    {
        if (Request.Query.ContainsKey("missingKey"))
        {
            ShowMissingKeyHint = true;
        }

        if (
            Request.Query.TryGetValue(
                TelemetryAdminAuthenticationDefaults.AuthMessageQueryParameter,
                out var reasonValues
            )
        )
        {
            var reason = reasonValues.ToString();
            switch (reason)
            {
                case TelemetryAdminAuthenticationDefaults.AuthFailureReasons.InvalidSession:
                    StatusMessage = "Your previous admin session is no longer valid. Enter the current API key to continue.";
                    StatusMessageCssClass = "alert-warning";
                    ShowMissingKeyHint = true;
                    break;
                case TelemetryAdminAuthenticationDefaults.AuthFailureReasons.InvalidAuthorizationHeader:
                    StatusMessage = "The Authorization header was malformed. Use \"Authorization: Bearer <admin API key>\" when accessing the admin API.";
                    StatusMessageCssClass = "alert-warning";
                    break;
                case TelemetryAdminAuthenticationDefaults.AuthFailureReasons.MissingSession:
                default:
                    StatusMessage = "Enter the configured admin API key to start a session.";
                    StatusMessageCssClass = "alert-info";
                    ShowMissingKeyHint = true;
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && !Url.IsLocalUrl(ReturnUrl))
        {
            ReturnUrl = "/admin";
        }

        if (
            Request.Query.TryGetValue(
                TelemetryAdminAuthenticationDefaults.ApiKeyQueryParameter,
                out var apiKeyFromQuery
            )
        )
        {
            var candidate = apiKeyFromQuery.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (SecureEquals(candidate, _adminOptions.Value.ApiKey))
                {
                    AppendSessionCookie(candidate);
                    return LocalRedirect(ResolveReturnUrl());
                }

                ModelState.AddModelError(nameof(ApiKey), "Invalid API key.");
                ApiKey = string.Empty;
                ShowMissingKeyHint = true;
            }
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/admin");
        }

        return Page();
    } // End of Method OnGet

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            ShowMissingKeyHint = true;
            return Page();
        }

        var configuredKey = _adminOptions.Value.ApiKey;
        if (!SecureEquals(ApiKey, configuredKey))
        {
            ModelState.AddModelError(nameof(ApiKey), "Invalid API key.");
            ShowMissingKeyHint = true;
            StatusMessage = "Invalid API key.";
            StatusMessageCssClass = "alert-danger";
            return Page();
        }

        AppendSessionCookie(ApiKey);
        return LocalRedirect(ResolveReturnUrl());
    } // End of Method OnPost

    public IActionResult OnPostLogout()
    {
        Response.Cookies.Delete(
            TelemetryAdminAuthenticationDefaults.ApiKeyCookieName,
            new CookieOptions
            {
                Path = "/",
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict
            }
        );

        return Redirect(TelemetryAdminAuthenticationDefaults.LoginPath);
    } // End of Method OnPostLogout

    private void AppendSessionCookie(string apiKey)
    {
        Response.Cookies.Append(
            TelemetryAdminAuthenticationDefaults.ApiKeyCookieName,
            apiKey,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromHours(8)
            }
        );
    } // End of Method AppendSessionCookie

    private static bool SecureEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);

        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    } // End of Method SecureEquals

    private string ResolveReturnUrl()
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl))
        {
            return "/admin";
        }

        return Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/admin";
    } // End of Method ResolveReturnUrl
} // End of Class LoginModel
