# BaaS Authentication Patterns: Supabase, Appwrite, and TansuCloud

> Research on how leading BaaS platforms handle per-tenant/per-project authentication

## Executive Summary

| Platform | Multi-Tenancy Model | Auth Isolation | User Scope | Identity Provider |
|----------|---------------------|----------------|------------|-------------------|
| **Supabase** | Database-per-project | PostgreSQL database isolation | Per-project | GoTrue (built on PostgreSQL) |
| **Appwrite** | Namespace-per-project | Collection-level isolation | Per-project | Built-in auth service (MariaDB) |
| **TansuCloud (Current)** | Database-per-tenant | Database isolation for data, NOT users | Platform-level | OpenIddict (single Identity DB) |
| **TansuCloud (Proposed)** | Database-per-tenant + User scoping | Database + user-level isolation | Per-tenant | OpenIddict (tenant-scoped users) |

## Supabase Architecture

### Overview

- **Auth Service**: GoTrue (Golang-based, originally from Netlify)
- **Database**: PostgreSQL per project
- **User Storage**: `auth.users` table in EACH project's database
- **Multi-Tenancy**: Project = isolated PostgreSQL instance

### How It Works

**Project Provisioning:**

```
User signs up on supabase.com (platform)
  ↓
Creates "Project A" (e.g., mobile app backend)
  ↓
Supabase provisions:
  - New PostgreSQL database
  - PostgREST API server
  - GoTrue auth instance
  - Realtime server
  - Storage buckets
  ↓
Project A has its own:
  - Database URL: db.project-a.supabase.co
  - API URL: api.project-a.supabase.co
  - Auth URL: auth.project-a.supabase.co
```

**End-User Authentication (in Project A):**

```javascript
// Developer's app code (mobile/web)
import { createClient } from '@supabase/supabase-js'

const supabase = createClient(
  'https://project-a.supabase.co',
  'project-a-anon-key'
)

// Sign up end user
await supabase.auth.signUp({
  email: 'user@example.com',
  password: 'password123'
})
// User stored in Project A's auth.users table

// Sign in end user
await supabase.auth.signInWithPassword({
  email: 'user@example.com',
  password: 'password123'
})
// Returns JWT signed by Project A's secret
```

**Database Schema (per project):**

```sql
-- Each project has its own PostgreSQL instance with:
CREATE SCHEMA auth;

CREATE TABLE auth.users (
    id UUID PRIMARY KEY,
    email TEXT UNIQUE,
    encrypted_password TEXT,
    email_confirmed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,
    ...
);

-- Developer's app tables:
CREATE TABLE public.posts (
    id BIGSERIAL PRIMARY KEY,
    user_id UUID REFERENCES auth.users(id),
    title TEXT,
    content TEXT
);

-- Row Level Security (RLS) for multi-tenancy WITHIN project:
ALTER TABLE public.posts ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can read their own posts"
ON public.posts FOR SELECT
USING (auth.uid() = user_id);
```

**Key Insights:**

- ✅ **Complete isolation**: Project A users ≠ Project B users (different databases)
- ✅ **No shared fate**: Project A outage doesn't affect Project B
- ✅ **Per-project configuration**: Each project has own auth settings, providers, etc.
- ✅ **RLS for app-level multi-tenancy**: Developers use RLS for THEIR multi-tenant apps
- ❌ **Resource intensive**: One PostgreSQL instance per project (100 projects = 100 DBs)
- ✅ **Simple mental model**: Project = isolated backend

**External Providers (OAuth):**

```javascript
// Each project configures its own OAuth apps
await supabase.auth.signInWithOAuth({
  provider: 'google',
  options: {
    redirectTo: 'https://myapp.com/callback'
  }
})
// Uses Project A's Google OAuth client ID/secret
```

Configuration per project:

- Dashboard → Authentication → Providers → Google
- Enter Project A's Google OAuth credentials
- Completely separate from other projects

---

## Appwrite Architecture

### Overview

- **Auth Service**: Built-in (PHP-based)
- **Database**: MariaDB (shared across all projects, namespaced by project ID)
- **User Storage**: `users` collection, filtered by `project_id`
- **Multi-Tenancy**: Soft multi-tenancy (shared DB, project-scoped queries)

### How It Works

**Project Provisioning:**

```
User signs up on appwrite.io (platform)
  ↓
Creates "Project X" (e.g., e-commerce app)
  ↓
Appwrite provisions:
  - Project ID (namespace)
  - API keys
  - Database collections
  - Storage buckets
  - Functions
  ↓
Project X has its own:
  - Project ID: 507f1f77bcf86cd799439011
  - Endpoint: https://cloud.appwrite.io/v1
  - Uses Project ID in ALL API calls
```

**End-User Authentication (in Project X):**

```javascript
// Developer's app code
import { Client, Account } from 'appwrite'

const client = new Client()
  .setEndpoint('https://cloud.appwrite.io/v1')
  .setProject('507f1f77bcf86cd799439011') // Project X ID

const account = new Account(client)

// Sign up end user
await account.create(
  'unique()',
  'user@example.com',
  'password123',
  'John Doe'
)
// User stored with project_id = 507f1f77bcf86cd799439011

// Sign in end user
await account.createEmailSession(
  'user@example.com',
  'password123'
)
// Returns JWT scoped to Project X
```

**Database Schema (shared, project-namespaced):**

```sql
-- Single MariaDB instance for ALL projects
CREATE TABLE users (
    id VARCHAR(36) PRIMARY KEY,
    project_id VARCHAR(36) NOT NULL,
    email VARCHAR(255),
    password_hash VARCHAR(255),
    name VARCHAR(255),
    created_at TIMESTAMP,
    updated_at TIMESTAMP,
    INDEX idx_project_email (project_id, email)
);

-- All queries MUST include project_id filter
SELECT * FROM users 
WHERE project_id = '507f1f77bcf86cd799439011' 
  AND email = 'user@example.com';

-- Developer's app data:
CREATE TABLE posts (
    id VARCHAR(36) PRIMARY KEY,
    project_id VARCHAR(36) NOT NULL,
    user_id VARCHAR(36) NOT NULL,
    title VARCHAR(255),
    content TEXT,
    INDEX idx_project_user (project_id, user_id)
);
```

**Key Insights:**

- ✅ **Resource efficient**: Single database for all projects
- ✅ **Fast provisioning**: No need to spin up new DB instances
- ✅ **Centralized management**: One database to backup/monitor
- ❌ **Shared fate**: Database issues affect all projects
- ❌ **Query filter discipline**: MUST include project_id in every query (security risk if forgotten)
- ⚠️ **Scaling challenges**: Single DB bottleneck at very large scale
- ✅ **Good for small-to-medium projects**: Cost-effective

**External Providers (OAuth):**

```javascript
// Each project configures its own OAuth apps
await account.createOAuth2Session(
  'google',
  'https://myapp.com/callback',
  'https://myapp.com/failure'
)
// Uses Project X's Google OAuth credentials
```

Configuration per project:

- Console → Auth → Google → Enable
- Enter Project X's Google OAuth client ID/secret
- Stored in `providers` table with `project_id`

---

## Firebase (for comparison)

### Overview

- **Auth Service**: Firebase Authentication
- **Database**: Firestore (document-based, soft multi-tenancy)
- **User Storage**: Firebase Auth user pool per project
- **Multi-Tenancy**: Project = Firebase project (Google Cloud project)

**Key Points:**

- Similar to Supabase: Project = isolated auth instance
- Users scoped per project
- Each project has own Firebase console
- Soft multi-tenancy within project via security rules

---

## TansuCloud Current vs. Desired State

### Current Architecture (Platform-Centric)

```
TansuCloud Platform Admin
  ↓
Creates Tenant 1, Tenant 2, Tenant 3
  ↓
Each Tenant has:
  - Database: tansu_tenant_<id> (PostgreSQL)
  - Storage: Tenant-scoped buckets
  - Data isolation: ✅ Complete
  - User isolation: ❌ NO - all users in tansu_identity DB
  ↓
Problem: Tenant 1 end-users NOT isolated from Tenant 2 end-users
```

**Current Identity Service:**

```csharp
// tansu_identity database (single, shared)
public class IdentityUser {
    public string Id { get; set; }
    public string Email { get; set; }
    // NO TenantId - all users mixed together
}

// Result: Cannot distinguish:
// - Platform admin (manages TansuCloud)
// - Tenant admin (manages Tenant 1)
// - Tenant 1 end-user (uses Tenant 1's app)
// - Tenant 2 end-user (uses Tenant 2's app)
```

### Proposed Architecture (BaaS-Ready)

**Option 1: Supabase Model (Database-per-Tenant Identity)**

```
TansuCloud Platform
  ↓
Tenant 1 provisioning creates:
  - tansu_tenant_acme_data (app data)
  - tansu_tenant_acme_identity (auth.users)
  - Separate Identity service instance (or multi-tenant aware)
  ↓
Tenant 1 App
  ↓
Identity API: https://tenant1.tansu.cloud/identity
  - Users stored in tansu_tenant_acme_identity
  - Completely isolated from other tenants
```

**Pros:**

- ✅ Complete isolation (Supabase-like)
- ✅ Per-tenant auth customization
- ✅ No shared fate
- ✅ Compliance-friendly (data residency)

**Cons:**

- ❌ Resource intensive (one Identity DB per tenant)
- ❌ Complex provisioning
- ❌ Key management overhead

**Option 2: Appwrite Model (Soft Multi-Tenancy)**

```
TansuCloud Platform
  ↓
Single tansu_identity database with TenantId scoping
  ↓
Tenant 1 provisioning creates:
  - tansu_tenant_acme_data (app data)
  - Entry in tansu_identity.Tenants table
  - OAuth providers for Tenant 1
  ↓
Tenant 1 App
  ↓
Identity API: https://tansu.cloud/identity?tenant=acme
  - Users filtered by TenantId = 'acme'
  - Shared Identity DB, tenant-scoped queries
```

**Database Schema:**

```csharp
// tansu_identity (single, shared)
public class TansuCloudUser : IdentityUser {
    public string Id { get; set; }
    public string Email { get; set; }
    
    [Required]
    [MaxLength(256)]
    public string TenantId { get; set; } // "acme", "globex", etc.
    
    public UserType Type { get; set; } // Platform | TenantUser
}

public enum UserType {
    Platform,   // TansuCloud admin (TenantId = NULL or "platform")
    TenantUser  // Tenant's end user (TenantId = tenant ID)
}

// All queries MUST filter by TenantId
var users = await userManager.Users
    .Where(u => u.TenantId == "acme" && u.Type == UserType.TenantUser)
    .ToListAsync();
```

**Pros:**

- ✅ Resource efficient (one DB for all tenants)
- ✅ Fast tenant provisioning
- ✅ Easier to manage (one Identity service)
- ✅ Matches current TansuCloud architecture (database-per-tenant for data)

**Cons:**

- ❌ Shared fate (Identity DB outage = all tenants affected)
- ❌ Query discipline required (must always filter TenantId)
- ⚠️ Email uniqueness: global or per-tenant? (Need decision)

---

## Recommendation for TansuCloud

### Adopt **Appwrite Model** (Soft Multi-Tenancy) with TansuCloud Enhancements

**Why?**

1. **Consistent with current architecture**: TansuCloud already uses database-per-tenant for **data**, but a shared approach for cross-cutting concerns is pragmatic.

2. **Resource efficiency**: Small-to-medium tenants don't need dedicated Identity instances.

3. **Faster provisioning**: No need to spin up new Identity DBs per tenant.

4. **Upgrade path**: Can offer "dedicated Identity" as premium feature for large enterprises.

### Proposed Implementation

**1. Identity User Model:**

```csharp
public class TansuCloudUser : IdentityUser
{
    // Existing ASP.NET Identity fields
    public override string Id { get; set; } = Guid.NewGuid().ToString();
    public override string Email { get; set; } = default!;
    
    // TansuCloud extensions
    [MaxLength(256)]
    public string? TenantId { get; set; } // NULL = platform user
    
    public UserType Type { get; set; } = UserType.TenantUser;
    
    // Metadata
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public enum UserType
{
    Platform,   // TansuCloud operator (manages tenants)
    TenantAdmin, // Manages tenant's users/settings
    TenantUser  // End user of tenant's app
}
```

**2. Authentication APIs (Tenant-Scoped):**

```csharp
// Registration (tenant-scoped)
POST /identity/api/{tenantId}/auth/signup
{
  "email": "user@example.com",
  "password": "SecurePass123!",
  "name": "John Doe"
}

// Response:
{
  "user": {
    "id": "uuid",
    "email": "user@example.com",
    "tenantId": "acme"
  },
  "session": {
    "access_token": "jwt...",
    "refresh_token": "...",
    "expires_in": 3600
  }
}

// Login (tenant-scoped)
POST /identity/api/{tenantId}/auth/signin
{
  "email": "user@example.com",
  "password": "SecurePass123!"
}

// OAuth (tenant-scoped)
GET /identity/api/{tenantId}/auth/authorize/google
  ?redirect_uri=https://myapp.com/callback
  
// Uses tenant's configured Google OAuth client
```

**3. Client Registration (per tenant):**

```csharp
// Tenant admin registers their app
POST /identity/api/tenants/{tenantId}/clients
Authorization: Bearer <tenant-admin-token>
{
  "clientId": "acme-mobile-app",
  "clientName": "Acme Mobile App",
  "clientType": "public", // or "confidential"
  "redirectUris": ["myapp://callback", "http://localhost:3000/callback"],
  "grantTypes": ["authorization_code", "refresh_token"],
  "scopes": ["openid", "profile", "email", "db.read", "db.write"]
}

// Stored in OpenIddictApplications with TenantId
```

**4. Tenant Admin Dashboard:**

```
/dashboard/tenant/{tenantId}/users
  - List tenant's users
  - Invite users
  - Reset passwords
  - View login activity

/dashboard/tenant/{tenantId}/auth-providers
  - Configure Google OAuth (tenant's credentials)
  - Configure Microsoft OAuth
  - Enable/disable providers

/dashboard/tenant/{tenantId}/clients
  - Register mobile/web/desktop apps
  - Manage client secrets
  - View OAuth flows
```

**5. Email Uniqueness Strategy:**

**Option A: Global Unique (Recommended)**

```csharp
// Email is globally unique across all tenants
// user@example.com can only exist ONCE in tansu_identity
// Pro: Prevents confusion, enables account linking
// Con: User must use different email for different tenants
```

**Option B: Per-Tenant Unique**

```csharp
// Email unique within tenant (unique index on TenantId + Email)
// user@example.com can exist in Tenant 1 AND Tenant 2
// Pro: Flexibility
// Con: Complex account management, potential confusion
```

I recommend **Option A** (global unique) with future support for account linking (user can be a member of multiple tenants with different roles).

**6. External Provider Scoping:**

```csharp
// Already have this! Just use it:
public class ExternalProviderSetting
{
    public string TenantId { get; set; } // Already tenant-scoped ✅
    public string Provider { get; set; } // "google", "microsoft", etc.
    public string Authority { get; set; }
    public string ClientId { get; set; } // Tenant's OAuth client ID
    public string ClientSecret { get; set; }
    public bool Enabled { get; set; }
}

// When user clicks "Sign in with Google" on Tenant 1's app:
// 1. Load ExternalProviderSettings WHERE TenantId = 'tenant1' AND Provider = 'google'
// 2. Redirect to Google with Tenant 1's OAuth client ID
// 3. Google redirects back to /identity/api/tenant1/auth/callback/google
// 4. Create/update user with TenantId = 'tenant1'
// 5. Issue JWT with tid = 'tenant1'
```

---

## Implementation Roadmap

### Phase 1: Core Multi-Tenant Auth (MVP)

- [ ] Add migration: `TenantId`, `Type` to `IdentityUser`
- [ ] Tenant-scoped registration/login APIs
- [ ] Token claims include `tid` (already done)
- [ ] Query filters enforce TenantId scoping
- [ ] Basic tests (user isolation, token validation)

### Phase 2: External Providers

- [ ] Wire existing `ExternalProviderSettings` to tenant-scoped login
- [ ] OAuth callback handlers per tenant
- [ ] Provider configuration UI in Dashboard
- [ ] Test Google/Microsoft/GitHub flows

### Phase 3: Client Management

- [ ] Client registration API (OpenIddict client per tenant)
- [ ] Dashboard UI for tenant admins to register apps
- [ ] Support public clients (mobile/SPA) and confidential (server-side)
- [ ] PKCE enforcement for public clients

### Phase 4: Tenant Admin Experience

- [ ] Dashboard pages for tenant user management
- [ ] Invitation system (email-based)
- [ ] Password reset flows (tenant-scoped)
- [ ] Login activity/audit logs per tenant
- [ ] Role management within tenant (TenantAdmin vs TenantUser)

### Phase 5: Advanced Features

- [ ] Account linking (user belongs to multiple tenants)
- [ ] MFA (TOTP, SMS, email) per tenant
- [ ] Passwordless (magic links, WebAuthn)
- [ ] Session management (revoke sessions)
- [ ] Rate limiting per tenant
- [ ] Custom domains (auth.tenant1.com)

---

## Comparison Table

| Feature | Supabase | Appwrite | TansuCloud (Proposed) |
|---------|----------|----------|------------------------|
| **Multi-Tenancy** | DB-per-project | Soft (project_id filter) | Soft (TenantId filter) + optional dedicated |
| **Auth Isolation** | Complete (separate DBs) | Query-level | Query-level + optional DB-per-tenant |
| **Resource Overhead** | High (1 DB per project) | Low (shared DB) | Low (shared) + premium dedicated option |
| **Provisioning Speed** | Slow (DB creation) | Fast (namespace) | Fast (namespace) + optional slow (dedicated) |
| **Scaling Limit** | Infrastructure-bound | DB-bound | Hybrid (shared for most, dedicated for large) |
| **Shared Fate** | No | Yes | Shared by default, isolated for premium |
| **Developer Experience** | Excellent (full isolation) | Good (simple API) | Excellent (best of both worlds) |
| **External OAuth** | Per-project config | Per-project config | Per-tenant config ✅ |
| **User Management** | Dashboard per project | Console per project | Dashboard per tenant ✅ |
| **Custom Domains** | ✅ (*.supabase.co) | ❌ (shared endpoint) | 🔄 Future (*.tansu.cloud) |

---

## Conclusion

**Adopt Appwrite's soft multi-tenancy model with these TansuCloud enhancements:**

1. ✅ **User table scoping**: Add `TenantId` to `IdentityUser`
2. ✅ **Tenant-scoped APIs**: Registration, login, OAuth per tenant
3. ✅ **Client registration**: Tenant admins register their apps
4. ✅ **External providers**: Already have per-tenant support
5. ✅ **Upgrade path**: Offer dedicated Identity instances for enterprise tenants
6. ✅ **Email uniqueness**: Global by default, prevent confusion
7. ✅ **Role hierarchy**: Platform → TenantAdmin → TenantUser

This gives TansuCloud:

- **Supabase-like developer experience** (complete isolation, per-tenant config)
- **Appwrite-like resource efficiency** (shared DB for small-to-medium tenants)
- **Firebase-like scalability** (optional dedicated instances for large tenants)
- **Better than all three**: Hybrid model adapts to tenant size

Next: Create implementation tasks for Phase 1 (MVP)?
