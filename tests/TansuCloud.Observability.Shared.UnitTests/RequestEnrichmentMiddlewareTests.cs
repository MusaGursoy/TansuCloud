// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using TansuCloud.Observability;
using Xunit;
using Microsoft.Extensions.Logging;

public class RequestEnrichmentMiddlewareTests
{
    [Fact]
    public async Task Adds_Correlation_Header_And_Scope_Values()
    {
        var capturedScopes = new ConcurrentBag<IDisposable>();
        var logger = new CapturingLogger<RequestEnrichmentMiddleware>(capturedScopes);
        var context = new DefaultHttpContext();
        context.Request.Path = "/db/api/items";
        context.Request.Headers["X-Tansu-Tenant"] = "acme-dev";

        var middleware = new RequestEnrichmentMiddleware(_ => Task.CompletedTask, logger);
        await middleware.InvokeAsync(context);

        context.Response.Headers.TryGetValue("X-Correlation-ID", out var corr).Should().BeTrue();
        corr.ToString().Should().NotBeNullOrWhiteSpace();

        // Our capturing logger records the latest scope dictionary
        var scopeDict = logger.LastScope as IReadOnlyDictionary<string, object?>;
        scopeDict.Should().NotBeNull();
        scopeDict!["Tenant"].Should().Be("acme-dev");
        scopeDict!["RouteBase"].Should().Be("db");
        scopeDict!["CorrelationId"].Should().NotBeNull();
    }

    [Fact]
    public async Task Adds_TraceId_SpanId_When_Activity_Present()
    {
        var capturedScopes = new ConcurrentBag<IDisposable>();
        var logger = new CapturingLogger<RequestEnrichmentMiddleware>(capturedScopes);
        var context = new DefaultHttpContext();
        context.Request.Path = "/storage/api/objects";

        using var activity = new Activity("test").Start();

        var middleware = new RequestEnrichmentMiddleware(_ => Task.CompletedTask, logger);
        await middleware.InvokeAsync(context);

        context.Response.Headers.TryGetValue("X-Correlation-ID", out var corr).Should().BeTrue();
        // When no header is provided, correlation falls back to Activity.TraceId
        corr.ToString().Should().Be(activity.TraceId.ToString());

        var scopeDict = logger.LastScope as IReadOnlyDictionary<string, object?>;
        scopeDict.Should().NotBeNull();
        scopeDict!["TraceId"].Should().NotBeNull().And.BeOfType<string>();
        scopeDict!["SpanId"].Should().NotBeNull().And.BeOfType<string>();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentBag<IDisposable> _scopes;
        public object? LastScope { get; private set; }
        public List<(EventId Id, string Message, object? Scope)> Events { get; } = new();
        public CapturingLogger(ConcurrentBag<IDisposable> scopes) => _scopes = scopes;

        IDisposable ILogger.BeginScope<TState>(TState state)
        {
            LastScope = state;
            var disp = new DummyScope();
            _scopes.Add(disp);
            return disp;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            Events.Add((eventId, msg, LastScope));
        }
        private sealed class DummyScope : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Logs_Include_Trace_Span_From_Scope()
    {
        var capturedScopes = new ConcurrentBag<IDisposable>();
        var logger = new CapturingLogger<RequestEnrichmentMiddleware>(capturedScopes);
        var context = new DefaultHttpContext();
        context.Request.Path = "/gateway/ping";

        using var activity = new Activity("trace-corr").Start();

        var middleware = new RequestEnrichmentMiddleware(_ =>
        {
            // Produce a log entry inside the enriched scope
            logger.LogInformation("Hello from inside pipeline");
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        logger.Events.Should().NotBeEmpty();
        var last = logger.Events[^1];
        var scopeDict = last.Scope as IReadOnlyDictionary<string, object?>;
        scopeDict.Should().NotBeNull();
        scopeDict!["TraceId"].Should().Be(activity.TraceId.ToString());
        scopeDict!["SpanId"].Should().NotBeNull();
        context.Response.Headers["X-Correlation-ID"].ToString().Should().Be(activity.TraceId.ToString());
    }
}
