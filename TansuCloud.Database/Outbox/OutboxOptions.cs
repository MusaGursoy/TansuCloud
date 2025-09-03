// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Database.Outbox;

public sealed class OutboxOptions
{
    [Range(1, 60)]
    public int PollSeconds { get; init; } = 2;

    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    [Range(0, 100)]
    public int MaxAttempts { get; init; } = 10;

    public string RedisConnection { get; init; } = string.Empty;

    public string Channel { get; init; } = "tansu.outbox";
}
