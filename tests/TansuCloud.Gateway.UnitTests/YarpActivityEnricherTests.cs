// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TansuCloud.Gateway.Observability;
using TansuCloud.Observability;
using Xunit;

namespace TansuCloud.Gateway.UnitTests;

public sealed class YarpActivityEnricherTests : IAsyncLifetime
{
    private readonly YarpActivityEnricher _enricher =
        new(NullLogger<YarpActivityEnricher>.Instance);

    public Task InitializeAsync() => _enricher.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _enricher.StopAsync(CancellationToken.None);
        _enricher.Dispose();
    }

    [Fact]
    public void Enricher_sets_tags_from_baggage()
    {
        using var source = new ActivitySource("Yarp.ReverseProxy");

        using var parent = new Activity("parent");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.AddBaggage(TelemetryConstants.Tenant, "acme");
        parent.AddBaggage(TelemetryConstants.RouteBase, "db");
        parent.AddBaggage(TelemetryConstants.RouteTemplate, "/db/{**catch-all}");
        parent.Start();

        var activity = source.StartActivity("proxy", ActivityKind.Client);
        activity.Should().NotBeNull();

        activity!.GetTagItem(TelemetryConstants.Tenant).Should().Be("acme");
        activity.GetTagItem(TelemetryConstants.RouteBase).Should().Be("db");
        activity.GetTagItem(TelemetryConstants.RouteTemplate).Should().Be("/db/{**catch-all}");
        activity.GetTagItem(TelemetryConstants.UpstreamService).Should().Be("database");
        activity.GetTagItem("http.route").Should().Be("/db/{**catch-all}");

        activity.Stop();
        parent.Stop();
    }

    [Fact]
    public void Enricher_fills_defaults_when_route_base_missing()
    {
        using var source = new ActivitySource("Yarp.ReverseProxy");

        using var parent = new Activity("parent");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.AddBaggage(TelemetryConstants.Tenant, "global");
        parent.Start();

        var activity = source.StartActivity("proxy", ActivityKind.Client);
        activity.Should().NotBeNull();

        activity!.GetTagItem(TelemetryConstants.RouteBase).Should().Be("gateway");
        activity.GetTagItem(TelemetryConstants.RouteTemplate).Should().Be("/");
        activity.GetTagItem(TelemetryConstants.UpstreamService).Should().Be("gateway");
        activity.GetTagItem("http.route").Should().Be("/");

        activity.Stop();
        parent.Stop();
    }

    [Fact]
    public void Enricher_copies_status_from_parent_when_missing()
    {
        using var source = new ActivitySource("Yarp.ReverseProxy");

        using var parent = new Activity("parent");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.AddBaggage(TelemetryConstants.RouteBase, "storage");
        parent.SetTag("http.status_code", "503");
        parent.Start();

        var activity = source.StartActivity("proxy", ActivityKind.Client);
        activity.Should().NotBeNull();

        activity!
            .GetTagItem(TelemetryConstants.RouteTemplate)
            .Should()
            .Be("/storage/{**catch-all}");
        activity.GetTagItem(TelemetryConstants.UpstreamService).Should().Be("storage");
        activity.GetTagItem("http.status_code").Should().Be("503");

        activity.Stop();
        parent.Stop();
    }
}
