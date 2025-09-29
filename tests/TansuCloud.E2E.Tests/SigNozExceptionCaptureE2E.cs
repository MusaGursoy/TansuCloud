// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class SigNozExceptionCaptureE2E
{
    private static readonly HttpClient Http = new HttpClient();

    [Fact]
    public async Task Storage_Throw_Emits_ErrorSpan_And_ErrorLog()
    {
        // Arrange: enable the dev throw route via env in compose before running this test.
        // Resolve gateway via shared helper to honor PUBLIC_BASE_URL overrides.
        var gatewayBase = TestUrls.GatewayBaseUrl;
        var clickhouseHttp =
            Environment.GetEnvironmentVariable("CLICKHOUSE_HTTP") ?? "http://127.0.0.1:8123";

        // Quick OTLP collector reachability check to surface clearer errors when the pipeline isn't up yet.
        var otlpHost = Environment.GetEnvironmentVariable("OTLP_GRPC_HOST") ?? "127.0.0.1";
        var otlpPort = int.TryParse(Environment.GetEnvironmentVariable("OTLP_GRPC_PORT"), out var p)
            ? p
            : 4317;
        if (!await TryWaitForTcpPortAsync(otlpHost, otlpPort, TimeSpan.FromSeconds(10)))
        {
            Console.WriteLine(
                $"[SKIP] OTLP gRPC not reachable at {otlpHost}:{otlpPort}. Ensure 'signoz-otel-collector' is running and port 4317 is published."
            );
            return; // effectively skip without failing when infra isn't up
        }

        var msg = $"e2e-{Guid.NewGuid():N}";

        // Act: trigger the exception on storage through gateway
        var throwUrl =
            $"{gatewayBase.TrimEnd('/')}/storage/dev/throw?message={Uri.EscapeDataString(msg)}";
        // The endpoint will throw; we expect 500
        using var resp = await Http.GetAsync(throwUrl);
        Assert.False(resp.IsSuccessStatusCode);

        // Give the pipeline a moment to flush to ClickHouse (collector -> ClickHouse write path)
        await Task.Delay(2000);

        // Assert 1 (logs-first): find an error log in signoz_logs.logs_v2 where body or attributes contain our message
        // Note: do not filter by service.name to tolerate missing/variant resource attributes; timestamp is UInt64 (ns)
        var logQuery =
            $@"SELECT count() FROM signoz_logs.logs_v2 WHERE (like(body, '%{msg}%') OR arrayExists(v -> like(v, '%{msg}%'), mapValues(attributes_string))) AND timestamp > toUnixTimestamp64Nano(now64(9) - INTERVAL 10 MINUTE)";
        var logCount = await ClickHousePollCountAsync(
            clickhouseHttp,
            logQuery,
            TimeSpan.FromSeconds(60)
        );
        Assert.True(
            logCount > 0,
            "Expected at least one error log with our exception message within 60s"
        );

        // Assert 2: find a span in signoz_traces.signoz_index_v3 matching the /dev/throw route (no strict service filter to tolerate resource issues)
        var spanQuery =
            $@"SELECT
    count()
FROM signoz_traces.signoz_index_v3
WHERE (
        like(name, '%/dev/throw%')
     OR (mapContains(attributes_string, 'http.route') AND attributes_string['http.route'] = '/dev/throw')
     OR (mapContains(attributes_string, 'http.target') AND like(attributes_string['http.target'], '%/dev/throw%'))
  )
  AND timestamp > now() - INTERVAL 10 MINUTE";
        var spanCount = await ClickHousePollCountAsync(
            clickhouseHttp,
            spanQuery,
            TimeSpan.FromSeconds(60)
        );
        Assert.True(spanCount > 0, "Expected at least one storage span for /dev/throw within 60s");
    }

    private static async Task<long> ClickHousePollCountAsync(
        string baseUrl,
        string sql,
        TimeSpan timeout
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = 0;
        Exception? lastError = null;
        while (sw.Elapsed < timeout)
        {
            try
            {
                last = await ClickHouseScalarOnceAsync(baseUrl, sql);
                if (last > 0)
                {
                    return last;
                }
            }
            catch (Exception ex)
            {
                // Capture the last error but keep retrying until timeout
                lastError = ex;
            }

            await Task.Delay(1000);
        }

        if (lastError is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"ClickHousePollCountAsync timed out after {timeout.TotalSeconds}s. Last error: {lastError.Message}"
            );
        }

        return last;
    }

    private static async Task<long> ClickHouseScalarOnceAsync(string baseUrl, string sql)
    {
        var user = Environment.GetEnvironmentVariable("CLICKHOUSE_USER") ?? "admin";
        var pwd = Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD") ?? "admin";
        var postUri =
            $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pwd)}";

        // First try POST
        using var postReq = new HttpRequestMessage(HttpMethod.Post, postUri)
        {
            Content = new StringContent(sql, Encoding.UTF8, "text/plain")
        };
        using var postResp = await Http.SendAsync(postReq);
        if (!postResp.IsSuccessStatusCode)
        {
            // Fallback to GET with query param if POST failed (some proxies may 404 POSTs)
            var getUri =
                $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pwd)}&query={Uri.EscapeDataString(sql)}";
            using var getReq = new HttpRequestMessage(HttpMethod.Get, getUri);
            using var getResp = await Http.SendAsync(getReq);
            if (!getResp.IsSuccessStatusCode)
            {
                var body = await getResp.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"ClickHouse query failed. POST status={(int)postResp.StatusCode} {postResp.ReasonPhrase}, GET status={(int)getResp.StatusCode} {getResp.ReasonPhrase}. Body: {body}"
                );
            }

            using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStreamAsync());
            return ExtractCount(getDoc);
        }

        using var doc = JsonDocument.Parse(await postResp.Content.ReadAsStreamAsync());
        return ExtractCount(doc);
    }

    private static long ExtractCount(JsonDocument doc)
    {
        // ClickHouse JSON format: { data: [{"count()": N}], ... }
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return 0;
        var first = data[0];
        foreach (var prop in first.EnumerateObject())
        {
            var name = prop.Name;
            if (name.Contains("count", StringComparison.OrdinalIgnoreCase))
            {
                if (
                    prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt64(out var n)
                )
                    return n;
            }
        }
        return 0;
    }

    private static async Task<bool> TryWaitForTcpPortAsync(string host, int port, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(2)
                );
                await client.ConnectAsync(host, port, cts.Token);
                if (client.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // swallow and retry until timeout
            }

            await Task.Delay(500);
        }

        return false;
    } // End of Method TryWaitForTcpPortAsync
}
