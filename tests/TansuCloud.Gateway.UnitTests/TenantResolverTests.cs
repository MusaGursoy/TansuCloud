// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using TansuCloud.Gateway.Services;
using Xunit;

namespace TansuCloud.Gateway.UnitTests;

public sealed class TenantResolverTests
{
    [Theory]
    [InlineData("/t/acme", "acme")]
    [InlineData("/t/acme/", "acme")]
    [InlineData("/dashboard/t/tenant-01/db", "tenant-01")]
    [InlineData("/storage/t/tenant_name/files", "tenant_name")]
    public void Resolve_WhenPathContainsTenant_ReturnsPathTenant(string path, string expected)
    {
        var result = TenantResolver.Resolve("localhost", path);

        result.TenantId.Should().Be(expected);
        result.From.Should().Be(TenantResolver.Source.Path);
    } // End of Method Resolve_WhenPathContainsTenant_ReturnsPathTenant

    [Theory]
    [InlineData("acme.tansu.local", "acme")]
    [InlineData("tenant-01.example.com", "tenant-01")]
    [InlineData("tenant_sub.domain.io", "tenant_sub")]
    public void Resolve_WhenSubdomainContainsTenant_UsesSubdomain(string host, string expected)
    {
        var result = TenantResolver.Resolve(host, "/db/health/live");

        result.TenantId.Should().Be(expected);
        result.From.Should().Be(TenantResolver.Source.Subdomain);
    } // End of Method Resolve_WhenSubdomainContainsTenant_UsesSubdomain

    [Fact]
    public void Resolve_WhenPathAndSubdomainPresent_PathWins()
    {
        var result = TenantResolver.Resolve("contoso.example.com", "/t/pathTenant/db/api");

        result.TenantId.Should().Be("pathTenant");
        result.From.Should().Be(TenantResolver.Source.Both);
    } // End of Method Resolve_WhenPathAndSubdomainPresent_PathWins

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.10")]
    public void TryFromSubdomain_IgnoresReservedHostsAndIps(string host)
    {
        TenantResolver.TryFromSubdomain(host).Should().BeNull();
    } // End of Method TryFromSubdomain_IgnoresReservedHostsAndIps

    [Fact]
    public void TryFromSubdomain_IgnoresWwwPrefix()
    {
        TenantResolver.TryFromSubdomain("www.example.com").Should().BeNull();
    } // End of Method TryFromSubdomain_IgnoresWwwPrefix
} // End of Class TenantResolverTests
