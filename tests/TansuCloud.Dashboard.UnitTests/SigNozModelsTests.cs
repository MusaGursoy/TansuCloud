// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using TansuCloud.Dashboard.Observability.SigNoz;
using Xunit;

namespace TansuCloud.Dashboard.UnitTests;

/// <summary>
/// Unit tests for SigNoz data models and helpers.
/// These tests verify the structure and behavior of SigNoz DTOs without requiring HTTP calls.
/// </summary>
public class SigNozModelsTests
{
    [Fact]
    public void ServiceStatusResult_InitializesCorrectly()
    {
        // Arrange & Act
        var result = new ServiceStatusResult(
            ServiceName: "gateway",
            ErrorRatePercent: 2.5,
            P95LatencyMs: 95.0,
            P99LatencyMs: 150.0,
            RequestCount: 1000,
            StartTime: DateTime.UtcNow.AddHours(-1),
            EndTime: DateTime.UtcNow
        );

        // Assert
        result.ServiceName.Should().Be("gateway");
        result.ErrorRatePercent.Should().Be(2.5);
        result.P95LatencyMs.Should().Be(95.0);
        result.P99LatencyMs.Should().Be(150.0);
        result.RequestCount.Should().Be(1000);
        result.StartTime.Should().BeCloseTo(DateTime.UtcNow.AddHours(-1), TimeSpan.FromSeconds(5));
        result.EndTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ServiceTopologyResult_SupportsMultipleNodesAndEdges()
    {
        // Arrange
        var nodes = new List<ServiceNode>
        {
            new("gateway", "gateway", 1.5, 100.0),
            new("identity", "service", 0.5, 50.0),
            new("database", "database", 0.1, 500.0)
        };
        var edges = new List<ServiceEdge>
        {
            new("gateway", "identity", 50, 0.5),
            new("gateway", "database", 500, 0.1)
        };

        // Act
        var topology = new ServiceTopologyResult(nodes, edges);

        // Assert
        topology.Nodes.Should().HaveCount(3);
        topology.Edges.Should().HaveCount(2);
        topology.Nodes.Should().Contain(n => n.ServiceName == "gateway");
        topology
            .Edges.Should()
            .Contain(e => e.SourceService == "gateway" && e.TargetService == "identity");
    }

    [Fact]
    public void ServiceListResult_HoldsServiceInfo()
    {
        // Arrange
        var services = new List<ServiceInfo>
        {
            new("gateway", DateTime.UtcNow, new[] { "prod", "v1" }),
            new("identity", DateTime.UtcNow.AddMinutes(-5), new[] { "prod", "auth" })
        };

        // Act
        var result = new ServiceListResult(services);

        // Assert
        result.Services.Should().HaveCount(2);
        result.Services.Should().Contain(s => s.ServiceName == "gateway");
        result.Services.First(s => s.ServiceName == "gateway").Tags.Should().Contain("prod");
    }

    [Fact]
    public void CorrelatedLogsResult_AssociatesLogsWithTrace()
    {
        // Arrange
        var logs = new List<LogEntry>
        {
            new(
                Timestamp: DateTime.UtcNow,
                Level: "ERROR",
                Message: "Authentication failed",
                ServiceName: "identity",
                SpanId: "span-123",
                Attributes: new Dictionary<string, string> { ["user"] = "admin@tansu.local" }
            )
        };

        // Act
        var result = new CorrelatedLogsResult("trace-456", "span-123", logs);

        // Assert
        result.TraceId.Should().Be("trace-456");
        result.SpanId.Should().Be("span-123");
        result.Logs.Should().ContainSingle();
        result.Logs[0].Level.Should().Be("ERROR");
        result.Logs[0].Message.Should().Be("Authentication failed");
        result.Logs[0].ServiceName.Should().Be("identity");
    }

    [Fact]
    public void OtlpHealthResult_TracksExporterStatus()
    {
        // Arrange
        var exporters = new List<OtlpExporterStatus>
        {
            new("gateway", IsHealthy: true, DateTime.UtcNow, null),
            new("identity", IsHealthy: false, DateTime.UtcNow.AddMinutes(-10), "Connection timeout")
        };

        // Act
        var result = new OtlpHealthResult(exporters);

        // Assert
        result.Exporters.Should().HaveCount(2);
        result.Exporters.Should().Contain(e => e.ServiceName == "gateway" && e.IsHealthy);
        result.Exporters.Should().Contain(e => e.ServiceName == "identity" && !e.IsHealthy);
        result
            .Exporters.First(e => e.ServiceName == "identity")
            .ErrorMessage.Should()
            .Be("Connection timeout");
    }

    [Fact]
    public void RecentErrorsResult_StoresErrorTraces()
    {
        // Arrange
        var errors = new List<ErrorTrace>
        {
            new(
                TraceId: "trace-789",
                SpanId: "span-012",
                Timestamp: DateTime.UtcNow,
                ServiceName: "gateway",
                ErrorMessage: "500 Internal Server Error",
                ExceptionType: "System.InvalidOperationException",
                StackTrace: "at Gateway.Process()",
                Attributes: new Dictionary<string, string> { ["http.status_code"] = "500" }
            )
        };

        // Act
        var result = new RecentErrorsResult(errors);

        // Assert
        result.Errors.Should().ContainSingle();
        var error = result.Errors[0];
        error.TraceId.Should().Be("trace-789");
        error.ServiceName.Should().Be("gateway");
        error.ErrorMessage.Should().Be("500 Internal Server Error");
        error.ExceptionType.Should().Be("System.InvalidOperationException");
        error.Attributes.Should().ContainKey("http.status_code");
    }

    [Fact]
    public void SigNozQueryOptions_HasReasonableDefaults()
    {
        // Arrange & Act
        var options = new SigNozQueryOptions();

        // Assert
        options.ApiBaseUrl.Should().Contain("signoz");
        options.TimeoutSeconds.Should().Be(30);
        options.MaxRetries.Should().Be(2);
        options.EnableQueryAllowlist.Should().BeTrue();
        options.SigNozUiBaseUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SigNozQueryOptions_SectionName_IsCorrect()
    {
        // Assert
        SigNozQueryOptions.SectionName.Should().Be("SigNozQuery");
    }

    [Theory]
    [InlineData("gateway", "gateway")]
    [InlineData("identity", "service")]
    [InlineData("database", "database")]
    public void ServiceNode_ClassifiesServiceTypes(string serviceName, string expectedType)
    {
        // Arrange & Act
        var node = new ServiceNode(serviceName, expectedType, 1.0, 100.0);

        // Assert
        node.ServiceName.Should().Be(serviceName);
        node.ServiceType.Should().Be(expectedType);
    }

    [Fact]
    public void ServiceEdge_ConnectsServices()
    {
        // Arrange & Act
        var edge = new ServiceEdge("gateway", "identity", 1000, 0.5);

        // Assert
        edge.SourceService.Should().Be("gateway");
        edge.TargetService.Should().Be("identity");
        edge.CallCount.Should().Be(1000);
        edge.ErrorRate.Should().Be(0.5);
    }

    [Fact]
    public void LogEntry_PreservesAllFields()
    {
        // Arrange
        var timestamp = DateTime.UtcNow;
        var attributes = new Dictionary<string, string>
        {
            ["user.id"] = "12345",
            ["http.method"] = "POST"
        };

        // Act
        var log = new LogEntry(
            Timestamp: timestamp,
            Level: "WARN",
            Message: "Rate limit approaching",
            ServiceName: "gateway",
            SpanId: "span-999",
            Attributes: attributes
        );

        // Assert
        log.Timestamp.Should().Be(timestamp);
        log.Level.Should().Be("WARN");
        log.Message.Should().Be("Rate limit approaching");
        log.ServiceName.Should().Be("gateway");
        log.SpanId.Should().Be("span-999");
        log.Attributes.Should().HaveCount(2);
        log.Attributes["user.id"].Should().Be("12345");
    }

    [Fact]
    public void ErrorTrace_CapturesCompleteStackTrace()
    {
        // Arrange
        var stackTrace =
            @"at TansuCloud.Gateway.Middleware.Process()
at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke()";

        // Act
        var error = new ErrorTrace(
            TraceId: "trace-error-1",
            SpanId: "span-error-1",
            Timestamp: DateTime.UtcNow,
            ServiceName: "gateway",
            ErrorMessage: "Null reference exception",
            ExceptionType: "System.NullReferenceException",
            StackTrace: stackTrace,
            Attributes: new Dictionary<string, string>()
        );

        // Assert
        error.ExceptionType.Should().Be("System.NullReferenceException");
        error.StackTrace.Should().Contain("TansuCloud.Gateway.Middleware.Process");
        error.StackTrace.Should().Contain("Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke");
    }
}
