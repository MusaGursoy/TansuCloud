// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TansuCloud.Observability;
using Xunit;

public class RequestEnrichmentScopeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestEnrichmentScopeTests(WebApplicationFactory<Program> factory) =>
        _factory = factory; // End of Constructor RequestEnrichmentScopeTests

    [Fact(
        DisplayName = "Gateway scope contains correlation/tenant/route/trace/span and echoes header"
    )]
    public async Task Scope_And_Header_Echo_Work()
    {
        var provider = new ScopeCapturingLoggerProvider();

        using var app = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddProvider(provider);
                lb.SetMinimumLevel(LogLevel.Information);
            });
            builder.Configure(app =>
            {
                // Run enrichment middleware before routing
                app.UseMiddleware<RequestEnrichmentMiddleware>();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet(
                        "/__scope-probe",
                        async context =>
                        {
                            var lf = context.RequestServices.GetRequiredService<ILoggerFactory>();
                            var log = lf.CreateLogger("ScopeProbe");
                            log.LogInformation("Hello from probe");
                            await context.Response.WriteAsync("ok");
                        }
                    );
                });
            });
        });

        var client = app.CreateClient();
        var corr = "gw-scope-corr";
        var tenant = "acme-dev";
        using var req = new HttpRequestMessage(HttpMethod.Get, "/__scope-probe");
        req.Headers.TryAddWithoutValidation("X-Correlation-ID", corr);
        req.Headers.TryAddWithoutValidation("X-Tansu-Tenant", tenant);

        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        resp.Headers.TryGetValues("X-Correlation-ID", out var echoed).Should().BeTrue();
        echoed!.Should().ContainSingle().Which.Should().Be(corr);

        // Find our probe log event and inspect the captured scope
        var lastEvent = provider.Events.LastOrDefault(e => e.Category == "ScopeProbe");
        lastEvent.Should().NotBeNull();
        var scope = lastEvent!.Scope as IReadOnlyDictionary<string, object?>;
        scope.Should().NotBeNull();
        scope!["CorrelationId"].Should().Be(corr);
        scope!["Tenant"].Should().Be(tenant);
        scope!["RouteBase"].Should().Be("__scope-probe");
        scope!.ContainsKey("TraceId").Should().BeTrue();
        scope!.ContainsKey("SpanId").Should().BeTrue();
    }

    private sealed class ScopeCapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, ScopeLogger> _loggers = new();
        private readonly AsyncLocal<Stack<object?>> _scopes = new();
        public List<(
            string Category,
            LogLevel Level,
            EventId Id,
            string Message,
            object? Scope
        )> Events { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, c => new ScopeLogger(c, this));

        public void Dispose() { }

        private sealed class ScopeLogger : ILogger
        {
            private readonly string _category;
            private readonly ScopeCapturingLoggerProvider _owner;

            public ScopeLogger(string category, ScopeCapturingLoggerProvider owner)
            {
                _category = category;
                _owner = owner;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                var stack = _owner._scopes.Value ??= new Stack<object?>();
                stack.Push(state!);
                return new PopOnDispose(stack);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                var stack = _owner._scopes.Value;
                var current = stack is { Count: > 0 } ? stack.Peek() : null;
                _owner.Events.Add(
                    (_category, logLevel, eventId, formatter(state, exception), current)
                );
            }

            private sealed class PopOnDispose : IDisposable
            {
                private readonly Stack<object?> _stack;

                public PopOnDispose(Stack<object?> stack) => _stack = stack;

                public void Dispose()
                {
                    if (_stack.Count > 0)
                        _stack.Pop();
                }
            }
        }
    }
}
