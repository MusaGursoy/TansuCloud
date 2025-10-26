using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;

namespace TansuCloud.Database.Helpers;

/// <summary>
/// Helper class to create a Newtonsoft.Json-based input formatter for JSON Patch operations.
/// </summary>
/// <remarks>
/// This is required because Microsoft.AspNetCore.JsonPatch only supports Newtonsoft.Json,
/// while the rest of the application uses System.Text.Json for JSON serialization.
/// See: https://learn.microsoft.com/en-us/aspnet/core/web-api/jsonpatch#add-support-for-json-patch-when-using-systemtextjson
/// </remarks>
public static class JsonPatchInputFormatterHelper
{
    /// <summary>
    /// Creates and returns a Newtonsoft.Json-based input formatter for JSON Patch requests.
    /// </summary>
    /// <returns>NewtonsoftJsonPatchInputFormatter configured for JSON Patch media types.</returns>
    public static NewtonsoftJsonPatchInputFormatter GetJsonPatchInputFormatter()
    {
        var builder = new ServiceCollection()
            .AddLogging()
            .AddMvc()
            .AddNewtonsoftJson()
            .Services.BuildServiceProvider();

        return builder
            .GetRequiredService<IOptions<MvcOptions>>()
            .Value
            .InputFormatters
            .OfType<NewtonsoftJsonPatchInputFormatter>()
            .First();
    }
}
