// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Dashboard.Observability;

namespace TansuCloud.Dashboard.UnitTests;

public class PrometheusQueryServiceTests
{
    private static (PrometheusQueryService svc, HttpMessageHandlerStub stub) CreateService(
        string? headerTenant = null,
        ClaimsPrincipal? user = null,
        string baseUrl = "http://prom/"
    )
    {
        // IHttpClientFactory that returns a client using our stub handler
        var stub = new HttpMessageHandlerStub();
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient("prometheus"))
            .Returns(() => new HttpClient(stub) { BaseAddress = new Uri(baseUrl) });

        // Options
        var opts = Options.Create(
            new PrometheusOptions
            {
                BaseUrl = baseUrl,
                TimeoutSeconds = 5,
                DefaultRangeMinutes = 10,
                MaxRangeMinutes = 60,
                MaxStepSeconds = 60,
                CacheTtlSeconds = 0, // disable cache to make URL assertions easier
            }
        );

        // HttpContextAccessor with optional X-Tansu-Tenant and user
        var accessor = new HttpContextAccessor();
        var ctx = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(headerTenant))
            ctx.Request.Headers["X-Tansu-Tenant"] = headerTenant;
        if (user != null)
            ctx.User = user;
        accessor.HttpContext = ctx;

        var svc = new PrometheusQueryService(
            factory.Object,
            opts,
            NullLogger<PrometheusQueryService>.Instance,
            accessor
        );
        return (svc, stub);
    }

    private static ClaimsPrincipal MakeUser(bool isAdmin)
    {
        if (!isAdmin)
            return new ClaimsPrincipal(new ClaimsIdentity());
        // admin via role
        return new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Admin") })
        );
    }

    [Fact(DisplayName = "Non-admin tenant override is ignored; header tenant enforced")]
    public async Task NonAdminTenantOverrideIgnored()
    {
        var (svc, stub) = CreateService(headerTenant: "acme", user: MakeUser(false));

        await svc.QueryRangeAsync(
            "storage.http.rps",
            tenant: "other",
            service: null,
            range: TimeSpan.FromMinutes(5)
        );

        stub.LastRequestUri.Should().NotBeNull();
        var dict = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query);
        var query = dict["query"].ToString(); // PromQL
        query.Should().Contain("tenant=\"acme\"");
        // Ensure selector is attached to the metric inside rate()
        query.Should().Contain("rate(tansu_storage_responses_total{tenant=\"acme\"}[");
        query.Should().NotContain("tenant=\"other\"");
    }

    [Fact(DisplayName = "Admin tenant override allowed when header missing")]
    public async Task AdminTenantOverrideAllowed()
    {
        var (svc, stub) = CreateService(headerTenant: null, user: MakeUser(true));

        await svc.QueryRangeAsync(
            "storage.http.rps",
            tenant: "acme",
            service: null,
            range: TimeSpan.FromMinutes(5)
        );

        var dict = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query);
        var query = dict["query"].ToString();
        query.Should().Contain("tenant=\"acme\"");
    }

    [Fact(DisplayName = "Gateway charts do not include tenant label")]
    public async Task GatewayChartsHaveNoTenantFilter()
    {
        var (svc, stub) = CreateService(headerTenant: "acme", user: MakeUser(false));

        await svc.QueryRangeAsync(
            "gateway.http.rps.byroute",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );

        var dict = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query);
        var query = dict["query"].ToString();
        query.Should().Contain("tansu_gateway_proxy_requests_total");
        query.Should().NotContain("tenant=");
    }

    [Fact(DisplayName = "Outbox metric names mapped as expected")]
    public async Task OutboxMetricNames()
    {
        var (svc, stub) = CreateService();

        await svc.QueryRangeAsync(
            "db.outbox.dispatched.rate",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q1 = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q1.Should().Contain("outbox_dispatched_total");

        await svc.QueryRangeAsync(
            "db.outbox.retried.rate",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q2 = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q2.Should().Contain("outbox_retried_total");

        await svc.QueryRangeAsync(
            "db.outbox.deadlettered.rate",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q3 = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q3.Should().Contain("outbox_deadlettered_total");
    }

    [Fact(DisplayName = "Latency charts use histogram buckets with unit suffixes")]
    public async Task LatencyBucketNames()
    {
        var (svc, stub) = CreateService(headerTenant: "acme");

        await svc.QueryRangeAsync(
            "storage.http.latency.p95",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q1 = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q1.Should().Contain("histogram_quantile(0.95");
        q1.Should().Contain("tansu_storage_request_duration_ms_milliseconds_bucket");
        q1.Should().Contain("sum by(op, le)");
        q1.Should().Contain("tenant=\"acme\"");

        await svc.QueryRangeAsync(
            "gateway.http.latency.p95.byroute",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q2 = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q2.Should().Contain("histogram_quantile(0.95");
        q2.Should().Contain("tansu_gateway_proxy_request_duration_ms_milliseconds_bucket");
        q2.Should().Contain("sum by(route, le)");
        q2.Should().NotContain("tenant=");
    }

    [Fact(DisplayName = "Overview errors include 4xx/5xx filter and tenant when present")]
    public async Task OverviewErrorsFilter()
    {
        var (svc, stub) = CreateService(headerTenant: "t1");

        await svc.QueryRangeAsync(
            "overview.errors.byservice",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q.Should().Contain("status=~\"4xx|5xx\"");
        q.Should().Contain("tenant=\"t1\"");
        q.Should().Contain("sum by(service)");
    }

    [Fact(DisplayName = "Storage status groups by status and includes tenant filter")]
    public async Task StorageStatusByStatusIncludesTenant()
    {
        var (svc, stub) = CreateService(headerTenant: "acme");

        await svc.QueryRangeAsync(
            "storage.http.status",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5)
        );
        var q = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query)["query"].ToString();
        q.Should().Contain("sum by(status)");
        q.Should().Contain("tansu_storage_responses_total");
        q.Should().Contain("tenant=\"acme\"");
    }

    [Fact(DisplayName = "Step is clamped to MaxStepSeconds when larger is requested")]
    public async Task StepClampToMax()
    {
        var (svc, stub) = CreateService();

        await svc.QueryRangeAsync(
            "storage.http.rps",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5),
            step: TimeSpan.FromSeconds(600)
        );
        // default MaxStepSeconds in options is 60, so step should be 60
        var dict = QueryHelpers.ParseQuery(stub.LastRequestUri!.Query);
        dict["step"].ToString().Should().Be("60");
    }

    [Fact(DisplayName = "Unknown chart id throws InvalidOperationException")]
    public async Task UnknownChartIdThrows()
    {
        var (svc, _) = CreateService();

        var act = async () =>
            await svc.QueryRangeAsync(
                "does.not.exist",
                tenant: null,
                service: null,
                range: TimeSpan.FromMinutes(1)
            );
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(DisplayName = "Deserialization of Prometheus matrix response returns series and values")]
    public async Task PrometheusMatrixDeserialization()
    {
        var (svc, stub) = CreateService();
        stub.JsonPayload =
            "{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[{\"metric\":{\"route\":\"/db\"},\"values\":[[1700000000,\"1.5\"],[1700000300,\"2.0\"]]},{\"metric\":{\"route\":\"/storage\"},\"values\":[[1700000000,\"3\"],[1700000300,\"4\"]]}]}}";

        var res = await svc.QueryRangeAsync(
            "gateway.http.rps.byroute",
            tenant: null,
            service: null,
            range: TimeSpan.FromMinutes(5),
            step: TimeSpan.FromSeconds(1)
        );
        res.Should().NotBeNull();
        res!.resultType.Should().Be("matrix");
        res.result.Should().HaveCount(2);
        res.result[0].metric.Should().ContainKey("route");
        res.result[0].values.Should().NotBeNull();
        res.result[0].values.Should().NotBeEmpty();
    }
}
