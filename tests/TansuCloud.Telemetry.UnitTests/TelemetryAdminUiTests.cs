// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TansuCloud.Telemetry;
using TansuCloud.Telemetry.Configuration;
using TansuCloud.Telemetry.Data;
using TansuCloud.Telemetry.Data.Entities;
using TansuCloud.Telemetry.Security;

namespace TansuCloud.Telemetry.UnitTests;

public sealed class TelemetryAdminUiTests : IClassFixture<TelemetryWebApplicationFactory>
{
    private readonly TelemetryWebApplicationFactory _factory;

    public TelemetryAdminUiTests(TelemetryWebApplicationFactory factory)
    {
        _factory = factory;
    } // End of Constructor TelemetryAdminUiTests

    [Fact]
    public async Task AdminRoute_WithoutAuthentication_ShouldRedirectWithStatusMessage()
    {
        await _factory.ResetDatabaseAsync();

        using var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();

        var location = response.Headers.Location!;
        if (!location.IsAbsoluteUri)
        {
            location = new Uri(new Uri("http://localhost"), location);
        }
        location.AbsolutePath.Should().Be(TelemetryAdminAuthenticationDefaults.LoginPath);

        var query = QueryHelpers.ParseQuery(location.Query);
        query.Should().ContainKey("missingKey");
        query["missingKey"].ToString().Should().Be("1");
        query.Should().ContainKey(
            TelemetryAdminAuthenticationDefaults.AuthMessageQueryParameter
        );
        query[TelemetryAdminAuthenticationDefaults.AuthMessageQueryParameter]
            .ToString()
            .Should()
            .Be(TelemetryAdminAuthenticationDefaults.AuthFailureReasons.MissingSession);
    } // End of Test AdminRoute_WithoutAuthentication_ShouldRedirectWithStatusMessage

    [Fact]
    public async Task AdminRoute_WithInvalidCookie_ShouldRedirectWithInvalidSessionMessage()
    {
        await _factory.ResetDatabaseAsync();

        using var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{TelemetryAdminAuthenticationDefaults.ApiKeyCookieName}=stale-key"
        );

        var response = await client.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();

        var location = response.Headers.Location!;
        if (!location.IsAbsoluteUri)
        {
            location = new Uri(new Uri("http://localhost"), location);
        }
        location.AbsolutePath.Should().Be(TelemetryAdminAuthenticationDefaults.LoginPath);

        var query = QueryHelpers.ParseQuery(location.Query);
        query.Should().ContainKey("missingKey");
        query["missingKey"].ToString().Should().Be("1");
        query.Should().ContainKey(
            TelemetryAdminAuthenticationDefaults.AuthMessageQueryParameter
        );
        query[TelemetryAdminAuthenticationDefaults.AuthMessageQueryParameter]
            .ToString()
            .Should()
            .Be(TelemetryAdminAuthenticationDefaults.AuthFailureReasons.InvalidSession);
    } // End of Test AdminRoute_WithInvalidCookie_ShouldRedirectWithInvalidSessionMessage

    [Fact]
    public async Task AdminIndex_ShouldRenderEnvelopesAndMetrics()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Host = "edge-01";
            envelope.Service = "tansu.dashboard";
            envelope.Environment = "Production";
            envelope.SeverityThreshold = "Warning";
        });

        await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Host = "edge-02";
            envelope.Service = "tansu.gateway";
            envelope.Environment = "Production";
            envelope.SeverityThreshold = "Error";
            envelope.AcknowledgedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        });

        using var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Telemetry envelopes");
        html.Should().Contain("edge-01");
        html.Should().Contain("Queue depth");
        html.Should().Contain("Export CSV");

        html.Should().NotContain("edge-02");

        await using var scope = _factory.Services.CreateAsyncScope();
        var ingestionOptions = scope
            .ServiceProvider.GetRequiredService<IOptions<TelemetryIngestionOptions>>()
            .Value;
        var adminOptions = scope
            .ServiceProvider.GetRequiredService<IOptions<TelemetryAdminOptions>>()
            .Value;

        var expectedQueueRatio = string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} / {1:N0}",
            0,
            ingestionOptions.QueueCapacity
        );
        html.Should().Contain(expectedQueueRatio);

        var expectedCapacityHint = string.Format(
            CultureInfo.CurrentCulture,
            "{0}% capacity used",
            0
        );
        html.Should().Contain(expectedCapacityHint);

        var expectedExportLimit = string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0}",
            adminOptions.MaxExportItems
        );
        html.Should().Contain(expectedExportLimit);
        html.Should().Contain("Maximum envelopes per download");
    } // End of Test AdminIndex_ShouldRenderEnvelopesAndMetrics

    [Fact]
    public async Task AdminDetail_ShouldRenderEnvelopeItems()
    {
        await _factory.ResetDatabaseAsync();
        var envelopeId = await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Host = "edge-tenant";
            envelope.Service = "tansu.storage";
            envelope.Environment = "Staging";
            envelope.SeverityThreshold = "Warning";
        });

        using var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/admin/envelopes/detail/{envelopeId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Envelope detail");
        html.Should().ContainEquivalentOf("log items");
        html.Should().Contain("edge-tenant");
        html.Should().Contain("Level: Error");
        html.Should().Contain("Correlation:");
    } // End of Test AdminDetail_ShouldRenderEnvelopeItems

    [Fact]
    public async Task AdminExportCsv_ShouldRespectFilters()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Host = "edge-export";
            envelope.Service = "tansu.identity";
            envelope.Environment = "Production";
            envelope.SeverityThreshold = "Error";
        });

        using var client = _factory.CreateAuthenticatedClient();
        var pageResponse = await client.GetAsync("/admin?Service=tansu.identity");
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        var csvHref = ExtractHref(pageHtml, "Export CSV");
        var csvRequestUri = BuildRequestUriWithPaging(csvHref);
        var response = await client.GetAsync(csvRequestUri);

        var csvBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"CSV export failed: {csvBody}");
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        csvBody.Should().Contain("tansu.identity");
        csvBody.Should().Contain("edge-export");
    } // End of Test AdminExportCsv_ShouldRespectFilters

    [Fact]
    public async Task AdminExportJson_ShouldRespectFilters()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Host = "edge-json";
            envelope.Service = "tansu.gateway";
            envelope.Environment = "Production";
            envelope.SeverityThreshold = "Warning";
        });

        using var client = _factory.CreateAuthenticatedClient();
        var pageResponse = await client.GetAsync("/admin?Service=tansu.gateway");
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        var jsonHref = ExtractHref(pageHtml, "Export JSON");
        var jsonRequestUri = BuildRequestUriWithPaging(jsonHref);
        var response = await client.GetAsync(jsonRequestUri);

        var jsonBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"JSON export failed: {jsonBody}");
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        jsonBody.Should().Contain("\"service\": \"tansu.gateway\"");
        jsonBody.Should().Contain("edge-json");
        jsonBody.Should().Contain("\"items\"");
    } // End of Test AdminExportJson_ShouldRespectFilters

    [Fact]
    public async Task AdminIndex_ExportLinks_ShouldDropPagingParameters()
    {
        await _factory.ResetDatabaseAsync();
        await _factory.SeedEnvelopeAsync(envelope =>
        {
            envelope.Service = "tansu.identity";
            envelope.Environment = "Production";
            envelope.Host = "edge-route";
        });

        using var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync(
            "/admin?Page=2&PageSize=20&Service=tansu.identity&Acknowledged=true&IncludeAcknowledged=true"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        var csvHref = ExtractHref(html, "Export CSV");
        var jsonHref = ExtractHref(html, "Export JSON");

        csvHref.Should().StartWith("/api/admin/envelopes/export/csv");
        jsonHref.Should().StartWith("/api/admin/envelopes/export/json");

        AssertRoute(csvHref);
        AssertRoute(jsonHref);
    } // End of Test AdminIndex_ExportLinks_ShouldDropPagingParameters

    private static void AssertRoute(string href)
    {
        var decoded = WebUtility.HtmlDecode(href);
        var uri = new Uri(new Uri("http://localhost"), decoded);
        var query = QueryHelpers.ParseQuery(uri.Query);

        query.Keys.Should().NotContain("Page");
        query.Keys.Should().NotContain("PageSize");
        query.Should().ContainKey("Service");
        query.Should().ContainKey("IncludeAcknowledged");
        query.Should().ContainKey("IncludeDeleted");
    } // End of Method AssertRoute

    private static string ExtractHref(string html, string linkText)
    {
        var textToken = $">{linkText}<";
        var textIndex = html.IndexOf(textToken, StringComparison.OrdinalIgnoreCase);
        textIndex.Should().BeGreaterThan(0, $"Link text '{linkText}' not found in HTML payload.");

        var anchorStart = html.LastIndexOf("<a", textIndex, StringComparison.OrdinalIgnoreCase);
        anchorStart.Should().BeGreaterThanOrEqualTo(0, "Anchor start not found for link.");

        var hrefStart = html.IndexOf("href=\"", anchorStart, StringComparison.OrdinalIgnoreCase);
        hrefStart.Should().BeGreaterThanOrEqualTo(0, "href attribute not found on link.");
        hrefStart += 6;

        var hrefEnd = html.IndexOf('"', hrefStart);
        hrefEnd.Should().BeGreaterThan(hrefStart, "href attribute not terminated.");

        var href = html.Substring(hrefStart, hrefEnd - hrefStart);
        return WebUtility.HtmlDecode(href);
    } // End of Method ExtractHref

    private static string BuildRequestUriWithPaging(string href)
    {
        var uri = new Uri(new Uri("http://localhost"), href);
        var basePath = uri.GetLeftPart(UriPartial.Path);
        var query = QueryHelpers
            .ParseQuery(uri.Query)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (string?)kvp.Value.ToString(),
                StringComparer.OrdinalIgnoreCase
            );

        query["Page"] = "1";
        query["PageSize"] = "50";

        return QueryHelpers.AddQueryString(basePath, query);
    } // End of Method BuildRequestUriWithPaging
}

public sealed class TelemetryWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string AdminApiKey = "admin-test-key-0001";
    private const string IngestionApiKey = "ingest-test-key-0001";
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"telemetry-tests-{Guid.NewGuid():N}.db"
    );

    public async Task InitializeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        using var client = CreateAuthenticatedClient();
        await client.GetAsync("/health/ready");
        await ResetDatabaseAsync();
    } // End of Method InitializeAsync

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminApiKey
        );
        return client;
    } // End of Method CreateAuthenticatedClient

    public async Task ResetDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    } // End of Method ResetDatabaseAsync

    public async Task<Guid> SeedEnvelopeAsync(Action<TelemetryEnvelopeEntity>? configure = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<TelemetryRepository>();

        var envelope = new TelemetryEnvelopeEntity
        {
            Id = Guid.CreateVersion7(),
            ReceivedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Host = "edge-default",
            Environment = "Production",
            Service = "tansu.dashboard",
            SeverityThreshold = "Warning",
            WindowMinutes = 60,
            MaxItems = 1000,
            Items =
            {
                new TelemetryItemEntity
                {
                    Kind = "log",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-6),
                    Level = "Error",
                    Message = "Unhandled exception captured.",
                    TemplateHash = "template-001",
                    Exception = "System.InvalidOperationException: Sample",
                    Service = "tansu.dashboard",
                    Environment = "Production",
                    TenantHash = "tenant-abc",
                    CorrelationId = "corr-1",
                    TraceId = "trace-1",
                    SpanId = "span-1",
                    Category = "Tansu.Cloud.Sample",
                    EventId = 1010,
                    Count = 1,
                    PropertiesJson = "{\"key\":\"value\"}"
                },
                new TelemetryItemEntity
                {
                    Kind = "log",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-4),
                    Level = "Warning",
                    Message = "Recovered from transient failure.",
                    TemplateHash = "template-002",
                    Service = "tansu.dashboard",
                    Environment = "Production",
                    TenantHash = "tenant-abc",
                    CorrelationId = "corr-1",
                    TraceId = "trace-1",
                    SpanId = "span-2",
                    Category = "Tansu.Cloud.Sample",
                    EventId = 1011,
                    Count = 1,
                    PropertiesJson = "{\"retry\":true}"
                }
            }
        };

        foreach (var item in envelope.Items)
        {
            item.EnvelopeId = envelope.Id;
            item.Envelope = envelope;
        }

        envelope.ItemCount = envelope.Items.Count;
        configure?.Invoke(envelope);

        foreach (var item in envelope.Items)
        {
            item.EnvelopeId = envelope.Id;
            item.Envelope = envelope;
        }

        await repository.PersistAsync(envelope, CancellationToken.None);
        return envelope.Id;
    } // End of Method SeedEnvelopeAsync

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(
            (_, configurationBuilder) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Telemetry:Ingestion:ApiKey"] = IngestionApiKey,
                    ["Telemetry:Admin:ApiKey"] = AdminApiKey,
                    ["Telemetry:Database:FilePath"] = _databasePath,
                    ["Telemetry:Database:EnforceForeignKeys"] = bool.TrueString
                };

                configurationBuilder.AddInMemoryCollection(overrides);
            }
        );
    } // End of Method ConfigureWebHost

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
                try
                {
                    File.Delete(_databasePath);
                }
                catch
                {
                    // Allow cleanup to proceed even if the transient handle persists.
                }
            }
        }
    } // End of Method DisposeAsync
}
