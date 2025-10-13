# Task 19 Update Summary - SigNoz API Integration Implementation

**Date**: October 12, 2025  
**Task**: Task 19: Observability pages (SigNoz-first)  
**Status**: Updated with comprehensive implementation roadmap

## Overview

Task 19 has been updated to include detailed implementation guidance for embedding SigNoz observability data into the Admin Dashboard's Observability pages using the SigNoz REST API.

## Key Updates

### 1. Status Change

- **Previous**: Not started, minimal implementation details
- **Current**: Ready for implementation with comprehensive 6-phase roadmap

### 2. Foundation Documentation

Added reference to `docs/SigNoz-API-Integration-Guide.md` which provides:
- Complete SigNoz REST API endpoints reference
- Authentication patterns with API keys
- Sample query patterns for traces, metrics, logs
- Security considerations and best practices
- Production deployment options

### 3. Enhanced Acceptance Criteria

Added:
- Service topology visualization requirement
- Admin-only access enforcement
- Performance requirements (HybridCache with TTL)
- Explicit development SigNoz URL reference (`http://127.0.0.1:3301`)

### 4. Comprehensive Implementation Phases

#### Phase 1: Backend Infrastructure
- `ISigNozQueryService` interface and implementation
- HTTP client with retry, timeout, structured logging
- Query allowlist for security
- DTO models for API responses
- Admin API endpoints: `/api/admin/observability/*`

#### Phase 2: Dashboard UI Components
- Main Observability page at `/dashboard/admin/observability`
- Status cards (error rate, p95 latency, OTLP health)
- Service topology visualization
- Saved search shortcuts for common queries
- Correlated logs peek by trace/span ID
- Link-out buttons to SigNoz

#### Phase 3: Visualization & Interactivity
- Chart.js/Plotly integration via JS interop
- Sparklines on status cards
- Error/empty states handling
- Loading indicators
- Responsive design

#### Phase 4: Security & Performance
- Admin role authorization
- Rate limiting on API endpoints
- Audit logging with user context
- Query parameter validation/sanitization
- HybridCache strategy (1-5 min TTL)
- Structured performance logging

#### Phase 5: Testing & Documentation
- Unit tests (query service, DTOs, allowlist)
- Integration tests (mocked SigNoz responses)
- Playwright E2E tests
- Guide-For-Admins-and-Tenants.md updates
- Architecture.md updates if needed

#### Phase 6: Production Readiness
- Environment-specific configuration
- API key rotation documentation
- Health check integration
- Monitoring metrics
- Graceful degradation

### 5. Implementation Notes

Added critical guidance:
- **API-first approach**: All data through backend proxy, no direct browser calls
- **Read-only integration**: Query only, no dashboard/alert creation
- **Curated experience**: Focused subset of capabilities
- **Future extensions**: Links to Tasks 18 (tenant-scoped) and 20 (job correlation)

### 6. Security Considerations

Detailed security checklist from the integration guide:
1. Network isolation (internal Docker network)
2. API key authentication and rotation
3. Admin role authorization
4. Rate limiting
5. Query validation and sanitization
6. Audit logging

### 7. Sample Queries Reference

Cross-reference to integration guide for:
- Service latency (p95)
- Recent errors by service
- Log search by service/time
- Service dependency graph

### 8. Next Steps (Getting Started)

Clear onboarding for developers:
1. Spike: POC for `ISigNozQueryService`
2. Design UI mockup
3. Define API contract (DTOs, endpoints)
4. Implement MVP (Phase 1 + Phase 2)
5. Iterate with operator feedback

## Context: Related Changes

This update follows the SigNoz gateway proxy removal (2025-10-12):
- Gateway `/signoz/*` routes removed
- Direct access in dev: `http://127.0.0.1:3301`
- Production strategy: API integration, not reverse proxy
- `docs/SigNoz-Gateway-Proxy-Removal.md` documents the architectural decision

## Dependencies Status

All dependencies for Task 19 are now complete:
- ✅ Task 8: Telemetry infrastructure (OTLP export)
- ✅ Task 36: SigNoz integration (UI accessible, OTLP collector)
- ✅ Task 39: Observability instrumentation hardening

## Impact on Other Tasks

### Task 17 (Dashboard admin surfaces)
- Task 17 handles observability *configuration* (retention, sampling, PII redaction)
- Task 19 handles observability *visualization* (status cards, metrics, logs)
- Clear separation maintained

### Task 18 (Tenant manager surfaces)
- Future: Task 19 may be extended for tenant-scoped observability views
- Foundation being laid now supports multi-tenant filtering

### Task 20 (Background jobs UX)
- Future: Task 19 data (traces/logs) can correlate with job failures
- Integration point identified

## Files Changed

1. **Tasks-M3.md**
   - Task 19 section expanded from ~20 lines to ~140 lines
   - Added 6 implementation phases with 40+ subtasks
   - Added security, performance, and documentation requirements
   - Fixed markdown linting issues (MD036, MD032)

## Validation

- ✅ Markdown linting passes (no errors)
- ✅ All cross-references valid
- ✅ Integration guide exists and is comprehensive
- ✅ Dependencies verified as complete

## Next Actions

When starting Task 19 implementation:

1. Review `docs/SigNoz-API-Integration-Guide.md` thoroughly
2. Set up local SigNoz API access for testing
3. Create feature branch: `feature/task-19-observability-pages`
4. Start with Phase 1 (backend infrastructure)
5. Build incrementally with tests at each phase

---

*This update transforms Task 19 from a high-level outline into an actionable, well-scoped implementation plan ready for development.*
