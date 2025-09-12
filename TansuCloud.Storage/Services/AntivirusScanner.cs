// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Storage.Services;

public interface IAntivirusScanner
{
    Task<bool> ScanObjectAsync(string bucket, string key, CancellationToken ct);
}

internal sealed class NoOpAntivirusScanner : IAntivirusScanner
{
    public Task<bool> ScanObjectAsync(string bucket, string key, CancellationToken ct)
    {
        // Placeholder for future integration (e.g., ClamAV, ICAP, or external API)
        return Task.FromResult(true);
    }
} // End of Class NoOpAntivirusScanner
