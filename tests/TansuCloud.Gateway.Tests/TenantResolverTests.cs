// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using TansuCloud.Gateway.Services;
using Xunit;

namespace TansuCloud.Gateway.Tests;

public class TenantResolverTests
{
    [Theory]
    [InlineData("acme.example.com", "/dashboard", "acme", TenantResolver.Source.Subdomain)]
    [InlineData("www.example.com", "/t/foo/dashboard", "foo", TenantResolver.Source.Path)]
    [InlineData("localhost", "/t/bar/db", "bar", TenantResolver.Source.Path)]
    [InlineData("127.0.0.1", "/t/qux", "qux", TenantResolver.Source.Path)]
    [InlineData("tenant.example.com", "/t/override/path", "override", TenantResolver.Source.Both)]
    [InlineData("api.example.com", "/dashboard", "api", TenantResolver.Source.Subdomain)]
    public void Resolve_parses_expected_sources(
        string host,
        string path,
        string? expectedTenant,
        TenantResolver.Source expectedSource
    )
    {
        var r = TenantResolver.Resolve(host, path);
        r.TenantId.Should().Be(expectedTenant);
        r.From.Should().Be(expectedTenant == null ? TenantResolver.Source.None : expectedSource);
    }

    [Theory]
    [InlineData("/t/a", "a")]
    [InlineData("/db/t/a", "a")]
    [InlineData("/identity/t/a/login", "a")]
    [InlineData("/dashboard", null)]
    public void TryFromPath_works(string path, string? expected)
    {
        TenantResolver.TryFromPath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("tenant.example.com", "tenant")]
    [InlineData("www.example.com", null)]
    [InlineData("localhost", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("tenant.example.com:5000", "tenant")]
    public void TryFromSubdomain_works(string host, string? expected)
    {
        TenantResolver.TryFromSubdomain(host).Should().Be(expected);
    }
}
