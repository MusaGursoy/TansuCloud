# Init Script Deployment Analysis: How End Users Get the Database Schema

## Your Question

**"How will the init script be reachable by end users who have their own servers? Is this script part of the Docker image pulled by end users?"**

## Short Answer

**NO - Currently, the init script is NOT part of the Docker image.** This is a **production deployment gap** that needs to be fixed.

---

## Current State (Problem)

### How It Works Now

**In docker-compose.prod.yml:**

```yaml
postgres:
  image: tansu/citus-pgvector:local
  volumes:
    - tansu-pgdata:/var/lib/postgresql/data
    - ./dev/db-init:/docker-entrypoint-initdb.d:ro  # ⚠️ BIND MOUNT from host
```

**The Problem:**

- Init script is a **bind mount** from the host filesystem (`./dev/db-init`)
- NOT baked into the `tansu/citus-pgvector:local` image
- Requires the **source repository** to be present on the deployment server

### What This Means for End Users

**Scenario 1: End user pulls images from registry**

```bash
# User downloads your production images
docker pull yourorg/tansu-gateway:v1.0
docker pull yourorg/tansu-identity:v1.0
docker pull yourorg/citus-pgvector:v1.0  # ❌ Does NOT include init script

# User runs docker-compose.prod.yml
docker compose -f docker-compose.prod.yml up -d

# Result: ❌ FAILS
# - ./dev/db-init doesn't exist on their system
# - PostgreSQL starts but doesn't create tansu_identity or tansu_audit
# - All services fail to start (missing databases)
```

**Scenario 2: End user clones the repository**

```bash
# User clones the entire repo
git clone https://github.com/MusaGursoy/TansuCloud.git
cd TansuCloud

# User runs docker-compose.prod.yml
docker compose -f docker-compose.prod.yml up -d

# Result: ✅ WORKS
# - ./dev/db-init exists (from repo)
# - PostgreSQL runs init scripts
# - Databases created successfully
```

**Current limitation:** End users MUST have the source repository, which is:

- ❌ Not realistic for production deployments
- ❌ Exposes source code unnecessarily
- ❌ Requires Git and repo access
- ❌ Not a standard deployment pattern

---

## Production-Ready Solutions

### Solution 1: Bake Init Script into Docker Image ✅ RECOMMENDED

**Build a production-ready Postgres image with init script included.**

#### Step 1: Update Dockerfile.citus-pgvector

**File:** `dev/Dockerfile.citus-pgvector`

**Add these lines:**

```dockerfile
FROM citusdata/citus:latest

USER root
RUN set -eux; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        postgresql-17-pgvector \
    && rm -rf /var/lib/apt/lists/*

# Copy init scripts into the image
COPY dev/db-init/*.sql /docker-entrypoint-initdb.d/

# Hand control back to default user
USER postgres
```

**Result:** Init scripts are now INSIDE the image.

#### Step 2: Build and Push Image

```bash
# Build with init scripts
docker build -f dev/Dockerfile.citus-pgvector -t yourorg/citus-pgvector:v1.0 .

# Push to registry
docker push yourorg/citus-pgvector:v1.0
```

#### Step 3: Update docker-compose.prod.yml

```yaml
postgres:
  image: yourorg/citus-pgvector:v1.0  # Use versioned image with init scripts
  volumes:
    - tansu-pgdata:/var/lib/postgresql/data
    # Remove this line: - ./dev/db-init:/docker-entrypoint-initdb.d:ro
```

**Benefits:**

- ✅ No source repository required
- ✅ Standard Docker deployment
- ✅ Versioned and immutable
- ✅ Works anywhere Docker runs

---

### Solution 2: Provide Init Script as Separate Artifact

**For end users who want to use standard citusdata/citus image.**

#### Step 1: Publish Init Script

Provide the init script as a downloadable file:

```bash
# In your releases/documentation
wget https://github.com/MusaGursoy/TansuCloud/releases/download/v1.0/01-init.sql
```

#### Step 2: Document Manual Setup

**In deployment guide:**

```bash
# 1. Download init script
mkdir -p ./db-init
wget https://raw.githubusercontent.com/MusaGursoy/TansuCloud/main/dev/db-init/01-init.sql \
  -O ./db-init/01-init.sql

# 2. Update docker-compose.prod.yml to use local script
# postgres:
#   volumes:
#     - ./db-init:/docker-entrypoint-initdb.d:ro

# 3. Start services
docker compose -f docker-compose.prod.yml up -d
```

**Benefits:**

- ✅ Users can inspect the SQL before running
- ✅ Uses official citusdata/citus image
- ✅ Flexible for customization

**Drawbacks:**

- ❌ Manual setup step required
- ❌ Users might forget or misconfigure
- ❌ Not as clean as Solution 1

---

### Solution 3: Init Container Pattern

**Create a dedicated init container that runs SQL independently.**

#### Step 1: Create Init Container

**File:** `dev/Dockerfile.db-init`

```dockerfile
FROM postgres:17
COPY dev/db-init/*.sql /scripts/
COPY dev/tools/run-init.sh /run-init.sh
RUN chmod +x /run-init.sh
ENTRYPOINT ["/run-init.sh"]
```

**File:** `dev/tools/run-init.sh`

```bash
#!/bin/bash
set -euo pipefail

echo "Waiting for PostgreSQL..."
until psql "$POSTGRES_CONNECTION" -c "SELECT 1" > /dev/null 2>&1; do
  sleep 1
done

echo "Running init scripts..."
for script in /scripts/*.sql; do
  echo "Executing: $script"
  psql "$POSTGRES_CONNECTION" -f "$script"
done

echo "Init complete."
```

#### Step 2: Add to docker-compose.prod.yml

```yaml
db-init:
  build:
    context: .
    dockerfile: dev/Dockerfile.db-init
  container_name: tansu-db-init
  environment:
    POSTGRES_CONNECTION: "host=postgres port=5432 dbname=postgres user=${POSTGRES_USER} password=${POSTGRES_PASSWORD}"
  depends_on:
    postgres:
      condition: service_started
  networks:
    - tansucloud-network

postgres:
  image: citusdata/citus:latest
  # No init script volume needed
```

**Benefits:**

- ✅ Explicit and observable
- ✅ Can run even if data volume is not empty
- ✅ Idempotent (can re-run safely)

**Drawbacks:**

- ❌ More complex than Solution 1
- ❌ Extra container to manage

---

## Recommended Approach

### For TansuCloud Production Deployment: Use Solution 1

**Rationale:**

1. **Cleanest deployment** - End users just pull images and run compose
2. **Standard Docker pattern** - Everything in images, nothing on host
3. **Versioned and immutable** - Init scripts match app version
4. **No manual steps** - Zero-touch deployment

### Implementation Plan

#### 1. Update Dockerfile (Add Init Scripts)

**File:** `dev/Dockerfile.citus-pgvector`

```dockerfile
# Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

FROM citusdata/citus:latest

USER root
RUN set -eux; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        postgresql-17-pgvector \
    && rm -rf /var/lib/apt/lists/*

# Copy init scripts into the image (will run on first start with empty data dir)
COPY dev/db-init/*.sql /docker-entrypoint-initdb.d/

USER postgres
```

#### 2. Build and Publish Image

```bash
# Build
docker build -f dev/Dockerfile.citus-pgvector -t yourorg/citus-pgvector:v1.0 .

# Test locally
docker run --rm -e POSTGRES_PASSWORD=test yourorg/citus-pgvector:v1.0

# Push to registry
docker push yourorg/citus-pgvector:v1.0
```

#### 3. Update docker-compose.prod.yml

```yaml
postgres:
  image: yourorg/citus-pgvector:v1.0  # Versioned image with init scripts
  volumes:
    - tansu-pgdata:/var/lib/postgresql/data
    # REMOVED: - ./dev/db-init:/docker-entrypoint-initdb.d:ro
```

#### 4. Document in Guide

**Add to Guide-For-Admins-and-Tenants.md:**

```markdown
## Database Initialization

The PostgreSQL image (`yourorg/citus-pgvector`) includes initialization scripts that:
- Create `tansu_identity` database
- Create `tansu_audit` database
- Install extensions: `citus`, `vector`, `pg_trgm`

These scripts run automatically on first container start (empty data volume).

**No manual database creation required.**
```

---

## Current vs. Recommended

### Current State (Development)

```yaml
postgres:
  image: tansu/citus-pgvector:local  # Built locally
  volumes:
    - ./dev/db-init:/docker-entrypoint-initdb.d:ro  # Bind mount from repo
```

**Works for:** Development (repo is present)  
**Fails for:** Production (repo not present)

### Recommended State (Production)

```yaml
postgres:
  image: yourorg/citus-pgvector:v1.0  # Published image with init scripts
  volumes:
    - tansu-pgdata:/var/lib/postgresql/data  # Only data volume
```

**Works for:** Both development AND production (self-contained image)

---

## Migration Path

### For Existing Deployments

If databases already exist, init scripts won't re-run (PostgreSQL behavior).

**Option 1: Keep existing data**

```bash
# Just switch to new image
docker compose -f docker-compose.prod.yml pull postgres
docker compose -f docker-compose.prod.yml up -d postgres
# No effect on existing databases (data volume not empty)
```

**Option 2: Fresh start (dev/test only)**

```bash
# Remove data volume and start fresh
docker compose -f docker-compose.prod.yml down -v
docker compose -f docker-compose.prod.yml up -d
# Init scripts run, databases created
```

---

## Answer Summary

### Current Situation

❌ **Init script is NOT in the Docker image**  
❌ **Requires source repository on deployment server**  
❌ **Not production-ready for end users**

### Recommended Fix

✅ **Bake init script into custom PostgreSQL image**  
✅ **Publish versioned image to container registry**  
✅ **Remove bind mount from docker-compose.prod.yml**  
✅ **End users pull self-contained image - zero-touch deployment**

### Next Steps

1. Update `dev/Dockerfile.citus-pgvector` to include init scripts
2. Build and publish versioned image
3. Update `docker-compose.prod.yml` to use versioned image (remove bind mount)
4. Document in deployment guide
5. Test with fresh deployment (no repo, just images)

**This makes TansuCloud truly production-ready for end users.**
