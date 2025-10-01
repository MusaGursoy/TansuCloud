// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TansuCloud.Database.Provisioning;
using TansuCloud.Observability;
using Xunit;

namespace TansuCloud.Database.UnitTests;

public class TenantProvisionerActivityTests
{
    [Fact]
    public async Task ProvisionAsync_FailureStillEmitsActivityWithTenantTags()
    {
        var options = Options.Create(
            new ProvisioningOptions
            {
                AdminConnectionString =
                    "Host=127.0.0.1;Port=65535;Database=postgres;Username=postgres;Password=postgres"
            }
        );
        var provisioner = new TenantProvisioner(options, NullLogger<TenantProvisioner>.Instance);
        var request = new TenantProvisionRequest("activity-tenant", "Activity Tenant");

        const string backgroundSourceName = "TansuCloud.Background";
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source?.Name == backgroundSourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == backgroundSourceName)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.ThrowsAsync<NpgsqlException>(
            () => provisioner.ProvisionAsync(request, CancellationToken.None)
        );

        activities.Should().Contain(a => a.DisplayName == "TenantProvision");
        var provisioning = activities.First(a => a.DisplayName == "TenantProvision");
        provisioning.GetTagItem(TelemetryConstants.Tenant).Should().Be(request.TenantId);
        provisioning.GetTagItem("tansu.provision.db").Should().Be("tansu_tenant_activity_tenant");
        provisioning.GetTagItem("tansu.provision.extensions_total").Should().Be(2);
        provisioning.GetTagItem("tansu.provision.success").Should().Be(false);
        provisioning.Status.Should().Be(ActivityStatusCode.Error);
        provisioning.GetTagItem("tansu.provision.duration_ms").Should().NotBeNull();
    }
} // End of Class TenantProvisionerActivityTests
