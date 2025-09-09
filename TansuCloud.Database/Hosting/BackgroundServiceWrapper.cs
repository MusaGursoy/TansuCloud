// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.Extensions.Hosting;

namespace TansuCloud.Database.Hosting;

internal sealed class BackgroundServiceWrapper(Func<CancellationToken, Task> run) : BackgroundService
{
    private readonly Func<CancellationToken, Task> _run = run;
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _run(stoppingToken);
} // End of Class BackgroundServiceWrapper
