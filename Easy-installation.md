# TansuCloud Easy Installation Guide

This guide provides step-by-step instructions for installing TansuCloud on various Linux distributions. Perfect for system administrators deploying TansuCloud for the first time.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Installation by Distribution](#installation-by-distribution)
   - [Ubuntu / Debian](#ubuntu--debian)
   - [CentOS / RHEL / Rocky Linux](#centos--rhel--rocky-linux)
   - [Fedora](#fedora)
   - [Arch Linux](#arch-linux)
   - [Alpine Linux](#alpine-linux)
3. [TansuCloud Installation](#tansucloud-installation)
4. [Configuration](#configuration)
5. [Starting TansuCloud](#starting-tansucloud)
6. [Verification](#verification)
7. [Initial Setup](#initial-setup)
8. [Production Hardening](#production-hardening)
9. [Troubleshooting](#troubleshooting)
10. [Updating TansuCloud](#updating-tansucloud)

---

## Quick Start (TL;DR)

For experienced administrators, here's the complete installation in one sequence:

```bash
# 1. Install Docker (Ubuntu/Debian example)
sudo apt update && sudo apt install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin git
sudo usermod -aG docker $USER && newgrp docker

# 2. Clone TansuCloud
cd /opt && sudo git clone https://github.com/MusaGursoy/TansuCloud.git
cd TansuCloud && sudo chown -R $USER:$USER .

# 3. Configure environment
cp .env.example .env && nano .env  # Edit passwords and domain

# 4. Start services (uses pre-built images from GitHub Container Registry)
docker compose -f docker-compose.prod.yml --profile observability up -d

# 5. Verify
docker ps && docker logs signoz-init

# 6. Access
# http://your-domain.com/dashboard
```

**That's it!** No manual image building required - all images are pre-built and published to GitHub Container Registry (ghcr.io).

**Multi-Architecture Support:** TansuCloud images are built for both **x86_64/AMD64** and **ARM64/AARCH64** architectures. Docker automatically selects the correct architecture for your system:

- ✅ Intel/AMD servers (x86_64)
- ✅ Apple Silicon Macs (M1/M2/M3)
- ✅ Raspberry Pi 4/5 (ARM64, 8GB recommended)
- ✅ AWS Graviton, Oracle Ampere, Azure Cobalt

**For developers who want to build from source:**
See the [Building from Source](#building-from-source-optional) section below.

---

## Architecture Overview

### Production Security Model

**Exposed to Internet (via Gateway):**

- ✅ Gateway (port 80/443) - Only entry point
- ✅ Dashboard (via Gateway at `/dashboard`)
- ✅ Identity (via Gateway at `/identity`)
- ✅ Database API (via Gateway at `/db`)
- ✅ Storage API (via Gateway at `/storage`)

**Internal Docker Network Only:**

- 🔒 PostgreSQL (no host port)
- 🔒 Redis/Garnet (no host port)
- 🔒 PgCat (no host port)
- 🔒 SigNoz (no host port) - Accessed via Dashboard's embedded UI
- 🔒 ClickHouse (no host port)
- 🔒 OpenTelemetry Collector (no host port)

**Why SigNoz is Internal-Only:**

1. **Security:** Observability data can contain sensitive information
2. **Authentication:** Dashboard handles authentication, then calls SigNoz API
3. **User Experience:** End users see observability through Dashboard UI
4. **Network Isolation:** Reduces attack surface

**Data Flow:**

```
User Browser → Gateway (80/443) → Dashboard (8080)
                                      ↓
                               SigNoz API (8080)
                                      ↓
                               ClickHouse (9000)
```

All services send telemetry to OpenTelemetry Collector, which forwards to SigNoz for storage and querying.

---

## Prerequisites

TansuCloud requires:

- **Docker** (20.10 or later)
- **Docker Compose** (2.0 or later)
- **Git** (for cloning the repository)
- **2 GB RAM minimum** (4 GB recommended for production)
- **20 GB disk space minimum** (50 GB recommended for production)
- **Linux kernel 3.10+** (4.0+ recommended)
- **Architecture:** x86_64 (Intel/AMD) or ARM64 (Apple Silicon, Raspberry Pi, AWS Graviton)

---

## Installation by Distribution

### Ubuntu / Debian

**Supported versions:** Ubuntu 20.04+, Debian 10+

```bash
# Update package index
sudo apt update && sudo apt upgrade -y

# Install prerequisites
sudo apt install -y \
    apt-transport-https \
    ca-certificates \
    curl \
    gnupg \
    lsb-release \
    git

# Add Docker's official GPG key
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg

# Set up Docker repository
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
  $(lsb_release -cs) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

# Install Docker Engine
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Add current user to docker group (avoid sudo)
sudo usermod -aG docker $USER

# Apply group changes (or logout/login)
newgrp docker

# Verify installation
docker --version
docker compose version
```

---

### CentOS / RHEL / Rocky Linux

**Supported versions:** CentOS 8+, RHEL 8+, Rocky Linux 8+

```bash
# Update system
sudo dnf update -y

# Install prerequisites
sudo dnf install -y \
    git \
    yum-utils \
    device-mapper-persistent-data \
    lvm2

# Add Docker repository
sudo yum-config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo

# Install Docker Engine
sudo dnf install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Start and enable Docker service
sudo systemctl start docker
sudo systemctl enable docker

# Add current user to docker group
sudo usermod -aG docker $USER

# Apply group changes (or logout/login)
newgrp docker

# Verify installation
docker --version
docker compose version
```

**For older CentOS 7:**

```bash
sudo yum update -y
sudo yum install -y yum-utils git
sudo yum-config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo
sudo yum install -y docker-ce docker-ce-cli containerd.io

# Install Docker Compose separately
sudo curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
sudo chmod +x /usr/local/bin/docker-compose

sudo systemctl start docker
sudo systemctl enable docker
sudo usermod -aG docker $USER
newgrp docker
```

---

### Fedora

**Supported versions:** Fedora 36+

```bash
# Update system
sudo dnf update -y

# Install prerequisites
sudo dnf install -y git dnf-plugins-core

# Add Docker repository
sudo dnf config-manager --add-repo https://download.docker.com/linux/fedora/docker-ce.repo

# Install Docker Engine
sudo dnf install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Start and enable Docker service
sudo systemctl start docker
sudo systemctl enable docker

# Add current user to docker group
sudo usermod -aG docker $USER
newgrp docker

# Verify installation
docker --version
docker compose version
```

---

### Arch Linux

```bash
# Update system
sudo pacman -Syu

# Install Docker and Git
sudo pacman -S docker docker-compose git

# Start and enable Docker service
sudo systemctl start docker.service
sudo systemctl enable docker.service

# Add current user to docker group
sudo usermod -aG docker $USER
newgrp docker

# Verify installation
docker --version
docker compose version
```

---

### Alpine Linux

```bash
# Update package index
sudo apk update

# Install Docker and Git
sudo apk add docker docker-compose git

# Start Docker service
sudo rc-update add docker boot
sudo service docker start

# Add current user to docker group
sudo addgroup $USER docker
newgrp docker

# Verify installation
docker --version
docker compose version
```

---

## TansuCloud Installation

Once Docker is installed, follow these steps on **any Linux distribution**:

### Step 1: Clone Repository

```bash
# Navigate to installation directory
cd /opt  # or your preferred location

# Clone TansuCloud repository
sudo git clone https://github.com/MusaGursoy/TansuCloud.git
cd TansuCloud

# Change ownership to current user
sudo chown -R $USER:$USER /opt/TansuCloud
```

### Step 2: Create .env Configuration

```bash
# Copy example environment file
cp .env.example .env

# Edit configuration
nano .env  # or vim, vi, etc.
```

**Note:** TansuCloud uses pre-built Docker images published to **GitHub Container Registry (ghcr.io)**. You don't need to build anything - images are automatically pulled when you start services.

**Important:** To enable pre-built images, your `docker-compose.prod.yml` should reference ghcr.io images instead of local builds. Example:

```yaml
services:
  postgres:
    image: ghcr.io/musagursoy/tansucloud-postgres:latest
    # Instead of: build: { context: ., dockerfile: dev/Dockerfile.citus-pgvector }
  
  gateway:
    image: ghcr.io/musagursoy/tansucloud-gateway:latest
    # Instead of: build: { context: ., dockerfile: TansuCloud.Gateway/Dockerfile }
```

If your compose file still has `build:` sections, images will be built locally. See [Building from Source](#building-from-source-optional) section.

---

## Configuration

### Basic .env Configuration

Edit `.env` with your preferred text editor and configure these essential settings:

```bash
# =============================================================================
# Public URLs - Replace with your actual domain
# =============================================================================
PUBLIC_BASE_URL=http://your-domain.com
GATEWAY_BASE_URL=http://gateway:8080

# =============================================================================
# PostgreSQL Configuration
# =============================================================================
POSTGRES_USER=postgres
POSTGRES_PASSWORD=YOUR_SECURE_POSTGRES_PASSWORD_HERE  # Change this!

# =============================================================================
# PgCat (Connection Pooler)
# =============================================================================
PGCAT_ADMIN_USER=pgcatadmin
PGCAT_ADMIN_PASSWORD=YOUR_PGCAT_PASSWORD_HERE  # Change this!

# =============================================================================
# Dashboard OIDC Client Secret
# =============================================================================
DASHBOARD_CLIENT_SECRET=YOUR_DASHBOARD_CLIENT_SECRET_HERE  # Change this!

# =============================================================================
# SigNoz Observability
# =============================================================================
SIGNOZ_JWT_SECRET=changemewithrandom12characters  # Change this!

# SigNoz API credentials for automated initialization
# Password must be at least 12 characters with uppercase, lowercase, number, and symbol
SIGNOZ_API_EMAIL=admin@your-domain.com
SIGNOZ_API_PASSWORD=SecurePassword123!@#  # Change this! Min 12 chars

# =============================================================================
# Telemetry (Optional)
# =============================================================================
TELEMETRY__INGESTION__APIKEY=dev-telemetry-api-key-1234567890  # Change this!
TELEMETRY__ADMIN__APIKEY=dev-telemetry-admin-key-9876543210  # Change this!

# =============================================================================
# Log Reporting (Optional)
# =============================================================================
LOGREPORTING__ENABLED=true
LOGREPORTING__MAINSERVERURL=http://telemetry:8080/api/logs/report
LOGREPORTING__APIKEY=dev-telemetry-api-key-1234567890
LOGREPORTING__REPORTINTERVALMINUTES=60
LOGREPORTING__PSEUDONYMIZETENANTS=true
```

### Password Requirements

**SIGNOZ_API_PASSWORD requirements:**

- Minimum 12 characters
- At least one uppercase letter [A-Z]
- At least one lowercase letter [a-z]
- At least one number [0-9]
- At least one symbol [~!@#$%^&*()_+`-={}|[]\:";'<>?,./]

**Good examples:**

- `TansuCloud2025!@#`
- `MySecure$Pass123`
- `Adm1n@TansuCloud`

### Generate Random Passwords

```bash
# Generate secure random password (Linux)
openssl rand -base64 24

# Generate password with special characters
tr -dc 'A-Za-z0-9!@#$%^&*' < /dev/urandom | head -c 16 && echo
```

---

## Starting TansuCloud

### Production Deployment (Recommended)

```bash
# Start all services with observability enabled
# Docker automatically pulls pre-built images from ghcr.io
docker compose -f docker-compose.prod.yml --profile observability up -d
```

**What happens:**

1. Docker pulls pre-built images from GitHub Container Registry (ghcr.io)
2. Docker automatically selects the correct architecture (x86_64 or ARM64) for your system
3. First time takes 5-10 minutes to download ~2-3 GB of images
4. Subsequent starts are instant (images cached locally)
5. No building required!

### Development Mode

```bash
# Start with additional debugging ports exposed
docker compose up -d
```

### What Happens During Startup

1. **PostgreSQL** initializes and creates databases
2. **Redis (Garnet)** starts for caching and message queuing
3. **PgCat** starts as connection pooler
4. **ClickHouse** initializes for SigNoz data storage
5. **SigNoz** starts for observability (traces, metrics, logs)
6. **signoz-init** automatically registers admin user (no manual steps!)
7. **Identity** service starts (authentication/authorization)
8. **Database** service starts (tenant provisioning, document API)
9. **Storage** service starts (file storage, multipart uploads)
10. **Dashboard** service starts (admin UI)
11. **Telemetry** service starts (usage reporting)
12. **Gateway** starts and exposes port 80

**First startup takes 2-5 minutes** depending on your internet speed and server resources.

---

## Verification

### Check Container Status

```bash
# List all running containers
docker ps

# Expected: ~20 containers running
# Key containers: tansu-gateway, tansu-identity, tansu-dashboard, 
#                 tansu-db, tansu-storage, tansu-postgres, tansu-redis, 
#                 tansu-pgcat, signoz, signoz-otel-collector
```

### Check Logs

```bash
# View gateway logs
docker logs tansu-gateway

# View SigNoz initialization logs (should show success)
docker logs signoz-init

# Expected output:
# ✓ SigNoz is ready
# Registering first admin user in SigNoz...
# ✓ SigNoz initialized successfully
#   Admin user: admin@your-domain.com
#   Organization: TansuCloud
```

### Health Checks

```bash
# Check gateway health
curl http://localhost:8080/health/ready

# Expected: HTTP 200 OK

# Check individual services (inside Docker network)
docker exec tansu-gateway wget -qO- http://identity:8080/health/ready
docker exec tansu-gateway wget -qO- http://dashboard:8080/health/ready
docker exec tansu-gateway wget -qO- http://db:8080/health/ready
docker exec tansu-gateway wget -qO- http://storage:8080/health/ready
```

### Service URLs

```bash
# Gateway (main entry point)
curl http://localhost:8080/

# Dashboard
curl http://localhost:8080/dashboard/

# Dashboard Observability (embedded SigNoz data)
curl http://localhost:8080/dashboard/admin/observability/traces

# Identity service (OIDC discovery)
curl http://localhost:8080/identity/.well-known/openid-configuration
```

**Note:** By default, SigNoz UI is **NOT exposed** externally. All observability features are available through the Dashboard at `/dashboard/admin/observability/*`. Advanced users can optionally enable direct SigNoz UI access by adding `ports: ["3301:8080"]` to the signoz service in docker-compose.yml.

---

## Initial Setup

### Provision First Tenant

```bash
# Create a tenant via the provisioning API
curl -X POST http://localhost:8080/db/api/provisioning/tenants \
  -H "Content-Type: application/json" \
  -H "X-Provision-Key: letmein" \
  -d '{
    "tenantId": "acme-prod",
    "displayName": "Acme Corporation"
  }'

# Expected response:
# {
#   "tenantId": "acme-prod",
#   "displayName": "Acme Corporation",
#   "connectionString": "Host=postgres;Port=5432;Database=tenant_acme_prod;..."
# }
```

### Access Dashboard

1. **Open browser:** `http://your-domain.com/dashboard`
2. **Login with OIDC** (redirects to Identity service)
3. **Access Admin Panel**

### Access SigNoz (Observability)

**For 99% of Users (Recommended):**

Use the **TansuCloud Dashboard** at `http://your-domain.com/dashboard/admin/observability`:

- ✅ Secure by design - No direct SigNoz UI exposure
- ✅ User-friendly curated views (Overview, Traces, Logs, Configuration)
- ✅ Dashboard authenticates with SigNoz API internally
- ✅ Suitable for production deployments without additional security configuration

**For Advanced Users (~1% of deployments):**

If you need deep troubleshooting, custom dashboards, or advanced SigNoz features not yet exposed in the Dashboard UI, you can optionally enable direct access to the full SigNoz UI.

---

#### Option 1: SSH Tunnel (Recommended for temporary access)

**Best for:** Occasional debugging, troubleshooting production issues, creating custom dashboards

```bash
# From your local machine, create SSH tunnel to production server
ssh -L 3301:localhost:3301 user@your-server.com

# On the server, temporarily forward to SigNoz container
docker run --rm -p 3301:3301 --network tansucloud-network \
  alpine/socat TCP-LISTEN:3301,fork TCP:signoz:8080

# Access from local browser: http://localhost:3301
# Login credentials (from .env):
#   Email: SIGNOZ_API_EMAIL (e.g., admin@your-domain.com)
#   Password: SIGNOZ_API_PASSWORD
# Close tunnel when done (Ctrl+C in both terminals)
```

**Advantages:**

- ✅ No permanent firewall changes
- ✅ No configuration file edits required
- ✅ Automatically secure (requires SSH access)
- ✅ Easy to enable/disable on demand

---

#### Option 2: Permanent Port Exposure (Development/Staging)

**Best for:** Development environments, staging servers with restricted network access

Edit `docker-compose.yml` (dev) or `docker-compose.prod.yml` (staging):

```yaml
signoz:
  profiles: [observability]
  ports:
    - "3301:8080"  # Add this line
  # ... rest of config unchanged
```

Then restart SigNoz:

```bash
# Development
docker compose restart signoz

# Staging/Production
docker compose -f docker-compose.prod.yml --profile observability restart signoz
```

Access SigNoz UI at:

- Development: `http://127.0.0.1:3301`
- Staging: `http://staging-server.com:3301`

**⚠️ Production Warning:** If exposing port 3301 in production, you MUST:

1. Configure firewall to restrict access (see firewall rules below)
2. Use reverse proxy with TLS (see Option 3)
3. Consider IP allowlisting for additional security

---

#### Option 3: Production with Reverse Proxy (Advanced)

**Best for:** Production environments where multiple advanced users need regular SigNoz UI access

**Step 1: Expose SigNoz port internally only**

In `docker-compose.prod.yml`:

```yaml
signoz:
  profiles: [observability]
  # Do NOT add ports here - keep SigNoz internal
  # Reverse proxy will access via Docker network
```

**Step 2: Configure Nginx Reverse Proxy**

```bash
# Create Nginx configuration
sudo nano /etc/nginx/sites-available/signoz
```

Add configuration:

```nginx
# SigNoz UI (Advanced Users Only)
server {
    listen 443 ssl http2;
    server_name signoz.your-domain.com;
    
    # TLS configuration
    ssl_certificate /etc/letsencrypt/live/your-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-domain.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    
    # IP Allowlisting (IMPORTANT FOR SECURITY)
    # Option A: Allow specific office/VPN network
    allow 10.0.0.0/8;        # Internal network
    allow 203.0.113.0/24;    # Office network
    deny all;
    
    # Option B: Or allow specific admin IPs only
    # allow 203.0.113.5;     # Admin 1
    # allow 203.0.113.10;    # Admin 2
    # deny all;
    
    # Proxy to SigNoz (internal Docker network)
    location / {
        # If nginx is on host, use localhost:
        # proxy_pass http://localhost:3301;
        
        # If nginx is in Docker network:
        proxy_pass http://signoz:8080;
        
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # WebSocket support (for SigNoz live updates)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}

# Redirect HTTP to HTTPS
server {
    listen 80;
    server_name signoz.your-domain.com;
    return 301 https://$server_name$request_uri;
}
```

**Step 3: Enable and test**

```bash
# Enable site
sudo ln -s /etc/nginx/sites-available/signoz /etc/nginx/sites-enabled/

# Test configuration
sudo nginx -t

# Reload Nginx
sudo systemctl reload nginx

# Obtain SSL certificate (if not already done)
sudo certbot --nginx -d signoz.your-domain.com
```

**Step 4: Configure firewall**

```bash
# Allow HTTPS for SigNoz subdomain
sudo ufw allow 443/tcp comment "SigNoz HTTPS"

# Verify firewall rules
sudo ufw status numbered
```

Access SigNoz at: `https://signoz.your-domain.com`

**Advantages:**

- ✅ HTTPS/TLS encryption
- ✅ IP-based access control
- ✅ Centralized authentication (can add HTTP Basic Auth if needed)
- ✅ Proper subdomain with SSL certificate
- ✅ WebSocket support for real-time updates

---

#### Firewall Rules for Direct Access

If using Option 2 (permanent port exposure), configure firewall:

```bash
# Ubuntu/Debian (ufw)
# Allow only from specific network
sudo ufw allow from 10.0.0.0/8 to any port 3301 comment "SigNoz UI (internal network only)"

# Or allow from specific IP
sudo ufw allow from 203.0.113.5 to any port 3301 comment "SigNoz UI (admin IP)"

# CentOS/RHEL/Rocky (firewalld)
# Create rich rule for IP restriction
sudo firewall-cmd --permanent --add-rich-rule='rule family="ipv4" source address="10.0.0.0/8" port port="3301" protocol="tcp" accept'
sudo firewall-cmd --reload

# Verify rules
sudo ufw status numbered           # Ubuntu/Debian
sudo firewall-cmd --list-all       # CentOS/RHEL
```

---

#### When to Use Direct SigNoz UI Access

**✅ Use direct SigNoz UI when you need to:**

- Create custom dashboards with complex PromQL/SQL queries
- Set up advanced alerts with multi-condition logic
- Debug OpenTelemetry Collector pipeline configuration
- Analyze query performance and cardinality issues
- Configure retention policies beyond governance defaults
- Access experimental/beta SigNoz features
- Perform deep-dive trace analysis with custom filters
- Configure sampling rules for high-traffic services

**❌ You do NOT need direct SigNoz UI for:**

- Viewing service health and request rates (use Dashboard Overview)
- Browsing distributed traces (use Dashboard Traces page)
- Checking logs (use Dashboard Logs page - coming soon)
- Basic alerting (use Dashboard Configuration page)
- Day-to-day observability (use Dashboard curated views)

---

#### Security Best Practices

**⚠️ CRITICAL SECURITY WARNINGS:**

1. **Never expose SigNoz directly to the public internet**
   - Observability data can contain sensitive information (API keys, user data, internal architecture, PII)
   - Always use firewall rules, VPN, or IP allowlisting

2. **Always use HTTPS/TLS in production**
   - Use nginx/caddy reverse proxy with Let's Encrypt certificates
   - Never send SigNoz credentials over unencrypted HTTP

3. **Rotate credentials regularly**
   - Change `SIGNOZ_API_PASSWORD` in `.env` every 90 days
   - Use password manager to generate strong passwords (12+ characters)

4. **Monitor access logs**
   - Review nginx/caddy access logs for SigNoz subdomain
   - Set up alerts for unusual access patterns

5. **Principle of least privilege**
   - Only enable direct SigNoz access for users who absolutely need it
   - Consider creating read-only SigNoz users for developers

6. **Audit trail**
   - SigNoz logs all UI actions - review periodically
   - Track who has access to SigNoz credentials

**Authentication:**

- Login credentials are defined in `.env`:
  - Email: `SIGNOZ_API_EMAIL` (default: `admin@your-domain.com`)
  - Password: `SIGNOZ_API_PASSWORD` (minimum 12 characters with uppercase, lowercase, number, symbol)
- Credentials are automatically configured by `signoz-init` container on first startup
- To change password: Edit `.env`, run `docker compose restart signoz-init signoz`

---

## Production Hardening

### 1. Configure Firewall

```bash
# Ubuntu/Debian (ufw)
sudo ufw allow 22/tcp      # SSH
sudo ufw allow 80/tcp      # HTTP
sudo ufw allow 443/tcp     # HTTPS (if using TLS)
sudo ufw enable

# CentOS/RHEL/Rocky (firewalld)
sudo firewall-cmd --permanent --add-service=ssh
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --permanent --add-service=https
sudo firewall-cmd --reload

# Check status
sudo ufw status           # Ubuntu/Debian
sudo firewall-cmd --list-all  # CentOS/RHEL
```

### 2. Set Up TLS/SSL

**Option A: Using Nginx Reverse Proxy**

```bash
# Install Nginx
sudo apt install nginx  # Ubuntu/Debian
sudo dnf install nginx  # CentOS/RHEL

# Install Certbot for Let's Encrypt
sudo apt install certbot python3-certbot-nginx  # Ubuntu/Debian
sudo dnf install certbot python3-certbot-nginx  # CentOS/RHEL

# Obtain SSL certificate
sudo certbot --nginx -d your-domain.com

# Configure Nginx to proxy to Gateway
sudo nano /etc/nginx/sites-available/tansucloud

# Add configuration:
server {
    listen 80;
    server_name your-domain.com;
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name your-domain.com;

    ssl_certificate /etc/letsencrypt/live/your-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-domain.com/privkey.pem;

    location / {
        proxy_pass http://localhost:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

# Enable site and restart Nginx
sudo ln -s /etc/nginx/sites-available/tansucloud /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx
```

**Option B: Using Caddy (Automatic HTTPS)**

```bash
# Install Caddy
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install caddy

# Create Caddyfile
sudo nano /etc/caddy/Caddyfile

# Add configuration:
your-domain.com {
    reverse_proxy localhost:8080
}

# Reload Caddy (automatic HTTPS via Let's Encrypt)
sudo systemctl reload caddy
```

### 3. Update PUBLIC_BASE_URL

After configuring TLS, update `.env`:

```bash
# Edit .env
nano /opt/TansuCloud/.env

# Update URL to use HTTPS
PUBLIC_BASE_URL=https://your-domain.com

# Restart services
cd /opt/TansuCloud
docker compose -f docker-compose.prod.yml --profile observability down
docker compose -f docker-compose.prod.yml --profile observability up -d
```

### 4. Enable Docker Logging

```bash
# Configure Docker daemon logging
sudo nano /etc/docker/daemon.json

# Add logging configuration:
{
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "10m",
    "max-file": "3"
  }
}

# Restart Docker
sudo systemctl restart docker

# Restart TansuCloud
cd /opt/TansuCloud
docker compose -f docker-compose.prod.yml --profile observability restart
```

### 5. Set Up Automatic Backups

```bash
# Create backup script
sudo nano /usr/local/bin/backup-tansucloud.sh

# Add script content:
#!/bin/bash
BACKUP_DIR="/backup/tansucloud"
DATE=$(date +%Y%m%d_%H%M%S)

mkdir -p $BACKUP_DIR

# Backup Docker volumes
docker run --rm -v tansu-pgdata:/data -v $BACKUP_DIR:/backup \
  alpine tar czf /backup/pgdata-$DATE.tar.gz -C /data .

docker run --rm -v tansu-garnetdata:/data -v $BACKUP_DIR:/backup \
  alpine tar czf /backup/garnetdata-$DATE.tar.gz -C /data .

docker run --rm -v tansu-storagedata:/data -v $BACKUP_DIR:/backup \
  alpine tar czf /backup/storagedata-$DATE.tar.gz -C /data .

# Keep only last 7 days
find $BACKUP_DIR -name "*.tar.gz" -mtime +7 -delete

# Make script executable
sudo chmod +x /usr/local/bin/backup-tansucloud.sh

# Add to crontab (daily at 2 AM)
sudo crontab -e
# Add line:
0 2 * * * /usr/local/bin/backup-tansucloud.sh
```

### 6. Set Up Monitoring

**Access Observability via TansuCloud Dashboard:**

```bash
# Navigate to Dashboard observability section
http://your-domain.com/dashboard/admin/observability

# Available views:
# - /traces - View distributed traces
# - /logs - Search and filter logs (future)
# - /metrics - View performance metrics (future)
```

**Dashboard automatically:**

- Authenticates with SigNoz API using credentials from `.env`
- Displays traces, metrics, and logs in embedded UI
- No direct SigNoz UI access needed

**For advanced debugging (optional):**

- Use SSH tunnel to access SigNoz UI directly (see "Access SigNoz" section above)
- Configure alerts and dashboards in SigNoz
- Set up notification channels (email, Slack, PagerDuty, etc.)

---

## Building from Source (Optional)

**Most users don't need this section!** TansuCloud provides pre-built images on GitHub Container Registry.

This section is for:

- Developers contributing to TansuCloud
- Users who want to modify the source code
- Organizations that require building from source for security/compliance

### Prerequisites for Building

- .NET 8 SDK or later
- Docker with BuildKit enabled
- 8 GB RAM minimum (for building)
- 30 GB disk space (for build artifacts)

### Build Custom PostgreSQL Image

```bash
# Navigate to TansuCloud directory
cd /opt/TansuCloud

# Build custom Postgres image with Citus and pgvector
docker build -f dev/Dockerfile.citus-pgvector -t ghcr.io/musagursoy/tansucloud-postgres:latest .

# This takes 5-10 minutes
# Includes: PostgreSQL 17, Citus, pgvector, pg_trgm
```

### Build Application Images

```bash
# Build all TansuCloud services
docker compose -f docker-compose.prod.yml build

# Or build specific service
docker compose -f docker-compose.prod.yml build gateway

# Build time: 10-15 minutes first time, 2-5 minutes incremental
```

**What gets built:**

- Gateway (YARP reverse proxy)
- Identity (OIDC authentication)
- Dashboard (Blazor UI)
- Database (tenant provisioning + document API)
- Storage (file storage service)
- Telemetry (usage reporting)

### Use Local Images

After building, update `docker-compose.prod.yml` to use local images instead of ghcr.io:

```yaml
# Change from:
services:
  gateway:
    image: ghcr.io/musagursoy/tansucloud-gateway:latest

# To:
services:
  gateway:
    build:
      context: .
      dockerfile: TansuCloud.Gateway/Dockerfile
```

Or tag your local builds:

```bash
docker tag tansucloud-gateway ghcr.io/musagursoy/tansucloud-gateway:latest
```

### Publish Custom Images (For Maintainers)

```bash
# Login to GitHub Container Registry
echo $GITHUB_TOKEN | docker login ghcr.io -u USERNAME --password-stdin

# Tag images
docker tag tansucloud-postgres:latest ghcr.io/musagursoy/tansucloud-postgres:v1.0.0
docker tag tansucloud-gateway:latest ghcr.io/musagursoy/tansucloud-gateway:v1.0.0
# ... (repeat for all services)

# Push to registry
docker push ghcr.io/musagursoy/tansucloud-postgres:v1.0.0
docker push ghcr.io/musagursoy/tansucloud-gateway:v1.0.0
# ... (repeat for all services)
```

---

## Troubleshooting

### Image Pull Failures

**Cannot pull images from ghcr.io:**

```bash
# Check if you can reach GitHub Container Registry
curl -I https://ghcr.io

# Try pulling manually
docker pull ghcr.io/musagursoy/tansucloud-gateway:latest

# Common issues:
# - Network/firewall blocking ghcr.io
# - Rate limiting (wait and retry)
# - Wrong image name/tag
```

**Slow image downloads:**

```bash
# Check your internet connection
curl -o /dev/null https://ghcr.io/v2/ -w "Speed: %{speed_download} bytes/sec\n"

# Use a faster mirror or VPN if in restricted region
```

**Image not found (404):**

```bash
# Verify image exists
curl -H "Authorization: Bearer $(echo $GITHUB_TOKEN)" \
  https://ghcr.io/v2/musagursoy/tansucloud-gateway/tags/list

# If images don't exist yet, build from source (see Building from Source section)
```

### Build Failures (For Developers)

**PostgreSQL Image Build Failed:**

```bash
# Check if Dockerfile exists
ls -l dev/Dockerfile.citus-pgvector

# Clean Docker build cache and retry
docker builder prune -a
docker build -f dev/Dockerfile.citus-pgvector -t tansu/citus-pgvector:local .

# Check build logs for specific errors
docker build -f dev/Dockerfile.citus-pgvector -t tansu/citus-pgvector:local . 2>&1 | tee build.log
```

**Application Build Failed:**

```bash
# Check Docker Compose file syntax
docker compose -f docker-compose.prod.yml config

# Build with verbose output
docker compose -f docker-compose.prod.yml build --progress=plain --no-cache

# Build specific service to isolate issue
docker compose -f docker-compose.prod.yml build gateway

# Common issues:
# - Network timeout: Increase Docker timeout in /etc/docker/daemon.json
# - Disk space: Check with df -h, clean up with docker system prune
# - Memory: Ensure at least 4GB RAM available
```

**Out of Disk Space During Build:**

```bash
# Check disk usage
df -h

# Clean up Docker resources
docker system prune -a --volumes

# Remove dangling images
docker image prune -a

# Remove unused build cache
docker builder prune -a
```

### Container Won't Start

```bash
# Check container logs
docker logs <container-name>

# Check Docker daemon logs
sudo journalctl -u docker.service -n 100 --no-pager

# Restart specific container
docker restart <container-name>

# Rebuild and restart all
cd /opt/TansuCloud
docker compose -f docker-compose.prod.yml --profile observability down
docker compose -f docker-compose.prod.yml --profile observability up -d --build
```

### Port Already in Use

```bash
# Check what's using port 80
sudo lsof -i :80
sudo netstat -tlnp | grep :80

# Stop conflicting service (e.g., Apache)
sudo systemctl stop apache2  # Ubuntu/Debian
sudo systemctl stop httpd    # CentOS/RHEL

# Disable on boot
sudo systemctl disable apache2
```

### SigNoz Initialization Failed

```bash
# Check signoz-init logs
docker logs signoz-init

# Common issues:
# 1. Password too weak (must be 12+ chars with uppercase, lowercase, number, symbol)
# 2. SigNoz not healthy yet (wait 30 seconds and check again)

# Manually re-run initialization
docker compose restart signoz-init
docker logs -f signoz-init
```

### Database Connection Issues

```bash
# Check PostgreSQL logs
docker logs tansu-postgres

# Check PgCat logs
docker logs tansu-pgcat

# Test database connection
docker exec -it tansu-postgres psql -U postgres -c "SELECT version();"

# Check PgCat health
docker exec tansu-pgcat cat /proc/1/cmdline
```

### Out of Disk Space

```bash
# Check disk usage
df -h

# Clean up Docker resources
docker system prune -a --volumes

# Remove old images
docker image prune -a

# Check volume sizes
docker system df -v
```

### Gateway Returns 502 Bad Gateway

```bash
# Check if all services are healthy
docker ps

# Check gateway logs
docker logs tansu-gateway

# Check backend service health
docker exec tansu-gateway wget -qO- http://identity:8080/health/ready
docker exec tansu-gateway wget -qO- http://dashboard:8080/health/ready
docker exec tansu-gateway wget -qO- http://db:8080/health/ready
docker exec tansu-gateway wget -qO- http://storage:8080/health/ready

# Restart gateway
docker restart tansu-gateway
```

### Reset Everything (Fresh Start)

```bash
# WARNING: This deletes all data!

# Stop and remove all containers
cd /opt/TansuCloud
docker compose -f docker-compose.prod.yml --profile observability down -v

# Remove all volumes (ALL DATA WILL BE LOST)
docker volume rm tansu-pgdata tansu-garnetdata tansu-storagedata \
  signoz-clickhouse-data signoz-zookeeper-data signoz-data \
  tansu-gateway-keys tansu-dashboard-keys tansu-telemetrydata

# Start fresh
docker compose -f docker-compose.prod.yml --profile observability up -d
```

---

## Updating TansuCloud

### Pull Latest Changes

```bash
# Navigate to installation directory
cd /opt/TansuCloud

# Backup current .env
cp .env .env.backup

# Pull latest code
git fetch origin
git pull origin master

# Check if .env needs updates (compare with .env.example)
diff .env .env.example
```

### Apply Updates

```bash
# Navigate to TansuCloud directory
cd /opt/TansuCloud

# Pull latest code
git pull origin master

# Pull latest images from registry
docker compose -f docker-compose.prod.yml pull

# Restart services with new images
docker compose -f docker-compose.prod.yml --profile observability down
docker compose -f docker-compose.prod.yml --profile observability up -d

# Watch logs during startup
docker compose -f docker-compose.prod.yml --profile observability logs -f
```

**What gets updated:**

- ✅ Latest TansuCloud images from ghcr.io
- ✅ Configuration changes from updated docker-compose files
- ✅ Updated .env settings (preserve your passwords!)
- ⚠️ Data in volumes is preserved (Postgres data, Redis cache, file storage)

**No rebuilding required** - just pull and restart!

### Selective Update (Specific Service)

```bash
# Pull and restart only specific service
docker compose -f docker-compose.prod.yml pull gateway
docker compose -f docker-compose.prod.yml up -d gateway
```

### Building from Source After Update

If you're building from source (developers), rebuild after pulling:

### Building from Source After Update

If you're building from source (developers), rebuild after pulling:

```bash
# Rebuild changed services
docker compose -f docker-compose.prod.yml build

# Restart with new builds
docker compose -f docker-compose.prod.yml up -d
```

### Zero-Downtime Updates (Advanced)

```bash
# Use blue-green deployment strategy
# 1. Start new version on different ports
# 2. Test new version
# 3. Update reverse proxy to point to new version
# 4. Shut down old version

# See docker-compose documentation for advanced deployment strategies
```

---

## Image Management

### Pre-Built Images (Recommended)

TansuCloud publishes official **multi-architecture images** to **GitHub Container Registry (ghcr.io)**:

```bash
# Check pulled images
docker images | grep ghcr.io/musagursoy

# Expected images (pulled automatically):
# ghcr.io/musagursoy/tansucloud-postgres:latest    - PostgreSQL + Citus + pgvector (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-gateway:latest     - YARP reverse proxy (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-identity:latest    - OIDC authentication (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-dashboard:latest   - Blazor admin UI (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-db:latest          - Database provisioning API (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-storage:latest     - File storage service (x86_64, ARM64)
# ghcr.io/musagursoy/tansucloud-telemetry:latest   - Usage reporting (x86_64, ARM64)
```

**Architecture Support:**
All TansuCloud images include both **linux/amd64** (x86_64) and **linux/arm64** (AARCH64) variants. Docker automatically pulls the correct architecture for your system.

**Third-Party Images (Also Pulled Automatically):**

```bash
# clickhouse/clickhouse-server:latest
# bitnami/zookeeper:latest
# signoz/signoz:latest
# signoz/signoz-otel-collector:latest
# signoz/signoz-schema-migrator:latest
# ghcr.io/microsoft/garnet:latest
# ghcr.io/postgresml/pgcat:latest
# mcr.microsoft.com/powershell:latest
# prometheuscommunity/postgres-exporter:latest
# oliver006/redis_exporter:latest
```

### Update Images

```bash
# Pull latest images
docker compose -f docker-compose.prod.yml pull

# Check for updates
docker images | grep ghcr.io/musagursoy
```

### Use Specific Version

Edit `docker-compose.prod.yml` to pin versions:

```yaml
services:
  gateway:
    image: ghcr.io/musagursoy/tansucloud-gateway:v1.0.0  # Instead of :latest
```

### Built Images Overview

For developers building from source:

See [Building from Source](#building-from-source-optional) section for building locally.

### Clean Up Old Images

```bash
# Remove unused images
docker image prune

# Remove all unused images (including dangling)
docker image prune -a

# Remove specific image
docker rmi tansucloud-gateway:old-tag

# Force remove (if containers exist)
docker rmi -f tansucloud-gateway:old-tag
```

---

## System Requirements by Scale

### Small Deployment (1-100 users)

- **CPU:** 2 cores (x86_64 or ARM64)
- **RAM:** 4 GB
- **Disk:** 50 GB SSD
- **Network:** 10 Mbps
- **Examples:** Raspberry Pi 4 (8GB), small VPS, entry-level cloud VM

### Medium Deployment (100-1000 users)

- **CPU:** 4 cores (x86_64 or ARM64)
- **RAM:** 8 GB
- **Disk:** 200 GB SSD
- **Network:** 100 Mbps
- **Examples:** AWS t4g.large (Graviton), mid-tier VPS, dedicated server

### Large Deployment (1000+ users)

- **CPU:** 8+ cores (x86_64 or ARM64)
- **RAM:** 16+ GB
- **Disk:** 500 GB+ SSD (NVMe recommended)
- **Network:** 1 Gbps
- **Consider:** Separate database server, load balancer, CDN
- **Examples:** AWS c7g instances (Graviton), high-performance dedicated servers

---

## Getting Help

- **Documentation:** See `README.md` and other docs in the repository
- **Issues:** Report bugs at <https://github.com/MusaGursoy/TansuCloud/issues>
- **Logs:** Always include relevant logs when asking for help
- **Community:** (Add your community channels here)

---

## License

TansuCloud is licensed under the terms specified in `LICENSE.txt`.

---

**Last Updated:** October 29, 2025
