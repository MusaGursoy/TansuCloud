// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TansuCloud.Dashboard.UnitTests;

/// <summary>
/// A simple delegating handler stub that lets tests capture the requested URI
/// and return a provided JSON payload.
/// </summary>
public sealed class HttpMessageHandlerStub : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }
    public string JsonPayload { get; set; } = "{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonPayload, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(resp);
    }
}
