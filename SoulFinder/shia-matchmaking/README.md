# Shia Muslim Matchmaking Event Registration

> Registration website for the Shia Muslim Matchmaking Event sponsored by **Husaynia Islamic Society of Seattle**.

Built with Next.js 16, TypeScript, Tailwind CSS, shadcn/ui, Prisma, and Stripe.

---

## Table of Contents

- [Quick Start (Local Development)](#quick-start-local-development)
- [Project Structure](#project-structure)
- [Environment Modes](#environment-modes)
- [Database Setup](#database-setup)
- [Stripe Payments](#stripe-payments)
- [Admin Panel](#admin-panel)
- [Production Deployment (Azure)](#production-deployment-azure)
- [Azure Resources Required](#azure-resources-required)
- [GitHub Actions CI/CD](#github-actions-cicd)
- [Switching Between Local and Production](#switching-between-local-and-production)
- [NPM Scripts Reference](#npm-scripts-reference)
- [Troubleshooting](#troubleshooting)

---

## Quick Start (Local Development)

**Prerequisites:** Node.js 20+ and npm.

```bash
# 1. Clone and navigate to the project
cd shia-matchmaking

# 2. Install dependencies, generate Prisma client, create SQLite database
npm run setup

# 3. (Optional) Seed the database with sample data
npm run db:seed

# 4. Start the development server
npm run dev
```

Open [http://localhost:3000](http://localhost:3000) in your browser.

That's it! No database server, no API keys, no external services needed. The local setup uses:
- **SQLite** — database file created automatically at `dev.db`
- **Dev payment bypass** — Stripe is skipped; registrations are marked as PAID immediately

---

## Project Structure

```
shia-matchmaking/
├── .github/workflows/
│   └── azure-deploy.yml          # CI/CD pipeline for Azure
├── prisma/
│   ├── schema.prisma             # Active Prisma schema (SQLite for dev)
│   ├── schema.sqlite.prisma      # SQLite schema (local development)
│   ├── schema.sqlserver.prisma   # SQL Server schema (Azure production)
│   ├── seed.ts                   # Database seed script
│   └── migrations/               # Database migrations
├── src/
│   ├── app/                      # Next.js App Router pages
│   │   ├── page.tsx              # Landing page
│   │   ├── register/             # Registration wizard
│   │   ├── about/                # About page
│   │   ├── faq/                  # FAQ page
│   │   ├── privacy/              # Privacy policy
│   │   ├── terms/                # Terms & conditions
│   │   ├── admin/                # Admin dashboard
│   │   └── api/                  # API routes
│   ├── components/
│   │   ├── ui/                   # shadcn/ui components
│   │   ├── registration/         # 8-step registration wizard
│   │   └── layout/               # Header, Footer, IslamicPattern
│   ├── lib/
│   │   ├── prisma.ts             # Database client
│   │   ├── stripe.ts             # Stripe client
│   │   └── validations.ts        # Zod validation schemas
│   ├── hooks/                    # React hooks
│   └── types/                    # TypeScript types & constants
├── .env                          # Local environment (gitignored)
├── .env.example                  # Template for local development
├── .env.production.example       # Template for Azure production
├── next.config.ts                # Next.js configuration
└── package.json
```

---

## Environment Modes

The application operates in two modes controlled by environment variables:

### Local / Development Mode
| Setting | Value | Effect |
|---------|-------|--------|
| `DATABASE_URL` | `file:./dev.db` | Uses local SQLite file |
| `DEV_SKIP_PAYMENT` | `true` | Skips Stripe, auto-marks registrations as PAID |
| `STRIPE_SECRET_KEY` | *(empty)* | Not needed in dev mode |

### Production Mode (Azure)
| Setting | Value | Effect |
|---------|-------|--------|
| `DATABASE_URL` | `sqlserver://...` | Connects to Azure SQL Database |
| `DEV_SKIP_PAYMENT` | `false` or unset | Real Stripe payments required |
| `STRIPE_SECRET_KEY` | `sk_live_xxx` | Live Stripe key |

---

## Database Setup

### Local Development (SQLite)

SQLite requires zero setup. The database file (`dev.db`) is created automatically when you run migrations.

```bash
# Create/reset the database
npx prisma migrate dev

# View data in Prisma Studio (browser-based GUI)
npm run db:studio

# Seed with sample data
npm run db:seed

# Reset database (deletes all data)
npm run db:reset
```

### Production (Azure SQL Server)

1. Switch to the SQL Server schema:
   ```bash
   npm run db:use-sqlserver
   # or manually: copy prisma/schema.sqlserver.prisma prisma/schema.prisma
   ```

2. Set `DATABASE_URL` to your Azure SQL connection string:
   ```
   DATABASE_URL="sqlserver://YOUR-SERVER.database.windows.net:1433;database=matchmaking-db;user=sqladmin;password=YOUR_PASSWORD;encrypt=true"
   ```

3. Run migrations against production:
   ```bash
   npx prisma migrate deploy
   ```

4. To switch back to SQLite for local dev:
   ```bash
   npm run db:use-sqlite
   npx prisma migrate dev
   ```

---

## Stripe Payments

### Local Development

No Stripe account needed. When `DEV_SKIP_PAYMENT="true"`:
- Registration API skips Stripe checkout
- Registrations are marked as PAID immediately
- A mock payment record is created
- User is redirected straight to the confirmation page

### Production (with Stripe Test Mode)

To test with real Stripe (but no real charges):

1. Create a [Stripe account](https://dashboard.stripe.com/register)
2. Get your **test mode** keys from the Stripe Dashboard
3. Update `.env`:
   ```env
   NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY="pk_test_xxx"
   STRIPE_SECRET_KEY="sk_test_xxx"
   DEV_SKIP_PAYMENT="false"
   ```
4. For webhooks locally, use the [Stripe CLI](https://stripe.com/docs/stripe-cli):
   ```bash
   stripe listen --forward-to localhost:3000/api/webhook/stripe
   ```
   Copy the webhook signing secret to `STRIPE_WEBHOOK_SECRET`.

### Production (Live Mode)

1. Activate your Stripe account
2. Switch to **live mode** keys in the Stripe Dashboard
3. Set in Azure App Service Configuration:
   ```
   NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY=pk_live_xxx
   STRIPE_SECRET_KEY=sk_live_xxx
   DEV_SKIP_PAYMENT=false
   ```
4. Create a webhook endpoint in Stripe Dashboard:
   - URL: `https://your-domain.azurewebsites.net/api/webhook/stripe`
   - Events: `checkout.session.completed`, `checkout.session.expired`
   - Copy the signing secret to `STRIPE_WEBHOOK_SECRET`

---

## Admin Panel

Access at [http://localhost:3000/admin](http://localhost:3000/admin).

**Features:**
- Dashboard with registration stats (total, paid, revenue, gender split)
- Filterable/sortable registrations table
- Individual registration detail view
- Matchmaking notes per registrant
- CSV export

**Default login** (dev only): `admin@husaynia.org` / `admin`

> ⚠️ For production, implement proper NextAuth.js authentication with Microsoft Entra ID or hashed credentials.

---

## Production Deployment (Azure)

### Azure Resources Required

| Resource | Service | Tier / Notes |
|----------|---------|-------------|
| **Resource Group** | `rg-husaynia-matchmaking` | Container for all resources |
| **App Service Plan** | Linux, Node 20 | B1 tier minimum (scale as needed) |
| **App Service** | `husaynia-matchmaking` | Hosts the Next.js app |
| **Azure SQL Server** | `husaynia-sql-server` | Serverless Gen5, 1 vCore |
| **Azure SQL Database** | `matchmaking-db` | Serverless tier (auto-pause) |
| **Storage Account** | `husayniamatchphotos` | Blob container for profile photos |
| **Application Insights** | Connected to App Service | Error tracking & telemetry |
| **Key Vault** *(optional)* | For secrets | Store connection strings & API keys |

### Step-by-Step Deployment

#### 1. Provision Azure Resources

```bash
# Login to Azure CLI
az login

# Create resource group
az group create --name rg-husaynia-matchmaking --location westus2

# Create App Service Plan
az appservice plan create \
  --name husaynia-plan \
  --resource-group rg-husaynia-matchmaking \
  --sku B1 --is-linux

# Create App Service
az webapp create \
  --name husaynia-matchmaking \
  --resource-group rg-husaynia-matchmaking \
  --plan husaynia-plan \
  --runtime "NODE:20-lts"

# Create SQL Server
az sql server create \
  --name husaynia-sql-server \
  --resource-group rg-husaynia-matchmaking \
  --location westus2 \
  --admin-user sqladmin \
  --admin-password 'YOUR_STRONG_PASSWORD'

# Create SQL Database (Serverless)
az sql db create \
  --name matchmaking-db \
  --resource-group rg-husaynia-matchmaking \
  --server husaynia-sql-server \
  --compute-model Serverless \
  --edition GeneralPurpose \
  --family Gen5 \
  --capacity 1

# Allow Azure services to access SQL
az sql server firewall-rule create \
  --name AllowAzureServices \
  --resource-group rg-husaynia-matchmaking \
  --server husaynia-sql-server \
  --start-ip-address 0.0.0.0 \
  --end-ip-address 0.0.0.0

# Create Storage Account
az storage account create \
  --name husayniamatchphotos \
  --resource-group rg-husaynia-matchmaking \
  --sku Standard_LRS

# Create blob container
az storage container create \
  --name profile-photos \
  --account-name husayniamatchphotos
```

#### 2. Configure App Service Environment Variables

Set all variables from `.env.production.example` in Azure:

```bash
az webapp config appsettings set \
  --name husaynia-matchmaking \
  --resource-group rg-husaynia-matchmaking \
  --settings \
    DATABASE_URL="sqlserver://husaynia-sql-server.database.windows.net:1433;database=matchmaking-db;user=sqladmin;password=YOUR_PASSWORD;encrypt=true" \
    NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY="pk_live_xxx" \
    STRIPE_SECRET_KEY="sk_live_xxx" \
    STRIPE_WEBHOOK_SECRET="whsec_xxx" \
    NEXT_PUBLIC_APP_URL="https://husaynia-matchmaking.azurewebsites.net" \
    REGISTRATION_FEE_CENTS="5000" \
    DEV_SKIP_PAYMENT="false" \
    NEXTAUTH_SECRET="$(openssl rand -base64 48)" \
    NEXTAUTH_URL="https://husaynia-matchmaking.azurewebsites.net"
```

#### 3. Deploy the Application

**Option A: GitHub Actions (recommended)**

1. In the Azure Portal, download the **Publish Profile** from your App Service
2. Add it as a GitHub secret named `AZURE_WEBAPP_PUBLISH_PROFILE`
3. Also add `DATABASE_URL`, `NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY`, and `NEXT_PUBLIC_APP_URL` as GitHub secrets
4. Push to `main` — the workflow in `.github/workflows/azure-deploy.yml` handles the rest

**Option B: Manual deployment**

```bash
# Switch to SQL Server schema
npm run db:use-sqlserver

# Install, generate, build
npm ci
npx prisma generate
npm run build

# Run migrations against Azure SQL
DATABASE_URL="sqlserver://..." npx prisma migrate deploy

# Deploy to Azure
az webapp deploy \
  --name husaynia-matchmaking \
  --resource-group rg-husaynia-matchmaking \
  --src-path .next/standalone
```

#### 4. Configure Stripe Webhook

1. Go to [Stripe Dashboard → Webhooks](https://dashboard.stripe.com/webhooks)
2. Add endpoint: `https://husaynia-matchmaking.azurewebsites.net/api/webhook/stripe`
3. Select events: `checkout.session.completed`, `checkout.session.expired`
4. Copy the signing secret → set as `STRIPE_WEBHOOK_SECRET` in App Service config

#### 5. Verify Deployment

```bash
# Check the site is live
curl https://husaynia-matchmaking.azurewebsites.net

# Check API health
curl https://husaynia-matchmaking.azurewebsites.net/api/admin/registrations
```

---

## GitHub Actions CI/CD

The workflow at `.github/workflows/azure-deploy.yml` runs on every push to `main`:

1. Checks out code
2. Installs dependencies
3. Switches to SQL Server Prisma schema
4. Generates Prisma client
5. Builds the Next.js app
6. Deploys to Azure App Service

**Required GitHub Secrets:**

| Secret | Description |
|--------|-------------|
| `AZURE_WEBAPP_PUBLISH_PROFILE` | Download from Azure Portal → App Service → Get publish profile |
| `DATABASE_URL` | Azure SQL connection string |
| `NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY` | Stripe publishable key |
| `NEXT_PUBLIC_APP_URL` | Production URL |

---

## Switching Between Local and Production

### Local → Production Database

```bash
# 1. Switch schema to SQL Server
npm run db:use-sqlserver

# 2. Set DATABASE_URL to Azure SQL
export DATABASE_URL="sqlserver://..."

# 3. Generate client + deploy migrations
npx prisma generate
npx prisma migrate deploy
```

### Production → Local Database

```bash
# 1. Switch schema to SQLite
npm run db:use-sqlite

# 2. Regenerate client + run migrations
npx prisma generate
npx prisma migrate dev

# 3. Seed sample data
npm run db:seed
```

---

## NPM Scripts Reference

| Script | Description |
|--------|-------------|
| `npm run dev` | Start development server (port 3000) |
| `npm run build` | Production build |
| `npm run start` | Start production server |
| `npm run lint` | Run ESLint |
| `npm run setup` | Full local setup (install + generate + migrate) |
| `npm run setup:prod` | Full production setup (install + sqlserver + generate + migrate + build) |
| `npm run db:generate` | Generate Prisma client |
| `npm run db:migrate` | Run dev migrations |
| `npm run db:migrate:prod` | Deploy migrations to production |
| `npm run db:studio` | Open Prisma Studio (database GUI) |
| `npm run db:seed` | Seed database with sample data |
| `npm run db:reset` | Reset database (delete all data) |
| `npm run db:use-sqlite` | Switch to SQLite schema (local dev) |
| `npm run db:use-sqlserver` | Switch to SQL Server schema (production) |

---

## Troubleshooting

### "Cannot find module '@prisma/client'"
```bash
npx prisma generate
```

### Database file not found / empty data
```bash
npx prisma migrate dev
npm run db:seed
```

### Stripe errors in development
Make sure `DEV_SKIP_PAYMENT="true"` in your `.env` file. This bypasses Stripe entirely.

### Port 3000 already in use
```bash
npm run dev -- --port 3001
```

### Switching databases lost my migrations
Each database provider has its own migration history. When switching:
```bash
# SQLite
npm run db:use-sqlite && npx prisma migrate dev --name init

# SQL Server
npm run db:use-sqlserver && npx prisma migrate deploy
```

### Azure SQL connection refused
- Ensure the Azure SQL firewall allows your IP or Azure services
- Check the connection string format includes `encrypt=true`
- Verify the database user credentials

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Framework | Next.js 16 (App Router, TypeScript) |
| Styling | Tailwind CSS 4 + shadcn/ui |
| Database (local) | SQLite via Prisma 6 |
| Database (prod) | Azure SQL Server via Prisma 6 |
| Payments | Stripe Checkout |
| Hosting | Azure App Service (Linux, Node 20) |
| CI/CD | GitHub Actions |

---

**Sponsored by [Husaynia Islamic Society of Seattle](https://www.husaynia.org)**
