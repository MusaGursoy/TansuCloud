// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace TansuCloud.Storage.Services;

public interface IQuotaService
{
    Task<(long TotalBytes, long ObjectCount)> GetUsageAsync(CancellationToken ct);
    Task<bool> WillExceedAsync(long incomingBytes, CancellationToken ct);
    Task<QuotaEvaluation> EvaluateAsync(long incomingBytes, CancellationToken ct);
}

internal sealed class FilesystemQuotaService(
    IWebHostEnvironment env,
    IOptions<StorageOptions> options,
    ITenantContext tenant
) : IQuotaService
{
    private readonly StorageOptions _opts = options.Value;
    private readonly string _root = options.Value.RootPath ?? Path.Combine(env.ContentRootPath, "_storage");
    private string TenantRoot => Path.Combine(_root, tenant.TenantId);

    public async Task<(long TotalBytes, long ObjectCount)> GetUsageAsync(CancellationToken ct)
    {
        long bytes = 0;
        long count = 0;
        if (!Directory.Exists(TenantRoot))
            return (0, 0);
        foreach (var file in Directory.EnumerateFiles(TenantRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var fi = new FileInfo(file);
                bytes += fi.Length;
                count++;
            }
            catch { }
        }
        await Task.CompletedTask;
        return (bytes, count);
    }

    public async Task<bool> WillExceedAsync(long incomingBytes, CancellationToken ct)
    {
        var eval = await EvaluateAsync(incomingBytes, ct);
        return eval.Exceeded;
    }

    public async Task<QuotaEvaluation> EvaluateAsync(long incomingBytes, CancellationToken ct)
    {
        var limits = _opts.Quotas ?? new QuotaLimits();
        var (total, count) = await GetUsageAsync(ct);
        var exceeded = false;
        string? reason = null;
        if (limits.MaxObjectSizeBytes > 0 && incomingBytes > limits.MaxObjectSizeBytes)
        {
            exceeded = true;
            reason = "MaxObjectSizeBytes";
        }
        else if (limits.MaxTotalBytes > 0 && (total + incomingBytes) > limits.MaxTotalBytes)
        {
            exceeded = true;
            reason = "MaxTotalBytes";
        }
        else if (limits.MaxObjectCount > 0 && (count + 1) > limits.MaxObjectCount)
        {
            exceeded = true;
            reason = "MaxObjectCount";
        }
        return new QuotaEvaluation(
            exceeded,
            reason,
            limits.MaxTotalBytes,
            limits.MaxObjectSizeBytes,
            limits.MaxObjectCount,
            total,
            count,
            incomingBytes
        );
    }
} // End of Class FilesystemQuotaService

public sealed record QuotaEvaluation(
    bool Exceeded,
    string? Reason,
    long MaxTotalBytes,
    long MaxObjectSizeBytes,
    long MaxObjectCount,
    long CurrentTotalBytes,
    long CurrentObjectCount,
    long IncomingBytes
);
