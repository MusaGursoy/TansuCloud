// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TansuCloud.Storage.UnitTests.Support;

internal sealed class TestProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        httpContext ??= new DefaultHttpContext();
        var problem = new ProblemDetails
        {
            Status = statusCode ?? httpContext.Response.StatusCode,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance
        };

        ApplyDefaults(httpContext, problem);
        return problem;
    }

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        ModelStateDictionary modelStateDictionary,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        httpContext ??= new DefaultHttpContext();
        var validation = new ValidationProblemDetails(modelStateDictionary)
        {
            Status = statusCode ?? httpContext.Response.StatusCode,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance
        };

        ApplyDefaults(httpContext, validation);
        return validation;
    }

    private static void ApplyDefaults(HttpContext? httpContext, ProblemDetails problem)
    {
        if (!problem.Extensions.ContainsKey("traceId") && httpContext is not null)
        {
            var traceId = httpContext.TraceIdentifier;
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                problem.Extensions["traceId"] = traceId;
            }
        }
    }
} // End of Class TestProblemDetailsFactory
