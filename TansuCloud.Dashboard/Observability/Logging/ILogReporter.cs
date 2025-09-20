// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Threading;
using System.Threading.Tasks;

namespace TansuCloud.Dashboard.Observability.Logging
{
    public interface ILogReporter
    {
        Task ReportAsync(LogReportRequest request, CancellationToken cancellationToken = default);
    } // End of Interface ILogReporter
} // End of Namespace TansuCloud.Dashboard.Observability.Logging
