// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public class TracesChainSmoke
{
    private static readonly HttpClient Http = new HttpClient();

    [Fact]
    public async Task Gateway_To_Database_Span_And_One_Db_Span_Appear()
    {
        // Gate on OTLP collector readiness to avoid flakiness when observability stack isn't up yet.
        var otlpHost = Environment.GetEnvironmentVariable("OTLP_GRPC_HOST") ?? "127.0.0.1";
        var otlpPort = int.TryParse(Environment.GetEnvironmentVariable("OTLP_GRPC_PORT"), out var p)
            ? p
            : 4317;
        if (!await TryWaitForTcpPortAsync(otlpHost, otlpPort, TimeSpan.FromSeconds(10)))
        {
            Console.WriteLine(
                $"[SKIP] OTLP gRPC not reachable at {otlpHost}:{otlpPort}. Ensure 'signoz-otel-collector' is running and port 4317 is published."
            );
            return; // dev-only skip
        }

        var gatewayBase = TestUrls.GatewayBaseUrl;
        var clickhouseHttp =
            Environment.GetEnvironmentVariable("CLICKHOUSE_HTTP") ?? "http://127.0.0.1:8123";

        // Arrange: Use dev bypass for provisioning to force DB work without an auth token.
        var tenant = $"tc-traces-{Guid.NewGuid():N}";
        var postUri = $"{gatewayBase.TrimEnd('/')}/db/api/provisioning/tenants";
        using var req = new HttpRequestMessage(HttpMethod.Post, postUri)
        {
            Content = JsonContent.Create(new { tenantId = tenant, displayName = $"Trace {tenant}" })
        };
        req.Headers.Add("X-Provision-Key", "letmein");

        // Act: Fire the provisioning call (idempotent) to generate HTTP + DB spans.
        using var resp = await Http.SendAsync(req);
        Assert.True((int)resp.StatusCode is >= 200 and < 500, "Expect 2xx/4xx idempotent outcome");

        // Give the pipeline a brief moment to export/write to ClickHouse.
        await Task.Delay(2000);

        // Assert A: Recent HTTP server spans for this route exist.
        var routeQuery = @"SELECT count() FROM signoz_traces.signoz_index_v3
WHERE (
    (mapContains(attributes_string, 'http.route') AND attributes_string['http.route'] = '/api/provisioning/tenants')
    OR like(name, '%/api/provisioning/tenants%')
  )
  AND timestamp > now() - INTERVAL 10 MINUTE";
        var httpCount = await ClickHousePollCountAsync(clickhouseHttp, routeQuery, TimeSpan.FromSeconds(60));
        Assert.True(httpCount > 0, "Expected at least one HTTP span for provisioning route");

        // Assert B: A recent DB span (PostgreSQL) exists, indicating EF/Npgsql activity.
        var dbQuery = @"SELECT count() FROM signoz_traces.signoz_index_v3
WHERE (
    (mapContains(attributes_string, 'db.system') AND attributes_string['db.system'] = 'postgresql')
    OR (mapContains(attributes_string, 'db.name'))
  )
  AND timestamp > now() - INTERVAL 10 MINUTE";
        var dbCount = await ClickHousePollCountAsync(clickhouseHttp, dbQuery, TimeSpan.FromSeconds(60));
        Assert.True(dbCount > 0, "Expected at least one DB span (postgresql) within 10m window");
    } // End of Method Gateway_To_Database_Span_And_One_Db_Span_Appear

    private static async Task<long> ClickHousePollCountAsync(string baseUrl, string sql, TimeSpan timeout)
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
                lastError = ex; // retain and keep polling
            }

            await Task.Delay(1000);
        }

        if (lastError is not null)
        {
            throw new Xunit.Sdk.XunitException($"ClickHousePollCountAsync timed out after {timeout.TotalSeconds}s. Last error: {lastError.Message}");
        }

        return last;
    } // End of Method ClickHousePollCountAsync

    private static async Task<long> ClickHouseScalarOnceAsync(string baseUrl, string sql)
    {
        var user = Environment.GetEnvironmentVariable("CLICKHOUSE_USER") ?? "admin";
        var pwd = Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD") ?? "admin";
        var postUri = $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pwd)}";

        using var postReq = new HttpRequestMessage(HttpMethod.Post, postUri)
        {
            Content = new StringContent(sql, Encoding.UTF8, "text/plain")
        };
        using var postResp = await Http.SendAsync(postReq);
        if (!postResp.IsSuccessStatusCode)
        {
            var getUri = $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pwd)}&query={Uri.EscapeDataString(sql)}";
            using var getReq = new HttpRequestMessage(HttpMethod.Get, getUri);
            using var getResp = await Http.SendAsync(getReq);
            if (!getResp.IsSuccessStatusCode)
            {
                var body = await getResp.Content.ReadAsStringAsync();
                throw new HttpRequestException($"ClickHouse query failed. POST={(int)postResp.StatusCode} {postResp.ReasonPhrase}, GET={(int)getResp.StatusCode} {getResp.ReasonPhrase}. Body: {body}");
            }

            using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStreamAsync());
            return ExtractCount(getDoc);
        }

        using var doc = JsonDocument.Parse(await postResp.Content.ReadAsStreamAsync());
        return ExtractCount(doc);
    } // End of Method ClickHouseScalarOnceAsync

    private static long ExtractCount(JsonDocument doc)
    {
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return 0;
        var first = data[0];
        foreach (var prop in first.EnumerateObject())
        {
            if (prop.Name.Contains("count", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt64(out var n))
            {
                return n;
            }
        }
        return 0;
    } // End of Method ExtractCount

    private static async Task<bool> TryWaitForTcpPortAsync(string host, int port, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
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
} // End of Class TracesChainSmoke
