# Phase M5: Advanced Backend-as-a-Service Features

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [44) BaaS Authentication - Per-Tenant Identity (Complete Isolation)](#task-44-baas-authentication-per-tenant-identity)
- [ ] [45) Serverless Functions Service](#task-45-serverless-functions-service)

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
