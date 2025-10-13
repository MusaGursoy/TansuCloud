# TansuCloud Authentication & Authorization Quick Reference

> **Full documentation**: See `Architecture.md` → "Security > Authentication and Authorization Flows"

## TL;DR

- **Identity Provider**: OpenIddict (ASP.NET Core OIDC/OAuth2)
- **Algorithm**: RS256 (RSA 2048-bit, asymmetric)
- **Token Format**: JWT (JSON Web Tokens)
- **Flows**: Authorization Code + PKCE, Client Credentials, Refresh Token
- **Key Rotation**: Automated (30 days default), 7-day grace period
- **RBAC**: Admin, User roles + scope-based policies (db.read/write, storage.read/write)

## Quick Checks

### Is Identity Service Healthy?

```bash
# Check discovery endpoint
curl http://127.0.0.1:8080/identity/.well-known/openid-configuration | jq .

# Check JWKS (public keys)
curl http://127.0.0.1:8080/identity/.well-known/jwks | jq .

# Expected: 1-2 keys (current + retiring during rotation)
```

### Get a Token (Client Credentials)

```bash
# Development only
curl -X POST http://127.0.0.1:8080/identity/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=tansu-dashboard" \
  -d "client_secret=dev-secret" \
  -d "scope=db.write admin.full"
```

### Decode a JWT

```bash
# Copy token, then:
echo "<token>" | cut -d. -f2 | base64 -d | jq .

# Look for:
# - sub (user ID)
# - tid (tenant ID)
# - scope (permissions)
# - exp (expiration timestamp)
```

### Trigger Manual Key Rotation

```bash
POST /identity/admin/keys/rotate-now
Authorization: Bearer <admin-token>

# Response: 202 Accepted
```

### View Current Key Rotation Policy

```bash
GET /identity/admin/keys/policies
Authorization: Bearer <admin-token>

# Returns: JwksRotationPeriod, PasswordPolicy, TokenLifetimes
```

## Token Scopes

| Scope | Description | Used By |
|-------|-------------|---------|
| `openid` | OIDC authentication | All flows |
| `profile` | User profile claims | Dashboard |
| `email` | Email claim | Dashboard |
| `roles` | Role claims | Dashboard, APIs |
| `offline_access` | Refresh token | Dashboard, mobile |
| `db.read` | Read Database APIs | Apps, services |
| `db.write` | Write Database APIs | Apps, services |
| `storage.read` | Read Storage APIs | Apps, services |
| `storage.write` | Write Storage APIs | Apps, services |
| `admin.full` | Superuser (dev only) | E2E tests, scripts |

## Roles

| Role | Description | Assigned To |
|------|-------------|-------------|
| `Admin` | Full access to admin endpoints and Dashboard admin pages | System administrators |
| `User` | Standard tenant-scoped access | Regular users |

## Token Lifetimes

| Token Type | Default Lifetime | Configurable | Storage |
|------------|------------------|--------------|---------|
| Access Token | 1 hour | ✅ OpenIddict settings | None (stateless) |
| ID Token | 1 hour | ✅ OpenIddict settings | None (client-side) |
| Refresh Token | 14 days | ✅ OpenIddict settings | Database (encrypted) |
| Session Cookie | Session | ✅ Identity settings | Server-side |

## Key Facts

### Signing Keys
- **Algorithm**: RS256 (RSA with SHA-256)
- **Key Size**: 2048-bit
- **Storage**: PostgreSQL table `JwkKeys` in `tansu_identity` database
- **Rotation**: Every 30 days (configurable)
- **Grace Period**: 7 days (old key remains valid)
- **Public Exposure**: Via `/identity/.well-known/jwks` (public keys only)

### Why RS256?
✅ Asymmetric (public/private key pair)  
✅ Services verify with public key only  
✅ Private key never leaves Identity service  
✅ Easier key rotation  
✅ Industry standard for distributed systems  

❌ HS256 (symmetric) requires shared secret across all services (security risk)

## Common Issues

### "Invalid signature" errors
**Cause**: Service can't fetch or validate JWKS  
**Fix**: Check `/identity/.well-known/jwks` is reachable from service container

### "Issuer validation failed"
**Cause**: Token issuer doesn't match service's expected issuer  
**Dev**: Issuer is `http://127.0.0.1:8080/identity/` (note trailing slash)  
**Fix**: Ensure `Oidc:Issuer` config matches exactly (including trailing slash)

### "Audience validation failed"
**Cause**: Token `aud` claim doesn't include target service  
**Fix**: 
- Database expects `aud: ["tansu.db"]`
- Storage expects `aud: ["tansu.storage"]`
- Check client has permissions for target resources

### "Token expired"
**Cause**: Access token lifetime exceeded (1 hour default)  
**Fix**: Use refresh token to get new access token

### "Tenant mismatch"
**Cause**: `tid` claim doesn't match `X-Tansu-Tenant` header  
**Fix**: Ensure gateway sets `X-Tansu-Tenant` correctly; token issued for correct tenant

## Configuration Files

### Identity Service
- `TansuCloud.Identity/appsettings.json` — Production defaults
- `TansuCloud.Identity/appsettings.Development.json` — Dev overrides
- Env vars: `Oidc__Issuer`, `Oidc__Dashboard__ClientSecret`, `DASHBOARD_CLIENT_SECRET`

### Database Service
- `TansuCloud.Database/appsettings.json` — JWT validation settings
- Env vars: `Oidc__Issuer`, `Oidc__MetadataAddress`

### Storage Service
- `TansuCloud.Storage/appsettings.json` — JWT validation settings
- Env vars: `Oidc__Issuer`, `Oidc__MetadataAddress`

### Dashboard
- `TansuCloud.Dashboard/appsettings.json` — OIDC client settings
- Env vars: `Oidc__Authority`, `Oidc__MetadataAddress`, `Oidc__ClientId`, `Oidc__ClientSecret`

## Admin Dashboard Pages

**Current (Available):**
- `/dashboard/admin/providers` — Manage external OIDC providers (Google, Azure AD, etc.)
- `/dashboard/admin/security-events` — View security audit log (sign-ins, key rotations, etc.)

**TODO (Future Task):**
- `/dashboard/admin/identity-settings` — Comprehensive Identity configuration UI
  - Key rotation schedule
  - Token lifetime settings
  - Password policies
  - MFA configuration
  - Session timeout

**Workaround (Current):**  
Use REST API directly or update `appsettings.json`/environment variables

## Security Checklist

### Development
- ✅ HTTP allowed (`RequireHttpsMetadata=false`)
- ✅ Client Credentials enabled for testing
- ✅ Dev credentials: `admin@tansu.local` / `Passw0rd!`
- ✅ Loopback variants allowed (`127.0.0.1`, `localhost`)

### Production
- ✅ HTTPS required (`RequireHttpsMetadata=true`)
- ✅ Client Credentials disabled or restricted
- ✅ Strong admin credentials via env vars
- ✅ Single HTTPS issuer
- ✅ TLS termination at Gateway
- ✅ Persistent encryption keys
- ✅ Regular key rotation (30 days)
- ✅ Audit logs enabled
- ✅ Tenant isolation validated

## External Resources

- OpenIddict docs: https://documentation.openiddict.com/
- JWT debugger: https://jwt.io/
- OIDC playground: https://openidconnect.net/
- OAuth 2.0 playground: https://www.oauth.com/playground/

## Related Documentation

- `Architecture.md` → "Security > Authentication and Authorization Flows" (full details)
- `Guide-For-Admins-and-Tenants.md` → "Identity & Authentication" (operational guide)
- `TansuCloud.Identity/README.md` (if exists)
- `dev/identity-provisioning-token.http` (sample REST API calls)
