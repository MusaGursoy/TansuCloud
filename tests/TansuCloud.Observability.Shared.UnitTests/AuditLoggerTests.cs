// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TansuCloud.Observability.Auditing;
using Xunit;

namespace TansuCloud.Observability.Shared.UnitTests;

public class AuditLoggerTests
{
    [Fact]
    public void IdempotencyKey_Is_Stable_For_Natural_Key()
    {
        var e1 = new AuditEvent
        {
            Service = "svc",
            Subject = "user1",
            Action = "Act",
            CorrelationId = "corr",
            WhenUtc = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
        };
        var e2 = new AuditEvent
        {
            Service = "svc",
            Subject = "user1",
            Action = "Act",
            CorrelationId = "corr",
            WhenUtc = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
        };
        var k1 = AuditKey.Compute(e1);
        var k2 = AuditKey.Compute(e2);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void Truncates_Details_When_Exceeds_MaxBytes()
    {
        var opts = Options.Create(
            new AuditOptions
            {
                MaxDetailsBytes = 8,
                FullDropEnabled = true,
                ConnectionString = "Host=localhost;Port=5432;Database=x;Username=x;Password=x"
            }
        );
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var logger = new NullLogger<HttpAuditLogger>();
        var env = new FakeEnv();
        var sut = new PrivateAccessor(opts, http, logger, env);
        var big = new string('a', 100);
        var e = new AuditEvent { Details = JsonDocument.Parse($"\"{big}\"") };
        var enriched = sut.InvokeEnrichThenTruncate(e, http.HttpContext!);
        Assert.NotNull(enriched.Details);
        var json = enriched.Details!.RootElement.GetRawText();
        Assert.Contains("truncated", json);
    }

    [Fact]
    public void ClientIp_IsHashed_When_Salt_Configured()
    {
        var opts = Options.Create(
            new AuditOptions
            {
                MaxDetailsBytes = 1024,
                FullDropEnabled = true,
                ClientIpHashSalt = "salt",
                ConnectionString = "Host=localhost;Port=5432;Database=x;Username=x;Password=x"
            }
        );
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        var http = new HttpContextAccessor { HttpContext = ctx };
        var logger = new NullLogger<HttpAuditLogger>();
        var env = new FakeEnv();
        var sut = new PrivateAccessor(opts, http, logger, env);
        var enriched = sut.InvokeEnrichThenTruncate(new AuditEvent(), ctx);
        Assert.False(string.IsNullOrWhiteSpace(enriched.ClientIpHash));
        Assert.DoesNotContain("127.0.0.1", enriched.ClientIpHash);
    }

    [Fact]
    public void TryEnqueueRedacted_Only_Allows_Whitelisted_Fields()
    {
        var opts = Options.Create(
            new AuditOptions
            {
                MaxDetailsBytes = 1024,
                FullDropEnabled = true,
                ConnectionString = "Host=localhost;Port=5432;Database=x;Username=x;Password=x"
            }
        );
        var ctx = new DefaultHttpContext();
        var http = new HttpContextAccessor { HttpContext = ctx };
        var logger = new NullLogger<HttpAuditLogger>();
        var env = new FakeEnv();
        var sutWrap = new PrivateAccessor(opts, http, logger, env);
        var allow = new[] { "safe" };
        var source = new { safe = 123, secret = "hide" };
        // Build a base event and call extension via the inner IAuditLogger implementation
        var baseEvent = new AuditEvent { Action = "Test", Category = "Unit" };
        bool enq = sutWrap.EnqueueRedacted(baseEvent, source, allow);
        Assert.True(enq);
        // Verify helper produces only whitelisted fields
        var redacted = AuditHelpers.RedactToJson(source, allow);
        var json = redacted.RootElement.GetRawText();
        Assert.Contains("safe", json);
        Assert.DoesNotContain("secret", json);
    }

    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = ".";
        public string EnvironmentName { get; set; } = "Development";
    }

    // Access internal methods by wrapping HttpAuditLogger and exposing helpers (avoid deriving from sealed)
    private sealed class PrivateAccessor
    {
        private readonly HttpAuditLogger _impl;
        public PrivateAccessor(
            IOptions<AuditOptions> options,
            IHttpContextAccessor http,
            Microsoft.Extensions.Logging.ILogger<HttpAuditLogger> logger,
            Microsoft.Extensions.Hosting.IHostEnvironment env
        )
        {
            _impl = new HttpAuditLogger(options, http, logger, env);
        }

        public AuditEvent InvokeEnrichThenTruncate(AuditEvent e, HttpContext ctx)
        {
            // Use reflection to call private methods in the base class
            var enrich = typeof(HttpAuditLogger).GetMethod(
                "EnrichFromHttpContext",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!;
            var trunc = typeof(HttpAuditLogger).GetMethod(
                "TruncateDetailsIfNeeded",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            )!;
            var enriched = (AuditEvent)enrich.Invoke(_impl, new object[] { e, ctx })!;
            return (AuditEvent)trunc.Invoke(null, new object[] { enriched, 8 })!;
        }

        public bool EnqueueRedacted(AuditEvent seed, object src, IEnumerable<string> allow)
        {
            // Use the internal HttpAuditLogger instance to access the IAuditLogger
            IAuditLogger api = _impl;
            return AuditLoggerExtensions.TryEnqueueRedacted(api, seed, src, allow);
        }
    }
}
