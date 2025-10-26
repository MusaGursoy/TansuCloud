// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace TansuCloud.Database.Formatters;

/// <summary>
/// Custom output formatter for NDJSON (Newline Delimited JSON) streaming.
/// Serializes IAsyncEnumerable&lt;T&gt; as application/x-ndjson with O(1) memory usage.
/// </summary>
public sealed class NdjsonOutputFormatter : TextOutputFormatter
{
    public NdjsonOutputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/x-ndjson"));
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanWriteType(Type? type)
    {
        if (type == null)
            return false;

        // Support IAsyncEnumerable<T> for streaming
        if (type.IsGenericType)
        {
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            if (
                genericTypeDefinition == typeof(IAsyncEnumerable<>)
                || type.GetInterfaces()
                    .Any(i =>
                        i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)
                    )
            )
            {
                return true;
            }
        }

        return false;
    }

    public override async Task WriteResponseBodyAsync(
        OutputFormatterWriteContext context,
        Encoding selectedEncoding
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selectedEncoding);

        var httpContext = context.HttpContext;
        var response = httpContext.Response;

        // Ensure content type is set
        response.ContentType = "application/x-ndjson; charset=utf-8";

        // Get the IAsyncEnumerable<T> instance
        var asyncEnumerable = context.Object;
        if (asyncEnumerable == null)
            return;

        // Use dynamic to call the generic method without reflection
        await WriteNdjsonAsync((dynamic)asyncEnumerable, response.Body, selectedEncoding, httpContext.RequestAborted);
    }

    private static async Task WriteNdjsonAsync<T>(
        IAsyncEnumerable<T> asyncEnumerable,
        Stream outputStream,
        Encoding encoding,
        CancellationToken cancellationToken
    )
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false // Compact JSON for streaming
        };

        await using var writer = new StreamWriter(
            outputStream,
            encoding,
            bufferSize: 1024,
            leaveOpen: true
        );

        await foreach (var item in asyncEnumerable.WithCancellation(cancellationToken))
        {
            if (item == null)
                continue;

            // Serialize to JSON and write with newline
            var json = JsonSerializer.Serialize(item, jsonOptions);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync(); // Flush each line for streaming
        }

        await writer.FlushAsync();
    }
}
