#!/usr/bin/env bash
set -euo pipefail

echo "Running ClickHouse prepatches..."
found=0
for f in /prepatches/*.sql; do
  if [[ -f "$f" ]]; then
    found=1
    echo "Applying prepatch: $f"
    echo "----- BEGIN $f (first 40 lines) -----"
    sed -n '1,40p' "$f"
    echo "----- END HEAD $f -----"
    # Apply prepatch. Do not abort the whole run on a non-zero exit code from clickhouse-client;
    # some idempotent operations (e.g., REMOVE TTL when no TTL exists) return errors that are safe to ignore.
    if ! clickhouse-client --host clickhouse --port 9000 --user admin --password admin -n < "$f"; \
    then
      echo "WARNING: Non-fatal error applying $f. Continuing to next prepatch."
    fi
  fi
done

if [[ $found -eq 0 ]]; then
  echo "No prepatch .sql files found in /prepatches; skipping."
fi

echo "Prepatches completed."
