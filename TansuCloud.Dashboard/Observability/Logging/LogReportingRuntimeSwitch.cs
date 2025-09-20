// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Dashboard.Observability.Logging;

public sealed class LogReportingRuntimeSwitch : ILogReportingRuntimeSwitch
{
    private volatile bool _enabled;
    public LogReportingRuntimeSwitch(bool enabled) => _enabled = enabled; // End of Constructor LogReportingRuntimeSwitch
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    } // End of Property Enabled
} // End of Class LogReportingRuntimeSwitch
