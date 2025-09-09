// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using StackExchange.Redis;

namespace TansuCloud.Database.Outbox;

public interface IOutboxPublisher
{
    Task PublishAsync(string channel, string payload, CancellationToken ct);
}

internal sealed class RedisOutboxPublisher(ConnectionMultiplexer mux) : IOutboxPublisher
{
    private readonly ISubscriber _sub = mux.GetSubscriber();

    public Task PublishAsync(string channel, string payload, CancellationToken ct) =>
        _sub.PublishAsync(RedisChannel.Literal(channel), payload); // Redis API has no CT
} // End of Class RedisOutboxPublisher
