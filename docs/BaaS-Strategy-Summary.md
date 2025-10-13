# TansuCloud BaaS Strategy: Complete Isolation (One Database Per Tenant)

**Decision Date**: 2025-10-13  
**Status**: APPROVED  
**Model**: Complete physical isolation like Supabase — ONE PostgreSQL database per tenant containing both identity (users, roles, OAuth configs) AND business data (products, orders, collections, documents, files).

---

## Executive Summary

TansuCloud will adopt the **Supabase complete isolation model** where each tenant gets a dedicated PostgreSQL database containing:

- **Identity tables**: ASP.NET Identity users, roles, claims, logins, OpenIddict clients/applications/authorizations/tokens, JWT signing keys, external OAuth provider settings
- **Business tables**: Documents (Database API), Files (Storage API), and any future tenant-specific schemas

This approach provides:
- ✅ **Physical isolation**: Zero cross-tenant query risks
- ✅ **Simple backup/restore**: Single `pg_dump` captures entire tenant (users + data)
- ✅ **Citus distribution**: Can move large tenants to dedicated coordinators/workers
- ✅ **Independent migrations**: Each tenant DB can have custom schemas/extensions
- ✅ **Clean provisioning**: One connection string per tenant, one DB to manage
- ✅ **Security**: Complete data sovereignty, no shared tables

---

## Architecture Overview

### Current State (Platform-Only)

```
┌─────────────────────────────────────────────────────────────┐
│ PostgreSQL (tansu-postgres container)                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  tansu_identity (platform admins only)                      │
│    ├── AspNetUsers                                          │
│    ├── AspNetRoles                                          │
│    ├── OpenIddictApplications                               │
│    └── JwkKeys                                              │
│                                                             │
│  tansu_audit (platform-wide audit events)                   │
│    └── AuditEvents (all services' audit trail)              │
│                                                             │
│  tansu_tenant_acme (business data only, no users)           │
│    ├── Documents                                            │
│    ├── Collections                                          │
│    └── DocumentVectors                                      │
│                                                             │
│  tansu_tenant_widgets (business data only, no users)        │
│    ├── Documents                                            │
│    ├── Collections                                          │
│    └── DocumentVectors                                      │
│                                                             │
└─────────────────────────────────────────────────────────────┘

❌ Problem: Tenant users (mobile/web app end users) have nowhere to live
❌ Problem: No per-tenant OAuth configs, no client registration
❌ Problem: Tenant admin can't manage their own users
```

### Target State (BaaS-Ready)

```
┌─────────────────────────────────────────────────────────────┐
│ PostgreSQL (tansu-postgres container)                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  tansu_identity (platform admins ONLY - unchanged)          │
│    ├── AspNetUsers (admin@tansu.local)                      │
│    ├── AspNetRoles (Admin)                                  │
│    └── JwkKeys (platform RS256 keys)                        │
│                                                             │
│  tansu_audit (platform-wide audit events - unchanged)       │
│    └── AuditEvents (all services' audit trail)              │
│                                                             │
│  tansu_tenant_acme (identity + business data)               │
│    ├── AspNetUsers (alice@acme.com, bob@acme.com)          │
│    ├── AspNetRoles (TenantAdmin, User)                      │
│    ├── AspNetUserClaims                                     │
│    ├── AspNetUserLogins (OAuth external identities)         │
│    ├── OpenIddictApplications (acme-mobile-app, acme-web)   │
│    ├── OpenIddictAuthorizations                             │
│    ├── OpenIddictTokens                                     │
│    ├── JwkKeys (tenant-specific RS256 keys)                 │
│    ├── ExternalProviderSettings (Google/GitHub OAuth)       │
│    ├── Documents (business data)                            │
│    ├── Collections (business data)                          │
│    ├── DocumentVectors (business data)                      │
│    ├── Files (Storage API)                                  │
│    └── ... (tenant's custom tables)                         │
│                                                             │
│  tansu_tenant_widgets (identity + business data)            │
│    ├── AspNetUsers (carol@widgets.io, dave@widgets.io)     │
│    ├── AspNetRoles (TenantAdmin, User)                      │
│    ├── OpenIddictApplications (widgets-spa, widgets-api)    │
│    ├── JwkKeys (tenant-specific RS256 keys)                 │
│    ├── ExternalProviderSettings (Microsoft OAuth)           │
│    ├── Documents                                            │
│    ├── Collections                                          │
│    └── ... (tenant's custom tables)                         │
│                                                             │
└─────────────────────────────────────────────────────────────┘

✅ Each tenant: users + OAuth configs + JWT keys + business data
✅ Platform admins in separate tansu_identity database
✅ Complete isolation per tenant
```

---

## Database Naming Convention

- **Platform Identity DB**: `tansu_identity` (unchanged)
  - Contains: Platform admin users only
  - Used by: Dashboard login, Gateway admin endpoints

- **Platform Audit DB**: `tansu_audit` (unchanged)
  - Contains: Platform-wide audit events from all services
  - Used by: All services write audit events here (Identity, Database, Storage, Gateway, Dashboard, Telemetry)

- **Per-Tenant DB**: `tansu_tenant_{tenantId}` (e.g., `tansu_tenant_acme`)
  - Contains: Tenant users (ASP.NET Identity) + business data (Documents, Files, etc.)
  - Used by: Identity service (tenant-scoped), Database API, Storage API

---

## Identity Service Multi-Tenancy Strategy

### Tenant Resolution (Request Pipeline)

```csharp
// TansuCloud.Identity/Middleware/TenantResolutionMiddleware.cs
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver)
    {
        // 1. Resolve tenant from X-Tansu-Tenant header
        var tenantId = context.Request.Headers["X-Tansu-Tenant"].FirstOrDefault();
        
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // No tenant header → platform admin flow (use tansu_identity)
            context.Items["TenantId"] = null;
            context.Items["IsPlatform"] = true;
        }
        else
        {
            // Tenant header present → tenant user flow
            var tenant = await resolver.ResolveTenantAsync(tenantId);
            if (tenant == null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = "tenant_not_found" });
                return;
            }
            context.Items["TenantId"] = tenantId;
            context.Items["TenantDbName"] = $"tansu_tenant_{tenantId}";
            context.Items["IsPlatform"] = false;
        }

        await _next(context);
    }
}
```

### Per-Request DbContext Factory

```csharp
// TansuCloud.Identity/Infrastructure/TenantIdentityDbContext.cs
public class TenantIdentityDbContext : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantIdentityDbContext(
        DbContextOptions<TenantIdentityDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var tenantDbName = httpContext?.Items["TenantDbName"] as string ?? "tansu_identity";
            
            var connString = $"Host=postgres;Port=5432;Database={tenantDbName};Username=postgres;Password=postgres";
            optionsBuilder.UseNpgsql(connString);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        
        // Add OpenIddict entities
        builder.UseOpenIddict();
        
        // Add JwkKeys
        builder.Entity<JwkKey>(entity =>
        {
            entity.ToTable("JwkKeys");
            entity.HasKey(k => k.Id);
            entity.Property(k => k.KeyData).IsRequired();
            entity.Property(k => k.CreatedAt).IsRequired();
        });
        
        // Add ExternalProviderSettings
        builder.Entity<ExternalProviderSetting>(entity =>
        {
            entity.ToTable("ExternalProviderSettings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(200);
        });
    }
}

// Program.cs registration
builder.Services.AddDbContext<TenantIdentityDbContext>(options =>
{
    // Scoped per-request, actual connection resolved in OnConfiguring
    options.UseNpgsql(); // Default, overridden per-request
});
```

### OpenIddict Configuration (Multi-Tenant)

```csharp
// Program.cs - Identity service
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<TenantIdentityDbContext>(); // Uses tenant-resolved DB
    })
    .AddServer(options =>
    {
        options.SetIssuerUri(new Uri(publicBaseUrl + "/identity/"));
        options.SetTokenEndpointUris("/identity/connect/token");
        options.SetAuthorizationEndpointUris("/identity/connect/authorize");
        options.SetUserinfoEndpointUris("/identity/connect/userinfo");
        
        // Add RS256 signing (tenant-specific keys from JwkKeys table)
        options.AddSigningKey(/* load from tenant DB */);
        
        options.RegisterScopes("openid", "profile", "email", "offline_access");
        options.AllowPasswordFlow();
        options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
        options.AllowRefreshTokenFlow();
    });
```

---

## Tenant Provisioning Flow

### Step 1: Create Tenant Database

```csharp
// TansuCloud.Database/Provisioning/TenantProvisioner.cs (updated)
public async Task<ProvisioningResult> ProvisionTenantAsync(string tenantId, string displayName)
{
    var dbName = $"tansu_tenant_{NormalizeTenantId(tenantId)}";
    
    // 1. Create database
    await using var adminConn = new NpgsqlConnection(GetAdminConnectionString());
    await adminConn.OpenAsync();
    await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminConn);
    await cmd.ExecuteNonQueryAsync();
    
    // 2. Apply Identity migrations (ASP.NET Identity + OpenIddict + JwkKeys)
    await ApplyIdentityMigrationsAsync(dbName);
    
    // 3. Apply business data migrations (Documents, Collections, Files)
    await ApplyBusinessMigrationsAsync(dbName);
    
    // 4. Seed tenant admin user
    await SeedTenantAdminAsync(dbName, tenantId);
    
    // 5. Generate tenant-specific RSA signing key
    await GenerateTenantSigningKeyAsync(dbName);
    
    return new ProvisioningResult
    {
        TenantId = tenantId,
        DatabaseName = dbName,
        Success = true
    };
}

private async Task ApplyIdentityMigrationsAsync(string dbName)
{
    // Run EF Core migrations for Identity schema
    var connString = $"Host=postgres;Port=5432;Database={dbName};Username=postgres;Password=postgres";
    await using var context = new TenantIdentityDbContext(
        new DbContextOptionsBuilder<TenantIdentityDbContext>()
            .UseNpgsql(connString)
            .Options,
        null); // No HTTP context during provisioning
    
    await context.Database.MigrateAsync();
}

private async Task SeedTenantAdminAsync(string dbName, string tenantId)
{
    // Create first tenant admin user: admin@{tenantId}.local
    var connString = $"Host=postgres;Port=5432;Database={dbName};Username=postgres;Password=postgres";
    // ... UserManager.CreateAsync logic
}

private async Task GenerateTenantSigningKeyAsync(string dbName)
{
    // Generate 2048-bit RSA key pair for this tenant
    using var rsa = RSA.Create(2048);
    var jwkKey = new JwkKey
    {
        Id = Guid.NewGuid().ToString(),
        KeyData = JsonSerializer.Serialize(rsa.ExportParameters(true)),
        CreatedAt = DateTime.UtcNow
    };
    
    // Store in tenant's JwkKeys table
    var connString = $"Host=postgres;Port=5432;Database={dbName};Username=postgres;Password=postgres";
    await using var context = new TenantIdentityDbContext(
        new DbContextOptionsBuilder<TenantIdentityDbContext>()
            .UseNpgsql(connString)
            .Options,
        null);
    context.Set<JwkKey>().Add(jwkKey);
    await context.SaveChangesAsync();
}
```

### Step 2: PgCat Configuration (Wildcard Pools)

```toml
# dev/pgcat/pgcat.toml
[pools.tansu_tenant_wildcard]
pool_mode = "transaction"
default_role = "any"
query_parser_enabled = true
primary_reads_enabled = true
sharding_function = "pg_bigint_hash"

[pools.tansu_tenant_wildcard.users.0]
username = "postgres"
password = "postgres"
pool_size = 10

[[pools.tansu_tenant_wildcard.shards]]
database = "tansu_tenant_*"  # Wildcard pattern
host = "postgres"
port = 5432
```

**Note**: PgCat 1.1+ supports wildcard database patterns. All `tansu_tenant_*` databases automatically route through this pool.

---

## Backup and Restore Strategy

### Full Tenant Backup (Identity + Business Data)

```bash
#!/bin/bash
# dev/tools/backup-tenant.sh

TENANT_ID=$1
DB_NAME="tansu_tenant_${TENANT_ID}"
BACKUP_DIR="./backups"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)

# Single pg_dump captures EVERYTHING for this tenant
docker exec tansu-postgres pg_dump \
  -U postgres \
  -d "${DB_NAME}" \
  -F custom \
  -f "/tmp/${DB_NAME}_${TIMESTAMP}.dump"

docker cp "tansu-postgres:/tmp/${DB_NAME}_${TIMESTAMP}.dump" \
  "${BACKUP_DIR}/${DB_NAME}_${TIMESTAMP}.dump"

echo "✅ Tenant ${TENANT_ID} backed up to ${BACKUP_DIR}/${DB_NAME}_${TIMESTAMP}.dump"
```

### Full Tenant Restore

```bash
#!/bin/bash
# dev/tools/restore-tenant.sh

TENANT_ID=$1
BACKUP_FILE=$2
DB_NAME="tansu_tenant_${TENANT_ID}"

# Drop existing database (WARNING: destroys all data)
docker exec tansu-postgres psql -U postgres -c "DROP DATABASE IF EXISTS \"${DB_NAME}\";"

# Create fresh database
docker exec tansu-postgres psql -U postgres -c "CREATE DATABASE \"${DB_NAME}\";"

# Restore from backup
docker cp "${BACKUP_FILE}" "tansu-postgres:/tmp/restore.dump"
docker exec tansu-postgres pg_restore \
  -U postgres \
  -d "${DB_NAME}" \
  -F custom \
  /tmp/restore.dump

echo "✅ Tenant ${TENANT_ID} restored from ${BACKUP_FILE}"
```

---

## Citus Distribution Strategy

### Small/Medium Tenants (< 100GB)

Keep on main coordinator with standard `tansu_tenant_*` databases. PgCat wildcard pool handles routing automatically.

### Large Tenants (> 100GB)

**Option 1: Dedicated Coordinator**

```sql
-- Create tenant DB on separate Citus coordinator
CREATE DATABASE tansu_tenant_acme_large;

-- Update PgCat config to route this tenant to dedicated host
[pools.tansu_tenant_acme_large]
pool_mode = "transaction"
[[pools.tansu_tenant_acme_large.shards]]
database = "tansu_tenant_acme_large"
host = "citus-coordinator-2"  # Dedicated instance
port = 5432
```

**Option 2: Distributed Sharding** (for extremely large tenants)

```sql
-- On dedicated coordinator, shard the tenant's Documents table
CREATE TABLE documents (
  id UUID PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  content JSONB,
  created_at TIMESTAMPTZ DEFAULT NOW()
) PARTITION BY HASH (id);

-- Distribute across Citus workers
SELECT create_distributed_table('documents', 'id');
```

---

## Implementation Phases

### Phase 1: Core Multi-Tenant Identity (2-3 weeks)

**Goal**: Identity service can resolve tenant DB per-request and manage tenant users.

**Tasks**:
1. Create `TenantResolutionMiddleware` (resolve from `X-Tansu-Tenant` header)
2. Implement `TenantIdentityDbContext` with per-request connection resolution
3. Update `TenantProvisioner` to:
   - Create single DB per tenant (`tansu_tenant_{id}`)
   - Apply Identity + OpenIddict migrations
   - Apply business data migrations (Documents, Files)
   - Seed tenant admin user
   - Generate tenant-specific RSA signing key
4. Add PgCat wildcard pool for `tansu_tenant_*`
5. Build tenant-scoped signup API: `POST /identity/api/{tenantId}/auth/signup`
6. Build tenant-scoped signin API: `POST /identity/api/{tenantId}/auth/signin`
7. Update JWKS endpoint to serve tenant-specific keys: `GET /identity/{tenantId}/.well-known/jwks.json`

**Acceptance Criteria**:
- ✅ Provision tenant creates ONE database with Identity + business tables
- ✅ Identity service resolves correct tenant DB per request
- ✅ Tenant user signup creates user in `tansu_tenant_acme.AspNetUsers`
- ✅ Tenant user signin issues JWT with tenant-specific RS256 key
- ✅ Cross-tenant isolation verified: user from tenant A cannot authenticate as tenant B
- ✅ Platform admin login still works (no `X-Tansu-Tenant` header → `tansu_identity` DB)

**Tests**:
```csharp
// TansuCloud.E2E.Tests/IdentityMultiTenancyTests.cs
[Fact]
public async Task Provision_Tenant_Creates_One_Database_With_Identity_And_Business_Tables()
{
    var result = await Provisioner.ProvisionTenantAsync("test-tenant", "Test Tenant");
    
    // Verify database exists
    var dbExists = await DatabaseExistsAsync("tansu_tenant_test-tenant");
    Assert.True(dbExists);
    
    // Verify Identity tables exist
    var identityTables = await GetTablesAsync("tansu_tenant_test-tenant");
    Assert.Contains("AspNetUsers", identityTables);
    Assert.Contains("AspNetRoles", identityTables);
    Assert.Contains("OpenIddictApplications", identityTables);
    Assert.Contains("JwkKeys", identityTables);
    
    // Verify business tables exist
    Assert.Contains("Documents", identityTables);
    Assert.Contains("Collections", identityTables);
    Assert.Contains("Files", identityTables);
}

[Fact]
public async Task Tenant_User_Signup_Creates_User_In_Tenant_Database()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    
    var response = await HttpClient.PostAsJsonAsync(
        "/identity/api/acme/auth/signup",
        new { email = "alice@acme.com", password = "SecurePass123!" });
    
    response.EnsureSuccessStatusCode();
    
    // Verify user exists in tansu_tenant_acme database
    var user = await GetUserFromDatabaseAsync("tansu_tenant_acme", "alice@acme.com");
    Assert.NotNull(user);
}

[Fact]
public async Task Tenant_User_Cannot_Authenticate_To_Different_Tenant()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    await Provisioner.ProvisionTenantAsync("widgets", "Widgets Inc");
    
    // Create user in acme tenant
    await SignupTenantUserAsync("acme", "alice@acme.com", "SecurePass123!");
    
    // Try to sign in to widgets tenant with acme user
    var response = await HttpClient.PostAsJsonAsync(
        "/identity/api/widgets/auth/signin",
        new { email = "alice@acme.com", password = "SecurePass123!" });
    
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

---

### Phase 2: External OAuth Integration (1-2 weeks)

**Goal**: Tenants can configure Google/Microsoft/GitHub OAuth for their users.

**Tasks**:
1. Add `ExternalProviderSettings` table to Identity migrations (already defined, just needs migration)
2. Build OAuth authorize endpoint: `GET /identity/api/{tenantId}/auth/authorize/{provider}`
3. Build OAuth callback handler: `GET /identity/api/{tenantId}/auth/callback/{provider}`
4. Implement user lookup/creation logic (link Google account to tenant user)
5. Create Dashboard UI: `/dashboard/tenant/{id}/auth-providers` (CRUD for OAuth configs)

**Acceptance Criteria**:
- ✅ Tenant admin can add Google OAuth config via Dashboard
- ✅ Tenant user can sign in with "Sign in with Google" button
- ✅ First-time Google login creates new user in tenant DB
- ✅ Returning Google user links to existing account (by email)
- ✅ OAuth tokens stored in `AspNetUserLogins` table (tenant DB)

**Tests**:
```csharp
[Fact]
public async Task Tenant_OAuth_Config_Isolated_Per_Tenant()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    
    // Acme configures Google OAuth
    await ConfigureOAuthProviderAsync("acme", "Google", "acme-client-id", "acme-secret");
    
    // Verify Acme's config stored in tansu_tenant_acme.ExternalProviderSettings
    var provider = await GetOAuthProviderAsync("tansu_tenant_acme", "Google");
    Assert.Equal("acme-client-id", provider.ClientId);
    
    // Verify Widgets tenant cannot see Acme's config
    await Provisioner.ProvisionTenantAsync("widgets", "Widgets Inc");
    var widgetsProvider = await GetOAuthProviderAsync("tansu_tenant_widgets", "Google");
    Assert.Null(widgetsProvider); // Not configured for Widgets yet
}
```

---

### Phase 3: Client Registration API (1 week)

**Goal**: Tenant admins can register mobile/web/desktop apps that authenticate tenant users.

**Tasks**:
1. Build client registration API: `POST /identity/api/{tenantId}/clients`
2. Store OpenIddict applications in tenant DB (`tansu_tenant_{id}.OpenIddictApplications`)
3. Support public clients (PKCE for mobile/SPA) and confidential clients (server-side)
4. Create Dashboard UI: `/dashboard/tenant/{id}/clients` (list, create, delete clients)

**Acceptance Criteria**:
- ✅ Tenant admin can create client "acme-mobile-app" via API
- ✅ Client stored in `tansu_tenant_acme.OpenIddictApplications`
- ✅ Mobile app can authenticate using client_id + PKCE flow
- ✅ Confidential client can use client_secret for server-to-server auth

**Tests**:
```csharp
[Fact]
public async Task Tenant_Client_Registration_Isolated()
{
    await Provisioner.ProvisionTenantAsync("acme", "Acme Corp");
    
    // Register mobile app for Acme
    var client = await RegisterClientAsync("acme", new ClientRegistration
    {
        ClientId = "acme-mobile-app",
        DisplayName = "Acme Mobile App",
        Type = "public",
        RedirectUris = new[] { "acme://callback" }
    });
    
    Assert.NotNull(client);
    
    // Verify client stored in tenant DB
    var storedClient = await GetClientFromDatabaseAsync("tansu_tenant_acme", "acme-mobile-app");
    Assert.NotNull(storedClient);
}
```

---

### Phase 4: Tenant Admin Dashboard (1-2 weeks)

**Goal**: Tenant admins can manage users, OAuth providers, and clients via Dashboard.

**Tasks**:
1. Add `TenantAdmin` role to ASP.NET Identity
2. Build Dashboard pages:
   - `/dashboard/tenant/{id}/users` (list, invite, disable users)
   - `/dashboard/tenant/{id}/auth-providers` (OAuth config CRUD)
   - `/dashboard/tenant/{id}/clients` (app registration)
3. Implement user invitation system (send email with signup link)
4. Build login activity log (audit tenant user authentications)

**Acceptance Criteria**:
- ✅ Tenant admin can invite users via email
- ✅ Invited user receives signup link, creates account in tenant DB
- ✅ Tenant admin can disable/re-enable users
- ✅ Tenant admin can see login activity (last sign-in, IP address)

---

### Phase 5: Advanced Features (Ongoing)

**Optional enhancements**:
- Account linking (merge Google + email/password accounts)
- Multi-factor authentication (TOTP, SMS)
- Passwordless authentication (magic links, WebAuthn)
- Custom domains (tenant-specific issuer URIs)
- SSO integration (SAML, enterprise IdP)
- Audit log (track all identity operations per tenant)

---

## Testing Strategy

### Unit Tests
- `TenantResolutionMiddleware` correctly resolves tenant from header
- `TenantIdentityDbContext.OnConfiguring` builds correct connection string
- `TenantProvisioner` creates database with all required tables
- RSA key generation produces valid JWK

### Integration Tests
- Provision tenant → verify DB exists with Identity + business tables
- Signup tenant user → verify user in tenant DB
- Signin tenant user → verify JWT signed with tenant key
- Cross-tenant isolation → user cannot auth to wrong tenant
- OAuth flow → Google login creates user in tenant DB

### E2E Tests (Playwright)
- Navigate to `/dashboard/tenant/acme/users`
- Click "Invite User", enter email, send invitation
- Open invitation link, complete signup
- Verify new user appears in user list
- Sign out, sign in with new user credentials
- Verify user sees tenant-scoped Dashboard

---

## Security Considerations

### Tenant Isolation
- ✅ Each tenant DB is physically isolated (no shared tables)
- ✅ PgCat enforces connection pooling per database
- ✅ Identity service resolves tenant from trusted header (`X-Tansu-Tenant` set by Gateway after authz)
- ⚠️ Middleware MUST validate tenant exists before allowing access
- ⚠️ No direct database name input from users (always compute from validated tenantId)

### JWT Signing Keys
- ✅ Each tenant has unique RSA 2048-bit key pair
- ✅ Keys stored in tenant DB (`JwkKeys` table), not shared across tenants
- ✅ Key rotation per tenant (30-day default, 7-day grace period)
- ⚠️ Old keys must be retained during grace period for token validation

### OAuth Client Secrets
- ✅ Stored encrypted in `ExternalProviderSettings` table (tenant DB)
- ✅ Never logged or exposed in API responses
- ⚠️ Use ASP.NET Data Protection API for encryption at rest

---

## Operational Playbook

### Provision New Tenant

```bash
# Via Gateway (dev bypass)
curl -X POST http://127.0.0.1:8080/db/api/provisioning/tenants \
  -H "X-Provision-Key: letmein" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"acme","displayName":"Acme Corp"}'

# Verify database created
docker exec tansu-postgres psql -U postgres -l | grep tansu_tenant_acme
```

### Backup Tenant

```bash
./dev/tools/backup-tenant.sh acme
# Creates: ./backups/tansu_tenant_acme_20251013_143022.dump
```

### Restore Tenant

```bash
./dev/tools/restore-tenant.sh acme ./backups/tansu_tenant_acme_20251013_143022.dump
```

### Migrate Large Tenant to Dedicated Coordinator

```bash
# 1. Backup on main coordinator
./dev/tools/backup-tenant.sh acme-large

# 2. Restore on dedicated Citus coordinator
scp ./backups/tansu_tenant_acme_large_*.dump citus-coordinator-2:/tmp/
ssh citus-coordinator-2
pg_restore -U postgres -d tansu_tenant_acme_large -F custom /tmp/tansu_tenant_acme_large_*.dump

# 3. Update PgCat config to route to new host
# (edit dev/pgcat/pgcat.toml, add dedicated pool)

# 4. Reload PgCat
docker exec tansu-pgcat kill -HUP 1
```

### Rotate Tenant JWT Signing Key

```bash
# Identity service has automatic rotation, but manual trigger:
curl -X POST http://127.0.0.1:8080/identity/api/acme/admin/keys/rotate \
  -H "Authorization: Bearer <admin-token>"
```

---

## Migration Path from Current State

### Step 1: Update TenantProvisioner
- Modify to create ONE database per tenant (not separate identity DB)
- Apply both Identity + business migrations to same DB

### Step 2: Add Identity Service Multi-Tenancy
- Implement `TenantResolutionMiddleware`
- Create `TenantIdentityDbContext` with per-request connection
- Update OpenIddict to use tenant-resolved DB

### Step 3: Build Tenant User APIs
- Signup: `POST /identity/api/{tenantId}/auth/signup`
- Signin: `POST /identity/api/{tenantId}/auth/signin`
- JWKS: `GET /identity/{tenantId}/.well-known/jwks.json`

### Step 4: Update Database/Storage APIs
- Already tenant-scoped via `X-Tansu-Tenant` header
- No changes needed (continue using tenant data DB)

### Step 5: Build Dashboard Admin Pages
- Tenant user management UI
- OAuth provider configuration UI
- Client registration UI

---

## References

- [Supabase Architecture](https://github.com/supabase/supabase) - Database-per-project isolation
- [OpenIddict Multi-Tenancy](https://documentation.openiddict.com/) - Per-tenant OIDC configuration
- [ASP.NET Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity) - User management foundation
- [Citus Sharding](https://docs.citusdata.com/) - PostgreSQL distributed tables
- [PgCat Wildcard Pools](https://github.com/postgresml/pgcat) - Dynamic database routing

---

**Status**: APPROVED — Ready for Phase 1 implementation  
**Next Steps**: Create GitHub Issue/Epic, start Phase 1 tasks (TenantProvisioner update, TenantResolutionMiddleware, TenantIdentityDbContext)
