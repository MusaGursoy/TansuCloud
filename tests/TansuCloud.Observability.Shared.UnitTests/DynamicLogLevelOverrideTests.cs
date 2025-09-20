// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using Microsoft.Extensions.Logging;
using TansuCloud.Observability;
using Xunit;

public class DynamicLogLevelOverrideTests
{
    [Fact]
    public void Get_Returns_Level_Before_Expiry()
    {
        IDynamicLogLevelOverride svc = Create();
        svc.Set("Category.A", LogLevel.Trace, TimeSpan.FromSeconds(5));
        svc.Get("Category.A").Should().Be(LogLevel.Trace);
    }

    [Fact]
    public void Get_Returns_Null_After_Expiry()
    {
        IDynamicLogLevelOverride svc = Create();
        svc.Set("Category.B", LogLevel.Debug, TimeSpan.FromMilliseconds(10));
        Thread.Sleep(30);
        svc.Get("Category.B").Should().BeNull();
    }

    [Fact]
    public void Snapshot_Contains_Current_Overrides()
    {
        IDynamicLogLevelOverride svc = Create();
        svc.Set("X", LogLevel.Information, TimeSpan.FromSeconds(1));
        var snap = svc.Snapshot();
        snap.Should().ContainKey("X");
    }

    private static IDynamicLogLevelOverride Create() =>
        (IDynamicLogLevelOverride)
            Activator.CreateInstance(typeof(DynamicLogLevelOverride), nonPublic: true)!;
}
