// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Threading;
using System.Threading.Tasks;

namespace TansuCloud.Database.Outbox;

internal sealed class NoopOutboxPublisher : IOutboxPublisher
{
    public Task PublishAsync(string channel, string payload, CancellationToken ct) => Task.CompletedTask; // End of Method PublishAsync
} // End of Class NoopOutboxPublisher
