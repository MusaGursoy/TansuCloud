// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

namespace TansuCloud.Telemetry.Data;

/// <summary>
/// Represents a paged query result.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed record PagedResult<T>(long TotalCount, IReadOnlyList<T> Items); // End of Record PagedResult
