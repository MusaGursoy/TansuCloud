# Phase M2: Database + Storage core

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [9) Tenant provisioning service](#task-9-tenant-provisioning-service)
- [ ] [10) EF Core model + migrations with Citus/pgvector](#task-10-ef-core-model--migrations)
- [ ] [11) Database REST API v1](#task-11-database-rest-api-v1)
- [ ] [12) Outbox + idempotency worker](#task-12-outbox--idempotency-worker)
- [ ] [13) Storage service core (S3-compatible)](#task-13-storage-service-core)
- [ ] [14) Storage compression and transforms](#task-14-storage-compression-and-transforms)
- [ ] [15) HybridCache integration](#task-15-hybridcache-integration)
- [ ] [16) Gateway OutputCache/rate limits tuning](#task-16-gateway-outputcache--rate-limits-tuning)

---

## Tasks

<a id="task-9-tenant-provisioning-service"></a>

### Task 9: Tenant provisioning service

- Outcome: Create tenant -> template DB init (Citus+pgvector) -> migrations -> seed roles/config -> admin invite -> audit event.
- Dependencies: 4-5

<a id="task-10-ef-core-model--migrations"></a>

### Task 10: EF Core model + migrations with Citus/pgvector

- Outcome: Distributed/reference tables; HNSW indexes; deterministic migrations; compiled models.
- Dependencies: 9

<a id="task-11-database-rest-api-v1"></a>

### Task 11: Database REST API v1

- Outcome: CRUD with pagination/filter/sort/ETag; validation; problem+json errors; vector upsert/search requiring collection_id; two-step cross-collection ANN path.
- Dependencies: 10

<a id="task-12-outbox--idempotency-worker"></a>

### Task 12: Outbox + idempotency worker

- Outcome: Outbox table, background dispatcher with retries and idempotency keys; Redis pub/sub for cache busts.
- Dependencies: 11

<a id="task-13-storage-service-core"></a>

### Task 13: Storage service core (S3-compatible)

- Outcome: Buckets, object CRUD, presigned URLs, multipart; quotas; lifecycle scaffolding; content-type validation; optional AV hook.
- Dependencies: 9

<a id="task-14-storage-compression-and-transforms"></a>

### Task 14: Storage compression and transforms (optional)

- Outcome: Brotli for compressible types (respect Accept-Encoding; set Content-Encoding; preserve weak ETags; skip already-compressed); image transforms with signed URLs and per-tenant cache keyed by source ETag.
- Dependencies: 13

<a id="task-15-hybridcache-integration"></a>

### Task 15: HybridCache integration

- Outcome: Redis configured; cache key conventions; cached endpoints; invalidation via outbox.
- Dependencies: 11,13,12

<a id="task-16-gateway-outputcache--rate-limits-tuning"></a>

### Task 16: Gateway OutputCache/rate limits tuning

- Outcome: Per-route policies refined; public routes flagged; test vary keys; conditional requests (ETag/Last-Modified) honored end-to-end.
- Dependencies: 3,11,13

---

### Checklist item template

- [ ] <Task number>) <Task title>
  - Owner:
  - Status: Not Started | In Progress | Blocked | Done
  - Start:  YYYY-MM-DD   Due: YYYY-MM-DD
  - Notes:

<!-- markdownlint-enable MD029 MD033 -->