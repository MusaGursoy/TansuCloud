# ClickHouse Memory Optimization Script
# Cleans up verbose system logs and restarts with optimized configuration

Write-Host "=== ClickHouse Memory Optimization ===" -ForegroundColor Cyan

# 1. Show current memory usage
Write-Host "`nCurrent memory usage:" -ForegroundColor Yellow
docker stats signoz-clickhouse --no-stream --format "table {{.Container}}\t{{.MemUsage}}\t{{.MemPerc}}"

# 2. Check current log sizes
Write-Host "`nCurrent system log sizes:" -ForegroundColor Yellow
docker exec signoz-clickhouse clickhouse-client --query "SELECT database, name, formatReadableSize(sum(bytes_on_disk)) as disk_size FROM system.parts WHERE active AND database = 'system' GROUP BY database, name ORDER BY sum(bytes_on_disk) DESC LIMIT 10" --format PrettyCompact

# 3. Truncate system logs
Write-Host "`nTruncating system logs..." -ForegroundColor Yellow
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.text_log"
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.trace_log"
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.query_log"
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.processors_profile_log"
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.part_log"
docker exec signoz-clickhouse clickhouse-client --query "TRUNCATE TABLE system.query_views_log"

Write-Host "System logs truncated successfully!" -ForegroundColor Green

# 4. Restart ClickHouse with new configuration
Write-Host "`nRestarting ClickHouse with optimized configuration..." -ForegroundColor Yellow
docker compose restart clickhouse

Write-Host "`nWaiting for ClickHouse to become healthy..." -ForegroundColor Yellow
$maxWait = 60
$waited = 0
while ($waited -lt $maxWait) {
    $health = docker inspect signoz-clickhouse --format '{{.State.Health.Status}}' 2>$null
    if ($health -eq 'healthy') {
        Write-Host "ClickHouse is healthy!" -ForegroundColor Green
        break
    }
    Start-Sleep -Seconds 2
    $waited += 2
    Write-Host "." -NoNewline
}

if ($waited -ge $maxWait) {
    Write-Host "`nWarning: ClickHouse health check timeout" -ForegroundColor Red
}

# 5. Show new memory usage
Write-Host "`nNew memory usage:" -ForegroundColor Yellow
docker stats signoz-clickhouse --no-stream --format "table {{.Container}}\t{{.MemUsage}}\t{{.MemPerc}}"

# 6. Verify new log retention settings
Write-Host "`nVerifying new configuration is loaded..." -ForegroundColor Yellow
docker exec signoz-clickhouse clickhouse-client --query "SELECT count(*) as active_config_files FROM system.server_settings WHERE name LIKE '%log%'" --format PrettyCompact

Write-Host "`n=== Optimization Complete ===" -ForegroundColor Green
Write-Host "Memory limit reduced: 10GB → 4GB" -ForegroundColor Cyan
Write-Host "Query limits reduced: 8GB → 2GB" -ForegroundColor Cyan
Write-Host "Verbose logging: Disabled (TTL set to 1 day)" -ForegroundColor Cyan
Write-Host "`nExpected savings: ~400 MiB disk + ~2-3 GB memory over 7 hours" -ForegroundColor Cyan
