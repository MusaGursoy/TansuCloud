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
using Xunit.Abstractions;

public sealed class DeepLoggingSmokeTests
{
    private readonly ITestOutputHelper _output;
    public DeepLoggingSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RateLimit_Debug_BeforeAfter_Override_Smoke()
    {
        // Arrange
        var logger = new CapturingLogger<RateLimitRejectionAggregator>();
        LogLevel? currentOverride = null; // before: no override → null
        var overrides = new Mock<IDynamicLogLevelOverride>();
        overrides.Setup(o => o.Get(It.IsAny<string>())).Returns(() => currentOverride);

        var agg = new RateLimitRejectionAggregator(logger, overrides.Object, windowSeconds: 1);

        // BEFORE: generate some rejections, expect summary but no per-rejection debug
        agg.Report("db", "acme", "p1");
        agg.Report("db", "acme", "p1");
        agg.Report("db", "globex", "p2");
        Thread.Sleep(1200); // wait for the periodic flush

        var beforeEvents = logger.Events.ToArray();
        DumpEvents("before", beforeEvents);
        beforeEvents.Should().Contain(e => e.Id == LogEvents.RateLimitRejectedSummary.Id);
        beforeEvents.Should().NotContain(e => e.Id == LogEvents.RateLimitRejectedDebug.Id);

        // AFTER: enable debug override and report one more rejection → expect Debug log
        currentOverride = LogLevel.Debug;
        agg.Report("db", "acme", "p1");

        var afterEvents = logger.Events.ToArray();
        DumpEvents("after", afterEvents);
        afterEvents.Should().Contain(e => e.Id == LogEvents.RateLimitRejectedDebug.Id);
    }

    private void DumpEvents(string label, (int Id, string Message)[] events)
    {
        _output.WriteLine($"-- {label} events --");
        foreach (var (id, msg) in events)
        {
            _output.WriteLine($"[{id}] {msg}");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(int Id, string Message)> Events { get; } = new();

        IDisposable ILogger.BeginScope<TState>(TState state) => new Dummy();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((eventId.Id, formatter(state, exception)));

        private sealed class Dummy : IDisposable { public void Dispose() { } }
    }
}
