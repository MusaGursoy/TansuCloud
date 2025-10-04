// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using TansuCloud.Telemetry.Admin;
using TansuCloud.Telemetry.Configuration;

namespace TansuCloud.Telemetry.UnitTests;

public sealed class TelemetryEnvelopeRequestProcessorTests
{
    [Fact]
    public void TryCreateQuery_ShouldReturnErrors_WhenPageSizeExceedsMax()
    {
        var request = new TelemetryEnvelopeListRequest { Page = 2, PageSize = 999 };

        var options = CreateOptions();

        var success = TelemetryEnvelopeRequestProcessor.TryCreateQuery(
            request,
            options,
            out var query,
            out var errors
        );

        success.Should().BeFalse();
        query.Should().BeNull();
        errors.Should().ContainKey("pageSize");
    } // End of Method TryCreateQuery_ShouldReturnErrors_WhenPageSizeExceedsMax

    [Fact]
    public void TryCreateQuery_ShouldNormalizeValues_WhenRequestValid()
    {
        var request = new TelemetryEnvelopeListRequest
        {
            Page = 0,
            PageSize = 0,
            Service = " api ",
            Host = " host01 ",
            Environment = " Production ",
            SeverityThreshold = " Error ",
            Search = " gateway ",
            IncludeAcknowledged = false,
            IncludeDeleted = false
        };

        var options = CreateOptions();

        var success = TelemetryEnvelopeRequestProcessor.TryCreateQuery(
            request,
            options,
            out var query,
            out var errors
        );

        success.Should().BeTrue();
        errors.Should().BeEmpty();
        query.Page.Should().Be(1);
        query.PageSize.Should().Be(options.DefaultPageSize);
        query.Service.Should().Be("api");
        query.Host.Should().Be("host01");
        query.Environment.Should().Be("Production");
        query.SeverityThreshold.Should().Be("Error");
        query.Search.Should().Be("gateway");
        query.IncludeAcknowledged.Should().BeFalse();
        query.IncludeDeleted.Should().BeFalse();
    } // End of Method TryCreateQuery_ShouldNormalizeValues_WhenRequestValid

    [Fact]
    public void TryCreateQuery_ShouldForceInclusion_WhenFilterFlagsProvided()
    {
        var request = new TelemetryEnvelopeListRequest
        {
            Acknowledged = false,
            Deleted = true,
            IncludeAcknowledged = false,
            IncludeDeleted = false
        };

        var options = CreateOptions();

        var success = TelemetryEnvelopeRequestProcessor.TryCreateQuery(
            request,
            options,
            out var query,
            out var errors
        );

        success.Should().BeTrue();
        errors.Should().BeEmpty();
        query.Acknowledged.Should().BeFalse();
        query.Deleted.Should().BeTrue();
        query.IncludeAcknowledged.Should().BeTrue();
        query.IncludeDeleted.Should().BeTrue();
    } // End of Method TryCreateQuery_ShouldForceInclusion_WhenFilterFlagsProvided

    [Fact]
    public void TryCreateQuery_ShouldReturnError_WhenTimeRangeInvalid()
    {
        var request = new TelemetryEnvelopeListRequest
        {
            FromUtc = new DateTimeOffset(2025, 10, 2, 10, 0, 0, TimeSpan.Zero),
            ToUtc = new DateTimeOffset(2025, 10, 2, 9, 0, 0, TimeSpan.Zero)
        };

        var options = CreateOptions();

        var success = TelemetryEnvelopeRequestProcessor.TryCreateQuery(
            request,
            options,
            out var query,
            out var errors
        );

        success.Should().BeFalse();
        query.Should().BeNull();
        errors.Should().ContainKey("fromUtc");
    } // End of Method TryCreateQuery_ShouldReturnError_WhenTimeRangeInvalid

    private static TelemetryAdminOptions CreateOptions()
    {
        return new TelemetryAdminOptions
        {
            ApiKey = "PlaceholderApiKeyValue",
            DefaultPageSize = 50,
            MaxPageSize = 200
        };
    } // End of Method CreateOptions
} // End of Class TelemetryEnvelopeRequestProcessorTests
