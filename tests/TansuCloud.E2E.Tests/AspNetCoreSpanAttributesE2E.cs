// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TansuCloud.E2E.Tests;

public sealed class AspNetCoreSpanAttributesE2E
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Gateway_HealthReady_Span_Emits_Core_Tags()
    {
        var otlpHost = Environment.GetEnvironmentVariable("OTLP_GRPC_HOST") ?? "127.0.0.1";
        var otlpPort = int.TryParse(
            Environment.GetEnvironmentVariable("OTLP_GRPC_PORT"),
            out var parsedPort
        )
            ? parsedPort
            : 4317;
        if (!await TryWaitForTcpPortAsync(otlpHost, otlpPort, TimeSpan.FromSeconds(10)))
        {
            Console.WriteLine(
                $"[SKIP] OTLP gRPC not reachable at {otlpHost}:{otlpPort}. Ensure 'signoz-otel-collector' is running and port 4317 is published."
            );
            return;
        }

        var gatewayBase = TestUrls.GatewayBaseUrl;
        var clickhouseHttp =
            Environment.GetEnvironmentVariable("CLICKHOUSE_HTTP") ?? "http://127.0.0.1:8123";

        var tenantId = $"aspnet-span-{Guid.NewGuid():N}";
        var tenantLower = tenantId.ToLowerInvariant();
        var healthUri = $"{gatewayBase.TrimEnd('/')}/health/ready";
        using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
        request.Headers.Add("X-Tansu-Tenant", tenantId);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await Task.Delay(2000);

        var query =
            @$"SELECT count() FROM signoz_traces.signoz_index_v3
WHERE mapContains(attributes_string, 'http.route')
    AND attributes_string['http.route'] = '/health/ready'
    AND mapContains(attributes_string, 'http.status_code')
    AND toInt32OrNull(attributes_string['http.status_code']) = 200
    AND mapContains(attributes_string, 'tansu.tenant')
    AND attributes_string['tansu.tenant'] = '{tenantLower}'
    AND mapContains(attributes_string, 'tansu.route_base')
    AND attributes_string['tansu.route_base'] = 'health'
    AND mapContains(resources_string, 'service.name')
    AND resources_string['service.name'] = 'tansu.gateway'
    AND timestamp > now() - INTERVAL 10 MINUTE";

        var count = await ClickHousePollCountAsync(clickhouseHttp, query, TimeSpan.FromSeconds(60));
        Assert.True(
            count > 0,
            "Expected at least one gateway span with http.route, http.status_code, tansu.route_base, and tansu.tenant attributes recorded in ClickHouse within 60 seconds."
        );
    } // End of Method Gateway_HealthReady_Span_Emits_Core_Tags

    private static async Task<long> ClickHousePollCountAsync(
        string baseUrl,
        string sql,
        TimeSpan timeout
    )
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastCount = 0;
        Exception? lastError = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                lastCount = await ClickHouseScalarOnceAsync(baseUrl, sql);
                if (lastCount > 0)
                {
                    return lastCount;
                }
            }
            catch (Exception ex)
            {
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

        return lastCount;
    } // End of Method ClickHousePollCountAsync

    private static async Task<long> ClickHouseScalarOnceAsync(string baseUrl, string sql)
    {
        var user = Environment.GetEnvironmentVariable("CLICKHOUSE_USER") ?? "admin";
        var password = Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD") ?? "admin";
        var postUri =
            $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(password)}";

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, postUri)
        {
            Content = new StringContent(sql, Encoding.UTF8, "text/plain")
        };
        using var postResponse = await Http.SendAsync(postRequest);
        if (!postResponse.IsSuccessStatusCode)
        {
            var getUri =
                $"{baseUrl.TrimEnd('/')}/?database=default&default_format=JSON&user={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(password)}&query={Uri.EscapeDataString(sql)}";
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUri);
            using var getResponse = await Http.SendAsync(getRequest);
            if (!getResponse.IsSuccessStatusCode)
            {
                var body = await getResponse.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"ClickHouse query failed. POST status={(int)postResponse.StatusCode} {postResponse.ReasonPhrase}, GET status={(int)getResponse.StatusCode} {getResponse.ReasonPhrase}. Body: {body}"
                );
            }

            using var getDocument = JsonDocument.Parse(
                await getResponse.Content.ReadAsStreamAsync()
            );
            return ExtractCount(getDocument);
        }

        using var document = JsonDocument.Parse(await postResponse.Content.ReadAsStreamAsync());
        return ExtractCount(document);
    } // End of Method ClickHouseScalarOnceAsync

    private static long ExtractCount(JsonDocument doc)
    {
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        if (data.GetArrayLength() == 0)
        {
            return 0;
        }

        var first = data[0];
        foreach (var prop in first.EnumerateObject())
        {
            if (
                prop.Name.Contains("count", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt64(out var number)
            )
            {
                return number;
            }
        }

        return 0;
    } // End of Method ExtractCount

    private static async Task<bool> TryWaitForTcpPortAsync(string host, int port, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, port, cts.Token);
                if (client.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // continue polling until timeout elapses
            }

            await Task.Delay(500);
        }

        return false;
    } // End of Method TryWaitForTcpPortAsync
} // End of Class AspNetCoreSpanAttributesE2E
