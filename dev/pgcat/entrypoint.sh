#!/bin/sh
set -eu
CONFIG=/etc/pgcat/pgcat.toml

cat > "$CONFIG" <<EOF
[general]
host = "0.0.0.0"
port = 6432
enable_prometheus_exporter = true
prometheus_exporter_port = 9930
connect_timeout = 3000
idle_timeout = 30000
server_lifetime = 86400000
ban_time = 60
autoreload = 15000
worker_threads = 4
admin_username = "${PGCAT_ADMIN_USER:-admin_user}"
admin_password = "${PGCAT_ADMIN_PASSWORD:-admin_pass}"

[pools.tansu_identity]
pool_mode = "transaction"
query_parser_enabled = true
primary_reads_enabled = true
load_balancing_mode = "random"

[pools.tansu_identity.users.0]
username = "${POSTGRES_USER}"
password = "${POSTGRES_PASSWORD}"
pool_size = 20

[pools.tansu_identity.shards.0]
servers = [["${POSTGRES_HOST:-postgres}", 5432, "primary"]]
database = "tansu_identity"

[pools.postgres]
pool_mode = "transaction"
query_parser_enabled = false
primary_reads_enabled = true
load_balancing_mode = "random"

[pools.postgres.users.0]
username = "${POSTGRES_USER}"
password = "${POSTGRES_PASSWORD}"
pool_size = 10

[pools.postgres.shards.0]
servers = [["${POSTGRES_HOST:-postgres}", 5432, "primary"]]
database = "postgres"
EOF

exec pgcat "$CONFIG"
