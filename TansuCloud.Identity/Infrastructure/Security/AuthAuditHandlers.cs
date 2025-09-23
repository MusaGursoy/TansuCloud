// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Identity.Infrastructure.Security;

/// <summary>
/// Scoped OpenIddict handlers that emit PII-safe audit events for auth flows.
/// </summary>
internal static class AuthAuditHandlers
{
    internal sealed class ProcessSignInAuditHandler(
        IAuditLogger audit,
        IHttpContextAccessor accessor
    ) : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
    {
        public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
        {
            var http = accessor.HttpContext;
            if (http is null) return ValueTask.CompletedTask;

            try
            {
                var subject = context.Principal?.Identity?.Name
                    ?? context.Principal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value
                    ?? "unknown";
                var ev = new AuditEvent
                {
                    Category = "Auth",
                    Action = "TokenIssued",
                    Subject = subject,
                    Outcome = "Success",
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev, new { Endpoint = http.Request?.Path.Value }, new[] { "Endpoint" });
            }
            catch { /* swallow */ }
            return ValueTask.CompletedTask;
        } // End of Method HandleAsync
    } // End of Class ProcessSignInAuditHandler

    internal sealed class ApplyAuthorizationResponseAuditHandler(
        IAuditLogger audit,
        IHttpContextAccessor accessor
    ) : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>
    {
        public ValueTask HandleAsync(OpenIddictServerEvents.ApplyAuthorizationResponseContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Response?.Error))
                return ValueTask.CompletedTask;
            var http = accessor.HttpContext;
            if (http is null) return ValueTask.CompletedTask;
            try
            {
                var ev = new AuditEvent
                {
                    Category = "Auth",
                    Action = "Authorization",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    Outcome = "Failure",
                    ReasonCode = context.Response.Error,
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev,
                    new { Error = context.Response.Error, ErrorDescription = context.Response.ErrorDescription },
                    new[] { "Error", "ErrorDescription" });
            }
            catch { }
            return ValueTask.CompletedTask;
        } // End of Method HandleAsync
    } // End of Class ApplyAuthorizationResponseAuditHandler

    internal sealed class ApplyTokenResponseAuditHandler(
        IAuditLogger audit,
        IHttpContextAccessor accessor
    ) : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
    {
        public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Response?.Error))
                return ValueTask.CompletedTask;
            var http = accessor.HttpContext;
            if (http is null) return ValueTask.CompletedTask;
            try
            {
                var ev = new AuditEvent
                {
                    Category = "Auth",
                    Action = "Token",
                    Subject = http.User?.Identity?.Name ?? "anonymous",
                    Outcome = "Failure",
                    ReasonCode = context.Response.Error,
                    CorrelationId = http.TraceIdentifier
                };
                audit.TryEnqueueRedacted(ev,
                    new { Error = context.Response.Error, ErrorDescription = context.Response.ErrorDescription },
                    new[] { "Error", "ErrorDescription" });
            }
            catch { }
            return ValueTask.CompletedTask;
        } // End of Method HandleAsync
    } // End of Class ApplyTokenResponseAuditHandler
} // End of Class AuthAuditHandlers
