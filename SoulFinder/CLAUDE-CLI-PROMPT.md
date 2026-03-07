# Claude CLI Prompt — Shia Muslim Match Making Event Registration Site

Copy everything below the line into Claude CLI (`claude` in terminal) to plan and build the project.

---

## THE PROMPT

```
You are building a **Shia Muslim Match Making Event Registration Website** sponsored by **Husaynia Islamic Society of Seattle**. This is a production application to be hosted on **Microsoft Azure**.

---

## 1. PROJECT OVERVIEW

Build a full-stack web application that allows Shia Muslim individuals to register for an in-person matchmaking event. Registration costs **$50** (to ensure serious participants). The system must capture comprehensive personal, religious, educational, professional, and preference information so that event organizers can perform matchmaking.

**Sponsor:** Husaynia Islamic Society of Seattle
**Address:** 15231 State St, Snohomish, WA 98296, USA
**Phone:** +1 (425) 312-3196
**Email:** contact@husaynia.org
**Website:** https://www.husaynia.org
**Social:** Facebook (@husayniaseattle), Instagram (@husayniaseattle)

---

## 2. TECH STACK (Azure-Optimized)

| Layer | Technology | Why |
|-------|-----------|-----|
| **Framework** | Next.js 14+ (App Router, TypeScript) | SSR/SSG, API routes, Azure Static Web Apps / App Service native support |
| **Styling** | Tailwind CSS 3+ | Rapid theming, responsive, utility-first |
| **UI Components** | shadcn/ui + Radix UI | Accessible, composable, matches dark/elegant theme |
| **Database** | Azure SQL Database (Serverless) | Relational data with strong schema for matchmaking queries; cost-effective serverless tier |
| **ORM** | Prisma | Type-safe DB access, migrations, Azure SQL support |
| **Authentication** | NextAuth.js (Auth.js v5) | Admin dashboard login (Microsoft Entra ID / credentials) |
| **Payments** | Stripe Checkout + Webhooks | PCI-compliant $50 registration fee collection |
| **File Storage** | Azure Blob Storage | Profile photos upload |
| **Email** | Azure Communication Services Email (or SendGrid) | Confirmation emails |
| **Hosting** | Azure App Service (Linux, Node 20) | Full Next.js support with SSR; or Azure Static Web Apps |
| **CI/CD** | GitHub Actions → Azure | Auto deploy on push to main |
| **Monitoring** | Application Insights | Error tracking, telemetry |

---

## 3. BRAND / THEME (Matching husaynia.org)

### Color Palette
```css
:root {
  --color-primary: #1B5E20;       /* Deep Islamic Green */
  --color-primary-light: #2E7D32; /* Lighter green */
  --color-secondary: #C9A84C;     /* Gold/Amber accent */
  --color-secondary-light: #D4AF37; /* Lighter gold */
  --color-bg-dark: #0A0A0A;      /* Near-black background */
  --color-bg-card: #1A1A2E;      /* Dark card background */
  --color-bg-section: #16213E;   /* Dark blue-green sections */
  --color-text-primary: #FFFFFF; /* White text */
  --color-text-secondary: #B0BEC5; /* Muted text */
  --color-text-gold: #C9A84C;    /* Gold highlights */
  --color-accent-red: #8B0000;   /* Deep red (Karbala reference) */
  --color-border: #2D2D2D;       /* Subtle borders */
}
```

### Typography
- **Headings:** "Playfair Display" or "Amiri" (serif, elegant, Islamic feel)
- **Body:** "Inter" or "Nunito Sans" (clean sans-serif)
- **Arabic Script:** "Amiri" for any Arabic calligraphy elements
- Include the iconic Husaynia quote at top: "إن لقتل الحُسين حرارةٌ في قلوب المؤمنين لا تبرد أبداً"

### Design Language
- Dark, elegant, reverent aesthetic matching husaynia.org
- Subtle Islamic geometric patterns as background textures (SVG/CSS)
- Gold accent borders and dividers
- Card-based layouts with soft shadows
- Responsive: mobile-first (many registrants will use phones)
- Include Husaynia logo and "Sponsored by Husaynia Islamic Society of Seattle" footer
- Green CTA buttons with gold hover states

---

## 4. PAGES & ROUTES

```
/                          → Landing page (event info, CTA to register)
/register                  → Multi-step registration form
/register/payment          → Stripe Checkout redirect
/register/confirmation     → Post-payment success page
/register/cancel           → Payment cancelled page
/about                     → About the event + Husaynia info
/faq                       → Frequently asked questions
/privacy                   → Privacy policy
/terms                     → Terms and conditions
/admin                     → Admin login
/admin/dashboard           → View all registrations, export, matchmaking tools
/admin/registrations       → Filterable/sortable table of all registrants
/admin/registrations/[id]  → Individual registrant detail view
/api/register              → POST: Create registration
/api/webhook/stripe        → POST: Stripe webhook handler
/api/admin/registrations   → GET: List registrations (protected)
/api/admin/export          → GET: Export CSV/Excel (protected)
```

---

## 5. REGISTRATION FORM — COMPLETE DATA MODEL

The registration form should be a **multi-step wizard** (progress bar at top, save progress between steps). Capture ALL of the following:

### Step 1: Personal Information
- Full Legal Name (first, middle, last)
- Preferred/Known-As Name
- Date of Birth (age calculated, must be 18+)
- Gender (Male / Female)
- Profile Photo Upload (required, stored in Azure Blob)
- Phone Number (with country code, US default)
- Email Address
- Current City & State
- Country of Origin / Ethnicity
- Languages Spoken (multi-select: English, Arabic, Urdu, Farsi/Persian, Turkish, Hindi, Swahili, Other)
- How did you hear about this event?

### Step 2: Religious Background
- Are you a practicing Shia Muslim? (Yes / Trying to be / Revert)
- Sect/School (Ithna Ashari/Twelver, Ismaili, Zaydi, Other)
- How often do you pray? (5 times daily / Most prayers / Occasionally / Working on it)
- Do you fast during Ramadan? (Yes always / Mostly / Sometimes / No)
- Do you attend Majalis/Islamic gatherings? (Regularly / Sometimes / Rarely)
- Hijab preference (for women: Yes I wear / Planning to / No) (for men: Do you prefer spouse who wears hijab? Required / Preferred / No preference)
- Level of religious knowledge (Beginner / Intermediate / Advanced / Scholar)
- Have you performed Hajj? (Yes / No / Planning to)
- Have you performed Ziyarat? (Yes — where / No / Planning to)
- Marja (who do you follow for religious rulings? — optional text field)
- How important is religion in your daily life? (1-5 scale)

### Step 3: Education & Career
- Highest Education Level (High School, Associate's, Bachelor's, Master's, Doctorate/PhD, Professional Degree, Islamic Seminary/Hawza, Other)
- Field of Study
- University/Institution Name
- Current Occupation / Job Title
- Employer (optional)
- Annual Income Range (Prefer not to say, Under $30k, $30k-$50k, $50k-$75k, $75k-$100k, $100k-$150k, $150k-$200k, $200k+)
- Career goals / ambitions (short text)

### Step 4: Lifestyle & Personal
- Marital Status (Never married, Divorced, Widowed, Annulled)
- Do you have children? (No / Yes — how many?)
- Height (ft/in or cm)
- Body Type (Slim, Average, Athletic, Curvy, Heavy — optional)
- Do you smoke? (Never, Occasionally, Trying to quit, Yes)
- Health conditions or disabilities to disclose? (optional, confidential text)
- Living situation (With parents, Own place, Renting alone, Renting with roommates, Other)
- Willing to relocate? (Yes / Maybe / No / Already flexible)
- Do you have pets? (text, optional)
- Hobbies & Interests (multi-select + other: Reading, Sports, Travel, Cooking, Technology, Art, Volunteering, Outdoor activities, Gaming, Fitness, Islamic studies, Other)
- Describe yourself in 3-5 sentences (textarea, required)

### Step 5: Family Information
- Father's Name
- Father alive? (Yes/No)
- Mother alive? (Yes/No)
- Number of siblings
- Family's country/city of origin
- Is your family supportive of this matchmaking process? (Yes / Somewhat / They don't know yet / No)
- Family's level of religiosity (Very practicing / Moderate / Cultural / Mixed)
- Any family expectations for spouse? (optional textarea)

### Step 6: Partner Preferences
- Preferred age range (min — max)
- Preferred height range (optional)
- Preferred ethnicity (No preference, or multi-select same list)
- Preferred education level (minimum)
- Preferred income range (No preference, or select)
- Preferred location/willing to relocate for match
- Marital status preference (Never married only / Divorced OK / No preference)
- Children preference (No children / OK with children / No preference)
- Preferred level of religiosity (Very practicing / Moderate / No preference)
- Deal-breakers (textarea — what would absolutely not work)
- What are you looking for in a spouse? (textarea, required, min 100 chars)
- Any additional notes for the matchmakers? (textarea, optional)

### Step 7: Agreements & Wali/Guardian Info
- Wali/Guardian Name (required for women, optional for men)
- Wali/Guardian Phone
- Wali/Guardian Email
- Wali/Guardian Relationship (Father, Brother, Uncle, Other)
- Emergency Contact Name
- Emergency Contact Phone
- Emergency Contact Relationship
- Agree to Terms & Conditions (checkbox, required)
- Agree to Privacy Policy (checkbox, required)
- I confirm all information is truthful (checkbox, required)
- I understand the $50 registration fee is non-refundable (checkbox, required)
- I consent to my information being shared with potential matches through the organizers (checkbox, required)
- Digital Signature (typed full name as signature)

### Step 8: Payment
- Summary of all info entered (review page)
- Edit buttons to go back to any step
- "Proceed to Payment — $50" button → Stripe Checkout Session
- After successful Stripe payment, webhook updates registration status to "PAID"
- Confirmation page with registration ID and receipt

---

## 6. DATABASE SCHEMA (Prisma)

Design the Prisma schema with these models:
- `Registration` — all personal fields above, status enum (DRAFT, PENDING_PAYMENT, PAID, CONFIRMED, CANCELLED, REFUNDED)
- `PaymentRecord` — Stripe session ID, payment intent ID, amount, status, timestamps
- `ProfilePhoto` — blob URL, thumbnail URL, original filename
- `AdminUser` — for dashboard access
- `MatchNote` — admin notes for matchmaking (registrationId, note, createdBy, createdAt)
- `Event` — event details (name, date, location, price, active status) — for future multi-event support

Add proper indexes for: email (unique), phone, gender, age range, city/state, marital status, registration status — to enable efficient matchmaking queries.

---

## 7. STRIPE INTEGRATION

- Use **Stripe Checkout Sessions** (redirect mode, not embedded)
- Create checkout session in `/api/register` after form validation
- Product: "Shia Matchmaking Event Registration — $50"
- Include registrant name and email in Stripe metadata
- Webhook endpoint `/api/webhook/stripe` handles:
  - `checkout.session.completed` → Mark registration as PAID, send confirmation email
  - `checkout.session.expired` → Mark as EXPIRED
- Store Stripe customer ID, session ID, payment intent for records
- Handle idempotency (don't double-process webhooks)

---

## 8. ADMIN DASHBOARD

Protected by NextAuth.js (credentials provider or Microsoft Entra ID):

### Features:
- **Dashboard home:** Total registrations, paid count, revenue, gender split (pie chart), registrations over time (line chart)
- **Registrations table:** Sortable, filterable by gender, age, city, marital status, religiosity, education, registration status
- **Search:** Full-text search by name, email, phone
- **Individual view:** Full profile card with photo, all captured data, payment status
- **Matchmaking notes:** Add/edit notes per registrant for matchmaking process
- **Export:** CSV and Excel export of all registrations (with filters applied)
- **Charts library:** Use Recharts or Chart.js

---

## 9. EMAIL NOTIFICATIONS

Send via Azure Communication Services or SendGrid:

- **Registration confirmation** — After successful payment, send receipt with registration ID, event details, what to expect
- **Payment failed/expired** — Reminder to complete payment
- **Event reminder** — 1 week before event (cron job or Azure Function)

Email templates should match the brand theme (dark bg, gold accents, Husaynia logo).

---

## 10. SECURITY & PRIVACY

- All PII encrypted at rest (Azure SQL TDE enabled by default)
- HTTPS only (Azure App Service default)
- Rate limiting on registration API (prevent spam)
- CAPTCHA on registration form (reCAPTCHA v3 or hCaptcha)
- Input validation & sanitization (Zod schemas)
- CSRF protection (Next.js built-in)
- Stripe webhook signature verification
- Admin routes protected by auth middleware
- Photo uploads: validate file type (JPEG/PNG only), max 5MB, virus scan optional
- GDPR/privacy: data retention policy, ability to delete registrant data
- Environment variables for all secrets (never committed)

---

## 11. PROJECT STRUCTURE

```
shia-matchmaking/
├── .github/
│   └── workflows/
│       └── azure-deploy.yml        # CI/CD to Azure
├── prisma/
│   ├── schema.prisma               # Database schema
│   ├── seed.ts                     # Seed admin user + test data
│   └── migrations/
├── public/
│   ├── images/
│   │   ├── husaynia-logo.png
│   │   ├── islamic-pattern.svg
│   │   └── hero-bg.jpg
│   └── fonts/
├── src/
│   ├── app/
│   │   ├── layout.tsx              # Root layout (theme provider, fonts)
│   │   ├── page.tsx                # Landing page
│   │   ├── globals.css             # Tailwind + custom theme CSS
│   │   ├── register/
│   │   │   ├── page.tsx            # Multi-step form
│   │   │   ├── payment/page.tsx    # Payment redirect
│   │   │   ├── confirmation/page.tsx
│   │   │   └── cancel/page.tsx
│   │   ├── about/page.tsx
│   │   ├── faq/page.tsx
│   │   ├── privacy/page.tsx
│   │   ├── terms/page.tsx
│   │   ├── admin/
│   │   │   ├── layout.tsx          # Admin layout with sidebar
│   │   │   ├── page.tsx            # Dashboard
│   │   │   ├── registrations/
│   │   │   │   ├── page.tsx        # Table view
│   │   │   │   └── [id]/page.tsx   # Detail view
│   │   │   └── login/page.tsx
│   │   └── api/
│   │       ├── register/route.ts
│   │       ├── upload/route.ts     # Photo upload to Azure Blob
│   │       ├── webhook/
│   │       │   └── stripe/route.ts
│   │       └── admin/
│   │           ├── registrations/route.ts
│   │           └── export/route.ts
│   ├── components/
│   │   ├── ui/                     # shadcn/ui components
│   │   ├── registration/
│   │   │   ├── RegistrationWizard.tsx
│   │   │   ├── PersonalInfoStep.tsx
│   │   │   ├── ReligiousBackgroundStep.tsx
│   │   │   ├── EducationCareerStep.tsx
│   │   │   ├── LifestyleStep.tsx
│   │   │   ├── FamilyInfoStep.tsx
│   │   │   ├── PartnerPreferencesStep.tsx
│   │   │   ├── AgreementsStep.tsx
│   │   │   ├── ReviewPaymentStep.tsx
│   │   │   └── ProgressBar.tsx
│   │   ├── layout/
│   │   │   ├── Header.tsx
│   │   │   ├── Footer.tsx
│   │   │   ├── Navigation.tsx
│   │   │   └── IslamicPattern.tsx  # Decorative SVG component
│   │   ├── admin/
│   │   │   ├── DashboardStats.tsx
│   │   │   ├── RegistrationTable.tsx
│   │   │   ├── RegistrationDetail.tsx
│   │   │   ├── MatchNotes.tsx
│   │   │   └── ExportButton.tsx
│   │   └── common/
│   │       ├── Button.tsx
│   │       ├── Card.tsx
│   │       └── LoadingSpinner.tsx
│   ├── lib/
│   │   ├── prisma.ts               # Prisma client singleton
│   │   ├── stripe.ts               # Stripe client + helpers
│   │   ├── azure-blob.ts           # Azure Blob Storage helpers
│   │   ├── email.ts                # Email service
│   │   ├── auth.ts                 # NextAuth config
│   │   ├── validations.ts          # Zod schemas for all forms
│   │   └── utils.ts                # Utility functions
│   ├── hooks/
│   │   ├── useRegistrationForm.ts  # Multi-step form state management
│   │   └── useDebounce.ts
│   └── types/
│       ├── registration.ts         # TypeScript types
│       └── admin.ts
├── .env.example                    # Template for env vars
├── .env.local                      # Local dev env vars (gitignored)
├── .gitignore
├── next.config.js
├── tailwind.config.ts
├── tsconfig.json
├── package.json
├── postcss.config.js
└── README.md
```

---

## 12. ENVIRONMENT VARIABLES

```env
# Database
DATABASE_URL="sqlserver://your-server.database.windows.net:1433;database=matchmaking;user=admin;password=xxx;encrypt=true"

# Stripe
NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY="pk_live_xxx"
STRIPE_SECRET_KEY="sk_live_xxx"
STRIPE_WEBHOOK_SECRET="whsec_xxx"
STRIPE_PRICE_ID="price_xxx"

# Azure Blob Storage
AZURE_STORAGE_CONNECTION_STRING="DefaultEndpointsProtocol=https;AccountName=xxx;AccountKey=xxx"
AZURE_STORAGE_CONTAINER_NAME="profile-photos"

# NextAuth
NEXTAUTH_SECRET="generate-a-random-secret"
NEXTAUTH_URL="https://your-domain.azurewebsites.net"

# Email (SendGrid or Azure Communication Services)
SENDGRID_API_KEY="SG.xxx"
EMAIL_FROM="noreply@husaynia.org"

# App
NEXT_PUBLIC_APP_URL="https://your-domain.azurewebsites.net"
REGISTRATION_FEE_CENTS=5000
```

---

## 13. AZURE INFRASTRUCTURE

Provision these Azure resources (can use Bicep/Terraform or Azure Portal):

1. **Resource Group:** `rg-husaynia-matchmaking`
2. **Azure App Service Plan:** Linux, B1 tier (scale up as needed)
3. **Azure App Service:** Node 20 LTS, deploy from GitHub
4. **Azure SQL Server:** Serverless Gen5, 1 vCore
5. **Azure SQL Database:** `matchmaking-db`, serverless tier
6. **Azure Storage Account:** `husayniamatchphotos`, Blob containers
7. **Application Insights:** Connected to App Service
8. **Azure Key Vault:** Store connection strings and API keys (optional but recommended)

---

## 14. IMPLEMENTATION ORDER

Build in this sequence:

1. **Project scaffolding** — Next.js + TypeScript + Tailwind + shadcn/ui setup
2. **Theme & layout** — Global styles, header, footer, Islamic design elements matching husaynia.org
3. **Landing page** — Hero section, event details, CTA
4. **Database schema** — Prisma schema + initial migration
5. **Registration form** — All 7 steps with Zod validation
6. **Photo upload** — Azure Blob integration
7. **Stripe payment** — Checkout session + webhook
8. **Confirmation flow** — Post-payment page + email
9. **Admin auth** — NextAuth.js setup
10. **Admin dashboard** — Stats, registration table, detail view, export
11. **Matchmaking tools** — Notes, filtering, comparison view
12. **Email templates** — Branded confirmation + reminder emails
13. **Security hardening** — Rate limiting, CAPTCHA, input sanitization
14. **Testing** — Unit tests (Vitest), integration tests, Stripe test mode
15. **Azure deployment** — CI/CD pipeline, environment configuration
16. **DNS & SSL** — Custom domain if needed

---

## 15. KEY BUSINESS RULES

- Registrant must be **18 years or older**
- Registration is **not complete until $50 payment is processed**
- Each email address can only register **once**
- **Profile photo is mandatory** — helps with matchmaking
- **Women must provide Wali/Guardian information**
- All data is **confidential** — only shared with event organizers and potential matches through organizers (never publicly)
- **Non-refundable** registration fee (clearly stated)
- Admin can manually mark registrations as CONFIRMED, CANCELLED, or REFUNDED

---

## 16. LANDING PAGE CONTENT

Hero section should include:
- **Bismillah ar-Rahman ar-Raheem** (بسم الله الرحمن الرحيم)
- Event title: "Shia Muslim Matchmaking Event"
- Tagline: "Finding Your Better Half, the Halal Way"
- Sponsorship: "Proudly sponsored by Husaynia Islamic Society of Seattle"
- Event date/location (configurable from admin or env)
- Registration fee: $50
- CTA: "Register Now" button
- Brief about section: Why this event, Islamic perspective on marriage
- How it works: 3-step process (Register → Attend → Connect)
- FAQ preview
- Testimonials section (placeholder)
- Footer with Husaynia branding, social links, contact info

---

Now please build this complete application. Start by scaffolding the project, then implement each feature systematically following the implementation order above. Write production-quality, well-commented code. Use proper error handling throughout. Make every component responsive and accessible.

Begin now.
```
