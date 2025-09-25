// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

// Simple dev-time pgcat configurator. It discovers tenant databases in Postgres
// that match prefix and emits a pgcat.toml with a pool per tenant.

var adminConn =
    Environment.GetEnvironmentVariable("PG_ADMIN")
    ?? "Host=tansudbpg;Port=5432;Database=postgres;Username=postgres;Password=postgres";
var pgcatConfigPath = Environment.GetEnvironmentVariable("PGCAT_CONFIG") ?? "/etc/pgcat/pgcat.toml";
var dbPrefix = Environment.GetEnvironmentVariable("TENANT_DB_PREFIX") ?? "tansu_tenant_";
var pgUser = Environment.GetEnvironmentVariable("PG_USER") ?? "postgres";
var pgPass = Environment.GetEnvironmentVariable("PG_PASSWORD") ?? "postgres";
var pgHost = Environment.GetEnvironmentVariable("PG_HOST") ?? "tansudbpg";
var adminUser = Environment.GetEnvironmentVariable("PGCAT_ADMIN_USER") ?? "admin_user";
var adminPass = Environment.GetEnvironmentVariable("PGCAT_ADMIN_PASSWORD") ?? "admin_pass";

static async Task<List<string>> GetTenantDbsAsync(
    string connStr,
    string prefix,
    CancellationToken ct
)
{
    var list = new List<string>();
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync(ct);
    const string sql =
        "SELECT datname FROM pg_database WHERE datistemplate = false AND datname LIKE @p || '%' ORDER BY datname";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@p", prefix);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(reader.GetString(0));
    }
    return list;
}

static string GenerateToml(
    string host,
    string user,
    string pass,
    string adminUser,
    string adminPass,
    IEnumerable<string> tenantDbs
)
{
    var sb = new StringBuilder();
    sb.AppendLine("[general]");
    sb.AppendLine("host = \"0.0.0.0\"");
    sb.AppendLine("port = 6432");
    sb.AppendLine("connect_timeout = 3000");
    sb.AppendLine("idle_timeout = 30000");
    sb.AppendLine("server_lifetime = 86400000");
    sb.AppendLine("ban_time = 60");
    sb.AppendLine("autoreload = 15000");
    sb.AppendLine("worker_threads = 4");
    sb.AppendLine($"admin_username = \"{adminUser}\"");
    sb.AppendLine($"admin_password = \"{adminPass}\"");
    sb.AppendLine();

    // Core pools
    void Pool(string name, string database, int poolSize)
    {
        sb.AppendLine($"[pools.{name}]");
        sb.AppendLine("pool_mode = \"transaction\"");
        sb.AppendLine("query_parser_enabled = true");
        sb.AppendLine("primary_reads_enabled = true");
        sb.AppendLine("load_balancing_mode = \"random\"");
        sb.AppendLine();
        sb.AppendLine($"[pools.{name}.users.0]");
        sb.AppendLine($"username = \"{user}\"");
        sb.AppendLine($"password = \"{pass}\"");
        sb.AppendLine($"pool_size = {poolSize}");
        sb.AppendLine();
        sb.AppendLine($"[pools.{name}.shards.0]");
        sb.AppendLine($"servers = [[\"{host}\", 5432, \"primary\"]]");
        sb.AppendLine($"database = \"{database}\"");
        sb.AppendLine();
    }

    Pool("postgres", "postgres", 10);
    Pool("tansu_identity", "tansu_identity", 20);

    foreach (var db in tenantDbs)
    {
        // sanitize name for pool key (pgcat uses same as db ok)
        var poolName = db;
        Pool(poolName, db, 20);
    }

    return sb.ToString();
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    try
    {
        var tenants = await GetTenantDbsAsync(adminConn, dbPrefix, cts.Token);
        var toml = GenerateToml(pgHost, pgUser, pgPass, adminUser, adminPass, tenants);
        Directory.CreateDirectory(Path.GetDirectoryName(pgcatConfigPath)!);
        await File.WriteAllTextAsync(pgcatConfigPath, toml, Encoding.UTF8, cts.Token);
        Console.WriteLine(
            $"[PgcatConfigurator] Wrote config with {tenants.Count} tenant pools to {pgcatConfigPath}"
        );
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[PgcatConfigurator] Error: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
}
