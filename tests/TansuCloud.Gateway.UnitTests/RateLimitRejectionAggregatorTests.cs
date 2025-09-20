// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TansuCloud.Gateway.Services;
using TansuCloud.Observability;
using Xunit;

public class RateLimitRejectionAggregatorTests
{
    [Fact]
    public void Flush_Emits_Summary_With_Top3()
    {
        // Arrange
        var logger = new TestLogger<RateLimitRejectionAggregator>();
        var overrides = new Mock<IDynamicLogLevelOverride>();
        overrides.Setup(o => o.Get(It.IsAny<string>())).Returns((LogLevel?)null);
        var agg = new RateLimitRejectionAggregator(logger, overrides.Object, windowSeconds: 1);

        // Report some rejections
        agg.Report("db", "acme", "p1");
        agg.Report("db", "acme", "p1");
        agg.Report("db", "globex", "p2");

        // Act: wait for one flush window
        Thread.Sleep(1200);

        // Assert
        logger.Events.Should().Contain(e => e.Id == LogEvents.RateLimitRejectedSummary.Id)
            .Which.Message.Should().Contain("total=");
    }

    [Fact]
    public void PerRejection_Debug_When_Override_Active()
    {
        // Arrange
        var logger = new TestLogger<RateLimitRejectionAggregator>();
        var overrides = new Mock<IDynamicLogLevelOverride>();
        overrides.Setup(o => o.Get(It.IsAny<string>())).Returns(LogLevel.Debug);
        var agg = new RateLimitRejectionAggregator(logger, overrides.Object, windowSeconds: 10);

        // Act
        agg.Report("db", "acme", "p1");

        // Assert
        logger.Events.Should().Contain(e => e.Id == LogEvents.RateLimitRejectedDebug.Id);
    }

    [Fact]
    public void Narrative_BeforeAfter_Override_Toggles_Debug()
    {
        // BEFORE: override off → only summary expected on flush, no per-rejection debug
        var logger = new TestLogger<RateLimitRejectionAggregator>();
        var overrides = new Mock<IDynamicLogLevelOverride>();
        overrides.Setup(o => o.Get(It.IsAny<string>())).Returns((LogLevel?)null);
        var agg = new RateLimitRejectionAggregator(logger, overrides.Object, windowSeconds: 1);

        agg.Report("db", "acme", "p1");
        Thread.Sleep(1100); // wait for summary
        logger.Events.Exists(e => e.Id == LogEvents.RateLimitRejectedDebug.Id).Should().BeFalse("no override active");
        logger.Events.Exists(e => e.Id == LogEvents.RateLimitRejectedSummary.Id).Should().BeTrue();

        // AFTER: enable debug override → per-rejection debug appears
        overrides.Reset();
        overrides.Setup(o => o.Get(It.IsAny<string>())).Returns(LogLevel.Debug);
        var agg2 = new RateLimitRejectionAggregator(logger, overrides.Object, windowSeconds: 10);
        agg2.Report("db", "acme", "p2");
        logger.Events.Exists(e => e.Id == LogEvents.RateLimitRejectedDebug.Id).Should().BeTrue();
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(int Id, string Message)> Events { get; } = new();

        IDisposable ILogger.BeginScope<TState>(TState state) => new Dummy();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((eventId.Id, formatter(state, exception)));

        private sealed class Dummy : IDisposable { public void Dispose() { } }
    }
}