// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TansuCloud.Dashboard.Observability.Logging;

namespace TansuCloud.Dashboard.UnitTests;

public sealed class LogReportingBackgroundServiceTests
{
    [Fact(DisplayName = "Filter enforces severity, allowlist, and pseudonymization")]
    public void Filter_Respects_Severity_Allowlist_And_Hashing()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new List<LogRecord>
        {
            new()
            {
                Timestamp = now,
                Level = "Information",
                Category = "Tansu.Gateway",
                Message = "ignored info"
            },
            new()
            {
                Timestamp = now,
                Level = "Warning",
                Category = "Other.Namespace",
                Message = "sampled warning"
            },
            new()
            {
                Timestamp = now,
                Level = "Warning",
                Category = "Tansu.Gateway.Proxy",
                Message = "allow warning",
                Tenant = "tenant-123"
            },
            new()
            {
                Timestamp = now,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "storage failure",
                EventId = 4001,
                Tenant = "tenant-123"
            },
            new()
            {
                Timestamp = now,
                Level = "Error",
                Category = "Perf",
                Message = "http latency breach",
                EventId = 1501,
                Tenant = "tenant-123"
            }
        };

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Warning",
            AllowedWarningCategories = new[] { "Tansu.Gateway" },
            WarningSamplingPercent = 0,
            MaxItems = 10,
            QueryWindowMinutes = 60,
            PseudonymizeTenants = true,
            TenantHashSecret = "shared-secret"
        };

        var service = CreateService(new InMemoryLogBuffer(256));
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        items.Should().HaveCount(3);
        items.Should().OnlyContain(i => i.Level != "Information");
        items
            .Select(i => i.Kind)
            .Should()
            .Contain(new[] { "warning", "telemetry_internal", "perf_slo_breach" });

        var expectedHash = ComputeHmac("tenant-123", options.TenantHashSecret!);
        items
            .Where(i => i.Kind != "perf_slo_breach")
            .Select(i => i.TenantHash)
            .Should()
            .AllSatisfy(hash => hash.Should().Be(expectedHash));
    }

    [Fact(DisplayName = "Filter caps output at MaxItems and reports consumed count")]
    public void Filter_Honors_MaxItems()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Enumerable
            .Range(0, 60)
            .Select(i => new LogRecord
            {
                Timestamp = now,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = $"err-{i}"
            })
            .ToList();

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Warning",
            AllowedWarningCategories = Array.Empty<string>(),
            WarningSamplingPercent = 100,
            MaxItems = 50,
            QueryWindowMinutes = 60,
            PseudonymizeTenants = false
        };

        var service = CreateService(new InMemoryLogBuffer(256));
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        var consumed = (int)result.GetType().GetProperty("SourceCount")!.GetValue(result)!;

        items.Should().HaveCount(50);
        consumed.Should().Be(50);
    }

    [Fact(DisplayName = "Failed report retains buffered items for retry")]
    public async Task RunOnce_Failure_Retains_Buffer()
    {
        var buffer = new InMemoryLogBuffer(64);
        buffer.Add(
            new LogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "storage"
            }
        );

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Information",
            WarningSamplingPercent = 100,
            AllowedWarningCategories = new[] { "Tansu" },
            MaxItems = 100,
            QueryWindowMinutes = 60,
            MainServerUrl = "https://example.com/api/logs/report",
            HttpTimeoutSeconds = 5,
            PseudonymizeTenants = false
        };

        var reporter = new ThrowingReporter(
            new HttpRequestException("fail", null, System.Net.HttpStatusCode.Unauthorized)
        );
        var service = CreateService(buffer, reporter);
        var runOnce = GetRunOnceMethod();

        var success = await (Task<bool>)
            runOnce.Invoke(service, new object[] { options, CancellationToken.None })!;
        success.Should().BeFalse();
        buffer.Count.Should().Be(1);
    }

    [Fact(DisplayName = "Successful report removes consumed items from buffer")]
    public async Task RunOnce_Success_Removes_Items()
    {
        var buffer = new InMemoryLogBuffer(64);
        buffer.Add(
            new LogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "err-1"
            }
        );
        buffer.Add(
            new LogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "err-2"
            }
        );
        buffer.Add(
            new LogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Warning",
                Category = "Tansu.Gateway",
                Message = "warn-allow"
            }
        );

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Information",
            WarningSamplingPercent = 100,
            AllowedWarningCategories = new[] { "Tansu" },
            MaxItems = 10,
            QueryWindowMinutes = 60,
            MainServerUrl = "https://example.com/api/logs/report",
            HttpTimeoutSeconds = 5,
            PseudonymizeTenants = false
        };

        var reporter = new RecordingReporter();
        var service = CreateService(buffer, reporter);
        var runOnce = GetRunOnceMethod();

        var success = await (Task<bool>)
            runOnce.Invoke(service, new object[] { options, CancellationToken.None })!;
        success.Should().BeTrue();
        buffer.Count.Should().Be(0);
        reporter.LastRequest.Should().NotBeNull();
        reporter.LastRequest!.Items.Should().HaveCount(3);
        reporter
            .LastRequest.Items.Select(i => i.Message)
            .Should()
            .Contain(new[] { "err-1", "err-2", "warn-allow" });
    }

    [Fact(DisplayName = "Filter drops non-allowlisted warnings when sampling fails")]
    public void Filter_Drops_Warning_When_Sampling_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new List<LogRecord>
        {
            new()
            {
                Timestamp = now,
                Level = "Warning",
                Category = "Other.Namespace",
                Message = "should-drop"
            }
        };

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Warning",
            AllowedWarningCategories = Array.Empty<string>(),
            WarningSamplingPercent = 25,
            MaxItems = 10,
            QueryWindowMinutes = 60,
            PseudonymizeTenants = false
        };

        var service = CreateService(new InMemoryLogBuffer(32));
        SetRandomSequence(service, 80);
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        items.Should().BeEmpty();
    }

    [Fact(DisplayName = "Filter includes sampled warning when sampling threshold passes")]
    public void Filter_Includes_Warning_When_Sampling_Passes()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new List<LogRecord>
        {
            new()
            {
                Timestamp = now,
                Level = "Warning",
                Category = "Other.Namespace",
                Message = "should-pass"
            }
        };

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Warning",
            AllowedWarningCategories = Array.Empty<string>(),
            WarningSamplingPercent = 25,
            MaxItems = 10,
            QueryWindowMinutes = 60,
            PseudonymizeTenants = false
        };

        var service = CreateService(new InMemoryLogBuffer(32));
        SetRandomSequence(service, 10);
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        items.Should().ContainSingle();
        items[0].Message.Should().Be("should-pass");
    }

    [Fact(DisplayName = "Filter ignores records outside the query window")]
    public void Filter_Ignores_Records_Outside_Window()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new List<LogRecord>
        {
            new()
            {
                Timestamp = now.AddHours(-2),
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "stale"
            },
            new()
            {
                Timestamp = now.AddMinutes(-10),
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "recent"
            }
        };

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Information",
            AllowedWarningCategories = Array.Empty<string>(),
            WarningSamplingPercent = 100,
            MaxItems = 10,
            QueryWindowMinutes = 30,
            PseudonymizeTenants = false
        };

        var service = CreateService(new InMemoryLogBuffer(32));
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        items.Should().ContainSingle();
        items[0].Message.Should().Be("recent");
    }

    [Fact(DisplayName = "Filter aggregates perf_slo_breach events into single item")]
    public void Filter_Aggregates_Perf_Events()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new List<LogRecord>
        {
            new()
            {
                Timestamp = now,
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "perf",
                EventId = 1501
            },
            new()
            {
                Timestamp = now.AddSeconds(5),
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "perf",
                EventId = 1501
            },
            new()
            {
                Timestamp = now.AddSeconds(10),
                Level = "Error",
                Category = "Tansu.Storage",
                Message = "perf",
                EventId = 1501
            }
        };

        var options = new LogReportingOptions
        {
            SeverityThreshold = "Information",
            AllowedWarningCategories = Array.Empty<string>(),
            WarningSamplingPercent = 100,
            MaxItems = 10,
            QueryWindowMinutes = 60,
            PseudonymizeTenants = false
        };

        var service = CreateService(new InMemoryLogBuffer(32));
        var filter = GetFilterMethod();
        var result = filter.Invoke(service, new object[] { snapshot, options })!;

        var items =
            (IReadOnlyList<LogItem>)result.GetType().GetProperty("Items")!.GetValue(result)!;
        items.Should().ContainSingle();
        var perfItem = items[0];
        perfItem.Kind.Should().Be("perf_slo_breach");
        perfItem.Count.Should().Be(3);
    }

    private static MethodInfo GetFilterMethod()
    {
        return typeof(LogReportingBackgroundService).GetMethod(
                "FilterForReporting",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("FilterForReporting not found");
    }

    private static MethodInfo GetRunOnceMethod()
    {
        return typeof(LogReportingBackgroundService).GetMethod(
                "RunOnceAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("RunOnceAsync not found");
    }

    private static void SetRandomSequence(
        LogReportingBackgroundService service,
        params int[] sequence
    )
    {
        var field =
            typeof(LogReportingBackgroundService).GetField(
                "_rand",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("_rand field not found");
        field.SetValue(service, new SequenceRandom(sequence));
    }

    private static LogReportingBackgroundService CreateService(
        ILogBuffer buffer,
        ILogReporter? reporter = null
    )
    {
        var services = new ServiceCollection();
        services.AddScoped<ILogReporter>(_ => reporter ?? new NoopLogReporter());
        var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<LogReportingOptions>(new LogReportingOptions());
        var runtime = new LogReportingRuntimeSwitch(true);
        return new LogReportingBackgroundService(
            provider,
            NullLogger<LogReportingBackgroundService>.Instance,
            options,
            runtime,
            buffer
        );
    }

    private static string ComputeHmac(string tenant, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(tenant)));
    }

    private sealed class ThrowingReporter : ILogReporter
    {
        private readonly Exception _exception;

        public ThrowingReporter(Exception exception)
        {
            _exception = exception;
        }

        public Task ReportAsync(
            LogReportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromException(_exception);
        }
    }

    private sealed class RecordingReporter : ILogReporter
    {
        public LogReportRequest? LastRequest { get; private set; }

        public Task ReportAsync(
            LogReportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceRandom : Random
    {
        private readonly Queue<int> _values;

        public SequenceRandom(IEnumerable<int> values)
        {
            _values = new Queue<int>(values);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (_values.Count == 0)
            {
                return minValue;
            }

            var value = _values.Dequeue();
            if (value < minValue)
            {
                return minValue;
            }

            if (value >= maxValue)
            {
                return maxValue - 1;
            }

            return value;
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string> listener)
        {
            listener(_value, string.Empty);
            return new NoopDisposable();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
