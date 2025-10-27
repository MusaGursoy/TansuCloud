// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace TansuCloud.E2E.Tests;

/// <summary>
/// E2E tests for Database API advanced features: JSON Patch, Range Requests, and Streaming.
/// </summary>
[Collection("Sequential")]
public sealed class DatabaseAdvancedFeaturesE2E : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _client;
    private readonly string _tenant = "test-advanced-features";

    public DatabaseAdvancedFeaturesE2E(ITestOutputHelper output)
    {
        _output = output;
        _client = new HttpClient { BaseAddress = new Uri(TestUrls.GatewayBaseUrl) };
    } // End of Constructor DatabaseAdvancedFeaturesE2E

    public void Dispose()
    {
        _client.Dispose();
    } // End of Method Dispose

    private async Task<string> GetAdminTokenAsync()
    {
        var tokenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TestUrls.GatewayBaseUrl}/identity/connect/token"
        );
        tokenRequest.Content = new StringContent(
            "grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=admin.full",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );

        var tokenResponse = await _client.SendAsync(tokenRequest);
        tokenResponse.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    } // End of Method GetAdminTokenAsync

    [Fact]
    public async Task RangeRequest_Returns_PartialContent()
    {
        // Arrange
        var token = await GetAdminTokenAsync();
        await ProvisionTenantAsync(token);
        var collectionId = await CreateCollectionAsync("range-test", token);

        // Create a large document for range testing
        var docContent = new { data = new string('x', 1000) };
        var docId = await CreateDocumentAsync(collectionId, docContent, token);

        // Act: Request partial content
        var request = new HttpRequestMessage(HttpMethod.Get, $"/db/api/documents/{docId}");
        request.Headers.Add("X-Tansu-Tenant", _tenant);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = new RangeHeaderValue(0, 99); // First 100 bytes

        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.True(response.Content.Headers.Contains("Content-Range"));
        Assert.True(response.Headers.Contains("Accept-Ranges"));

        var contentRange = response.Content.Headers.GetValues("Content-Range").First();
        Assert.StartsWith("bytes 0-99/", contentRange);

        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, content.Length);

        _output.WriteLine(
            $"✅ Range request successful: {contentRange}, received {content.Length} bytes"
        );
    } // End of Method RangeRequest_Returns_206_PartialContent

    [Fact]
    public async Task RangeRequest_Invalid_Returns_416()
    {
        // Arrange
        var token = await GetAdminTokenAsync();
        await ProvisionTenantAsync(token);
        var collectionId = await CreateCollectionAsync("range-invalid-test", token);

        // Create a small document
        var content = new { data = "small" };
        var docId = await CreateDocumentAsync(collectionId, content, token);

        // Act: Request range beyond content size
        var request = new HttpRequestMessage(HttpMethod.Get, $"/db/api/documents/{docId}");
        request.Headers.Add("X-Tansu-Tenant", _tenant);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = new RangeHeaderValue(999999, 999999);

        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.True(response.Content.Headers.Contains("Content-Range"));

        _output.WriteLine($"✅ Invalid range request correctly returned 416");
    } // End of Method RangeRequest_InvalidRange_Returns_416

    [Fact]
    public async Task DocumentStream_Returns_NDJSON()
    {
        // Arrange
        var token = await GetAdminTokenAsync();
        await ProvisionTenantAsync(token);
        var collectionId = await CreateCollectionAsync("stream-test", token);

        // Create 5 documents
        for (int i = 0; i < 5; i++)
        {
            var doc = new
            {
                collectionId = collectionId,
                content = new { index = i, data = $"Document {i}" }
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "/db/api/documents")
            {
                Content = JsonContent.Create(
                    doc,
                    options: new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }
                )
            };
            req.Headers.Add("X-Tansu-Tenant", _tenant);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await _client.SendAsync(req);
        }

        // Act: Stream documents
        var streamRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/db/api/documents/stream?collectionId={collectionId}"
        );
        streamRequest.Headers.Add("X-Tansu-Tenant", _tenant);
        streamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Accept any content type for streaming
        streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var streamResponse = await _client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead
        );
        streamResponse.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal("application/x-ndjson", streamResponse.Content.Headers.ContentType?.MediaType);

        var stream = await streamResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var lineCount = 0;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(line);
                Assert.True(doc.TryGetProperty("id", out _));
                Assert.True(doc.TryGetProperty("content", out _));
                lineCount++;
            }
        }

        Assert.Equal(5, lineCount);
        _output.WriteLine($"✅ Document streaming successful: {lineCount} documents streamed");
    } // End of Method DocumentStream_Returns_NDJSON

    [Fact]
    public async Task CollectionStream_Returns_NDJSON()
    {
        // Arrange
        var token = await GetAdminTokenAsync();
        await ProvisionTenantAsync(token);

        // Create multiple collections
        for (var i = 0; i < 3; i++)
        {
            await CreateCollectionAsync($"stream-collection-{i}", token);
        }

        // Act: Stream collections
        var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/db/api/collections/stream");
        streamRequest.Headers.Add("X-Tansu-Tenant", _tenant);
        streamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Accept any content type for streaming
        streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var streamResponse = await _client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead
        );
        streamResponse.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal("application/x-ndjson", streamResponse.Content.Headers.ContentType?.MediaType);

        var stream = await streamResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var lineCount = 0;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                var collection = JsonSerializer.Deserialize<JsonElement>(line);
                Assert.True(collection.TryGetProperty("id", out _));
                Assert.True(collection.TryGetProperty("name", out _));
                lineCount++;
            }
        }

        Assert.True(lineCount >= 3, $"Expected at least 3 collections, got {lineCount}");
        _output.WriteLine($"✅ Collection streaming successful: {lineCount} collections streamed");
    } // End of Method CollectionStream_Returns_NDJSON

    // Helper methods
    private async Task ProvisionTenantAsync(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/db/api/provisioning/tenants")
        {
            Content = JsonContent.Create(
                new { tenantId = _tenant, displayName = "Advanced Features Test" }
            )
        };
        req.Headers.Add("X-Provision-Key", "letmein");

        var response = await _client.SendAsync(req);
        // Idempotent - 200 or 409 both OK
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict,
            $"Provisioning failed with {response.StatusCode}"
        );
    } // End of Method ProvisionTenantAsync

    private async Task<Guid> CreateCollectionAsync(string name, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/db/api/collections")
        {
            Content = JsonContent.Create(new { name })
        };
        req.Headers.Add("X-Tansu-Tenant", _tenant);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(req);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    } // End of Method CreateCollectionAsync

    private async Task<Guid> CreateDocumentAsync(Guid collectionId, object content, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/db/api/documents")
        {
            Content = JsonContent.Create(
                new { collectionId, content },
                options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }
            )
        };
        req.Headers.Add("X-Tansu-Tenant", _tenant);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(req);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    } // End of Method CreateDocumentAsync
} // End of Class DatabaseAdvancedFeaturesE2E
