# Phase M5: Advanced Backend-as-a-Service Features

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [44) BaaS Authentication - Per-Tenant Identity (Complete Isolation)](#task-44-baas-authentication-per-tenant-identity)
- [ ] [45) Serverless Functions Service](#task-45-serverless-functions-service)
- [ ] [46) Database Access Patterns - Direct PostgreSQL + Quota Management](#task-46-database-access-patterns-direct-postgresql-quota-management)
- [ ] [47) Migration from SigNoz to PGTL Stack (Prometheus + Grafana + Tempo + Loki)](#task-47-migration-from-signoz-to-pgtl-stack)

---

## Tasks

### Task 44: BaaS Authentication - Per-Tenant Identity (Complete Isolation) {#task-44-baas-authentication-per-tenant-identity}

**Status**: APPROVED — Ready for Phase 1 implementation  
**Decision Date**: 2025-10-13  
**Owner**: TBD  
**Priority**: HIGH  
**Dependencies**: Tasks 4-6 (Identity baseline), Task 9 (Tenant provisioning)

**Purpose**: Enable TansuCloud as a true Backend-as-a-Service (BaaS) platform where each tenant can manage their own end users (mobile/web/desktop app users) with complete physical isolation. Adopt the **Supabase complete isolation model**: ONE PostgreSQL database per tenant containing both identity tables (ASP.NET Identity users, roles, OpenIddict clients, OAuth configs, JWT keys) AND business data (Documents, Files, Collections). This allows tenant admins to register apps, configure OAuth providers (Google/Microsoft/GitHub), invite users, and provide authentication services to their own customers—all without cross-tenant security risks.

**Current state gaps**:

- ❌ Identity service only manages platform admins in `tansu_identity` database
- ❌ Tenant users (mobile/web app end users) have no place to register/login
- ❌ No per-tenant OAuth provider configuration (Google, GitHub, Microsoft)
- ❌ No client registration API (tenant can't register their mobile/web apps)
- ❌ No tenant admin role or Dashboard UI for user management
- ❌ No tenant-specific JWT signing keys (all tokens signed with platform key)

**Target state (BaaS-ready)**:

- ✅ Each tenant gets ONE database: `tansu_tenant_{id}` with Identity + business tables
- ✅ Platform admins remain in separate `tansu_identity` database (unchanged)
- ✅ Identity service resolves tenant DB per-request via `X-Tansu-Tenant` header
- ✅ Tenant users register/login via tenant-scoped APIs: `/identity/api/{tenantId}/auth/signup`, `/signin`
- ✅ Each tenant has unique RS256 JWT signing keys stored in their DB
- ✅ Tenant admins configure OAuth providers (Google/GitHub/Microsoft) via Dashboard
- ✅ Tenant admins register mobile/web/desktop apps (OpenIddict clients) via Dashboard
- ✅ Complete physical isolation: backup one tenant = `pg_dump tansu_tenant_acme` (users + data together)
- ✅ Citus distribution ready: can move large tenants to dedicated coordinators

**Reference documentation**:

- Full strategy: `docs/BaaS-Strategy-Summary.md`
- Architecture diagrams showing current vs. target state
- Implementation phases (5 phases detailed below)
- Code examples: `TenantResolutionMiddleware`, `TenantIdentityDbContext`, provisioning flow
- Backup/restore scripts, Citus distribution strategy, security considerations

---

#### Phase 1: Core Multi-Tenant Identity (2-3 weeks)

**Goal**: Identity service can resolve tenant DB per-request and manage tenant users.

**Subtasks**:

1. **Create `TenantResolutionMiddleware`** (2 days)
   - Resolve tenant from `X-Tansu-Tenant` header (set by Gateway after authz)
   - Fallback: No header → platform admin flow (use `tansu_identity` DB)
   - Store `TenantId` and `TenantDbName` in `HttpContext.Items`
   - Return 404 if tenant doesn't exist
   - Add unit tests for tenant resolution logic

2. **Implement `TenantIdentityDbContext`** (3 days)
   - Extend `IdentityDbContext<IdentityUser, IdentityRole, string>`
   - Override `OnConfiguring` to resolve connection string from `HttpContext.Items["TenantDbName"]`
   - Default to `tansu_identity` when no tenant context (platform admin flow)
   - Add OpenIddict entities: `builder.UseOpenIddict()`
   - Add `JwkKey` entity (tenant-specific RSA signing keys)
   - Add `ExternalProviderSetting` entity (per-tenant OAuth configs)
   - Add unit tests for connection string resolution

3. **Update `TenantProvisioner`** (5 days)
   - Modify to create ONE database per tenant: `tansu_tenant_{id}`
   - Apply Identity migrations (ASP.NET Identity + OpenIddict + JwkKeys) to new DB
   - Apply business data migrations (Documents, Collections, Files) to same DB
   - Seed tenant admin user: `admin@{tenantId}.local` with `TenantAdmin` role
   - Generate tenant-specific 2048-bit RSA signing key and store in `JwkKeys` table
   - Add rollback logic for failed provisioning (delete DB if any step fails)
   - Update integration tests to verify ONE database with all tables

4. **Add PgCat wildcard pool** (1 day)
   - Update `dev/pgcat/pgcat.toml` with wildcard pool for `tansu_tenant_*` databases
   - Verify PgCat routes connections correctly to tenant databases
   - Document PgCat configuration in `Guide-For-Admins-and-Tenants.md`

5. **Build tenant-scoped signup API** (3 days)
   - Endpoint: `POST /identity/api/{tenantId}/auth/signup`
   - Validate tenant exists, resolve tenant DB, create user in `AspNetUsers` table
   - Return JWT access token + refresh token signed with tenant's RSA key
   - Add rate limiting (10 signups per IP per hour to prevent abuse)
   - Add integration tests for signup flow and cross-tenant isolation

6. **Build tenant-scoped signin API** (3 days)
   - Endpoint: `POST /identity/api/{tenantId}/auth/signin`
   - Validate credentials against tenant DB, issue JWT with tenant-specific key
   - Include `tid` (tenant ID) claim in access token for downstream authorization
   - Support refresh token flow for long-lived sessions
   - Add integration tests for signin, token validation, and wrong-tenant rejection

7. **Update JWKS endpoint** (2 days)
   - Endpoint: `GET /identity/{tenantId}/.well-known/jwks.json`
   - Serve tenant-specific public keys from tenant DB's `JwkKeys` table
   - Cache keys in memory with 5-minute expiration
   - Add E2E tests verifying tenant-specific key discovery

**Acceptance criteria**:

- ✅ Provision tenant creates ONE database with Identity + business tables
- ✅ Identity service resolves correct tenant DB per request (verified via unit tests)
- ✅ Tenant user signup creates user in `tansu_tenant_acme.AspNetUsers` (verified via DB query)
- ✅ Tenant user signin issues JWT with tenant-specific RS256 key (verified via token inspection)
- ✅ Cross-tenant isolation verified: user from tenant A cannot authenticate as tenant B (integration test)
- ✅ Platform admin login still works (no `X-Tansu-Tenant` header → `tansu_identity` DB)
- ✅ All existing tests pass (no regressions in platform admin flows)

**Tests**:

```csharp
// TansuCloud.E2E.Tests/IdentityMultiTenancyTests.cs
[Fact]
public async Task Provision_Tenant_Creates_One_Database_With_Identity_And_Business_Tables()
{
    var result = await Provisioner.ProvisionTenantAsync("test-tenant", "Test Tenant");
    Assert.True(result.Success);
    
    var dbExists = await DatabaseExistsAsync("tansu_tenant_test-tenant");
    Assert.True(dbExists);
    
    var tables = await GetTablesAsync("tansu_tenant_test-tenant");
    // Identity tables
    Assert.Contains("AspNetUsers", tables);
    Assert.Contains("AspNetRoles", tables);
    Assert.Contains("OpenIddictApplications", tables);
    Assert.Contains("JwkKeys", tables);
    Assert.Contains("ExternalProviderSettings", tables);
    // Business tables
    Assert.Contains("Documents", tables);
    Assert.Contains("Collections", tables);
    Assert.Contains("Files", tables);
}

[Fact]
public async Task Tenant_User_Signup_Creates_User_In_Tenant_Database()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    
    var response = await HttpClient.PostAsJsonAsync(
        "/identity/api/acme/auth/signup",
        new { email = "alice@acme.com", password = "SecurePass123!" });
    
    response.EnsureSuccessStatusCode();
    var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(token.AccessToken);
    
    // Verify user exists in tenant DB
    var user = await GetUserFromDatabaseAsync("tansu_tenant_acme", "alice@acme.com");
    Assert.NotNull(user);
    Assert.Equal("alice@acme.com", user.Email);
}

[Fact]
public async Task Tenant_User_Cannot_Authenticate_To_Different_Tenant()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    await Provisioner.ProvisionTenantAsync("widgets", "Widgets Inc");
    
    // Create user in acme tenant
    await SignupTenantUserAsync("acme", "alice@acme.com", "SecurePass123!");
    
    // Try to sign in to widgets tenant with acme credentials
    var response = await HttpClient.PostAsJsonAsync(
        "/identity/api/widgets/auth/signin",
        new { email = "alice@acme.com", password = "SecurePass123!" });
    
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task Platform_Admin_Login_Still_Works_Without_Tenant_Header()
{
    // No X-Tansu-Tenant header → should use tansu_identity DB
    var response = await HttpClient.PostAsJsonAsync(
        "/identity/connect/token",
        new { username = "admin@tansu.local", password = "Passw0rd!", grant_type = "password" });
    
    response.EnsureSuccessStatusCode();
    var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(token.AccessToken);
}
```

**Risks and mitigations**:

| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking existing platform admin flows | HIGH | Comprehensive tests; fallback to `tansu_identity` when no tenant header |
| Performance: per-request DB resolution overhead | MEDIUM | Connection pooling via PgCat; cache tenant metadata in memory |
| Tenant DB migrations fail during provisioning | MEDIUM | Rollback logic; transaction-based provisioning; pre-flight validation |
| JWT key rotation complexity per tenant | MEDIUM | Reuse existing key rotation service; scope to tenant DB |
| Cross-tenant token leakage | HIGH | Strict validation: reject tokens with wrong `tid` claim at API gateways |

**Definition of done (Phase 1)**:

- [ ] `TenantResolutionMiddleware` implemented and tested
- [ ] `TenantIdentityDbContext` with per-request connection resolution
- [ ] `TenantProvisioner` creates ONE database with Identity + business tables
- [ ] PgCat wildcard pool configured and verified
- [ ] Tenant signup/signin APIs working and tested
- [ ] JWKS endpoint serves tenant-specific keys
- [ ] All integration tests passing (10+ tests covering isolation, provisioning, auth flows)
- [ ] E2E tests passing (signup → signin → token validation)
- [ ] Documentation updated: `Architecture.md`, `Guide-For-Admins-and-Tenants.md`
- [ ] No regressions in existing platform admin flows

---

#### Phase 2: External OAuth Integration (1-2 weeks)

**Goal**: Tenants can configure Google/Microsoft/GitHub OAuth for their users.

**Subtasks**:

1. **Add `ExternalProviderSettings` EF migration** (1 day)
   - Already defined in code; just needs migration for tenant DBs
   - Columns: Provider (Google/Microsoft/GitHub), ClientId, ClientSecret (encrypted), TenantId (not needed since per-tenant DB)
   - Add unique constraint on Provider per tenant

2. **Build OAuth authorize endpoint** (2 days)
   - Endpoint: `GET /identity/api/{tenantId}/auth/authorize/{provider}`
   - Load provider config from tenant DB's `ExternalProviderSettings` table
   - Redirect to OAuth provider (Google/Microsoft/GitHub) with correct client_id
   - Store PKCE code verifier and state in session/cache

3. **Build OAuth callback handler** (3 days)
   - Endpoint: `GET /identity/api/{tenantId}/auth/callback/{provider}`
   - Validate state parameter, exchange code for token
   - Fetch user info from OAuth provider (email, name, profile picture)
   - Lookup existing user by email or create new user in tenant DB
   - Link OAuth account to user via `AspNetUserLogins` table
   - Issue TansuCloud JWT access token for the user

4. **Create Dashboard UI for OAuth config** (3 days)
   - Page: `/dashboard/tenant/{id}/auth-providers`
   - CRUD operations: Add/edit/delete OAuth provider configs
   - Form fields: Provider dropdown, Client ID, Client Secret (masked), Redirect URI (auto-generated)
   - Show "Sign in with Google" button preview
   - Add authorization: only tenant admin can manage providers

5. **Add OAuth flow tests** (2 days)
   - Integration tests: Mock OAuth provider responses
   - E2E tests: Use test OAuth credentials (Google OAuth playground)
   - Verify account linking and user creation

**Acceptance criteria**:

- ✅ Tenant admin can add Google OAuth config via Dashboard
- ✅ Tenant user sees "Sign in with Google" button on login page
- ✅ First-time Google login creates new user in tenant DB
- ✅ Returning Google user links to existing account (by email)
- ✅ OAuth credentials stored encrypted in tenant DB
- ✅ Cross-tenant isolation: Acme's Google config doesn't affect Widgets tenant

**Definition of done (Phase 2)**:

- [ ] `ExternalProviderSettings` migration applied to all tenant DBs
- [ ] OAuth authorize and callback endpoints implemented
- [ ] Dashboard OAuth config UI working
- [ ] Integration and E2E tests passing
- [ ] Documentation updated with OAuth setup guide

---

#### Phase 3: Client Registration API (1 week)

**Goal**: Tenant admins can register mobile/web/desktop apps that authenticate tenant users.

**Subtasks**:

1. **Build client registration API** (3 days)
   - Endpoint: `POST /identity/api/{tenantId}/clients`
   - Create OpenIddict application in tenant DB's `OpenIddictApplications` table
   - Support public clients (PKCE for mobile/SPA) and confidential clients (client_secret for server-side)
   - Auto-generate client_id and client_secret (for confidential clients)
   - Return client credentials to tenant admin

2. **Create Dashboard client management UI** (2 days)
   - Page: `/dashboard/tenant/{id}/clients`
   - List registered clients with status (active/disabled)
   - Create new client form: Name, Type (public/confidential), Redirect URIs
   - Show client_secret once after creation (cannot be retrieved again)
   - Delete client confirmation dialog

3. **Add client tests** (1 day)
   - Integration tests: Register client, authenticate with client_id + PKCE
   - E2E tests: Full flow from Dashboard → client creation → mobile app auth

**Acceptance criteria**:

- ✅ Tenant admin can register "acme-mobile-app" via Dashboard
- ✅ Client stored in `tansu_tenant_acme.OpenIddictApplications`
- ✅ Mobile app can authenticate using client_id + PKCE flow
- ✅ Confidential client can use client_secret for server-to-server auth
- ✅ Clients isolated per tenant (Acme's clients not visible to Widgets)

**Definition of done (Phase 3)**:

- [ ] Client registration API implemented
- [ ] Dashboard client management UI working
- [ ] Public and confidential client flows tested
- [ ] Documentation updated with client registration guide

---

#### Phase 4: Tenant Admin Dashboard + Identity Settings UI (2-3 weeks)

**Goal**: Tenant admins can manage users, OAuth providers, and clients via Dashboard. Both platform admins and tenant admins can configure Identity settings appropriate to their scope.

**Subtasks**:

1. **Add `TenantAdmin` role** (1 day)
   - Add role to ASP.NET Identity during tenant provisioning
   - Seed first tenant admin user with this role
   - Add authorization policies for tenant admin endpoints

2. **Build user management UI** (3 days)
   - Page: `/dashboard/tenant/{id}/users`
   - List all users in tenant with status (active/disabled)
   - Invite user form: Email, Role (TenantAdmin/User)
   - Disable/re-enable user actions
   - Show last sign-in timestamp and IP address

3. **Implement user invitation system** (2 days)
   - Generate secure invitation token (JWT with short expiration)
   - Send invitation email with signup link
   - Validate invitation token on signup page
   - Auto-assign role from invitation

4. **Build login activity log** (2 days)
   - Store login events in tenant DB (or `tansu_audit` for cross-tenant queries)
   - Show recent logins: Timestamp, IP address, User agent, Success/failure
   - Filter by user, date range, status

5. **Platform Admin Identity Settings UI** (3 days)
   - Page: `/dashboard/admin/identity-settings`
   - Platform-wide settings (apply to `tansu_identity` and default for new tenants):
     - JWT token lifetimes: Access token (default 1hr), ID token (1hr), Refresh token (14 days)
     - Key rotation schedule: Rotation interval (default 30 days), grace period (7 days)
     - Password policy: Min length, require uppercase/lowercase/digit/special char, password history
     - Session settings: Absolute timeout, idle timeout, require re-authentication for sensitive operations
     - MFA defaults: Enable/disable TOTP, SMS fallback, recovery codes
     - Account lockout: Max failed attempts (default 5), lockout duration (15 minutes)
   - Authorization: Require `admin.full` scope (platform admin only)
   - Persistence: Store in `tansu_identity` DB or configuration table

6. **Tenant Identity Settings UI** (3 days)
   - Page: `/dashboard/tenant/{id}/identity-settings`
   - Tenant-scoped settings (override platform defaults, stored in tenant DB):
     - **OAuth Providers**: Configure Google/Microsoft/GitHub client credentials
       - Form: Provider dropdown, Client ID (text), Client Secret (password, masked after save)
       - Display callback URL for tenant: `https://apps.example.com/identity/api/{tenantId}/auth/callback/google`
       - Test connection button (validates credentials with provider)
       - Show provider status: Active, Inactive, Error (with error message)
     - **Password Policy** (optional override): Use platform default or customize per tenant
     - **Session Settings** (optional override): Customize timeouts for tenant users
     - **Branding** (future): Logo URL, primary color, login page custom text
   - Authorization: Require `TenantAdmin` role or tenant-scoped `admin.full` scope
   - Persistence: Store in tenant DB's `ExternalProviderSettings` table + new `TenantIdentitySettings` table

7. **API endpoints for settings CRUD** (2 days)
   - Platform settings:
     - `GET /identity/admin/settings` (read platform defaults)
     - `PUT /identity/admin/settings` (update platform defaults)
   - Tenant settings:
     - `GET /identity/api/{tenantId}/settings` (read tenant settings with platform defaults as fallback)
     - `PUT /identity/api/{tenantId}/settings` (update tenant overrides)
     - `POST /identity/api/{tenantId}/settings/oauth-providers` (add OAuth provider)
     - `PUT /identity/api/{tenantId}/settings/oauth-providers/{providerId}` (update OAuth provider)
     - `DELETE /identity/api/{tenantId}/settings/oauth-providers/{providerId}` (delete OAuth provider)

**Acceptance criteria**:

- ✅ Tenant admin can invite users via email
- ✅ Invited user receives signup link and creates account
- ✅ Tenant admin can disable/re-enable users
- ✅ Tenant admin can see login activity for their tenant only
- ✅ **Platform admin can configure platform-wide Identity settings** (token lifetimes, password policy, MFA defaults)
- ✅ **Tenant admin can configure OAuth providers** (Google/GitHub/Microsoft) with their own client credentials
- ✅ **Tenant admin can override platform defaults** (password policy, session timeouts) for their tenant
- ✅ **OAuth credentials stored encrypted** in tenant DB (ASP.NET Data Protection API)
- ✅ **Callback URLs auto-generated** and displayed for easy copy-paste into OAuth provider console
- ✅ Authorization enforced: regular users cannot access admin pages; tenants cannot see other tenants' settings

**Definition of done (Phase 4)**:

- [ ] `TenantAdmin` role implemented
- [ ] User management UI working
- [ ] Invitation system functional with email delivery
- [ ] Login activity log displayed correctly
- [ ] **Platform Admin Identity Settings UI complete** (`/dashboard/admin/identity-settings`)
- [ ] **Tenant Identity Settings UI complete** (`/dashboard/tenant/{id}/identity-settings`)
- [ ] **OAuth provider CRUD working** (add/edit/delete Google/GitHub/Microsoft credentials)
- [ ] **Settings API endpoints tested** (platform and tenant-scoped)
- [ ] **Encryption verified** (OAuth secrets not readable in DB without decryption key)
- [ ] Authorization policies tested
- [ ] Documentation updated with Identity Settings guide

---

#### Phase 5: Advanced Features (Ongoing)

**Optional enhancements** (post-MVP, prioritize based on customer demand):

1. **Account linking** (1 week)
   - Allow users to link multiple OAuth providers to one account
   - UI to manage linked accounts (add/remove)

2. **Multi-factor authentication (MFA)** (2 weeks)
   - TOTP support (Google Authenticator, Authy)
   - SMS fallback (Twilio integration)
   - Recovery codes

3. **Passwordless authentication** (1 week)
   - Magic links via email
   - WebAuthn support (biometrics, security keys)

4. **Custom domains** (2 weeks)
   - Tenant-specific issuer URIs: `auth.acme.com` instead of `tansu.cloud/identity/acme`
   - Custom branding on login pages

5. **SSO integration** (3 weeks)
   - SAML support for enterprise customers
   - Azure AD, Okta, Auth0 integration

6. **Audit log** (1 week)
   - Track all identity operations per tenant (signup, signin, password reset, role change)
   - Export audit log to CSV

---

#### Overall Task 44 Acceptance Criteria

- ✅ Each tenant has ONE database (`tansu_tenant_{id}`) with Identity + business tables
- ✅ Platform admins remain in separate `tansu_identity` database (no changes)
- ✅ Tenant users can signup/signin via tenant-scoped APIs
- ✅ Each tenant has unique JWT signing keys (RS256)
- ✅ Tenant admins can configure OAuth providers (Google/GitHub/Microsoft)
- ✅ Tenant admins can register mobile/web/desktop apps (OpenIddict clients)
- ✅ Tenant admins can invite and manage users via Dashboard
- ✅ Complete physical isolation: backup/restore one tenant = one `pg_dump` command
- ✅ Cross-tenant isolation verified: no data leakage between tenants
- ✅ All existing platform admin flows still work (no regressions)
- ✅ Documentation complete: `docs/BaaS-Strategy-Summary.md`, `Architecture.md`, `Guide-For-Admins-and-Tenants.md`
- ✅ All integration and E2E tests passing (50+ new tests)

---

#### Dependencies and Prerequisites

**Required before starting**:

- Tasks 4-6 (Identity baseline) must be complete
- Task 9 (Tenant provisioning) must be complete
- `tansu-postgres` container with Citus + pgvector available
- PgCat connection pooler configured and running

**Blocks**:

- No current blockers; all prerequisites met

**Enables future work**:

- Task 45: Serverless Functions Service (webhook/event handlers)
- Task 46 (hypothetical): Tenant usage analytics and billing
- SDK improvements: Add authentication helpers to .NET SDK (NuGet package)

---

#### Risks and Global Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking existing platform admin flows | HIGH | Extensive tests; fallback logic; feature flag to disable tenant auth |
| Per-request DB resolution overhead | MEDIUM | PgCat connection pooling; in-memory tenant metadata cache |
| Tenant DB provisioning failures | MEDIUM | Transactional provisioning; rollback on failure; pre-flight validation |
| JWT key rotation per tenant complexity | MEDIUM | Reuse existing rotation service; scope to tenant DB |
| Cross-tenant token leakage | HIGH | Strict `tid` claim validation at all API gateways; audit logs |
| OAuth credential leaks | HIGH | Encrypt credentials at rest; mask in UI; rate-limit OAuth flows |
| Invitation token replay attacks | MEDIUM | Single-use tokens; short expiration (24 hours); validate email ownership |
| Dashboard UI performance with many users | MEDIUM | Pagination; search/filter; lazy loading |

---

#### Effort Estimate

- **Phase 1** (Core Multi-Tenant Identity): 2-3 weeks (1 developer)
- **Phase 2** (OAuth Integration): 1-2 weeks (1 developer)
- **Phase 3** (Client Registration): 1 week (1 developer)
- **Phase 4** (Tenant Admin Dashboard + Identity Settings UI): 2-3 weeks (1 developer)
- **Phase 5** (Advanced Features): Ongoing (prioritize based on demand)

**Total**: 6-10 weeks for MVP (Phases 1-4)

---

#### Success Metrics

**Technical**:

- Zero cross-tenant data leakage incidents
- JWT token validation latency < 10ms (p95)
- Tenant provisioning time < 5 seconds (includes DB creation + migrations)
- Uptime: 99.9% for Identity service endpoints

**Product**:

- 10+ tenants actively using per-tenant authentication within 3 months of launch
- 90%+ of tenants configure OAuth providers (Google/GitHub)
- 80%+ of tenants register at least one mobile/web app client
- Positive feedback from early adopters on Dashboard UX

---

#### Documentation and Training

**Docs to create/update**:

- ✅ `docs/BaaS-Strategy-Summary.md` (already created)
- [ ] `Architecture.md`: Add "BaaS Authentication" section with architecture diagrams
- [ ] `Guide-For-Admins-and-Tenants.md`: Add tenant user management guide, OAuth setup, client registration
- [ ] API reference: Document tenant-scoped auth endpoints (`/identity/api/{tenantId}/*`)
- [ ] SDK guide: Update .NET SDK NuGet package with authentication examples

**Training materials**:

- [ ] Video tutorial: "Setting up per-tenant authentication in TansuCloud"
- [ ] Blog post: "Building a BaaS with complete tenant isolation"
- [ ] Sample apps: Mobile app (Swift/Kotlin) using TansuCloud auth, Web app (React) with OAuth

---

#### Next Steps After Task 44 Creation

1. **Review and approve** task scope and phasing with team
2. **Break down Phase 1** into daily subtasks with clear owners
3. **Set up feature branch**: `feature/baas-authentication`
4. **Create GitHub Epic**: "BaaS Authentication" with milestones for each phase
5. **Begin Phase 1 implementation**:
   - Start with `TenantResolutionMiddleware` (low-risk, high-value)
   - Then `TenantIdentityDbContext` (foundation for all other work)
   - Then `TenantProvisioner` updates (enables testing)
6. **Weekly demos** to show progress and gather feedback
7. **Beta testing** with 2-3 pilot tenants after Phase 1 completes

---

**Status**: Task 44 created and awaiting Phase 1 kickoff approval ✅

---

### Task 45: Serverless Functions Service {#task-45-serverless-functions-service}

**Status**: DRAFT — Awaiting approval  
**Decision Date**: 2025-10-13  
**Owner**: TBD  
**Priority**: MEDIUM  
**Dependencies**: Task 13 (Storage core), Task 12 (Outbox), Task 20 (Background jobs), Task 17 (Dashboard admin)

**Purpose**: Enable tenants to run custom backend logic for integrations (payment processing, email notifications, SMS, push notifications, calendar integrations, webhooks, scheduled jobs) without managing infrastructure. Provide a serverless execution environment with built-in connectors for common third-party services, full tenant isolation, and comprehensive observability.

**Performance Optimization Decision (2025-10-20)**:

⚡ **Use EF Core Compiled Model for Functions Service**

Unlike long-running services (Database, Gateway, Dashboard, Storage, Identity) that start once and run for days/weeks, the Functions service will experience frequent cold starts due to its serverless nature. EF Core compiled model optimization is critical here:

- **Standard services**: 500ms EF startup cost paid ONCE → negligible (kept runtime model for flexibility)
- **Functions service**: 500ms EF startup cost paid PER COLD START → significant
- **Performance gain**: ~100ms (20%) per cold start
- **Impact at scale**:
  - 1,000 invocations/day: ~8 min/day → ~7 min/day (saves 1 min/day)+
  - 100,000 invocations/day: ~14 hours/day → ~11 hours/day (saves 3 hours/day)

**Implementation checklist**:

- ✅ Compiled model infrastructure already exists in `TansuDbContextFactory.cs` (disabled but ready)
- ⏳ Generate compiled model before Task 45 implementation: `dotnet ef dbcontext optimize --output-dir EF --namespace TansuCloud.Database.EF`
- ⏳ Enable compiled model ONLY in `TansuCloud.Functions` service by uncommenting `TryUseCompiledModel()`
- ⏳ Add model regeneration to CI/CD pipeline (before Docker build)
- ⏳ Document usage in Functions service README

**Rationale**: For serverless workloads with frequent cold starts, the 20% reduction in EF startup time directly improves user-facing latency and reduces compute costs. For long-running services, the flexibility of runtime model outweighs the one-time 100ms savings at startup.

**Reference**: See `TansuCloud.Database/Services/TenantDbContextFactory.cs` for implementation details and uncommenting instructions.

---

**Current state gaps**:

- ❌ No way for tenants to run custom backend code
- ❌ No built-in integrations for common services (Stripe, SendGrid, Twilio, etc.)
- ❌ Webhooks mentioned in Task 28 but not implemented
- ❌ Background jobs (Task 20) exist but require code changes in core services
- ❌ No event-driven triggers from Database service outbox events
- ❌ No scheduled/cron job capability for tenants

**Target state (Functions-as-a-Service)**:

- ✅ Tenant-scoped function runtime with full isolation (separate namespaces/contexts)
- ✅ HTTP-triggered functions: `GET/POST /functions/{tenant}/{functionName}`
- ✅ Event-driven functions: triggered by Database outbox events
- ✅ Scheduled functions: cron expressions managed via Dashboard
- ✅ Built-in connector SDK for: Stripe, PayPal, SendGrid, AWS SES, Twilio, Firebase, Google Calendar, Outlook Calendar
- ✅ Function code stored in Storage service with versioning and hot-reload
- ✅ Tenant-scoped secret/credential management (encrypted at rest)
- ✅ Dashboard UI for deploying, testing, viewing logs, managing secrets
- ✅ Full OpenTelemetry tracing and audit logging for all executions
- ✅ Dead-letter queue for failed event-driven invocations

---

#### Phase 1: Core Runtime and HTTP Triggers (2-3 weeks)

**Goal**: Functions service can execute tenant-scoped C# scripts via HTTP requests.

**Subtasks**:

1. **Create TansuCloud.Functions service** (3 days)
   - ASP.NET Core Web API service
   - Endpoint: `POST /functions/{tenant}/{functionName}` (execute function)
   - JWT authentication with `functions.execute` scope
   - Tenant isolation via `X-Tansu-Tenant` header
   - Health endpoints (`/health/live`, `/health/ready`)
   - **Add Gateway reverse proxy routing** (update `TansuCloud.Gateway/Program.cs`):
     - Add to `initialProxyRoutes` array:

       ```csharp
       new RouteConfig
       {
           RouteId = "functions-route",
           ClusterId = "functions",
           Match = new RouteMatch { Path = "/functions/{**catch-all}" },
           Transforms = new[]
           {
               new Dictionary<string, string> { ["PathRemovePrefix"] = "/functions" },
               new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
               new Dictionary<string, string>
               {
                   ["RequestHeader"] = "X-Forwarded-Prefix",
                   ["Set"] = "/functions"
               },
               new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
               new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
           }
       }
       ```

     - Add to `initialProxyClusters` array:

       ```csharp
       new ClusterConfig
       {
           ClusterId = "functions",
           Destinations = new Dictionary<string, DestinationConfig>
           {
               ["functions-primary"] = new()
               {
                   Address = "http://functions:5050"
               }
           }
       }
       ```

     - Add health check exclusion for `/functions/health` endpoints in the existing health check skip logic
     - Test gateway routing: `curl http://127.0.0.1:8080/functions/health/live` should route correctly
   - **Add Docker Compose configuration**:
     - Add `functions` service to `docker-compose.yml` and `docker-compose.prod.yml`:

       ```yaml
       functions:
         build:
           context: .
           dockerfile: TansuCloud.Functions/Dockerfile
         container_name: tansu-functions
         environment:
           - ASPNETCORE_ENVIRONMENT=Development
           - ASPNETCORE_URLS=http://+:5050
           - ConnectionStrings__DefaultConnection=${POSTGRES_CONNECTION_STRING}
           - Redis__Configuration=${REDIS_CONNECTION_STRING}
           - Oidc__Issuer=${OIDC_ISSUER}
           - Oidc__MetadataAddress=${OIDC_METADATA_ADDRESS}
         env_file:
           - .env
         depends_on:
           - postgres
           - redis
           - storage
         networks:
           - tansu-network
         healthcheck:
           test: ["CMD", "curl", "-f", "http://localhost:5050/health/live"]
           interval: 10s
           timeout: 5s
           retries: 3
           start_period: 10s
       ```

     - Ensure consistency between dev and prod compose files per repository guidelines

2. **Implement function code storage layer** (2 days)
   - Store function code in Storage service: `{tenant}/functions/{functionName}/{version}/code.cs`
   - Metadata stored in Database: `Functions` table (FunctionId, Name, TenantId, Version, EntryPoint, CreatedAt, UpdatedAt)
   - Support versioning: `{functionName}/v1/code.cs`, `{functionName}/v2/code.cs`
   - Active version pointer in Database

3. **Build C# script executor with Roslyn** (5 days)
   - Use `Microsoft.CodeAnalysis.CSharp.Scripting` for dynamic compilation
   - Security sandbox: restrict file system access, network calls (only via connectors), reflection
   - Execution context: `FunctionContext` with `Logger`, `Secrets`, `Http` (connector), `Db` (connector)
   - Memory limits: 512 MB per function execution (configurable)
   - Timeout: 30 seconds per execution (configurable)
   - Hot-reload: recompile on code change, cache compiled assemblies

4. **Add HTTP request/response handling** (2 days)
   - Function signature: `Task<FunctionResponse> ExecuteAsync(FunctionRequest req, FunctionContext ctx)`
   - `FunctionRequest`: Headers, Query, Body (JSON), Method
   - `FunctionResponse`: StatusCode, Headers, Body (JSON)
   - Support async/await in function code

5. **Implement basic Dashboard UI** (3 days)
   - Page: `/dashboard/tenant/{id}/functions`
   - List functions with status (active/inactive), version, last executed timestamp
   - Create new function form: Name, Entry point (default: `ExecuteAsync`)
   - Code editor: Monaco Editor (VS Code editor component) with C# syntax highlighting
   - Deploy button: uploads code to Storage, updates active version in Database
   - Delete function confirmation dialog

6. **Add function testing console** (2 days)
   - Page: `/dashboard/tenant/{id}/functions/{name}/test`
   - Form: HTTP method, query params, headers, body (JSON)
   - Execute button: calls function API, displays response and logs
   - Execution history: last 10 invocations with status, duration, error messages

**Acceptance criteria**:

- ✅ Tenant can create a simple HTTP function via Dashboard
- ✅ Function executes when called via `POST /functions/{tenant}/{functionName}`
- ✅ Function has access to `ctx.Logger` for structured logging
- ✅ Function code stored in Storage service and versioned
- ✅ Dashboard shows function list and allows inline editing
- ✅ Test console can invoke function and display results
- ✅ Tenant isolation: Acme's functions cannot be called by Widgets tenant
- ✅ Security: functions cannot access file system or make arbitrary network calls

**Definition of done (Phase 1)**:

- [ ] TansuCloud.Functions service implemented and running
- [ ] **Gateway routing configured and tested** (`/functions/*` → `http://functions:5050`)
- [ ] **Gateway health check exclusions added** for `/functions/health/*` endpoints
- [ ] HTTP-triggered functions working with JWT auth
- [ ] Code storage and versioning in place
- [ ] Roslyn-based executor with security sandbox
- [ ] Dashboard function management UI working
- [ ] Test console functional
- [ ] Integration tests: create function, execute, verify response
- [ ] Security tests: verify sandbox restrictions
- [ ] E2E tests: verify gateway routing, tenant isolation, health endpoints
- [ ] Documentation: `Guide-For-Admins-and-Tenants.md` updated with Functions guide

---

#### Phase 2: Built-in Connectors (2 weeks)

**Goal**: Functions can integrate with common third-party services via pre-built connectors.

**Subtasks**:

1. **Design connector SDK** (2 days)
   - Base interface: `IConnector<TConfig>` with `InitializeAsync`, `ExecuteAsync`
   - Connector discovery: loaded from DI container
   - Configuration: tenant-scoped secrets (API keys, tokens)
   - Rate limiting per connector (e.g., 100 Twilio SMS per hour)

2. **Implement Payment connectors** (3 days)
   - **Stripe**: `ctx.Stripe.Charges.CreateAsync(amount, currency, source)`, `ctx.Stripe.PaymentIntents.CreateAsync(...)`
   - **PayPal**: `ctx.PayPal.Payments.CreateAsync(amount, currency, returnUrl, cancelUrl)`
   - Support webhooks: tenant can register webhook URLs for payment events

3. **Implement Email connectors** (2 days)
   - **SendGrid**: `ctx.SendGrid.SendAsync(from, to, subject, htmlContent)`
   - **AWS SES**: `ctx.AwsSes.SendAsync(from, to, subject, htmlContent)`
   - **SMTP**: `ctx.Smtp.SendAsync(host, port, username, password, from, to, subject, body)`

4. **Implement SMS connector** (1 day)
   - **Twilio**: `ctx.Twilio.SendSmsAsync(from, to, body)`
   - **AWS SNS**: `ctx.AwsSns.PublishAsync(phoneNumber, message)`

5. **Implement Push Notification connectors** (2 days)
   - **Firebase Cloud Messaging (FCM)**: `ctx.Firebase.SendAsync(deviceToken, title, body, data)`
   - **OneSignal**: `ctx.OneSignal.SendAsync(playerIds, heading, content, data)`

6. **Implement Calendar connectors** (3 days)
   - **Google Calendar**: `ctx.GoogleCalendar.Events.CreateAsync(calendarId, summary, start, end, attendees)`
   - **Outlook Calendar**: `ctx.OutlookCalendar.Events.CreateAsync(calendarId, subject, start, end, attendees)`
   - OAuth integration: tenant provides OAuth tokens for calendar access

7. **Add Dashboard UI for connector configuration** (2 days)
   - Page: `/dashboard/tenant/{id}/functions/{name}/connectors`
   - List available connectors: Stripe, SendGrid, Twilio, Firebase, Google Calendar, etc.
   - Enable/disable connectors per function
   - Configure secrets: API keys, OAuth tokens (masked in UI)
   - Test connector button: validates credentials

**Acceptance criteria**:

- ✅ Function can send email via SendGrid: `await ctx.SendGrid.SendAsync(...)`
- ✅ Function can charge credit card via Stripe: `await ctx.Stripe.Charges.CreateAsync(...)`
- ✅ Function can send SMS via Twilio: `await ctx.Twilio.SendSmsAsync(...)`
- ✅ Function can send push notification via Firebase: `await ctx.Firebase.SendAsync(...)`
- ✅ Function can create calendar event via Google Calendar: `await ctx.GoogleCalendar.Events.CreateAsync(...)`
- ✅ Connector credentials stored encrypted in Database
- ✅ Rate limiting enforced per connector (e.g., max 100 SMS per hour)
- ✅ Dashboard UI shows enabled connectors and allows configuration

**Definition of done (Phase 2)**:

- [ ] Connector SDK designed and documented
- [ ] Payment connectors (Stripe, PayPal) implemented
- [ ] Email connectors (SendGrid, AWS SES, SMTP) implemented
- [ ] SMS connector (Twilio, AWS SNS) implemented
- [ ] Push notification connectors (Firebase, OneSignal) implemented
- [ ] Calendar connectors (Google Calendar, Outlook) implemented
- [ ] Dashboard connector config UI working
- [ ] Integration tests for each connector (mocked APIs)
- [ ] Documentation: connector usage examples, rate limits, error handling

---

#### Phase 3: Event-Driven Triggers (1-2 weeks)

**Goal**: Functions can be triggered by Database outbox events.

**Subtasks**:

1. **Add event subscription model** (2 days)
   - New table: `FunctionTriggers` (FunctionId, TriggerType [HTTP|Event|Schedule], EventFilter, IsActive)
   - Event filter: match outbox event types (e.g., `document.created`, `file.uploaded`)
   - Support wildcards: `document.*`, `*.deleted`

2. **Implement event dispatcher** (3 days)
   - Background worker: polls Database outbox for new events
   - Match events to subscribed functions via `FunctionTriggers` table
   - Invoke function with event payload: `ExecuteAsync(event, ctx)`
   - At-least-once delivery: retry on failure with exponential backoff

3. **Add dead-letter queue** (2 days)
   - Failed events after 3 retries moved to DLQ table: `FunctionDlq` (EventId, FunctionId, Payload, ErrorMessage, FailedAt)
   - Dashboard UI to view DLQ and manually retry events

4. **Add Dashboard UI for event triggers** (2 days)
   - Page: `/dashboard/tenant/{id}/functions/{name}/triggers`
   - Create event trigger: Event type (dropdown), Event filter (wildcard)
   - List active triggers with last invocation timestamp
   - Enable/disable trigger toggle

5. **Add event replay capability** (1 day)
   - Dashboard UI to replay failed events from DLQ
   - Bulk replay: select multiple events and retry all

**Acceptance criteria**:

- ✅ Function subscribed to `document.created` is invoked when a document is created
- ✅ Function receives event payload with `document.id`, `document.collectionId`, `document.createdAt`
- ✅ Failed function executions retried 3 times with exponential backoff (1s, 2s, 4s)
- ✅ Events that fail 3 times moved to dead-letter queue
- ✅ Dashboard shows DLQ with error messages
- ✅ Tenant can manually retry failed events from Dashboard

**Definition of done (Phase 3)**:

- [ ] Event subscription model implemented
- [ ] Event dispatcher worker running in background
- [ ] Dead-letter queue functional
- [ ] Dashboard UI for event triggers working
- [ ] Event replay capability added
- [ ] Integration tests: trigger function via outbox event, verify retry/DLQ
- [ ] Documentation: event-driven functions guide, event types, retry policy

---

#### Phase 4: Scheduled Functions (1 week)

**Goal**: Functions can run on a schedule (cron expressions).

**Subtasks**:

1. **Add cron scheduler** (2 days)
   - Use `Cronos` NuGet package for cron expression parsing
   - Background worker: checks `FunctionTriggers` table for schedule triggers every minute
   - Invoke function when cron expression matches current time
   - Store last execution timestamp to prevent duplicate runs

2. **Add Dashboard UI for scheduled triggers** (2 days)
   - Page: `/dashboard/tenant/{id}/functions/{name}/triggers` (same as event triggers)
   - Create schedule trigger: Cron expression (e.g., `0 0 * * *` for daily at midnight)
   - Cron builder UI: dropdown for common patterns (hourly, daily, weekly, monthly, custom)
   - Preview next 5 execution times based on cron expression
   - Enable/disable schedule toggle

3. **Add execution history** (1 day)
   - Table: `FunctionExecutions` (ExecutionId, FunctionId, TriggerType, StartedAt, CompletedAt, Status, ErrorMessage, DurationMs)
   - Dashboard UI: show last 100 executions per function
   - Filter by status (success/failure), date range

4. **Add timezone support** (1 day)
   - Tenant-scoped timezone setting (default: UTC)
   - Cron expressions evaluated in tenant's timezone
   - Dashboard shows next execution times in tenant's timezone

**Acceptance criteria**:

- ✅ Function with cron schedule `0 9 * * *` runs daily at 9:00 AM in tenant's timezone
- ✅ Dashboard cron builder helps tenant create schedule without knowing cron syntax
- ✅ Execution history shows all past runs with status and duration
- ✅ Failed scheduled executions logged with error messages
- ✅ Timezone setting respected for all scheduled functions

**Definition of done (Phase 4)**:

- [ ] Cron scheduler implemented with `Cronos`
- [ ] Dashboard UI for scheduled triggers working
- [ ] Cron builder UI with common patterns
- [ ] Execution history table and UI
- [ ] Timezone support added
- [ ] Integration tests: schedule function, verify execution at correct time
- [ ] Documentation: scheduled functions guide, cron syntax, timezone handling

---

#### Phase 5: Advanced Features (Ongoing)

**Optional enhancements** (post-MVP, prioritize based on demand):

1. **Function secrets vault** (1 week)
   - Encrypted secret storage per tenant: API keys, OAuth tokens, database passwords
   - Secrets accessible in functions: `await ctx.Secrets.GetAsync("stripe-api-key")`
   - Automatic secret rotation reminders

2. **Function chaining** (1 week)
   - Function can invoke another function: `await ctx.Functions.InvokeAsync("send-email", payload)`
   - Async invocations: fire-and-forget or wait for response
   - Prevent infinite loops: max chain depth = 5

3. **Multi-language support** (3 weeks)
   - JavaScript/TypeScript: Use `Jint` or Node.js child process
   - Python: Use `Python.NET` or Python child process
   - Language selection in Dashboard

4. **Function templates/marketplace** (2 weeks)
   - Pre-built function templates: "Send welcome email on user signup", "Charge customer on subscription renewal"
   - One-click install from marketplace
   - Community-contributed templates

5. **Function versioning and rollback** (1 week)
   - Keep all function versions in Storage
   - Dashboard UI to view version history and roll back to previous version
   - Canary deployments: route 10% of traffic to new version, monitor errors, roll back automatically on high error rate

6. **Function metrics dashboard** (1 week)
   - Charts: Invocations per hour, success/failure rate, p50/p95/p99 latency, error rate by function
   - Alerts: trigger webhook when error rate > 5%, latency > 1s, or DLQ depth > 100

---

#### Overall Task 45 Acceptance Criteria

- ✅ Tenants can deploy HTTP-triggered functions via Dashboard
- ✅ Functions can use built-in connectors for Stripe, SendGrid, Twilio, Firebase, Google Calendar
- ✅ Functions can be triggered by Database outbox events (event-driven)
- ✅ Functions can run on schedules (cron expressions)
- ✅ Complete tenant isolation: functions cannot access other tenants' data or secrets
- ✅ Dashboard UI for function management: create, edit, deploy, test, view logs, configure connectors
- ✅ Dead-letter queue for failed event-driven invocations with manual retry
- ✅ Full OpenTelemetry tracing: function executions appear in SigNoz traces
- ✅ Audit logging: all function deployments, executions, and configuration changes logged
- ✅ Security sandbox: functions cannot access file system or make arbitrary network calls
- ✅ Documentation complete: Functions guide in `Guide-For-Admins-and-Tenants.md`
- ✅ All integration and E2E tests passing (20+ tests covering HTTP triggers, event-driven, scheduled, connectors)

---

#### Dependencies and Prerequisites

**Required before starting**:

- Task 13 (Storage core) must be complete (function code storage)
- Task 12 (Outbox) must be complete (event-driven triggers)
- Task 20 (Background jobs) must be complete (scheduler infrastructure)
- Task 17 (Dashboard admin) must be complete (UI foundation)

**Blocks**:

- No current blockers; all prerequisites met

**Enables future work**:

- Task 46 (hypothetical): Workflow orchestration (Temporal/Durable Functions)
- Task 47 (hypothetical): Real-time functions (WebSocket handlers per tenant)
- Enhanced SDK: Add functions client to .NET SDK (NuGet package)

---

#### Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Function code execution security vulnerabilities | HIGH | Strict sandbox with no file system/network access except via connectors; code review and security scanning |
| Denial of service via infinite loops or resource exhaustion | HIGH | Execution timeout (30s), memory limits (512 MB), rate limiting per tenant (100 executions/minute) |
| Connector API key leakage | HIGH | Encrypt secrets at rest (ASP.NET Data Protection); mask in UI; audit all secret access |
| Event-driven functions failing silently | MEDIUM | Dead-letter queue; alerting on DLQ depth; retry with exponential backoff |
| Scheduled functions not firing due to clock drift | MEDIUM | Use UTC for all scheduling; verify cron scheduler accuracy with tests |
| Hot-reload causing stale code execution | LOW | Cache compiled assemblies with TTL (5 minutes); force recompile on deploy |
| Dashboard performance with many functions (100+) | MEDIUM | Pagination; search/filter; lazy loading of execution history |

---

#### Effort Estimate

- **Phase 1** (Core Runtime and HTTP Triggers): 2-3 weeks (1 developer)
- **Phase 2** (Built-in Connectors): 2 weeks (1 developer)
- **Phase 3** (Event-Driven Triggers): 1-2 weeks (1 developer)
- **Phase 4** (Scheduled Functions): 1 week (1 developer)
- **Phase 5** (Advanced Features): Ongoing (prioritize based on demand)

**Total**: 6-8 weeks for MVP (Phases 1-4)

---

#### Success Metrics

**Technical**:

- Zero cross-tenant function execution incidents
- Function execution latency < 500ms (p95) for HTTP-triggered functions
- Event-driven function latency < 2s (p95) from outbox event to execution start
- Uptime: 99.9% for Functions service endpoints
- Dead-letter queue depth < 100 events per tenant

**Product**:

- 20+ tenants actively using functions within 3 months of launch
- 80%+ of tenants configure at least one connector (Stripe/SendGrid/Twilio)
- 50%+ of tenants use event-driven functions
- Average 5+ functions per tenant
- Positive feedback from early adopters on Dashboard UX and connector ease-of-use

---

#### Documentation and Training

**Docs to create/update**:

- [ ] `Architecture.md`: Add "Functions Service" section with architecture diagram
- [ ] `Guide-For-Admins-and-Tenants.md`: Add comprehensive Functions guide (HTTP triggers, event-driven, scheduled, connectors, secrets, testing)
- [ ] API reference: Document Functions API endpoints (`/functions/{tenant}/{functionName}`)
- [ ] Connector reference: Usage examples for Stripe, SendGrid, Twilio, Firebase, Google Calendar
- [ ] Security guide: Sandbox restrictions, secret management, rate limits

**Training materials**:

- [ ] Video tutorial: "Building your first serverless function in TansuCloud"
- [ ] Blog post: "Integrating payment processing with Stripe functions"
- [ ] Sample functions: Welcome email on user signup, Stripe subscription webhook handler, daily report generator
- [ ] Function templates: Starter templates for common use cases

---

#### Next Steps After Task 45 Creation

1. **Review and approve** task scope and phasing with team
2. **Prioritize Phase 1** subtasks and assign owners
3. **Set up feature branch**: `feature/serverless-functions`
4. **Create GitHub Epic**: "Serverless Functions Service" with milestones for each phase
5. **Begin Phase 1 implementation**:
   - Start with TansuCloud.Functions service skeleton (low-risk)
   - Then code storage layer (depends on Task 13 Storage)
   - Then Roslyn executor (core functionality)
6. **Weekly demos** to show progress and gather feedback
7. **Beta testing** with 3-5 pilot tenants after Phase 1 completes

---

**Status**: Task 45 created and awaiting approval ✅

---

### Task 46: Database Access Patterns - Direct PostgreSQL + Quota Management {#task-46-database-access-patterns-direct-postgresql-quota-management}

**Status**: IN PROGRESS — Documentation ✅ COMPLETED, Implementation PENDING  
**Decision Date**: 2025-01-19  
**Owner**: TBD  
**Priority**: HIGH  
**Dependencies**: Task 10 (EF Core model), Task 11 (Database REST API), Task 15 (HybridCache)

**Purpose**: Formalize and implement the **dual database access patterns** to give tenants flexibility in how they interact with their data: cached REST API for hot reads (mobile/web apps) and direct PostgreSQL access for complex analytics/BI tools. Enable admins to manage per-tenant connection quotas and provide tenants with self-service visibility into their usage. This brings TansuCloud closer to the **Supabase model** where tenants can use any language/ORM (Python/psycopg2, Node.js/pg, Go/pgx) for full SQL power while maintaining the performance benefits of the REST API for common use cases.

**Current state gaps**:

- ❌ Documentation mentions PgCat but doesn't formalize two access patterns
- ❌ No connection limit enforcement at PostgreSQL or PgCat level
- ❌ No Dashboard UI for admins to manage per-tenant connection quotas
- ❌ No Dashboard UI for tenants to view their own connection limits and usage
- ❌ REST API caching benefits (HybridCache/Garnet) not clearly documented vs direct access trade-offs
- ❌ No guidance on when to use REST API vs direct PostgreSQL access
- ❌ No client-side caching strategies documented for direct access users
- ❌ Connection string format and security best practices not documented

**Target state (Dual Access Patterns)**:

- ✅ **REST API Access** (port 8080 via Gateway):
  - Cached by HybridCache with Garnet backing (30s TTL)
  - Automatic cache invalidation on writes via outbox events
  - Best for hot reads, mobile/web apps, user-facing queries
  - JSON responses with pagination, filtering, ETags
- ✅ **Direct PostgreSQL Access** (port 6432 via PgCat):
  - Full SQL access with any language/ORM (Python, Node.js, Go, Rust, etc.)
  - No automatic caching (PgCat is connection pooler only)
  - Best for complex analytics, JOINs, CTEs, BI tools, ETL pipelines
  - Per-tenant connection limits enforced at PostgreSQL role + PgCat pool
- ✅ **Admin Dashboard** (`/dashboard/admin/database/quotas`):
  - View all tenant connection limits and current usage
  - Edit connection quotas with validation and audit logging
  - Connection usage charts (current vs limit over time)
- ✅ **Tenant Self-Service** (`/dashboard/tenant/database`):
  - View own connection limits and current usage
  - Connection usage charts with trends
  - Direct SQL credentials management (view connection string, rotate password)
- ✅ **Comprehensive Documentation**:
  - Guide section 4.1 with 2000+ words covering both patterns
  - Performance comparison table (latency, throughput, flexibility)
  - Code examples: Python (psycopg2), Node.js (pg), curl
  - Client-side caching strategies for direct access users
  - Security and access control details
  - Migration path for existing tenants

**Reference documentation**:

- **Architecture.md**: Updated with dual access patterns in Database component section
- **Requirements.md**: Split into "Database API (REST Access)" and "Database Direct Access (PostgreSQL via PgCat)" sections
- **Guide-For-Admins-and-Tenants.md**: New section 4.1 (2000+ words) with comprehensive guidance

---

#### Phase 1: Connection Limit Enforcement (1 week)

**Goal**: PostgreSQL and PgCat enforce per-tenant connection limits with clear error messages.

**Subtasks**:

1. **Implement PostgreSQL role connection limits** (2 days)
   - Update `TenantProvisioner` to set `CONNECTION LIMIT` on tenant roles:

     ```sql
     ALTER ROLE tansu_tenant_acme_user CONNECTION LIMIT 50;
     ```

   - Default limit: 50 connections per tenant (configurable via `Database:DefaultConnectionLimit`)
   - Store limit in `TenantMetadata` table for future updates
   - Add migration to apply limits to existing tenants

2. **Integrate PgCat Admin API for dynamic pools** (3 days)
   - Use PgCat Admin API (`POST /pools`) to create per-tenant pool configurations
   - Pool config includes:
     - `pool_name`: `tansu_tenant_{id}`
     - `max_connections`: Same as PostgreSQL role limit
     - `default_role`: Tenant-specific PostgreSQL role
   - Update pool configuration when admin changes quota via Dashboard
   - Add retry logic with exponential backoff for PgCat API calls
   - Add health check to verify PgCat Admin API availability

3. **Add error handling for connection limit exceeded** (1 day)
   - Catch PostgreSQL error code 53300 (`too_many_connections`)
   - Return clear error message: `"Connection limit exceeded. Your tenant has {current}/{max} active connections. Please close idle connections or contact support to increase your limit."`
   - Log connection limit violations with tenant ID, timestamp, current usage
   - Add metric counter: `tansu_database_connection_limit_exceeded_total` (tagged by tenant)

4. **Add connection usage tracking** (1 day)
   - Query `pg_stat_activity` to get current active connections per tenant:

     ```sql
     SELECT datname, usename, count(*) as active_connections
     FROM pg_stat_activity
     WHERE datname LIKE 'tansu_tenant_%'
     GROUP BY datname, usename;
     ```

   - Cache results in memory (TTL: 30 seconds) to avoid repeated queries
   - Expose via internal API: `GET /db/internal/connections` (admin only)

**Acceptance criteria**:

- ✅ Tenant provisioning sets `CONNECTION LIMIT 50` on PostgreSQL role
- ✅ PgCat pool created with matching `max_connections: 50`
- ✅ Direct connection attempt when limit reached returns PostgreSQL error 53300
- ✅ Error message includes current/max usage and actionable guidance
- ✅ Connection usage queryable via `pg_stat_activity`
- ✅ Metric counter tracks connection limit violations per tenant

**Definition of done (Phase 1)**:

- [ ] PostgreSQL role connection limits applied to all tenants
- [ ] PgCat Admin API integration functional with retry logic
- [ ] Error handling returns clear messages on limit exceeded
- [ ] Connection usage tracking implemented
- [ ] Integration tests: provision tenant → verify limit → exceed limit → verify error
- [ ] E2E test: connect via psycopg2 → verify limit enforcement
- [ ] Metrics exported to OpenTelemetry (connection limit violations)
- [ ] Documentation: `Guide-For-Admins-and-Tenants.md` updated with limit enforcement details

---

#### Phase 2: Quotas REST API (1 week)

**Goal**: REST API endpoints allow admins to manage quotas and tenants to view their own usage.

**Subtasks**:

1. **Create quota model and persistence** (1 day)
   - Add `TenantConnectionQuota` table:
     - `TenantId` (PK, string)
     - `MaxConnections` (int, default 50)
     - `UpdatedAt` (timestamp)
     - `UpdatedBy` (admin user ID)
   - EF Core migration and entity mapping
   - Seed existing tenants with default quota (50 connections)

2. **Build admin quota endpoints** (2 days)
   - `GET /db/api/admin/quotas` - List all tenant quotas with current usage
     - Response: `{ tenantId, maxConnections, currentConnections, usagePercent }`
     - Sort by `usagePercent` descending (tenants closest to limit first)
     - Pagination (50 tenants per page)
   - `GET /db/api/admin/quotas/{tenantId}` - Get specific tenant quota
     - Response includes quota history (last 10 changes with timestamps and admin IDs)
   - `PUT /db/api/admin/quotas/{tenantId}` - Update tenant connection limit
     - Request body: `{ maxConnections: 100 }`
     - Validation: min 10, max 500 connections
     - Updates PostgreSQL role: `ALTER ROLE ... CONNECTION LIMIT {maxConnections}`
     - Updates PgCat pool via Admin API
     - Audit log entry: admin ID, old limit, new limit, reason (optional)
   - Authorization: Require `admin.full` scope

3. **Build tenant quota endpoints** (1 day)
   - `GET /db/api/quotas` - Get own tenant quota
     - Response: `{ maxConnections, currentConnections, usagePercent, peakConnections24h }`
     - Peak connections: max usage in last 24 hours (stored in metrics)
   - `GET /db/api/connections` - Get active connections for own tenant
     - Response: List of active connections with query text (redacted), duration, state
     - Query `pg_stat_activity` filtered by tenant database name
     - Redact query text to first 100 characters (prevent sensitive data exposure)
   - Authorization: Require `db.read` scope and matching tenant context

4. **Add OpenTelemetry metrics** (1 day)
   - Gauge: `tansu_database_connection_current` (current active connections per tenant)
   - Gauge: `tansu_database_connection_limit` (max connections per tenant)
   - Histogram: `tansu_database_connection_usage_percent` (current/max * 100)
   - Counter: `tansu_database_quota_updates_total` (admin quota changes)
   - Update metrics every 30 seconds via background worker

**Acceptance criteria**:

- ✅ Admin can list all tenant quotas via `GET /db/api/admin/quotas`
- ✅ Admin can update tenant quota via `PUT /db/api/admin/quotas/{tenantId}`
- ✅ Quota update triggers PostgreSQL `ALTER ROLE` and PgCat pool update
- ✅ Tenant can view own quota via `GET /db/api/quotas`
- ✅ Tenant can see active connections via `GET /db/api/connections`
- ✅ Audit log captures quota changes with admin identity
- ✅ Metrics exported to OpenTelemetry and visible in SigNoz

**Definition of done (Phase 2)**:

- [ ] `TenantConnectionQuota` table and migration created
- [ ] Admin quota endpoints implemented and tested
- [ ] Tenant quota endpoints implemented and tested
- [ ] PostgreSQL role update logic working
- [ ] PgCat Admin API update logic working
- [ ] Audit logging functional
- [ ] OpenTelemetry metrics exported
- [ ] Integration tests: CRUD operations on quotas
- [ ] E2E tests: admin updates quota → tenant sees new limit → connection enforcement updated
- [ ] Documentation: API reference for quota endpoints

---

#### Phase 3: Admin Dashboard UI (1 week)

**Goal**: Admin Dashboard page for managing per-tenant connection quotas.

**Subtasks**:

1. **Create admin quotas page** (2 days)
   - Route: `/dashboard/admin/database/quotas`
   - **MudDataGrid** component with columns:
     - Tenant ID (link to tenant detail page)
     - Current Connections (gauge with color: green < 70%, yellow 70-90%, red > 90%)
     - Max Connections (editable inline or via dialog)
     - Usage % (with trend arrow: ↑ increasing, ↓ decreasing, → stable)
     - Peak 24h (highest usage in last 24 hours)
     - Last Updated (timestamp and admin name)
     - Actions: Edit quota, View history
   - Toolbar: Search by tenant ID, filter by usage % (>50%, >80%, >90%)
   - Pagination: 50 tenants per page

2. **Create quota edit dialog** (2 days)
   - **MudDialog** component with form:
     - Tenant ID (read-only)
     - Current usage (gauge)
     - Max Connections (numeric input with validation: 10-500)
     - Reason for change (optional text area, stored in audit log)
     - Preview impact: "This will update PostgreSQL role and PgCat pool configuration"
   - Save button: calls `PUT /db/api/admin/quotas/{tenantId}`
   - Show success/error **MudSnackbar**
   - Close dialog on success

3. **Add quota history dialog** (1 day)
   - **MudDialog** showing timeline of quota changes:
     - Timestamp, Admin name, Old limit, New limit, Reason
     - MudTimeline component with color-coded items
   - Last 10 changes shown, with "Load more" button

4. **Add connection usage chart** (1 day)
   - **MudChart** (line chart) showing connection usage over last 24 hours
   - X-axis: time (hourly buckets)
   - Y-axis: connection count
   - Two lines: Current connections (blue), Max limit (red dashed)
   - Hover tooltip: exact timestamp, connection count
   - Chart loaded on quota row expand

**Acceptance criteria**:

- ✅ Admin Dashboard shows `/dashboard/admin/database/quotas` page
- ✅ MudDataGrid displays all tenants with current usage and limits
- ✅ Admin can edit quota via inline dialog
- ✅ Quota change updates PostgreSQL and PgCat immediately
- ✅ History dialog shows past changes with admin names
- ✅ Connection usage chart visualizes trends
- ✅ Search and filter work correctly

**Definition of done (Phase 3)**:

- [ ] Admin quotas page implemented with MudDataGrid
- [ ] Quota edit dialog functional
- [ ] Quota history dialog functional
- [ ] Connection usage chart rendering correctly
- [ ] Search and filter working
- [ ] E2E tests: admin navigates to quotas page → edits quota → verifies change
- [ ] Documentation: Admin guide updated with quota management screenshots

---

#### Phase 4: Tenant Self-Service UI (1 week)

**Goal**: Tenant Dashboard page for viewing connection limits and usage.

**Subtasks**:

1. **Create tenant database page** (2 days)
   - Route: `/dashboard/tenant/database`
   - **MudCard** components for sections:
     - **Connection Quota Card**:
       - Gauge showing current/max connections (with color coding)
       - Text: "Your tenant has {current}/{max} active connections"
       - Peak usage in last 24 hours
       - Link: "Need more connections? Contact support" (opens mailto or support ticket form)
     - **Connection Details Card**:
       - MudDataGrid showing active connections:
         - Query text (first 100 chars, redacted)
         - Duration (formatted: "5 min 32 sec")
         - State (active, idle, idle in transaction)
         - Backend PID (for support troubleshooting)
       - Refresh button (polls `GET /db/api/connections`)

2. **Add connection usage chart** (1 day)
   - **MudChart** (line chart) showing connection usage over last 7 days
   - X-axis: date (daily buckets)
   - Y-axis: connection count
   - Two lines: Average connections (blue), Max limit (red dashed)
   - Hover tooltip: date, avg connections, peak connections
   - Chart updates on page load

3. **Add credentials management section** (2 days)
   - **MudCard** for "Direct PostgreSQL Access":
     - **Connection String** (masked): `postgres://***:***@pgcat:6432/tansu_tenant_acme`
       - Show button (reveals full connection string for 30 seconds)
       - Copy button (copies to clipboard)
     - **Rotate Password** button:
       - Opens MudDialog with confirmation
       - Generates new password and updates PostgreSQL role
       - Shows new connection string once (cannot be retrieved again)
       - Sends notification email to tenant admin
     - **Generate Read-Only Token** button (future enhancement):
       - Creates separate PostgreSQL role with SELECT-only grants
       - Returns read-only connection string for BI tools
       - Token expires after configurable period (default: 90 days)

**Acceptance criteria**:

- ✅ Tenant Dashboard shows `/dashboard/tenant/database` page
- ✅ Quota card displays current/max connections with gauge
- ✅ Connection details grid shows active connections
- ✅ Connection usage chart visualizes 7-day trend
- ✅ Connection string masked by default, reveal on click
- ✅ Password rotation functional with confirmation
- ✅ Tenant receives email notification after password rotation

**Definition of done (Phase 4)**:

- [ ] Tenant database page implemented with MudCards and MudDataGrid
- [ ] Connection quota card functional
- [ ] Connection details grid showing live data
- [ ] Connection usage chart rendering correctly
- [ ] Credentials management section functional
- [ ] Password rotation working with email notification
- [ ] E2E tests: tenant views quotas → rotates password → verifies new connection string works
- [ ] Documentation: Tenant guide updated with database access screenshots

---

#### Phase 5: Direct PostgreSQL Access Testing (3 days)

**Goal**: Verify direct PostgreSQL access works from standard drivers in multiple languages.

**Subtasks**:

1. **Python (psycopg2) E2E test** (1 day)
   - Install psycopg2 in test environment
   - Connect to PgCat: `psycopg2.connect("host=127.0.0.1 port=6432 dbname=tansu_tenant_acme user=acme_user password=***")`
   - Execute SELECT query: `SELECT id, content FROM documents LIMIT 10`
   - Verify results returned correctly
   - Test connection limit enforcement: open N+1 connections → verify error

2. **Node.js (pg) E2E test** (1 day)
   - Install pg library in test environment
   - Connect to PgCat: `new Client({ host: '127.0.0.1', port: 6432, database: 'tansu_tenant_acme', user: 'acme_user', password: '***' })`
   - Execute INSERT query: `INSERT INTO documents (content) VALUES ($1)` with parameterized query
   - Verify row inserted and returned
   - Test connection pooling: create pool with max 5 connections → verify pool behavior

3. **Connection limit enforcement test** (1 day)
   - Provision tenant with limit of 5 connections
   - Open 5 concurrent connections from Python client
   - Attempt 6th connection → verify PostgreSQL error 53300
   - Verify error message includes current/max usage
   - Close 1 connection → verify 6th connection now succeeds

**Acceptance criteria**:

- ✅ Python psycopg2 can connect to PgCat and execute queries
- ✅ Node.js pg can connect to PgCat and execute queries
- ✅ Connection limit enforcement works across different clients
- ✅ Error messages are clear and actionable
- ✅ PgCat connection pooling works correctly

**Definition of done (Phase 5)**:

- [ ] Python E2E tests passing
- [ ] Node.js E2E tests passing
- [ ] Connection limit enforcement tests passing
- [ ] Documentation: Direct access guide with code examples in Python/Node.js
- [ ] Sample applications: Python script and Node.js script demonstrating direct access

---

#### Overall Task 46 Acceptance Criteria

- ✅ **Documentation Complete** (Architecture.md, Requirements.md, Guide section 4.1)
- ✅ Connection limits enforced at PostgreSQL role and PgCat pool level
- ✅ Admin Dashboard UI for managing per-tenant connection quotas
- ✅ Tenant Dashboard UI for viewing connection limits and usage
- ✅ REST API endpoints for quota CRUD operations (admin + tenant)
- ✅ Direct PostgreSQL access working from Python (psycopg2) and Node.js (pg)
- ✅ Connection usage metrics exported to OpenTelemetry
- ✅ Audit logging for all quota changes
- ✅ Clear error messages when connection limit exceeded
- ✅ Password rotation functional with email notifications
- ✅ Connection usage charts showing trends (24h for admin, 7d for tenant)
- ✅ Comprehensive documentation with code examples and performance comparison table
- ✅ All integration and E2E tests passing (15+ tests)

---

#### Dependencies and Prerequisites

**Required before starting**:

- Task 10 (EF Core model) must be complete
- Task 11 (Database REST API) must be complete
- Task 15 (HybridCache) must be complete (for REST API caching context)
- PgCat running and accessible via Admin API

**Blocks**:

- No current blockers; all prerequisites met

**Enables future work**:

- Enhanced multi-language SDK support (Python, Node.js, Go SDKs with direct access examples)
- BI tool integrations (Metabase, Grafana) documentation and setup guides
- Advanced analytics dashboard (tenant query performance insights)

---

#### Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| PgCat Admin API unavailable during quota update | MEDIUM | Retry with exponential backoff; fallback to PostgreSQL role limit only; alert admin |
| Connection limit changes require PgCat restart | HIGH | Use PgCat Admin API for dynamic updates (no restart needed); test thoroughly |
| Tenant credentials leaked in logs/UI | HIGH | Mask passwords in UI; redact from logs; audit all credential access |
| Direct access bypasses REST API rate limits | MEDIUM | Document trade-off clearly; recommend hybrid approach; consider PostgreSQL-level rate limits (future) |
| Connection tracking overhead on pg_stat_activity | LOW | Cache results with 30s TTL; optimize query with indexes |
| Dashboard performance with 1000+ tenants | MEDIUM | Pagination (50/page); server-side filtering; lazy loading of charts |

---

#### Effort Estimate

- **Phase 1** (Connection Limit Enforcement): 1 week (1 developer)
- **Phase 2** (Quotas REST API): 1 week (1 developer)
- **Phase 3** (Admin Dashboard UI): 1 week (1 developer)
- **Phase 4** (Tenant Self-Service UI): 1 week (1 developer)
- **Phase 5** (Direct PostgreSQL Access Testing): 3 days (1 developer)

**Total**: 4-5 weeks for full implementation

---

#### Success Metrics

**Technical**:

- Zero cross-tenant connection leaks
- Connection limit enforcement latency < 50ms (p95)
- Quota update propagation time < 5 seconds (PostgreSQL + PgCat)
- Dashboard page load time < 2 seconds (p95)
- Connection usage tracking overhead < 1% CPU

**Product**:

- 30%+ of tenants use direct PostgreSQL access within 3 months of launch
- 90%+ of connection limit violations include clear error messages
- 80%+ of tenants view connection usage at least once per week
- Positive feedback from BI tool users on direct access ease-of-use
- Zero support tickets related to connection limit confusion

---

#### Documentation and Training

**Docs to create/update**:

- ✅ `Architecture.md`: Dual access patterns documented
- ✅ `Requirements.md`: Direct access requirements added
- ✅ `Guide-For-Admins-and-Tenants.md`: Section 4.1 comprehensive guide (2000+ words)
- [ ] API reference: Quota endpoints (`/db/api/admin/quotas/*`, `/db/api/quotas`, `/db/api/connections`)
- [ ] Admin guide: Screenshots of quota management UI
- [ ] Tenant guide: Screenshots of database page with usage charts
- [ ] Direct access guide: Code examples in Python, Node.js, Go, Rust
- [ ] Security guide: Connection string management, password rotation, read-only tokens

**Training materials**:

- [ ] Video tutorial: "Managing tenant database quotas in TansuCloud"
- [ ] Video tutorial: "Accessing your database directly with Python/Node.js"
- [ ] Blog post: "REST API vs Direct SQL: Choosing the right access pattern"
- [ ] Sample scripts: Python analytics script, Node.js ETL pipeline, BI tool connection guide
- [ ] FAQ: "When should I use REST API vs direct PostgreSQL access?"

---

#### Next Steps After Task 46 Creation

1. **Review and approve** documentation updates (Architecture.md, Requirements.md, Guide)
2. **Prioritize Phase 1** subtasks and assign owner
3. **Set up feature branch**: `feature/database-access-patterns`
4. **Create GitHub Epic**: "Database Access Patterns - Direct PostgreSQL + Quota Management" with milestones for each phase
5. **Begin Phase 1 implementation**:
   - Start with PostgreSQL role connection limits (low-risk)
   - Then PgCat Admin API integration (foundation for dynamic updates)
   - Then connection usage tracking (enables monitoring)
6. **Weekly demos** to show progress and gather feedback
7. **Beta testing** with 3-5 pilot tenants after Phase 2 completes (REST API functional, UI pending)

---

**Status**: Task 46 created — Documentation ✅ COMPLETED, Implementation PENDING ✅

---

### Task 47: Migration from SigNoz to PGTL Stack (Prometheus + Grafana + Tempo + Loki) {#task-47-migration-from-signoz-to-pgtl-stack}

**Status**: DRAFT — Awaiting approval  
**Decision Date**: 2025-11-10  
**Owner**: TBD  
**Priority**: HIGH  
**Dependencies**: Task 8 (OpenTelemetry baseline), Task 19 (Observability pages), Task 36 (SigNoz dashboards), Task 39 (Instrumentation hardening)

**Purpose**: Replace SigNoz with the PGTL stack (Prometheus + Grafana + Tempo + Loki) for unified observability. Address upstream SigNoz schema compatibility issues blocking data persistence. Provide a production-ready, resource-efficient observability solution integrated with TansuCloud Dashboard's Admin UI as the primary interface.

**Background**: SigNoz v0.99.0 has persistent schema incompatibility between collector and ClickHouse (documented in `docs/SigNoz-Schema-Compatibility-Issue.md`). Multiple attempts to resolve via version pinning, volume cleanup, and schema patches have failed. The PGTL stack offers:

- **Proven stability**: Netflix (Tempo/Loki), Shopify (Prometheus), thousands of production deployments
- **Vendor-neutral**: OpenTelemetry OTLP protocol works with all PGTL components
- **Resource efficiency**: Specialized components (vs. SigNoz's all-in-one ClickHouse)
- **TansuCloud Storage integration**: Tempo and Loki can use existing Storage service for object storage

**Current state gaps**:

- ❌ SigNoz unable to persist traces/metrics/logs due to ClickHouse schema mismatch
- ❌ Dashboard observability pages show empty data (no ClickHouse data)
- ❌ Dashboard integration tightly coupled to SigNoz REST APIs (3 services: ISigNozQueryService, ISigNozTracesService, ISigNozLogsService)
- ❌ No retention management (SigNoz governance.dev.json unused)
- ❌ No sampling controls for logs/traces
- ❌ Resource overhead: SigNoz stack (4 containers: signoz, collector, ClickHouse, ZooKeeper) uses ~8GB memory

**Target state (PGTL-ready)**:

- ✅ Prometheus for metrics (local TSDB, 7-day retention)
- ✅ Grafana for visualization and dashboards (PostgreSQL backend, reuse tansu-postgres)
- ✅ Tempo for distributed tracing (TansuCloud Storage service for traces)
- ✅ Loki for log aggregation (TansuCloud Storage service for log chunks)
- ✅ Dashboard Admin UI remains primary interface (new IPrometheusQueryService, ITempoTracesService, ILokiLogsService)
- ✅ Optional direct access to Grafana UI (internal Docker network only, documented for advanced users)
- ✅ Resource-efficient: Target <4GB total memory for PGTL stack (vs. 8GB for SigNoz)
- ✅ Retention management: Admin Dashboard UI controls (7-day default, configurable via UI)
- ✅ Sampling controls: Admin Dashboard UI for logs/traces sampling percentages
- ✅ All PGTL services internal-only (no exposed host ports by default)

**Design decisions**:

1. **Storage strategy**:
   - Prometheus: Local TSDB (`prometheus-data` volume), 7-day retention
   - Grafana: PostgreSQL database (reuse `tansu-postgres` with `grafana` database)
   - Tempo: TansuCloud Storage service (`/observability/tempo/` prefix) for trace blocks
   - Loki: TansuCloud Storage service (`/observability/loki/` prefix) for log chunks, local BoltDB for index

2. **Retention and sampling**:
   - Default retention: 7 days (all components)
   - Configurable via Dashboard Admin UI: `/admin/observability/config`
   - Sampling: Logs 100% (no sampling by default), Traces 100% (adjustable for high-volume tenants)
   - Dashboard UI persists settings in `tansu_identity` database (new `ObservabilitySettings` table)

3. **Resource limits** (docker-compose):
   - Prometheus: 512MB memory, 0.5 CPU
   - Grafana: 256MB memory, 0.5 CPU
   - Tempo: 1GB memory, 1 CPU (handles trace ingestion + queries)
   - Loki: 1GB memory, 1 CPU (handles log ingestion + queries)
   - Total: ~3GB (vs. SigNoz 8GB)

4. **Network and access**:
   - All PGTL services internal Docker network only (no host ports exposed by default)
   - Dashboard Admin UI is primary interface (99% of users)
   - Advanced users can optionally expose Grafana UI (documented in `Guide-For-Admins-and-Tenants.md`)
   - Grafana access via Gateway reverse proxy: `/grafana/*` → `http://grafana:3000` (optional, disabled by default)

5. **Docker image versions** (for docker-compose.prod.yml):
   - Prometheus: `prom/prometheus:v3.0.1` (latest stable, Nov 2025 - major version with improved OTLP support)
   - Grafana: `grafana/grafana:11.3.1` (latest stable, Nov 2025)
   - Tempo: `grafana/tempo:2.6.1` (latest stable, Nov 2025)
   - Loki: `grafana/loki:3.3.1` (latest stable, Nov 2025)
   - OpenTelemetry Collector: `otel/opentelemetry-collector-contrib:0.114.0` (latest stable, Nov 2025)
   - Dev (docker-compose.yml): Use `:latest` tags for fast iteration

6. **Database initialization** (requirement #2):
   - Grafana requires PostgreSQL schema initialization
   - Leverage **TansuCloud.Database** service provisioning API to create `grafana` database
   - Provisioning endpoint: `POST /db/api/provisioning/databases` with `{ "databaseName": "grafana", "purpose": "observability" }`
   - Database created automatically on first Grafana startup via init script
   - No manual SQL required - Grafana auto-creates tables on first connection
   - Other PGTL components (Prometheus, Tempo, Loki) are schema-less (use filesystem/object storage)

7. **Grafana dashboard embedding** (requirement #3):
   - ✅ **Yes, we can embed Grafana dashboards in TansuCloud Admin Dashboard**
   - Implementation via **Grafana iframe embedding** with authentication:
     - Grafana anonymous access mode: `GF_AUTH_ANONYMOUS_ENABLED=true` with `viewer` role
     - TansuCloud Dashboard Admin pages embed Grafana dashboards via `<iframe src="http://grafana:3000/d/{dashboardId}?kiosk">`
     - Kiosk mode hides Grafana UI chrome (navigation, menus) for seamless embedding
   - **Dashboard pages**:
     - `/admin/observability` (Overview): Embed "TansuCloud System Overview" dashboard
     - `/admin/observability/services`: Embed "Service Health" dashboard with `var-service={serviceName}` filter
     - `/admin/observability/infrastructure`: Embed "Infrastructure Metrics" dashboard
   - **Security**: Grafana on internal Docker network only, iframe accessible from Dashboard backend
   - **Alternative approach** (more work, better UX): Query Prometheus/Tempo/Loki APIs directly from Dashboard and render charts with MudBlazor/ChartJS (like current SigNoz integration)
   - **Recommendation**: Start with iframe embedding (Phase 4), migrate to native API queries in Phase 5 if needed

---

#### Phase 1: Prometheus Metrics Migration (1-2 weeks)

**Goal**: Replace SigNoz metrics with Prometheus, update Dashboard to query Prometheus API.

**Subtasks**:

1. **Deploy Prometheus with OTLP receiver** (2 days)
   - Add `prometheus` service to `docker-compose.yml` and `docker-compose.prod.yml`
   - Image: `prom/prometheus:latest` (dev), `prom/prometheus:v3.0.1` (prod)
   - Config: `dev/prometheus-config.yml` with OTLP receiver (port 4317/4318) and scrape jobs
   - Scrape jobs:
     - Existing: `postgres-exporter:9187`, `redis-exporter:9121`, `pgcat:9930`
     - New: Prometheus self-monitoring `:9090/metrics`
   - Storage: `prometheus-data` volume, 7-day retention (`--storage.tsdb.retention.time=7d`)
   - Resources: `memory: 512M`, `cpus: 0.5`
   - Health check: `wget --spider -q http://localhost:9090/-/healthy`
   - Network: `tansucloud-network` internal only (no exposed ports by default)

2. **Update all services to export metrics to Prometheus** (1 day)
   - Change `OpenTelemetry__Otlp__Endpoint` from `http://signoz-otel-collector:4317` to `http://prometheus:4317`
   - Verify: All 5 services (Gateway, Identity, Dashboard, Database, Storage) export metrics
   - Test: Check Prometheus UI (internal) at `http://prometheus:9090/targets` shows all services "UP"

3. **Implement IPrometheusQueryService** (3 days)
   - New interface: `IPrometheusQueryService` in `TansuCloud.Dashboard/Observability/Prometheus/`
   - Methods:
     - `Task<PrometheusQueryResult> QueryAsync(string promQL, DateTime? time = null)`
     - `Task<PrometheusRangeQueryResult> QueryRangeAsync(string promQL, DateTime start, DateTime end, string step)`
     - `Task<List<PrometheusTarget>> GetTargetsAsync()` (scrape target health)
     - `Task<List<string>> GetMetricNamesAsync()` (metadata)
   - Implementation: `PrometheusQueryService` with HttpClient, HybridCache (2-5 min TTL), circuit breaker
   - DTOs: `PrometheusQueryResult`, `PrometheusMetric`, `PrometheusValue`, `PrometheusTarget`
   - Error handling: Graceful degradation, log errors, return empty results

4. **Update ObservabilityOverview.razor for Prometheus** (3 days)
   - Replace `ISigNozQueryService` with `IPrometheusQueryService`
   - Update PromQL queries:
     - Service health: `up{service_name=~"tansu.*"}`
     - Error rate: `sum(rate(http_server_request_duration_count{http_response_status_code=~"5.."}[5m])) by (service_name)`
     - Latency p95: `histogram_quantile(0.95, sum(rate(http_server_request_duration_bucket[5m])) by (service_name, le))`
     - Throughput: `sum(rate(http_server_request_duration_count[5m])) by (service_name)`
   - Update service cards: Keep existing MudCard layout, change data source only
   - Test: Dashboard shows correct metrics from Prometheus

5. **Add Prometheus health check to Dashboard** (1 day)
   - Add Prometheus readiness check in Dashboard startup
   - Verify Prometheus `/api/v1/query` endpoint is reachable
   - Fail gracefully if Prometheus unavailable (show warning banner on observability pages)

6. **Update documentation** (1 day)
   - Update `Guide-For-Admins-and-Tenants.md` section 6.5: Observability (Admin)
   - Document: Prometheus replaces SigNoz for metrics, internal-only access, 7-day retention
   - Remove SigNoz references from metrics sections

**Acceptance criteria**:

- ✅ Prometheus deployed in both dev and prod compose files
- ✅ All 5 services export metrics to Prometheus via OTLP
- ✅ Dashboard ObservabilityOverview page shows metrics from Prometheus (no SigNoz dependency)
- ✅ Prometheus targets page shows all services + exporters healthy
- ✅ No regressions: Existing E2E tests pass (DashboardMetricsSmoke, HealthEndpointsE2E)
- ✅ Prometheus uses <512MB memory (verified via `docker stats`)
- ✅ Documentation updated

**Tests**:

```csharp
// TansuCloud.E2E.Tests/PrometheusMetricsE2E.cs
[Fact]
public async Task Prometheus_Receives_Metrics_From_All_Services()
{
    var services = new[] { "tansu-gateway", "tansu-identity", "tansu-dashboard", "tansu-db", "tansu-storage" };
    
    foreach (var service in services)
    {
        var query = $"up{{service_name=\"{service}\"}}";
        var result = await PrometheusQuery(query);
        Assert.Contains(result.Data.Result, m => m.Value[1] == "1"); // UP
    }
}

[Fact]
public async Task Dashboard_Observability_Overview_Shows_Prometheus_Metrics()
{
    await LoginAsAdmin();
    await NavigateTo("/admin/observability");
    
    // Wait for service cards to load
    await WaitForElement("[data-testid='service-card-gateway']");
    
    // Verify error rate, latency, throughput displayed
    var errorRate = await GetText("[data-testid='service-card-gateway'] [data-testid='error-rate']");
    Assert.NotEmpty(errorRate);
}
```

---

#### Phase 2: Tempo Traces Migration (2-3 weeks)

**Goal**: Replace SigNoz traces with Tempo, integrate with TansuCloud Storage service.

**Subtasks**:

1. **Deploy Tempo with Storage service backend** (3 days)
   - Add `tempo` service to `docker-compose.yml` and `docker-compose.prod.yml`
   - Image: `grafana/tempo:latest` (dev), `grafana/tempo:2.6.0` (prod)
   - Config: `dev/tempo-config.yml` with:
     - Receiver: OTLP gRPC (4317) and HTTP (4318)
     - Storage: TansuCloud Storage service backend
       - Backend type: `s3` (Storage service is S3-compatible)
       - Endpoint: `http://storage:8080` (internal Docker network)
       - Bucket: `observability-tempo`
       - Prefix: `/traces/`
       - Access: Use internal service-to-service auth (no public S3 credentials needed)
     - Query: TraceQL enabled
   - Resources: `memory: 1G`, `cpus: 1`
   - Health check: `wget --spider -q http://localhost:3200/ready`
   - Network: `tansucloud-network` internal only

2. **Create observability bucket in Storage service** (1 day)
   - Update Storage service provisioning to auto-create `observability-tempo` bucket on startup
   - Set lifecycle: 7-day retention (delete traces older than 7 days)
   - Internal-only: No tenant access (admin-owned bucket)

3. **Update all services to export traces to Tempo** (1 day)
   - Change `OpenTelemetry__Otlp__Endpoint` to `http://tempo:4317` (traces only, metrics stay in Prometheus)
   - OR: Use OpenTelemetry Collector to route traces to Tempo, metrics to Prometheus (preferred)
   - Verify: Traces appear in Tempo query API

4. **Deploy OpenTelemetry Collector (optional but recommended)** (2 days)
   - Add `otel-collector` service to route telemetry:
     - Receivers: OTLP gRPC (4317), OTLP HTTP (4318)
     - Exporters:
       - Metrics → Prometheus OTLP receiver
       - Traces → Tempo OTLP receiver
       - Logs → Loki (Phase 3)
     - Processors: Batch, resource detection, sampling (configurable)
   - Image: `otel/opentelemetry-collector-contrib:latest` (dev), `otel/opentelemetry-collector-contrib:0.111.0` (prod)
   - Config: `dev/otel-collector-config.yaml`
   - All services point to collector: `http://otel-collector:4317`
   - Resources: `memory: 512M`, `cpus: 0.5`

5. **Implement ITempoTracesService** (4 days)
   - New interface: `ITempoTracesService` in `TansuCloud.Dashboard/Observability/Tempo/`
   - Methods:
     - `Task<TempoTraceSearchResult> SearchTracesAsync(TempoSearchFilters filters)`
     - `Task<TempoTrace> GetTraceByIdAsync(string traceId)`
     - `Task<List<string>> GetServicesAsync()`
   - Filters: Service, min/max duration, status (OK/ERROR), time range, TraceQL query
   - Implementation: TraceQL queries via Tempo `/api/search` and `/api/traces/{traceId}` endpoints
   - DTOs: `TempoTrace`, `TempoSpan`, `TempoSearchFilters`, `TempoSearchResult`
   - Cache: HybridCache with 5-10 min TTL for traces (immutable once complete)

6. **Update Traces.razor page for Tempo** (3 days)
   - Replace `ISigNozTracesService` with `ITempoTracesService`
   - Update UI: Keep existing MudBlazor layout (search filters, results table, span tree dialog)
   - TraceQL support: Add "Advanced Query" text box for custom TraceQL (optional, for power users)
   - Test: Trace search, trace detail view, span attributes, waterfall visualization (tree view)

7. **Add Tempo health check to Dashboard** (1 day)
   - Verify Tempo `/ready` endpoint before rendering Traces page
   - Graceful degradation: Show error banner if Tempo unavailable

8. **Update documentation** (1 day)
   - Update `Guide-For-Admins-and-Tenants.md` section 6.6: Observability Traces
   - Document: Tempo replaces SigNoz for traces, Storage service backend, 7-day retention

**Acceptance criteria**:

- ✅ Tempo deployed with TansuCloud Storage service backend
- ✅ All services export traces to Tempo (via collector or direct)
- ✅ Dashboard Traces page shows traces from Tempo (no SigNoz dependency)
- ✅ Trace search filters work (service, duration, status, time range)
- ✅ Trace detail view shows span tree with attributes
- ✅ Storage service contains trace data in `observability-tempo` bucket
- ✅ Tempo uses <1GB memory (verified via `docker stats`)
- ✅ No regressions: Existing E2E tests pass
- ✅ Documentation updated

**Tests**:

```csharp
// TansuCloud.E2E.Tests/TempoTracesE2E.cs
[Fact]
public async Task Tempo_Receives_Traces_From_All_Services()
{
    // Trigger trace by making a request through Gateway → Database
    var response = await HttpClient.GetAsync("/db/health/ready");
    response.EnsureSuccessStatusCode();
    
    await Task.Delay(5000); // Wait for trace to be ingested
    
    // Query Tempo for traces from tansu-gateway
    var traces = await TempoSearchTraces(service: "tansu-gateway", lookbackMinutes: 5);
    Assert.NotEmpty(traces);
}

[Fact]
public async Task Dashboard_Traces_Page_Shows_Tempo_Data()
{
    await LoginAsAdmin();
    await NavigateTo("/admin/observability/traces");
    
    // Wait for traces table to load
    await WaitForElement("[data-testid='traces-table']");
    
    // Verify at least one trace displayed
    var traceCount = await GetElementCount("[data-testid='trace-row']");
    Assert.True(traceCount > 0);
}
```

---

#### Phase 3: Loki Logs Migration (2-3 weeks)

**Goal**: Replace SigNoz logs with Loki, integrate with TansuCloud Storage service.

**Subtasks**:

1. **Deploy Loki with Storage service backend** (3 days)
   - Add `loki` service to `docker-compose.yml` and `docker-compose.prod.yml`
   - Image: `grafana/loki:latest` (dev), `grafana/loki:3.2.0` (prod)
   - Config: `dev/loki-config.yml` with:
     - Receiver: OTLP HTTP (4318)
     - Storage:
       - Chunks: TansuCloud Storage service (S3-compatible)
         - Endpoint: `http://storage:8080`
         - Bucket: `observability-loki`
         - Prefix: `/chunks/`
       - Index: Local BoltDB (`/loki/index` volume) for fast queries
     - Retention: 7 days (delete chunks older than 7 days)
     - Limits: `max_query_series: 500`, `max_query_lookback: 7d`
   - Resources: `memory: 1G`, `cpus: 1`
   - Health check: `wget --spider -q http://localhost:3100/ready`
   - Network: `tansucloud-network` internal only

2. **Create observability bucket for Loki** (1 day)
   - Update Storage service provisioning to auto-create `observability-loki` bucket
   - Set lifecycle: 7-day retention (delete log chunks older than 7 days)
   - Internal-only: No tenant access

3. **Update OpenTelemetry Collector to route logs to Loki** (1 day)
   - Update `otel-collector-config.yaml`:
     - Receivers: OTLP (existing)
     - Exporters: Add Loki OTLP exporter (`otlphttp/loki` → `http://loki:4318`)
     - Pipelines: `logs` pipeline → Loki exporter
   - Verify: Logs appear in Loki via LogQL queries

4. **Implement ILokiLogsService** (4 days)
   - New interface: `ILokiLogsService` in `TansuCloud.Dashboard/Observability/Loki/`
   - Methods:
     - `Task<LokiLogSearchResult> SearchLogsAsync(LokiSearchFilters filters)`
     - `Task<LokiLog> GetLogByIdAsync(string id)` (if Loki supports, else remove)
     - `Task<List<string>> GetLogFieldsAsync()` (labels)
     - `Task<List<string>> GetServicesAsync()` (unique service names)
   - Filters: Service, severity (TRACE/DEBUG/INFO/WARN/ERROR/FATAL), time range, full-text search
   - Implementation: LogQL queries via Loki `/loki/api/v1/query_range` endpoint
   - DTOs: `LokiLog`, `LokiStream`, `LokiSearchFilters`, `LokiSearchResult`
   - Cache: HybridCache with 1-2 min TTL (logs change frequently)

5. **Update Logs.razor page for Loki** (3 days)
   - Replace `ISigNozLogsService` with `ILokiLogsService`
   - Update UI: Keep existing MudBlazor layout (search filters, results table, detail dialog)
   - LogQL support: Add "Advanced Query" text box for custom LogQL (optional)
   - Test: Log search, log detail view, trace correlation, pagination

6. **Add Loki health check to Dashboard** (1 day)
   - Verify Loki `/ready` endpoint before rendering Logs page
   - Graceful degradation: Show error banner if Loki unavailable

7. **Update documentation** (1 day)
   - Update `Guide-For-Admins-and-Tenants.md` section 6.7: Observability Logs
   - Document: Loki replaces SigNoz for logs, Storage service backend, 7-day retention

**Acceptance criteria**:

- ✅ Loki deployed with TansuCloud Storage service backend
- ✅ All services export logs to Loki (via OpenTelemetry Collector)
- ✅ Dashboard Logs page shows logs from Loki (no SigNoz dependency)
- ✅ Log search filters work (service, severity, time range, full-text)
- ✅ Log detail view shows full message, attributes, trace correlation
- ✅ Storage service contains log chunks in `observability-loki` bucket
- ✅ Loki uses <1GB memory (verified via `docker stats`)
- ✅ No regressions: Existing E2E tests pass
- ✅ Documentation updated

**Tests**:

```csharp
// TansuCloud.E2E.Tests/LokiLogsE2E.cs
[Fact]
public async Task Loki_Receives_Logs_From_All_Services()
{
    // Wait for logs to be ingested
    await Task.Delay(5000);
    
    // Query Loki for logs from tansu-gateway
    var logs = await LokiSearchLogs(service: "tansu-gateway", lookbackMinutes: 5);
    Assert.NotEmpty(logs);
}

[Fact]
public async Task Dashboard_Logs_Page_Shows_Loki_Data()
{
    await LoginAsAdmin();
    await NavigateTo("/admin/observability/logs");
    
    // Wait for logs table to load
    await WaitForElement("[data-testid='logs-table']");
    
    // Verify at least one log displayed
    var logCount = await GetElementCount("[data-testid='log-row']");
    Assert.True(logCount > 0);
}
```

---

#### Phase 4: Grafana Deployment and Dashboard Integration (1-2 weeks)

**Goal**: Deploy Grafana as optional advanced UI, connect to Prometheus/Tempo/Loki data sources.

**Subtasks**:

1. **Provision Grafana database via TansuCloud.Database service** (1 day)
   - **Leverage existing Database service provisioning API** (no manual SQL)
   - Call provisioning endpoint during stack initialization:
     ```bash
     curl -X POST http://gateway:8080/db/api/provisioning/databases \
       -H "X-Provision-Key: ${PROVISION_KEY}" \
       -H "Content-Type: application/json" \
       -d '{"databaseName": "grafana", "purpose": "observability", "owner": "platform"}'
     ```
   - Database service creates `grafana` database in `tansu-postgres` automatically
   - Alternative: Add init container in compose that calls provisioning API before Grafana starts
   - Verify: `docker exec tansu-postgres psql -U postgres -l | grep grafana`
   - Note: Grafana auto-creates schema (tables, indexes) on first connection - no migrations needed

2. **Deploy Grafana with PostgreSQL backend** (2 days)
   - Add `grafana` service to `docker-compose.yml` and `docker-compose.prod.yml`
   - Image: `grafana/grafana:latest` (dev), `grafana/grafana:11.3.1` (prod)
   - Config:
     - Database: PostgreSQL (reuse `tansu-postgres`, `grafana` database from subtask 1)
       - `GF_DATABASE_TYPE=postgres`
       - `GF_DATABASE_HOST=postgres:5432`
       - `GF_DATABASE_NAME=grafana`
       - `GF_DATABASE_USER=${POSTGRES_USER}`
       - `GF_DATABASE_PASSWORD=${POSTGRES_PASSWORD}`
     - Security:
       - `GF_SECURITY_ADMIN_USER=admin`
       - `GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD}` (from .env)
       - `GF_AUTH_DISABLE_LOGIN_FORM=false`
       - `GF_AUTH_ANONYMOUS_ENABLED=true` (for iframe embedding)
       - `GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer` (read-only for embedded dashboards)
     - Server:
       - `GF_SERVER_ROOT_URL=http://127.0.0.1:8080/grafana` (dev) or `${PUBLIC_BASE_URL}/grafana` (prod)
       - `GF_SERVER_SERVE_FROM_SUB_PATH=true`
   - Resources: `memory: 256M`, `cpus: 0.5`
   - Health check: `wget --spider -q http://localhost:3000/api/health`
   - Network: `tansucloud-network` internal only (no exposed ports by default)
   - Depends on: `postgres`, `prometheus`, `tempo`, `loki`

3. **Configure Grafana data sources** (2 days)
   - Provisioning config: `dev/grafana/provisioning/datasources/datasources.yml`
   - Data sources:
     - Prometheus: `http://prometheus:9090` (default)
     - Tempo: `http://tempo:3200`
     - Loki: `http://loki:3100`
   - All data sources: `access: proxy` (Grafana server-side queries, not browser)
   - Test: Query Grafana API to verify data sources are healthy

4. **Create default Grafana dashboards** (3 days)
   - Provisioning config: `dev/grafana/provisioning/dashboards/dashboards.yml`
   - Dashboards:
     - **TansuCloud Overview**: Service health, request rates, error rates, latency percentiles (from Prometheus)
     - **Service Details**: Per-service dashboard with RED metrics (Rate, Errors, Duration)
     - **Traces Explorer**: Tempo traces with filters (service, duration, status)
     - **Logs Browser**: Loki logs with filters (service, severity, time range)
     - **Infrastructure**: PostgreSQL, Redis, PgCat metrics from exporters
   - Dashboard JSON: Store in `dev/grafana/dashboards/*.json`
   - Auto-import on Grafana startup

5. **Embed Grafana dashboards in TansuCloud Admin Dashboard** (3 days)
   - **Goal**: Show Grafana dashboards directly in Admin UI (no separate login/navigation)
   - **Implementation**:
     - Update `TansuCloud.Dashboard/Pages/Admin/ObservabilityOverview.razor`:
       - Add `<iframe>` component pointing to `http://grafana:3000/d/{dashboardId}?kiosk&theme=light`
       - Kiosk mode hides Grafana UI chrome (menus, navigation) for seamless embedding
       - Dashboard loads via Grafana anonymous auth (configured in subtask 2)
     - New pages with embedded dashboards:
       - `/admin/observability` → "TansuCloud Overview" dashboard
       - `/admin/observability/services` → "Service Details" dashboard with `var-service={serviceName}` filter
       - `/admin/observability/infrastructure` → "Infrastructure Metrics" dashboard
     - Add `GrafanaEmbedService` helper class:
       - Generates iframe URLs with correct dashboard IDs and parameters
       - Handles dashboard discovery via Grafana API (`GET /api/search?tag=tansucloud`)
       - Fallback: Show link to Grafana UI if iframe fails to load
   - **Security**: Grafana on internal network, only Dashboard backend can access (no direct browser → Grafana)
   - **UX**: Dashboard pages feel native (no visible iframe borders, responsive height)
   - **Alternative considered**: Query Prometheus/Tempo/Loki APIs directly and render with MudBlazor charts
     - Deferred to future (more work, but better control and performance)
     - Current iframe approach provides immediate value with minimal code

6. **Optional: Gateway reverse proxy for Grafana** (2 days)
   - **Default posture**: Grafana UI NOT exposed (internal Docker network only)
   - **Optional**: Advanced users can enable Grafana UI access
   - Implementation:
     - Add `/grafana/*` route in Gateway YARP config
     - Forward to `http://grafana:3000`
     - Require authentication: `admin.full` scope or `Admin` role
     - Set `X-Forwarded-Prefix: /grafana` header
   - Documented in `Guide-For-Admins-and-Tenants.md` section "Advanced: Grafana UI Access"
   - Environment variable gate: `GRAFANA_UI_ENABLED=false` (default), set to `true` to enable

6. **Update documentation** (1 day)
   - Update `Guide-For-Admins-and-Tenants.md`:
     - Section 6.5.2: Grafana Dashboards (optional, for advanced users)
     - Document: Internal-only by default, how to enable `/grafana` route, default credentials
   - Update `Easy-installation.md`: Grafana setup steps (optional)

**Acceptance criteria**:

- ✅ Grafana deployed with PostgreSQL backend
- ✅ Grafana connected to Prometheus, Tempo, Loki data sources (all healthy)
- ✅ Default dashboards imported and functional (TansuCloud Overview, Service Details, etc.)
- ✅ Grafana UI NOT exposed to host by default (internal Docker network only)
- ✅ Optional Gateway reverse proxy documented (disabled by default)
- ✅ Grafana uses <256MB memory (verified via `docker stats`)
- ✅ Documentation updated

**Tests**:

```csharp
// TansuCloud.E2E.Tests/GrafanaIntegrationE2E.cs
[Fact]
public async Task Grafana_Connects_To_Prometheus_Tempo_Loki()
{
    // Query Grafana API for data sources
    var dataSources = await GrafanaApi_GetDataSources();
    
    Assert.Contains(dataSources, ds => ds.Type == "prometheus" && ds.Url.Contains("prometheus:9090"));
    Assert.Contains(dataSources, ds => ds.Type == "tempo" && ds.Url.Contains("tempo:3200"));
    Assert.Contains(dataSources, ds => ds.Type == "loki" && ds.Url.Contains("loki:3100"));
}

[Fact]
public async Task Grafana_Default_Dashboards_Imported()
{
    var dashboards = await GrafanaApi_GetDashboards();
    
    Assert.Contains(dashboards, d => d.Title == "TansuCloud Overview");
    Assert.Contains(dashboards, d => d.Title == "Service Details");
}

[Fact]
public async Task Dashboard_Embeds_Grafana_Iframe()
{
    await Page.GotoAsync($"{BaseUrl}/admin/observability");
    
    // Check iframe element exists
    var iframe = Page.Locator("iframe[src*='grafana']");
    await Expect(iframe).ToBeVisibleAsync();
    
    // Verify iframe src contains kiosk mode
    var src = await iframe.GetAttributeAsync("src");
    Assert.Contains("kiosk", src);
    Assert.Contains("/d/", src); // Dashboard UID
}

[Fact]
public async Task Dashboard_Service_Page_Shows_Grafana_With_Filter()
{
    await Page.GotoAsync($"{BaseUrl}/admin/observability/services?service=gateway");
    
    // Check iframe has var-service filter
    var iframe = Page.Locator("iframe[src*='grafana']");
    var src = await iframe.GetAttributeAsync("src");
    Assert.Contains("var-service=gateway", src);
}
```

---

#### Phase 5: Retention and Sampling Management UI (1 week)

**Goal**: Admin Dashboard UI for managing retention policies and sampling rates.

**Subtasks**:

1. **Create ObservabilitySettings model** (1 day)
   - New table: `ObservabilitySettings` in `tansu_identity` database (Gateway EF context)
   - Columns:
     - `Id` (int, PK)
     - `Component` (string: "prometheus", "tempo", "loki") UNIQUE
     - `RetentionDays` (int, default 7)
     - `SamplingPercent` (int, default 100, 0-100 range)
     - `Enabled` (bool, default true)
     - `UpdatedAt` (DateTime)
     - `UpdatedBy` (string, admin user email)
   - EF migration: Add table and seed defaults

2. **Implement ObservabilitySettings API** (2 days)
   - Admin API endpoints in Gateway:
     - `GET /api/admin/observability/settings` → List all settings
     - `GET /api/admin/observability/settings/{component}` → Get single setting
     - `PUT /api/admin/observability/settings/{component}` → Update setting
   - Authorization: `admin.full` scope required
   - Audit logging: Log all setting changes with old/new values
   - Apply settings: Call Prometheus/Tempo/Loki admin APIs to update retention/sampling

3. **Update Configuration.razor page** (2 days)
   - Move from `/admin/observability-config` to `/admin/observability/config`
   - UI:
     - MudCard per component (Prometheus, Tempo, Loki)
     - MudSwitch: Enabled/Disabled toggle
     - MudNumericField: Retention Days (1-365 range)
     - MudSlider: Sampling Percent (0-100 range, step 1)
     - MudButton: "Save Changes" per component
   - Validation: Retention 1-365 days, Sampling 0-100%
   - Feedback: MudSnackbar success/error messages
   - Test: Change settings, verify persisted in database and applied to components

4. **Implement settings application logic** (2 days)
   - Background worker in Gateway: `ObservabilitySettingsApplier`
   - On settings change:
     - Prometheus: Update retention via Prometheus admin API (if available) or require container restart with new `--storage.tsdb.retention.time` flag
     - Tempo: Update compactor retention via Tempo runtime config (reload config file)
     - Loki: Update retention via Loki runtime config (reload config file)
     - Sampling: Update OpenTelemetry Collector sampling processor config (reload collector)
   - Fallback: If admin APIs unavailable, log warning and require container restart

5. **Add retention enforcement** (1 day)
   - Prometheus: `--storage.tsdb.retention.time` flag set from `ObservabilitySettings` table on startup
   - Tempo: Compactor deletes blocks older than retention days
   - Loki: Retention enforcer deletes chunks older than retention days
   - Storage service: Lifecycle policies on `observability-tempo` and `observability-loki` buckets

6. **Update documentation** (1 day)
   - Update `Guide-For-Admins-and-Tenants.md` section 6.5.3: Retention and Sampling
   - Document: Default settings, how to change, restart requirements (if any)

**Acceptance criteria**:

- ✅ ObservabilitySettings table exists in `tansu_identity` database
- ✅ Admin API endpoints functional for settings CRUD
- ✅ Dashboard Configuration page allows editing retention and sampling
- ✅ Settings changes applied to Prometheus, Tempo, Loki (verified via retention enforcement)
- ✅ Audit logging captures setting changes
- ✅ Documentation updated

**Tests**:

```csharp
// TansuCloud.E2E.Tests/ObservabilitySettingsE2E.cs
[Fact]
public async Task Admin_Can_Update_Retention_Settings()
{
    await LoginAsAdmin();
    await NavigateTo("/admin/observability/config");
    
    // Change Prometheus retention to 14 days
    await SetNumericField("[data-testid='prometheus-retention']", 14);
    await ClickButton("[data-testid='prometheus-save']");
    
    // Verify success message
    await WaitForElement("[data-testid='snackbar-success']");
    
    // Verify persisted in database
    var setting = await DbQuery<ObservabilitySetting>("SELECT * FROM ObservabilitySettings WHERE Component = 'prometheus'");
    Assert.Equal(14, setting.RetentionDays);
}

[Fact]
public async Task Sampling_Changes_Applied_To_Collector()
{
    await LoginAsAdmin();
    await UpdateSampling("tempo", 50); // 50% trace sampling
    
    // Verify collector config updated
    var collectorConfig = await ReadCollectorConfig();
    Assert.Contains("sampling_percentage: 50", collectorConfig);
}
```

---

#### Phase 6: Complete SigNoz Removal and Validation (1 week)

**Goal**: Remove **ALL** SigNoz code, dependencies, and references from codebase and documentation. After this phase, the repository must have zero SigNoz footprint except in historical task documentation (Tasks-M*.md files).

**Critical Requirements**:
- ✅ **Zero SigNoz code** in production codebase (services, models, config)
- ✅ **Zero SigNoz references** in user-facing documentation (Guide, Easy-installation, Architecture)
- ✅ **Zero SigNoz services** in docker-compose files
- ✅ **Only exception**: Tasks-M*.md files may retain SigNoz mentions for historical development documentation

**Subtasks**:

1. **Remove SigNoz services from compose files** (1 day)
   - **Delete from `docker-compose.yml` and `docker-compose.prod.yml`**:
     - `signoz` service (main UI service)
     - `signoz-otel-collector` service (collector with ClickHouse exporter)
     - `clickhouse` service (SigNoz storage backend)
     - `zookeeper` service (ClickHouse dependency)
     - `signoz-init` service (initialization container)
     - `signoz-schema-migrator-sync` service
     - `signoz-schema-migrator-async` service
     - `clickhouse-prepatch` service
     - `clickhouse-compat-init` service
   - **Delete SigNoz-specific volumes**:
     - `signoz-data`
     - `signoz-clickhouse-data`
     - `signoz-zookeeper-data`
   - **Delete SigNoz config files**:
     - `dev/signoz-otel-collector-config.yaml`
     - `dev/clickhouse/*.xml` (all ClickHouse config files)
     - `SigNoz/governance.defaults.json` (if exists)
     - Any `SigNoz/` directory contents
   - **Remove SigNoz environment variables** from `.env` template:
     - Any `SIGNOZ_*` prefixed variables
     - Any `CLICKHOUSE_*` prefixed variables (if SigNoz-specific)
   - **Validation**: Run `docker compose config` and verify no SigNoz services appear
   - **Validation**: Search compose files for "signoz" (case-insensitive) and ensure zero matches

2. **Remove SigNoz code from Dashboard service** (2 days)
   - **Delete entire SigNoz directory**: `TansuCloud.Dashboard/Observability/SigNoz/`
     - `ISigNozAuthenticationService.cs`
     - `SigNozAuthenticationService.cs`
     - `ISigNozQueryService.cs`
     - `SigNozQueryService.cs`
     - `ISigNozTracesService.cs`
     - `SigNozTracesService.cs`
     - `ISigNozLogsService.cs`
     - `SigNozLogsService.cs`
     - All SigNoz DTOs: `SigNozServiceDto.cs`, `SigNozTraceDto.cs`, `SigNozLogDto.cs`, etc.
     - All SigNoz options classes: `SigNozQueryOptions.cs`, `SigNozAuthOptions.cs`
   - **Remove from `TansuCloud.Dashboard/Program.cs`**:
     - SigNoz service registrations (`AddHttpClient<ISigNozQueryService>`, etc.)
     - SigNoz configuration binding (`builder.Services.Configure<SigNozQueryOptions>`)
     - SigNoz admin API endpoints (search for `/api/admin/observability/*` using SigNoz services)
   - **Remove from `appsettings*.json`**:
     - `SigNozQuery` configuration section
     - `SigNozAuth` configuration section
     - Any SigNoz-related settings
   - **Update Razor pages**:
     - Remove `@inject ISigNozQueryService` (replaced with IPrometheusQueryService, ITempoTracesService, ILokiLogsService)
     - Update `ObservabilityOverview.razor` to use PGTL services or Grafana iframes
   - **Validation**: Search Dashboard project for "SigNoz" (case-insensitive) and ensure zero matches

3. **Remove SigNoz code from other services** (1 day)
   - **Gateway service**:
     - Remove any SigNoz-specific YARP routes (if any `/signoz/*` routes exist)
     - Remove SigNoz admin API proxy endpoints (if any)
     - Search Gateway project for "SigNoz" and remove all references
   - **Identity, Database, Storage, Telemetry services**:
     - These services should not have SigNoz code (they only export OTLP, which remains)
     - Verify by searching each project for "SigNoz" (should be zero matches)
   - **Shared libraries** (if any):
     - Check `TansuCloud.Observability.Shared/` or similar for SigNoz-specific code
     - Remove any SigNoz DTOs or helper classes
   - **Unit test projects**:
     - Delete SigNoz-specific unit tests: `*SigNoz*.cs` test files
     - Keep tests for PGTL services (Prometheus, Tempo, Loki)
   - **E2E test projects**:
     - Delete SigNoz-specific E2E tests: `SigNoz*E2E.cs` files
     - Keep observability E2E tests that now use PGTL stack
   - **Validation**: Run full-repo search for "SigNoz" excluding Tasks-M*.md files, ensure zero code matches

4. **Update all user-facing documentation** (2 days)
   - **`Architecture.md`**:
     - Section: Observability Architecture
       - **Remove**: SigNoz architecture diagram, SigNoz component descriptions
       - **Add**: PGTL stack architecture diagram (Prometheus, Grafana, Tempo, Loki)
       - **Add**: PGTL component descriptions, resource requirements, data flow
     - Section: Telemetry Pipeline
       - Update: Services → OpenTelemetry Collector → PGTL stack (not SigNoz)
     - **Validation**: Search for "SigNoz" (case-insensitive), ensure zero matches
   
   - **`Guide-For-Admins-and-Tenants.md`**:
     - **Section 6: Observability** (complete rewrite):
       - 6.1: Observability Overview (PGTL stack introduction)
       - 6.2: Admin Dashboard Observability Pages (embedded Grafana)
       - 6.3: Metrics (Prometheus) - Query examples, retention management
       - 6.4: Traces (Tempo) - Trace search, correlation, retention
       - 6.5: Logs (Loki) - Log search, filtering, retention
       - 6.6: Grafana Dashboards (optional advanced access)
       - 6.7: Retention and Sampling Management (Admin UI)
       - 6.8: Advanced: Enabling Grafana UI Access (for 1% power users)
     - **Remove all SigNoz sections**:
       - Delete: "Accessing SigNoz UI"
       - Delete: "SigNoz Authentication"
       - Delete: "SigNoz Governance Settings"
       - Delete: Any SigNoz API references
     - **Validation**: Search for "SigNoz" (case-insensitive), ensure zero matches
   
   - **`Easy-installation.md`**:
     - Section: Observability Stack
       - **Remove**: SigNoz setup instructions, SigNoz profile steps
       - **Add**: PGTL stack deployment (Prometheus, Tempo, Loki deployed by default)
       - **Add**: Optional Grafana UI access steps
     - Section: Environment Variables
       - **Remove**: `SIGNOZ_*`, `CLICKHOUSE_*` variables
       - **Add**: `GRAFANA_ADMIN_PASSWORD`, PGTL retention settings
     - **Validation**: Search for "SigNoz" (case-insensitive), ensure zero matches
   
   - **`README.md`** (if it mentions observability):
     - Update: Observability powered by PGTL stack (not SigNoz)
     - **Validation**: Search for "SigNoz", ensure zero matches

4. **Full E2E test validation** (1 day)
   - Run all E2E tests: `dotnet test tests/TansuCloud.E2E.Tests/ -c Debug`
   - Expected results: All tests pass (170/170)
   - New tests: PrometheusMetricsE2E, TempoTracesE2E, LokiLogsE2E, GrafanaIntegrationE2E, ObservabilitySettingsE2E
   - **Validation**: Search all docs for "SigNoz" (case-insensitive), verify only Tasks-M*.md matches

5. **Comprehensive validation searches** (1 day)
   - **Code validation**:
     - `grep -ri "signoz" docker-compose.yml docker-compose.prod.yml` → Expected: 0 matches
     - `grep -ri "signoz" TansuCloud.Dashboard/` → Expected: 0 matches (excluding Tasks-M*.md)
     - `grep -ri "signoz" TansuCloud.Gateway/` → Expected: 0 matches
     - `grep -ri "signoz" tests/` → Expected: 0 code matches (git history references OK)
     - `docker compose config | grep -i signoz` → Expected: 0 matches
     - `docker compose -f docker-compose.prod.yml config | grep -i signoz` → Expected: 0 matches
   
   - **Documentation validation**:
     - `grep -ri "signoz" Architecture.md` → Expected: 0 matches
     - `grep -ri "signoz" Guide-For-Admins-and-Tenants.md` → Expected: 0 matches
     - `grep -ri "signoz" Easy-installation.md` → Expected: 0 matches
     - `grep -ri "signoz" README.md` → Expected: 0 matches
     - `grep -ri "signoz" docs/*.md` → Expected: 0 matches (except `docs/archived/SigNoz-*.md`)
   
   - **Exception validation**:
     - SigNoz mentions allowed ONLY in `Tasks-M*.md` files (for historical dev documentation)
     - Verify Tasks-M3.md Task 19 and Tasks-M4.md Task 36 contain historical notes sections
   
   - **PGTL stack health checks**:
     - All services healthy: `docker compose ps` shows all "Up (healthy)"
     - Prometheus: `curl http://127.0.0.1:9090/-/healthy` = 200 OK
     - Grafana: `curl http://127.0.0.1:3000/api/health` = 200 OK
     - Tempo: `curl http://127.0.0.1:3200/ready` = 200 OK
     - Loki: `curl http://127.0.0.1:3100/ready` = 200 OK
     - Grafana database provisioned via TansuCloud.Database service
   
   - **Full E2E test validation**:
     - Command: `dotnet test tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj -c Debug`
     - Expected: 200+ tests passing (all existing + 30 new PGTL tests)
     - Zero SigNoz-related test failures
     - New PGTL tests (Prometheus, Tempo, Loki, Grafana) all passing
     - Dashboard observability pages rendering correctly
     - Embedded Grafana iframe loading with anonymous auth
   
   - Fix any regressions

6. **Resource usage validation and migration guide** (1 day)
   - **Docker stats baseline**:
     - Start full stack: `docker compose up -d --build`
     - Wait 5 minutes for services to stabilize
     - Measure memory usage: `docker stats --no-stream --format "table {{.Container}}\t{{.MemUsage}}\t{{.CPUPerc}}"`
     - Expected totals:
       - Prometheus: <512MB (target: 256-512MB)
       - Grafana: <256MB (target: 128-256MB)
       - Tempo: <1GB (target: 512MB-1GB)
       - Loki: <1GB (target: 512MB-1GB)
       - OpenTelemetry Collector: <512MB (target: 256-512MB)
       - **Total PGTL stack: <4GB** (vs SigNoz 8GB = 50% reduction)
     - Document actual values in migration guide
   
   - **Create migration guide**: `docs/PGTL-Migration-Guide.md`
     - Section 1: Migration Overview (why migrate from SigNoz, PGTL benefits)
     - Section 2: Pre-Migration Checklist (backup existing data, dependencies)
     - Section 3: Step-by-Step Migration:
       1. Stop SigNoz containers (no data migration needed - fresh start)
       2. Pull latest TansuCloud images (with PGTL support)
       3. Update `.env` with new variables (Grafana admin password, retention settings)
       4. Run `docker compose up -d --build` (PGTL stack auto-deploys)
       5. Verify: Check Dashboard observability pages, Grafana dashboards
       6. Optional: Enable Grafana UI access via Gateway (for 1% power users)
     - Section 4: Validation Steps:
       - All grep searches (code + docs) showing zero SigNoz matches
       - E2E tests passing
       - Resource usage under targets
       - Health checks passing
     - Section 5: Rollback Plan (emergency revert if needed)
     - Section 6: Post-Migration Tasks (archive SigNoz configs, update monitoring alerts)
     - Downtime estimate: ~5-10 minutes (no data loss, SigNoz data discarded)
     - Rollback note: Not needed (SigNoz compatibility broken upstream, no working baseline)

**Acceptance criteria**:

- ✅ All SigNoz code and config removed from repository
- ✅ All documentation updated to reflect PGTL stack
- ✅ All E2E tests passing (100% pass rate)
- ✅ PGTL stack uses <4GB memory total (verified via `docker stats`)
- ✅ Migration guide created for existing deployments
- ✅ No broken links or references to SigNoz in docs

**Tests**:

```csharp
// TansuCloud.E2E.Tests/PGTLStackE2E.cs
[Fact]
public async Task PGTL_Stack_All_Components_Healthy()
{
    // Verify all PGTL components are healthy
    var prometheus = await HttpClient.GetAsync("http://prometheus:9090/-/healthy");
    var tempo = await HttpClient.GetAsync("http://tempo:3200/ready");
    var loki = await HttpClient.GetAsync("http://loki:3100/ready");
    var grafana = await HttpClient.GetAsync("http://grafana:3000/api/health");
    
    prometheus.EnsureSuccessStatusCode();
    tempo.EnsureSuccessStatusCode();
    loki.EnsureSuccessStatusCode();
    grafana.EnsureSuccessStatusCode();
}

[Fact]
public async Task PGTL_Stack_Memory_Usage_Under_4GB()
{
    var stats = await DockerStats();
    
    var prometheusMemory = stats["prometheus"].MemoryUsageMB;
    var tempoMemory = stats["tempo"].MemoryUsageMB;
    var lokiMemory = stats["loki"].MemoryUsageMB;
    var grafanaMemory = stats["grafana"].MemoryUsageMB;
    var collectorMemory = stats["otel-collector"].MemoryUsageMB;
    
    var total = prometheusMemory + tempoMemory + lokiMemory + grafanaMemory + collectorMemory;
    
    Assert.True(total < 4096, $"PGTL stack uses {total}MB (expected <4GB)");
}
```

---

#### Overall Task 47 Acceptance Criteria

**Functional Requirements:**

- ✅ Prometheus deployed for metrics (7-day retention, <512MB memory)
- ✅ Grafana deployed for visualization (PostgreSQL backend provisioned via TansuCloud.Database, <256MB memory)
- ✅ Tempo deployed for traces (TansuCloud Storage backend, 7-day retention, <1GB memory)
- ✅ Loki deployed for logs (TansuCloud Storage backend, 7-day retention, <1GB memory)
- ✅ OpenTelemetry Collector routes telemetry to PGTL components (<512MB memory)
- ✅ Dashboard Admin UI updated: New IPrometheusQueryService, ITempoTracesService, ILokiLogsService
- ✅ Dashboard observability pages functional: /admin/observability (metrics), /admin/observability/traces, /admin/observability/logs
- ✅ Grafana dashboards embedded in TansuCloud Admin UI via iframe (kiosk mode, anonymous viewer auth)
- ✅ Retention and sampling management via Dashboard Admin UI: /admin/observability/config
- ✅ All PGTL services internal-only (no exposed host ports by default)
- ✅ Optional Grafana UI access documented (disabled by default)
- ✅ Total PGTL stack memory: <4GB (vs. SigNoz 8GB = 50% reduction)

**SigNoz Removal Requirements (MANDATORY):**

- ✅ **Zero SigNoz code in codebase**:
  - `grep -ri "signoz" TansuCloud.Dashboard/ TansuCloud.Gateway/ TansuCloud.*/` → 0 matches
  - No ISigNozQueryService, ISigNozTracesService, ISigNozLogsService interfaces
  - No SigNoz* classes, DTOs, or models
  - No `using TansuCloud.Dashboard.Observability.SigNoz;` statements
  - No SigNoz authentication services or configuration classes

- ✅ **Zero SigNoz in Docker Compose**:
  - `docker-compose.yml`: No `signoz`, `signoz-otel-collector`, `signoz-schema-migrator`, `clickhouse`, `zookeeper` services
  - `docker-compose.prod.yml`: No SigNoz services or profiles
  - `docker compose config | grep -i signoz` → 0 matches
  - `docker compose -f docker-compose.prod.yml config | grep -i signoz` → 0 matches

- ✅ **Zero SigNoz in Documentation** (except Tasks-M*.md):
  - `Architecture.md`: No SigNoz architecture diagrams, references, or configuration notes
  - `Guide-For-Admins-and-Tenants.md`: No SigNoz sections, UI access instructions, API references
  - `Easy-installation.md`: No SigNoz deployment steps, profile activation, or environment variables
  - `README.md`: No SigNoz mentions in observability section
  - `docs/*.md`: All SigNoz-specific docs moved to `docs/archived/SigNoz-*.md` (historical reference only)
  - Allowed: `Tasks-M1.md`, `Tasks-M2.md`, `Tasks-M3.md`, `Tasks-M4.md`, `Tasks-M5.md` (development history)

- ✅ **Zero SigNoz Environment Variables**:
  - `.env.example`: No `SIGNOZ_*`, `CLICKHOUSE_*`, `ZOOKEEPER_*` variables
  - Service configs: No `SigNozQuery:*`, `SigNoz:*` configuration sections
  - All SigNoz-related secrets removed from environment templates

- ✅ **Zero SigNoz Dependencies**:
  - No NuGet packages related to ClickHouse client libraries (if any were added)
  - No Docker volumes for SigNoz data (`signoz-data`, `clickhouse-data`, `zookeeper-data`)
  - `docker volume ls | grep signoz` → 0 matches after cleanup

**Validation Checklist (Must Pass):**

- ✅ All E2E tests passing (170 existing + 30 new PGTL tests = 200 total)
- ✅ Code grep validation: `grep -ri "signoz" . --exclude-dir=node_modules --exclude="Tasks-M*.md"` → 0 matches
- ✅ Compose validation: `docker compose config | grep -i signoz` → 0 matches
- ✅ Docker ps check: `docker ps -a | grep signoz` → 0 containers (after `docker compose down -v`)
- ✅ Volume check: `docker volume ls | grep signoz` → 0 volumes
- ✅ Documentation validation: Manual review of Architecture.md, Guide, Easy-installation confirms zero SigNoz
- ✅ PGTL stack health: All 5 components (Prometheus, Grafana, Tempo, Loki, Collector) healthy
- ✅ Resource limits: `docker stats` confirms <4GB total for PGTL stack
- ✅ Migration guide created: `docs/PGTL-Migration-Guide.md` with step-by-step instructions

---

#### Dependencies and Prerequisites

**Required before starting**:

- Task 8 (OpenTelemetry baseline) must be complete ✅
- Task 19 (Observability pages) Phase 1-7 complete ✅
- Task 36 (SigNoz dashboards) complete ✅
- Task 39 (Instrumentation hardening) complete ✅
- TansuCloud Storage service operational (for Tempo/Loki backends) ✅
- Docker Compose with resource limits support ✅

**Blocks**:

- No current blockers; all prerequisites met

**Enables future work**:

- Task 44: BaaS Authentication (observability for per-tenant users)
- Task 45: Serverless Functions (function execution traces/logs)
- Task 46: Database Quotas (connection pool metrics)
- Enhanced multi-tenant observability (per-tenant metrics/traces/logs isolation)

---

#### Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Tempo/Loki cannot connect to Storage service | HIGH | Test Storage service S3-compatible API compatibility; add integration tests; provide fallback to local volumes for dev |
| Prometheus retention deletion breaks queries | MEDIUM | Implement graceful degradation in Dashboard; cache recent query results; document retention behavior |
| PGTL stack exceeds 4GB memory target | MEDIUM | Monitor with `docker stats`; adjust resource limits; reduce retention to 3-5 days if needed; disable Grafana in resource-constrained environments |
| OpenTelemetry Collector becomes bottleneck | HIGH | Load test with realistic traffic; add batching and buffering; consider horizontal scaling (multiple collector instances) |
| Grafana UI exposure increases attack surface | MEDIUM | Keep disabled by default; require strong authentication; document security best practices; audit access logs |
| Dashboard queries timeout with large datasets | MEDIUM | Implement query timeouts (30s); add pagination to results; cache frequently-accessed queries; optimize PromQL/TraceQL/LogQL queries |
| Storage service lifecycle policies fail | HIGH | Add monitoring for lifecycle job execution; alert on failures; provide manual cleanup scripts as fallback |

---

#### Effort Estimate

- **Phase 1** (Prometheus Metrics): 1-2 weeks (1 developer)
- **Phase 2** (Tempo Traces): 2-3 weeks (1 developer)
- **Phase 3** (Loki Logs): 2-3 weeks (1 developer)
- **Phase 4** (Grafana Deployment): 1-2 weeks (1 developer)
- **Phase 5** (Retention/Sampling UI): 1 week (1 developer)
- **Phase 6** (Cleanup/Validation): 1 week (1 developer)

**Total**: 8-12 weeks for full PGTL migration (can be parallelized: 2 developers = 6-8 weeks)

**Quick win**: Phase 1 (Prometheus) can be completed in 1-2 weeks and already provides value (metrics working)

---

#### Success Metrics

**Technical**:

- PGTL stack memory usage: <4GB total (50% reduction vs. SigNoz 8GB)
- Query latency: Prometheus queries <500ms (p95), Tempo trace lookups <1s, Loki log searches <2s
- Data retention: 7 days enforced across all components
- Uptime: 99.9% for all PGTL components
- Storage efficiency: Tempo/Loki use <10GB disk per 7 days of data (1000 req/min baseline)

**Product**:

- Dashboard observability pages functional within 2 weeks of Phase 1 completion (metrics working)
- Full PGTL migration completed within 12 weeks
- 90%+ of operators prefer Dashboard Admin UI over direct Grafana access (validated via feedback survey)
- Zero data loss during migration (SigNoz data discarded as expected, fresh PGTL start)
- Positive feedback from early adopters on PGTL performance and reliability

---

#### Documentation and Training

**Docs to create/update**:

- ✅ `Architecture.md`: Update Observability section with PGTL architecture diagram
- ✅ `Guide-For-Admins-and-Tenants.md`: Sections 6.5-6.9 (Prometheus, Tempo, Loki, Grafana, Retention)
- ✅ `Easy-installation.md`: Update observability profile setup
- ✅ `docs/PGTL-Migration-Guide.md`: Step-by-step migration from SigNoz
- ✅ `Tasks-M3.md` Task 19: Archive SigNoz implementation notes, update status
- ✅ `Tasks-M4.md` Task 36: Archive SigNoz notes, update status
- ⏳ `docs/Observability-Alternative-Solutions-Analysis.md`: Mark as "COMPLETED - PGTL chosen"

**Training materials**:

- [ ] Video tutorial: "TansuCloud Observability with PGTL Stack"
- [ ] Blog post: "Migrating from SigNoz to PGTL: Lessons Learned"
- [ ] Admin guide: "Managing Retention and Sampling in TansuCloud"
- [ ] Developer guide: "Writing PromQL/TraceQL/LogQL queries for TansuCloud"

---

#### Next Steps After Task 47 Creation

1. **Review and approve** task scope and phasing with team ✅
2. **Prioritize Phase 1** (Prometheus) for immediate value
3. **Set up feature branch**: `feature/pgtl-migration`
4. **Create GitHub Epic**: "PGTL Stack Migration" with milestones for each phase
5. **Begin Phase 1 implementation**:
   - Deploy Prometheus with OTLP receiver (2 days)
   - Implement IPrometheusQueryService (3 days)
   - Update Dashboard ObservabilityOverview page (3 days)
6. **Weekly demos** to show progress and gather feedback
7. **Resource monitoring**: Track memory usage throughout migration to validate <4GB target
8. **Early validation**: After Phase 1, validate Prometheus with 1 week of production-like load

---

**Status**: Task 47 created — Ready for Phase 1 kickoff approval ✅

---
