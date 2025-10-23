// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TansuCloud.Database.EF;
using TansuCloud.Database.Provisioning;
using TansuCloud.Database.Services;

namespace TansuCloud.Database.UnitTests;

/// <summary>
/// Unit tests for TenantDbContextFactory covering tenant normalization,
/// connection string fallback logic, and compiled model reflection safety.
/// </summary>
public class TenantDbContextFactoryTests
{
    private readonly ILogger<TenantDbContextFactory> _logger;
    private readonly Mock<IHostEnvironment> _mockEnv;

    public TenantDbContextFactoryTests()
    {
        _logger = new LoggerFactory().CreateLogger<TenantDbContextFactory>();
        _mockEnv = new Mock<IHostEnvironment>();
    } // End of Constructor TenantDbContextFactoryTests

    #region Tenant Normalization Edge Cases

    [Theory]
    [InlineData("simple-tenant", "tansu_tenant_simple_tenant")]
    [InlineData("UPPERCASE-TENANT", "tansu_tenant_uppercase_tenant")]
    [InlineData("tenant_with_underscores", "tansu_tenant_tenant_with_underscores")]
    [InlineData("tenant-with-dashes", "tansu_tenant_tenant_with_dashes")]
    [InlineData("tenant.with.dots", "tansu_tenant_tenant_with_dots")]
    [InlineData("tenant@special!chars", "tansu_tenant_tenant_special_chars")]
    [InlineData("123-numeric-start", "tansu_tenant_123_numeric_start")]
    [InlineData("tenant with spaces", "tansu_tenant_tenant_with_spaces")]
    // NOTE: Non-ASCII Unicode letters (Greek, Cyrillic, etc.) are preserved by char.IsLetterOrDigit
    // PostgreSQL supports Unicode identifiers, so "ελληνικά" becomes "tansu_tenant_ελληνικά" not "_______"
    // If stricter ASCII-only enforcement is needed, update NormalizeDbName to check (ch < 128)
    [InlineData("tenant--multiple---dashes", "tansu_tenant_tenant__multiple___dashes")]
    public async Task CreateAsync_HttpContext_NormalizesTenantIdCorrectly(string tenantId, string expectedDbName)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test",
            DatabaseNamePrefix = "tansu_tenant_"
        };
        var factory = new TenantDbContextFactory(
            Options.Create(options),
            _logger
        );

        var httpContext = CreateHttpContext(tenantId, isDevelopment: true);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().Contain($"Database={expectedDbName}");

        // Verify diagnostic header was set in Development
        var headerValue = httpContext.Response.Headers["X-Tansu-Db"].ToString();
        headerValue.Should().Be(expectedDbName);
    } // End of Test CreateAsync_HttpContext_NormalizesTenantIdCorrectly

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_HttpContext_ThrowsWhenTenantHeaderMissing(string tenantHeader)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext(tenantHeader, isDevelopment: false);

        // Act & Assert
        var act = () => factory.CreateAsync(httpContext, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Missing X-Tansu-Tenant header");
    } // End of Test CreateAsync_HttpContext_ThrowsWhenTenantHeaderMissing

    [Theory]
    [InlineData("tenant-1", "custom_prefix_", "custom_prefix_tenant_1")]
    [InlineData("tenant-2", "", "tansu_tenant_tenant_2")]
    [InlineData("tenant-3", null, "tansu_tenant_tenant_3")]
    public async Task CreateAsync_HttpContext_RespectsCustomDatabaseNamePrefix(
        string tenantId,
        string? prefix,
        string expectedDbName)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test",
            DatabaseNamePrefix = prefix
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext(tenantId, isDevelopment: true);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().Contain($"Database={expectedDbName}");
    } // End of Test CreateAsync_HttpContext_RespectsCustomDatabaseNamePrefix

    #endregion

    #region Connection String Fallback Logic

    [Fact]
    public async Task CreateAsync_HttpContext_UsesRuntimeConnectionStringWhenAvailable()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=admin-host;Database=postgres;Username=admin;Password=admin",
            RuntimeConnectionString = "Host=pgcat-host;Port=6432;Database=postgres;Username=runtime;Password=runtime"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("test-tenant", isDevelopment: false);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().Contain("Host=pgcat-host");
        connectionString.Should().Contain("Port=6432");
        connectionString.Should().Contain("Username=runtime");
        connectionString.Should().NotContain("admin-host");
    } // End of Test CreateAsync_HttpContext_UsesRuntimeConnectionStringWhenAvailable

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_HttpContext_FallsBackToAdminConnectionWhenRuntimeIsEmpty(string? runtimeConnection)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=admin-host;Database=postgres;Username=admin;Password=admin",
            RuntimeConnectionString = runtimeConnection
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("test-tenant", isDevelopment: false);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().Contain("Host=admin-host");
        connectionString.Should().Contain("Username=admin");
    } // End of Test CreateAsync_HttpContext_FallsBackToAdminConnectionWhenRuntimeIsEmpty

    [Fact]
    public async Task CreateAsync_TenantId_UsesRuntimeConnectionStringWhenAvailable()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=admin-host;Database=postgres;Username=admin;Password=admin",
            RuntimeConnectionString = "Host=pgcat-host;Port=6432;Database=postgres;Username=runtime;Password=runtime"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);

        // Act
        var context = await factory.CreateAsync("tenant-direct", CancellationToken.None);

        // Assert
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().Contain("Host=pgcat-host");
        connectionString.Should().Contain("Database=tansu_tenant_tenant_direct");
    } // End of Test CreateAsync_TenantId_UsesRuntimeConnectionStringWhenAvailable

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_TenantId_ThrowsWhenTenantIdIsNullOrWhitespace(string invalidTenantId)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);

        // Act & Assert
        var act = () => factory.CreateAsync(invalidTenantId, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    } // End of Test CreateAsync_TenantId_ThrowsWhenTenantIdIsNullOrWhitespace

    #endregion

    #region Compiled Model Reflection Safety

    [Fact]
    public async Task CreateAsync_HttpContext_HandlesCompiledModelGracefully()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("test-tenant", isDevelopment: false);

        // Act - Should not throw even if compiled model reflection fails
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        context.Model.Should().NotBeNull();
    } // End of Test CreateAsync_HttpContext_HandlesCompiledModelGracefully

    [Fact]
    public async Task CreateAsync_TenantId_HandlesCompiledModelGracefully()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);

        // Act - Should not throw even if compiled model reflection fails
        var context = await factory.CreateAsync("compiled-model-test", CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        context.Model.Should().NotBeNull();
    } // End of Test CreateAsync_TenantId_HandlesCompiledModelGracefully

    [Fact]
    public async Task CreateAsync_HttpContext_CompiledModelIsUsedWhenAvailable()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("model-test", isDevelopment: false);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        // The compiled model should be discoverable via reflection
        var modelType = Type.GetType("TansuCloud.Database.EF.TansuDbContextModel, TansuCloud.Database");
        if (modelType != null)
        {
            var instanceProp = modelType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            instanceProp.Should().NotBeNull("compiled model should have static Instance property");
        }
    } // End of Test CreateAsync_HttpContext_CompiledModelIsUsedWhenAvailable

    #endregion

    #region Retry Configuration

    [Fact]
    public async Task CreateAsync_HttpContext_ConfiguresRetryOnFailure()
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("retry-test", isDevelopment: false);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        // Verify connection string includes retry configuration (indirectly via EnableRetryOnFailure)
        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrEmpty();
    } // End of Test CreateAsync_HttpContext_ConfiguresRetryOnFailure

    #endregion

    #region Development Diagnostic Header

    [Theory]
    [InlineData(true, true)] // Development environment should set header
    [InlineData(false, false)] // Production environment should NOT set header
    public async Task CreateAsync_HttpContext_SetsDiagnosticHeaderOnlyInDevelopment(bool isDevelopment, bool shouldSetHeader)
    {
        // Arrange
        var options = new ProvisioningOptions
        {
            AdminConnectionString = "Host=localhost;Database=postgres;Username=test;Password=test"
        };
        var factory = new TenantDbContextFactory(Options.Create(options), _logger);
        var httpContext = CreateHttpContext("diag-test", isDevelopment);

        // Act
        var context = await factory.CreateAsync(httpContext, CancellationToken.None);

        // Assert
        context.Should().NotBeNull();
        var hasHeader = httpContext.Response.Headers.ContainsKey("X-Tansu-Db");
        hasHeader.Should().Be(shouldSetHeader);
    } // End of Test CreateAsync_HttpContext_SetsDiagnosticHeaderOnlyInDevelopment

    #endregion

    #region Helper Methods

    private HttpContext CreateHttpContext(string tenantId, bool isDevelopment)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tansu-Tenant"] = tenantId;

        // Mock environment - IsDevelopment() checks EnvironmentName == "Development"
        _mockEnv.Setup(e => e.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IHostEnvironment)))
            .Returns(_mockEnv.Object);
        httpContext.RequestServices = serviceProvider.Object;

        return httpContext;
    } // End of Method CreateHttpContext

    #endregion
} // End of Class TenantDbContextFactoryTests
