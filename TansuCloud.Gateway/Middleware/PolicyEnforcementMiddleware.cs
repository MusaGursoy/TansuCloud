// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using TansuCloud.Gateway.Services;

namespace TansuCloud.Gateway.Middleware;

/// <summary>
/// Middleware that enforces CORS and IP access policies with staged rollout support.
/// Evaluates policies in Shadow, Audit Only, or Enforce modes.
/// </summary>
public class PolicyEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IPolicyRuntime _policyRuntime;
    private readonly ILogger<PolicyEnforcementMiddleware> _logger;
    private readonly bool _trustForwardedHeaders;

    private static readonly ActivitySource ActivitySrc = new("TansuCloud.Gateway.PolicyEnforcement");
    private static readonly Meter PolicyMeter = new("TansuCloud.Gateway.Policy", "1.0.0");

    // Metrics
    private static readonly Counter<long> PolicyEvaluationsTotal = PolicyMeter.CreateCounter<long>(
        name: "tansu_gateway_policy_evaluations_total",
        unit: "evaluations",
        description: "Total policy evaluations performed"
    );

    private static readonly Counter<long> PolicyViolationsTotal = PolicyMeter.CreateCounter<long>(
        name: "tansu_gateway_policy_violations_total",
        unit: "violations",
        description: "Total policy violations detected (would block in Enforce mode)"
    );

    private static readonly Counter<long> PolicyBlocksTotal = PolicyMeter.CreateCounter<long>(
        name: "tansu_gateway_policy_blocks_total",
        unit: "blocks",
        description: "Total requests blocked by policies in Enforce mode"
    );

    private static readonly Histogram<double> PolicyEvaluationDurationMs = PolicyMeter.CreateHistogram<double>(
        name: "tansu_gateway_policy_evaluation_duration_ms",
        unit: "ms",
        description: "Policy evaluation duration in milliseconds"
    );

    public PolicyEnforcementMiddleware(
        RequestDelegate next,
        IPolicyRuntime policyRuntime,
        ILogger<PolicyEnforcementMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _policyRuntime = policyRuntime;
        _logger = logger;
        _trustForwardedHeaders = configuration.GetValue<bool>("Gateway:TrustForwardedHeaders", false);
    } // End of Constructor PolicyEnforcementMiddleware

    public async Task InvokeAsync(HttpContext context)
    {
        using var activity = ActivitySrc.StartActivity("PolicyEnforcement");
        var startTimestamp = Stopwatch.GetTimestamp();

        var clientIp = GetClientIp(context);
        var origin = context.Request.Headers.Origin.ToString();
        var isPreflightRequest = context.Request.Method == "OPTIONS"
            && !string.IsNullOrEmpty(context.Request.Headers.AccessControlRequestMethod);

        activity?.SetTag("client.ip", clientIp?.ToString());
        activity?.SetTag("http.request.origin", origin);
        activity?.SetTag("http.request.is_preflight", isPreflightRequest);

        var allPolicies = await _policyRuntime.GetAllAsync();
        var enabledPolicies = allPolicies.Where(p => p.Enabled).ToList();

        // Track evaluation duration
        var evaluationStartTime = Stopwatch.GetTimestamp();

        // Evaluate IP policies first (deny then allow)
        var ipDenyResult = await EvaluateIpDenyPoliciesAsync(context, clientIp, enabledPolicies);
        if (ipDenyResult.ShouldBlock)
        {
            var durationMs = Stopwatch.GetElapsedTime(evaluationStartTime).TotalMilliseconds;
            PolicyEvaluationDurationMs.Record(durationMs, 
                new("policy.type", "ip_deny"),
                new("policy.result", "blocked"));
            
            activity?.SetTag("policy.result", "blocked_ip_deny");
            await HandleBlockedRequestAsync(context, ipDenyResult, clientIp);
            return;
        }

        var ipAllowResult = await EvaluateIpAllowPoliciesAsync(context, clientIp, enabledPolicies);
        if (ipAllowResult.ShouldBlock)
        {
            var durationMs = Stopwatch.GetElapsedTime(evaluationStartTime).TotalMilliseconds;
            PolicyEvaluationDurationMs.Record(durationMs, 
                new("policy.type", "ip_allow"),
                new("policy.result", "blocked"));
            
            activity?.SetTag("policy.result", "blocked_ip_allow");
            await HandleBlockedRequestAsync(context, ipAllowResult, clientIp);
            return;
        }

        // Handle CORS policies
        var corsPolicies = enabledPolicies.Where(p => p.Type == PolicyType.Cors).ToList();
        if (corsPolicies.Any() && !string.IsNullOrEmpty(origin))
        {
            var corsResult = await EvaluateCorsAsync(context, origin, isPreflightRequest, corsPolicies);
            
            if (corsResult.ShouldBlock)
            {
                var durationMs = Stopwatch.GetElapsedTime(evaluationStartTime).TotalMilliseconds;
                PolicyEvaluationDurationMs.Record(durationMs, 
                    new("policy.type", "cors"),
                    new("policy.result", "blocked"));
                
                activity?.SetTag("policy.result", "blocked_cors");
                await HandleBlockedRequestAsync(context, corsResult, clientIp);
                return;
            }

            // Apply CORS headers for allowed origins
            if (corsResult.AllowOrigin)
            {
                ApplyCorsHeaders(context, origin, corsResult);
                
                // Preflight requests end here
                if (isPreflightRequest)
                {
                    var durationMs = Stopwatch.GetElapsedTime(evaluationStartTime).TotalMilliseconds;
                    PolicyEvaluationDurationMs.Record(durationMs, 
                        new("policy.type", "cors"),
                        new("policy.result", "preflight_success"));
                    
                    context.Response.StatusCode = 204;
                    activity?.SetTag("policy.result", "cors_preflight_success");
                    return;
                }
            }
        }

        // Note: Rate limit policies are enforced via ASP.NET Core's built-in RateLimiter middleware.
        // Policy-based rate limits can be applied by creating custom partition strategies.
        // For now, rate limit policies are used for simulation and metrics only.
        // Future enhancement: Add per-route rate limit policy enforcement here.

        // Record successful evaluation
        var totalDurationMs = Stopwatch.GetElapsedTime(evaluationStartTime).TotalMilliseconds;
        PolicyEvaluationDurationMs.Record(totalDurationMs, 
            new("policy.type", "all"),
            new("policy.result", "allowed"));

        activity?.SetTag("policy.result", "allowed");
        await _next(context);
    } // End of Method InvokeAsync

    private IPAddress? GetClientIp(HttpContext context)
    {
        // If we trust forwarded headers (behind proxy/load balancer), check X-Forwarded-For
        if (_trustForwardedHeaders)
        {
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                // X-Forwarded-For can be a comma-separated list; take the first (original client)
                var firstIp = forwardedFor.Split(',')[0].Trim();
                if (IPAddress.TryParse(firstIp, out var parsedIp))
                {
                    return parsedIp;
                }
            }
        }

        // Fall back to connection remote IP
        return context.Connection.RemoteIpAddress;
    } // End of Method GetClientIp

    private async Task<PolicyResult> EvaluateIpDenyPoliciesAsync(
        HttpContext context,
        IPAddress? clientIp,
        List<PolicyEntry> enabledPolicies)
    {
        var ipDenyPolicies = enabledPolicies.Where(p => p.Type == PolicyType.IpDeny).ToList();
        
        if (!ipDenyPolicies.Any() || clientIp == null)
        {
            return new PolicyResult { AllowOrigin = false, ShouldBlock = false };
        }

        foreach (var policy in ipDenyPolicies)
        {
            var ipConfig = ParseIpConfig(policy.Config);
            if (ipConfig == null) continue;

            var isMatch = ipConfig.Cidrs.Any(cidr => IsIpInCidr(clientIp, cidr));
            if (isMatch)
            {
                var shouldBlock = policy.Mode == PolicyEnforcementMode.Enforce;
                
                _logger.LogWarning(
                    "IP Deny policy {PolicyId} ({Mode}) matched client IP {ClientIp}. Would block: {WouldBlock}",
                    policy.Id, policy.Mode, clientIp, shouldBlock);

                await EmitPolicyMetricAsync(context, policy, "ip_deny", "violation");

                return new PolicyResult
                {
                    AllowOrigin = false,
                    ShouldBlock = shouldBlock,
                    PolicyId = policy.Id,
                    Reason = $"IP {clientIp} is in deny list"
                };
            }
        }

        return new PolicyResult { AllowOrigin = false, ShouldBlock = false };
    } // End of Method EvaluateIpDenyPoliciesAsync

    private async Task<PolicyResult> EvaluateIpAllowPoliciesAsync(
        HttpContext context,
        IPAddress? clientIp,
        List<PolicyEntry> enabledPolicies)
    {
        var ipAllowPolicies = enabledPolicies.Where(p => p.Type == PolicyType.IpAllow).ToList();
        
        // If no allow policies exist, allow all IPs
        if (!ipAllowPolicies.Any())
        {
            return new PolicyResult { AllowOrigin = false, ShouldBlock = false };
        }

        if (clientIp == null)
        {
            // No client IP available but allow policies exist - block
            _logger.LogWarning("IP Allow policies exist but client IP is null. Blocking request.");
            return new PolicyResult
            {
                AllowOrigin = false,
                ShouldBlock = true,
                Reason = "Client IP unavailable"
            };
        }

        // Check if IP is in any allow list
        foreach (var policy in ipAllowPolicies)
        {
            var ipConfig = ParseIpConfig(policy.Config);
            if (ipConfig == null) continue;

            var isMatch = ipConfig.Cidrs.Any(cidr => IsIpInCidr(clientIp, cidr));
            if (isMatch)
            {
                _logger.LogInformation(
                    "IP Allow policy {PolicyId} matched client IP {ClientIp}. Request allowed.",
                    policy.Id, clientIp);
                
                return new PolicyResult { AllowOrigin = false, ShouldBlock = false };
            }
        }

        // IP not in any allow list - check enforcement mode
        var enforceModePolicy = ipAllowPolicies
            .OrderByDescending(p => p.Mode)
            .FirstOrDefault();

        var shouldBlock = enforceModePolicy?.Mode == PolicyEnforcementMode.Enforce;

        _logger.LogWarning(
            "IP Allow policies exist but client IP {ClientIp} not in any allow list. Mode: {Mode}, Would block: {WouldBlock}",
            clientIp, enforceModePolicy?.Mode, shouldBlock);

        await EmitPolicyMetricAsync(context, enforceModePolicy, "ip_allow", "violation");

        return new PolicyResult
        {
            AllowOrigin = false,
            ShouldBlock = shouldBlock,
            PolicyId = enforceModePolicy?.Id,
            Reason = $"IP {clientIp} not in allow list"
        };
    } // End of Method EvaluateIpAllowPoliciesAsync

    private async Task<PolicyResult> EvaluateCorsAsync(
        HttpContext context,
        string origin,
        bool isPreflightRequest,
        List<PolicyEntry> corsPolicies)
    {
        foreach (var policy in corsPolicies)
        {
            var corsConfig = ParseCorsConfig(policy.Config);
            if (corsConfig == null) continue;

            // Check if origin is allowed
            var originAllowed = corsConfig.Origins.Contains("*") 
                || corsConfig.Origins.Contains(origin);

            if (!originAllowed)
            {
                var shouldBlock = policy.Mode == PolicyEnforcementMode.Enforce;
                
                _logger.LogWarning(
                    "CORS policy {PolicyId} ({Mode}) denied origin {Origin}. Would block: {WouldBlock}",
                    policy.Id, policy.Mode, origin, shouldBlock);

                await EmitPolicyMetricAsync(context, policy, "cors", "violation");

                return new PolicyResult
                {
                    AllowOrigin = false,
                    ShouldBlock = shouldBlock,
                    PolicyId = policy.Id,
                    Reason = $"Origin {origin} not allowed"
                };
            }

            // For preflight, check method
            if (isPreflightRequest)
            {
                var requestMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
                var methodAllowed = corsConfig.Methods.Contains(requestMethod);

                if (!methodAllowed)
                {
                    var shouldBlock = policy.Mode == PolicyEnforcementMode.Enforce;
                    
                    _logger.LogWarning(
                        "CORS policy {PolicyId} ({Mode}) denied method {Method}. Would block: {WouldBlock}",
                        policy.Id, policy.Mode, requestMethod, shouldBlock);

                    await EmitPolicyMetricAsync(context, policy, "cors", "violation");

                    return new PolicyResult
                    {
                        AllowOrigin = false,
                        ShouldBlock = shouldBlock,
                        PolicyId = policy.Id,
                        Reason = $"Method {requestMethod} not allowed"
                    };
                }
            }

            // Origin (and method if preflight) allowed
            _logger.LogInformation(
                "CORS policy {PolicyId} allowed origin {Origin}",
                policy.Id, origin);

            return new PolicyResult
            {
                AllowOrigin = true,
                ShouldBlock = false,
                PolicyId = policy.Id,
                CorsConfig = corsConfig
            };
        }

        // No CORS policies matched - default deny in Enforce mode if any Enforce policies exist
        var enforcePolicy = corsPolicies.FirstOrDefault(p => p.Mode == PolicyEnforcementMode.Enforce);
        var shouldBlockDefault = enforcePolicy != null;

        if (shouldBlockDefault)
        {
            _logger.LogWarning(
                "No CORS policy matched origin {Origin} and Enforce mode is active. Blocking.",
                origin);
        }

        return new PolicyResult
        {
            AllowOrigin = false,
            ShouldBlock = shouldBlockDefault,
            Reason = $"No CORS policy matched origin {origin}"
        };
    } // End of Method EvaluateCorsAsync

    private void ApplyCorsHeaders(HttpContext context, string origin, PolicyResult corsResult)
    {
        if (corsResult.CorsConfig == null) return;

        context.Response.Headers.AccessControlAllowOrigin = origin;

        if (corsResult.CorsConfig.AllowCredentials)
        {
            context.Response.Headers.AccessControlAllowCredentials = "true";
        }

        if (corsResult.CorsConfig.Methods.Any())
        {
            context.Response.Headers.AccessControlAllowMethods = string.Join(", ", corsResult.CorsConfig.Methods);
        }

        if (corsResult.CorsConfig.Headers.Any())
        {
            context.Response.Headers.AccessControlAllowHeaders = string.Join(", ", corsResult.CorsConfig.Headers);
        }

        if (corsResult.CorsConfig.ExposedHeaders?.Any() == true)
        {
            context.Response.Headers.AccessControlExposeHeaders = string.Join(", ", corsResult.CorsConfig.ExposedHeaders);
        }

        if (corsResult.CorsConfig.MaxAgeSeconds > 0)
        {
            context.Response.Headers.AccessControlMaxAge = corsResult.CorsConfig.MaxAgeSeconds.ToString();
        }
    } // End of Method ApplyCorsHeaders

    private async Task HandleBlockedRequestAsync(
        HttpContext context,
        PolicyResult result,
        IPAddress? clientIp)
    {
        _logger.LogWarning(
            "Request blocked by policy {PolicyId}. Client IP: {ClientIp}, Reason: {Reason}",
            result.PolicyId, clientIp, result.Reason);

        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";

        var problemDetails = new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            title = "Forbidden",
            status = 403,
            detail = result.Reason ?? "Access denied by policy",
            instance = context.Request.Path.Value
        };

        await context.Response.WriteAsJsonAsync(problemDetails);
    } // End of Method HandleBlockedRequestAsync

    private async Task EmitPolicyMetricAsync(
        HttpContext context,
        PolicyEntry? policy,
        string policyType,
        string eventType)
    {
        if (policy == null)
        {
            await Task.CompletedTask;
            return;
        }

        // Increment evaluation counter
        PolicyEvaluationsTotal.Add(1,
            new("policy.id", policy.Id),
            new("policy.type", policyType),
            new("policy.mode", policy.Mode.ToString()),
            new("event.type", eventType));

        // Increment violation counter (for all modes)
        if (eventType == "violation")
        {
            PolicyViolationsTotal.Add(1,
                new("policy.id", policy.Id),
                new("policy.type", policyType),
                new("policy.mode", policy.Mode.ToString()));

            // Increment block counter only for Enforce mode
            if (policy.Mode == PolicyEnforcementMode.Enforce)
            {
                PolicyBlocksTotal.Add(1,
                    new("policy.id", policy.Id),
                    new("policy.type", policyType),
                    new("policy.mode", "Enforce"));
            }
        }

        await Task.CompletedTask;
    } // End of Method EmitPolicyMetricAsync

    private IpConfig? ParseIpConfig(System.Text.Json.JsonElement config)
    {
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return System.Text.Json.JsonSerializer.Deserialize<IpConfig>(config.GetRawText(), options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse IP config");
            return null;
        }
    } // End of Method ParseIpConfig

    private CorsConfig? ParseCorsConfig(System.Text.Json.JsonElement config)
    {
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return System.Text.Json.JsonSerializer.Deserialize<CorsConfig>(config.GetRawText(), options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse CORS config");
            return null;
        }
    } // End of Method ParseCorsConfig

    private bool IsIpInCidr(IPAddress clientIp, string cidr)
    {
        try
        {
            // Handle single IP addresses (no CIDR notation)
            if (!cidr.Contains('/'))
            {
                return IPAddress.TryParse(cidr, out var singleIp) && clientIp.Equals(singleIp);
            }

            var parts = cidr.Split('/');
            if (parts.Length != 2) return false;

            if (!IPAddress.TryParse(parts[0], out var networkAddress)) return false;
            if (!int.TryParse(parts[1], out var prefixLength)) return false;

            // Ensure both IPs are same family
            if (clientIp.AddressFamily != networkAddress.AddressFamily) return false;

            var clientBytes = clientIp.GetAddressBytes();
            var networkBytes = networkAddress.GetAddressBytes();

            if (clientBytes.Length != networkBytes.Length) return false;

            // Calculate number of bytes to check
            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            // Check full bytes
            for (int i = 0; i < fullBytes; i++)
            {
                if (clientBytes[i] != networkBytes[i]) return false;
            }

            // Check remaining bits
            if (remainingBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((clientBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse CIDR {Cidr}", cidr);
            return false;
        }
    } // End of Method IsIpInCidr

    private class PolicyResult
    {
        public bool AllowOrigin { get; set; }
        public bool ShouldBlock { get; set; }
        public string? PolicyId { get; set; }
        public string? Reason { get; set; }
        public CorsConfig? CorsConfig { get; set; }
    } // End of Class PolicyResult
} // End of Class PolicyEnforcementMiddleware
