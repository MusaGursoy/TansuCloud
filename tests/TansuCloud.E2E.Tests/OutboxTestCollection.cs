// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Xunit;

namespace TansuCloud.E2E.Tests;

// Central collection to serialize tests that observe global Outbox metrics counters.
[CollectionDefinition("OutboxMetricsSerial", DisableParallelization = true)]
public sealed class OutboxMetricsSerialCollection { }
