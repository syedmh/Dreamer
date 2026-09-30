# Husaynia.org Replica Architecture

Date: 2026-08-15  
Status: APPROVED for decomposition; no production deployment or SKU selection is authorized.

Amended: 2026-08-15 — deterministic T01 browser capture; section 17 consolidates rework A-I and
supersedes conflicting section 16 details. See ADR-009.

## 1. Verified current state

- **FACT:** `HusayniaSite/` is empty; it has no entry point, solution, data model, configuration,
  deployment definition, test harness, or ADR.
- **FACT:** The frozen contract prioritizes exact visual, URL, content, integration, and journey
  fidelity in a maintainable Visual Studio solution (`requirements.md:9-11,49-68`).
- **FACT:** The prior application is a Visual Studio 17, three-project .NET 8 solution
  (`../../../Husaynia/Husaynia.sln:2-8`;
  `../../../Husaynia/Husaynia.Web/Husaynia.Web.csproj:1-23`).
- **FACT:** The prior composition root directly wires MVC/Razor Pages, EF Core SQL Server, ASP.NET
  Core Identity, memory/response caching, ImageSharp, repositories, prayer calculation, and an
  in-process cache worker (`../../../Husaynia/Husaynia.Web/Program.cs:11-49`).
- **FACT:** Its domain/persistence model covers events, announcements, media, prayer times,
  donations, campaigns, site configuration, and Identity, but not versioned pages, redirects,
  forms, consent, import reconciliation, audit records, payment events, or prayer overrides
  (`../../../Husaynia/Husaynia.Infrastructure/Data/ApplicationDbContext.cs:12-20`).
- **FACT:** Every inspected admin page uses only `[Authorize]`; no named role is required
  (`../../../Husaynia/Husaynia.Web/Areas/Admin/Pages/Index.cshtml.cs:8-9`;
  `../../../Husaynia/Husaynia.Web/Areas/Admin/Pages/Announcements/Index.cshtml.cs:8-10`).
- **FACT:** Media upload is a public controller POST, writes to the app filesystem, trusts the file
  extension, and has no authorization or antiforgery attribute
  (`../../../Husaynia/Husaynia.Web/Controllers/MediaController.cs:60-101`).
- **FACT:** Contact submission logs the visitor name and email and does not persist or deliver the
  message (`../../../Husaynia/Husaynia.Web/Controllers/AboutController.cs:24-47`).
- **FACT:** Stripe mode and campaign names are hard-coded; both the browser success action and
  webhook can complete a donation and increment a campaign in separate writes
  (`../../../Husaynia/Husaynia.Web/Controllers/DonationsController.cs:77-79,105,145-166,189-225`).
- **FACT:** `Donation.TransactionId` has no shown unique constraint, and campaign increment is a
  read-modify-write without concurrency protection
  (`../../../Husaynia/Husaynia.Core/Entities/Donation.cs:5-20`;
  `../../../Husaynia/Husaynia.Infrastructure/Repositories/DonationCampaignRepository.cs:50-58`).
- **FACT:** Prayer calculation uses server-local `DateTime.Today` and a fixed UTC-8 offset, so it
  does not implement the required named timezone/DST contract
  (`../../../Husaynia/Husaynia.Web/Services/PrayerTimeService.cs:30-33,115-116,154-181`).
- **FACT:** Search loads records and filters in process; the Islamic calendar is a placeholder
  (`../../../Husaynia/Husaynia.Web/Controllers/SearchController.cs:21-64`;
  `../../../Husaynia/Husaynia.Web/Controllers/CalendarController.cs:39-44`).
- **FACT:** The prior solution has no automated test project; its README supplies a manual checklist
  (`../../../Husaynia/Husaynia.sln:5-8`; `../../../Husaynia/README.md:167-182`).
- **FACT:** .NET 10 is supported through 2028-11-15, and ASP.NET Core/EF Core follow the .NET
  lifecycle. Visual Studio 2026 is the current supported IDE generation. Sources:
  [Microsoft .NET lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)
  and [Visual Studio 2026 release notes](https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes),
  inspected 2026-08-15.

## 2. Selected architecture

Build a **.NET 10 LTS modular monolith** using ASP.NET Core MVC/Razor Pages and progressive
enhancement. Public pages are server-rendered for URL/SEO parity, predictable accessibility, small
client payloads, and straightforward visual regression. Admin, public site, webhooks, scheduled
refreshes, and migration composition remain one deployable application/solution, but business
modules have explicit Application interfaces and Infrastructure adapters.

Dependency direction:

```text
Husaynia.Web -> Husaynia.Application -> Husaynia.Domain
             -> Husaynia.Infrastructure -> Application + Domain
Husaynia.Migration -> Application + Infrastructure
Domain -> no project dependency
```

No SPA framework, microservice, Redis, queue, Azure AI Search, CDN/Front Door, or separate CMS is
introduced. These do not improve the frozen fidelity contract enough to justify cost and operational
surface at the required load.

## 3. Solution layout

```text
HusayniaSite/
  HusayniaSite.sln
  global.json                    pinned supported .NET 10 feature band
  Directory.Build.props
  Directory.Packages.props      centrally pinned dependencies; locked restore
  src/
    Husaynia.Domain/             entities, value objects, invariants, domain errors
    Husaynia.Application/        use cases, ports, DTOs, authorization policies
    Husaynia.Infrastructure/     EF Core, Blob, Stripe, prayer/social/email adapters
    Husaynia.Web/                MVC public site, Admin area, webhook endpoints, workers
  tools/
    Husaynia.Migration/          crawl/export adapters, dry-run/import/reconcile/report CLI
  tests/
    Husaynia.Domain.Tests/
    Husaynia.Application.Tests/
    Husaynia.IntegrationTests/
    Husaynia.ContractTests/
    Husaynia.E2ETests/           Playwright visual/browser/accessibility journeys
    Husaynia.ArchitectureTests/  dependency and public/admin boundary rules
  infra/
    main.bicep
    modules/
    parameters/{development,staging,production}.bicepparam
  pipelines/
    pr.yml
    deploy.yml
  docs/
    development.md deployment.md operations/ content-editor.md security-privacy.md
```

## 4. Modules and boundaries

| Module | Owns | Public application ports |
|---|---|---|
| Content | Pages, religious text, announcements, navigation, SEO, revisions | `IContentReader`, `IContentEditor`, `IPublisher` |
| Calendar | Events/programs, categories, recurrence instances, iCal | `IEventReader`, `IEventEditor`, `ICalendarExporter` |
| Prayer | calculation/provider settings, generated snapshots, overrides | `IPrayerScheduleService`, `IPrayerSource` |
| Media | asset metadata, versions, aliases, validation, delivery | `IMediaLibrary`, `IMediaStore`, `IUploadSafetyValidator` |
| Search | published search projection and query rules | `IPublicSearch` |
| Forms | contact/pledge submissions, anti-abuse, delivery attempts | `IFormSubmissionService`, `IOutboundMessageSender` |
| Donations | categories/modes, Checkout, webhook events, reconciliation | `IDonationService`, `IPaymentGateway` |
| Social | normalized feed snapshots and refresh state | `ISocialFeedReader`, `ISocialFeedProvider` |
| Identity | users, six roles/policies, privileged action audit | `IUserAdministration`, `IAuditWriter` |
| Migration | crawl/export readers, mapping, conflict/reconciliation reports | `IImportSource`, `IImportPlanner`, `IImportExecutor` |
| Operations | health, durable jobs, retention, consent configuration | `IJobLeaseStore`, `IConsentConfiguration` |

Public request handlers call Application use cases only. Infrastructure never contains
authorization decisions. Cross-module writes are coordinated by one EF Core transaction where
atomicity is required.

## 5. Core contracts and error semantics

Expected failures return typed results; controllers map them consistently:

```csharp
Task<Result<ContentView, ContentError>> GetByPathAsync(string path, CancellationToken ct);
Task<Result<PublishReceipt, PublishError>> PublishAsync(
    ContentId id, RevisionId expectedDraft, UserContext actor, CancellationToken ct);

Task<Result<PrayerSchedule, PrayerError>> GetMonthAsync(
    YearMonth month, TimeZoneInfo zone, CancellationToken ct);

Task<Result<SearchPage, SearchError>> SearchAsync(
    string query, int page, int pageSize, CancellationToken ct);

Task<Result<CheckoutRedirect, DonationError>> CreateCheckoutAsync(
    DonationIntent intent, string idempotencyKey, CancellationToken ct);
Task<Result<WebhookReceipt, WebhookError>> HandleWebhookAsync(
    ReadOnlyMemory<byte> body, string signature, CancellationToken ct);

Task<Result<ImportPlan, ImportError>> PlanAsync(
    ImportSource source, ImportMode mode, CancellationToken ct); // dry-run by default
```

| Condition | HTTP/behavior |
|---|---|
| Invalid public input | 400 with accessible field errors; no partial write |
| Unauthenticated admin | 401/challenge |
| Authenticated but forbidden | 403 and audited |
| Missing/private content | 404, never disclose draft existence |
| Optimistic concurrency conflict | 409 with refresh/compare guidance |
| Rate limited/anti-abuse | 429, generic response, correlation ID |
| Dependency timeout | bounded retry only when safe; cached/empty state or 503 for that operation |
| Duplicate idempotent operation | return prior successful receipt, not a second mutation |
| Unexpected failure | generic 500; structured private telemetry with correlation ID |

All external calls use named `HttpClient` policies with explicit connect/overall timeouts, bounded
jittered retries only for idempotent reads, and circuit breaking. Provider diagnostics never reach
public output.

## 6. Logical data model

All mutable aggregate roots have `rowversion`, `CreatedAtUtc`, `UpdatedAtUtc`, and actor fields where
applicable. Public timestamps are stored as UTC plus the source timezone identifier.

- `ContentItem(Id, Kind, CanonicalPath, State, PublishedRevisionId, DraftRevisionId)`
- `ContentRevision(Id, ContentId, Version, Title, Summary, BodyHtml, StructuredJson,
  SeoJson, Checksum, CreatedBy, CreatedAtUtc)`; revisions are immutable.
- `NavigationItem`, `LegacyRedirect(SourcePath unique, TargetPath, StatusCode)`.
- `Event(Id, Slug unique, LocalStart, LocalEnd, TimeZoneId, Location, State, RecurrenceRule,
  PublishedRevision)` and materialized `EventOccurrence` where recurrence requires it.
- `MediaAsset(Id, StablePath unique, Kind, State, CurrentVersionId, AltText, Caption)` and
  immutable `MediaVersion(BlobName, Sha256, Length, MimeType, Width, Height)`.
- `PrayerProfile(ProviderKind, Latitude, Longitude, MethodJson, TimeZoneId, EffectiveFrom)`,
  `PrayerSnapshot(Date, ProfileHash, ValuesJson, GeneratedAtUtc, Source, IsValid)`, and
  `PrayerOverride(Date, Field, Value, Reason, EffectiveRevision)`.
- `SocialFeedSnapshot(Provider, ItemKey, PayloadJson, PublishedAtUtc, FetchedAtUtc, ExpiresAtUtc)`
  plus `IntegrationState`.
- `FormDefinition`, `FormSubmission`, `DeliveryAttempt`; normalized duplicate fingerprint and
  retention status are stored, but raw secrets are not.
- `DonationCategory(Slug unique, Currency, AllowedModes, AmountRulesJson)`,
  `Donation(Id, CheckoutSessionId unique, PaymentIntentId unique nullable, Status, Amount,
  CategoryId, IsAnonymous, Donor fields)`, `PaymentEvent(ProviderEventId unique, Type, ReceivedAtUtc,
  ProcessedAtUtc, Outcome)`, `CampaignLedger(ProviderEventId unique, Delta)`. Campaign totals are
  derived/summed or updated in the same serializable transaction as the unique ledger entry.
- ASP.NET Core Identity tables plus `AuditEvent(Id, ActorId, RolesJson, Action, TargetType,
  TargetId, Outcome, CorrelationId, OccurredAtUtc, DetailJson)`.
- `SearchDocument(ContentKey unique, Kind, CanonicalPath, Title, Body, PublishedAtUtc)` contains
  only published public material and uses Azure SQL Full-Text Search. Azure SQL support is documented
  by [Microsoft Full-Text Search](https://learn.microsoft.com/en-us/sql/relational-databases/search/full-text-search).
- `ImportRun`, `ImportCandidate(SourceKey + SourceVersion unique, SourceChecksum, TargetKey,
  Decision, ConflictReason)` and `BackgroundJob` with lease/attempt/next-run fields.

Published content is never edited in place: editors create a draft revision, preview a signed,
short-lived non-indexable route, then atomically publish. Rollback republishes a prior immutable
revision as a new version, preserving audit history.

## 7. Rendering, URLs, media, and integrations

- **Rendering:** MVC views/components reproduce each approved public template. CSS design tokens
  capture crimson, typography, spacing, breakpoints, and asset treatment. JavaScript is limited to
  navigation, consent, lightbox/load-more, forms, and payment redirection.
- **URLs/SEO:** An approved route manifest is executable contract data. Explicit endpoint mappings
  take precedence over conventional routes. Redirect middleware reads `LegacyRedirect`, allows one
  permanent hop only, and rejects target cycles during administration/import. Sitemaps, RSS, iCal,
  canonical/robots/OG/Twitter/JSON-LD are generated from published records.
- **Media:** Azure Blob Storage holds immutable versions. `/wp-content/uploads/{**path}` and other
  stable paths are served through a same-origin media endpoint with ETag, range requests, cache
  headers, and alias lookup. Images are decoded/re-encoded and metadata stripped; all types require
  allowlisted MIME, extension and magic-byte agreement, size limits, randomized internal blob names,
  and non-executable response headers. PDF/document types default to attachment disposition.
- **Search:** requests are length/page bounded and query only `SearchDocument`. If full-text search
  is unavailable, the search region returns an actionable dependency state; no draft/admin/donor
  table participates.
- **Stripe:** Infrastructure owns the Stripe SDK. Category, amount, currency, receipt, and
  one-time/subscription mode are configuration/data, not controller constants. Only a verified
  webhook may move a donation to completed. The success page is read-only status display.
- **Prayer:** a deterministic local calculator is the default source; an approved external source
  can be configured behind `IPrayerSource`. Values are generated using
  `America/Los_Angeles`, a settings hash, DST-aware dates, and persisted snapshots. Manual overrides
  apply last. Public reads use the latest approved snapshot and label stale data.
- **Social:** scheduled jobs fetch and normalize provider data into snapshots. Public requests never
  call Facebook/Instagram. A stale snapshot, empty state, and provider-error state are explicit;
  visitor-identifying embeds/scripts load only after social consent.
- **Forms/email:** submissions are transactionally persisted before a durable DB-backed delivery
  job is created. Duplicate fingerprint, honeypot, rate limit, size limits, and antiforgery apply.
  Development uses a local sink; Staging uses a non-public test destination; Production provider
  credentials/configuration remain environment-specific.
- **Consent:** essential cookies work immediately. Analytics, Clarity/session recording, marketing,
  and social embed categories default off. Scripts are emitted only after versioned consent, and
  withdrawal suppresses future loading and removes known first-party consent-controlled cookies.

## 8. Identity, security, privacy, and trust boundaries

No public self-registration is enabled. ASP.NET Core Identity remains in Azure SQL; administrators
invite users. Confirmed account, lockout, secure password policy, MFA for every privileged role, and
secure recovery are required.

| Capability | SiteAdmin | Content | Event | Media | Donation | Auditor |
|---|---:|---:|---:|---:|---:|---:|
| users/roles/integrations/settings | RW | - | - | - | - | R reports |
| pages/announcements/religious content | RW | RW | - | - | - | R audit |
| events/iCal | RW | - | RW | - | - | R audit |
| media/assets | RW | - | - | RW | - | R audit |
| donation status/reconcile | RW | - | - | - | RW | R reports |
| audit/operational reports | RW | - | - | - | limited | R |

Policies are deny-by-default and applied at endpoint plus use-case boundary. Every permitted and
denied privileged attempt writes an append-only `AuditEvent`.

Trust boundaries are: anonymous browser, authenticated editor, Stripe webhook, scheduled external
provider, migration operator, Azure managed identity, database, and media store. Validate at each
boundary. TLS, CSP with per-request nonce, HSTS, nosniff, frame-ancestors/frame protection,
referrer policy, permissions policy, CSRF, webhook signatures, output encoding, rich-content
sanitization, upload controls, log redaction, and least-privilege managed identity are mandatory.
Donor/form data never enters analytics. Retention, export, deletion/anonymization, and legal-hold
rules are configuration with audited operator workflows.

## 9. Azure topology and configuration

Each of Development, Staging, and Production has an isolated resource group and:

- one Azure App Service Web App with system-assigned managed identity;
- one isolated Azure SQL Database logical database;
- one StorageV2 account/containers for media, import evidence, and reports;
- one Key Vault for secrets; App Service settings use managed-identity Key Vault references;
- Application Insights + Log Analytics, health checks, availability test, alerts/action group.

Bicep defines all resources and environment differences. No SKU is selected by this architecture;
parameters constrain only capabilities required by the stage. App Service slots, private endpoints,
zone redundancy, Front Door/CDN, Defender for Storage, and higher backup redundancy are optional
costed enhancements requiring measured need/CTO approval.

Non-secret configuration is environment app settings and validated strongly typed options. Secrets
are Key Vault references. The app uses managed identity for Key Vault/Storage where supported.
Microsoft documents these patterns in
[Key Vault references](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references),
[App Service managed identity](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity),
and [Bicep](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/overview).

## 10. CI/CD, observability, and recovery

**PR:** locked restore, build with warnings as errors, unit/integration/contract/architecture tests,
format/analyzers, secret scan, dependency vulnerability scan, Bicep lint/what-if validation,
Playwright smoke/accessibility/visual checks against an ephemeral local app, and publish one
versioned zip/package plus migrations, manifest, SBOM, test results, and SHA-256.

**Promotion:** the exact application package checksum is promoted. Development auto-deploys from the
protected integration branch. Staging applies expand-compatible migrations, deploys, then runs
health, smoke, crawl/redirect, visual, accessibility, performance, migration dry-run, restore,
and sandbox integration gates. Production requires explicit approval and the evidence named in
R-37; it is not executed by this mission.

Database migrations run as an explicit pipeline step under a migration identity, not during app
startup. Use expand/migrate/contract changes; destructive contract steps wait until rollback and
compatibility windows expire. Application deployment is retryable. Previous packages and database
restore points remain addressable; on failed health, redeploy the prior package and invoke the
documented database rollback/forward-fix decision.

Structured OpenTelemetry/Application Insights telemetry includes correlation/trace IDs, deployment
version, route template, latency, status, dependency name/outcome, stale-cache age, job attempts,
webhook event outcome, import conflicts, and privileged audit correlation. Never log message bodies,
full email addresses, payment metadata, tokens, or secrets. Alerts cover availability, 5xx/error
rate, latency, dependency failures, stale prayer/social snapshots, delivery backlog, failed
webhooks, job dead letters, storage/DB capacity, backup/restore evidence expiry, and secret expiry.
See [Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview)
and [App Service monitoring](https://learn.microsoft.com/en-us/azure/app-service/monitor-app-service).

Azure SQL point-in-time backups cover the database. Blob versioning plus blob/container soft delete,
lifecycle rules, and a resource lock cover media; configuration references and IaC are versioned in
Git, while secret values remain in Key Vault. A Staging restore exercise proves A-01 (RPO 24h/RTO
4h). Microsoft documents
[Azure SQL automated backups](https://learn.microsoft.com/en-us/azure/azure-sql/database/automated-backups-overview),
[blob versioning](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-overview), and
[blob soft delete](https://learn.microsoft.com/en-us/azure/storage/blobs/soft-delete-blob-overview).

## 11. Migration, cutover, and rollback

1. Capture timestamped crawl, screenshots, HTTP/SEO metadata, content checksums, links, forms,
   dynamic-region fixtures, and public media into immutable evidence.
2. `Husaynia.Migration` adapters read crawl/media now and authorized WP XML/DB/media/config later
   into one canonical `ImportCandidate` model.
3. Dry-run validates schema, rights allowlist, route collisions, checksums, links, media, and
   redirects. It outputs create/update/skip/conflict/orphan decisions without mutation.
4. Apply uses `SourceKey + SourceVersion` idempotency and optimistic target checks. A changed editor
   target becomes a conflict; imports never overwrite it silently.
5. Re-run and reconcile until the manifest accounts for every approved item. Produce pre/post crawl
   diffs and unresolved-risk report.
6. Cutover: freeze legacy editing, final delta import, backup, deploy approved package, validate
   hostname/TLS/headers/health/crawl/sandbox-disabled production integrations, then change DNS.
7. Rollback criteria: failed health, material URL/content loss, security control failure, payment
   misrouting, or unreconciled data. Restore DNS to legacy origin, redeploy last package as needed,
   and do not reverse an incompatible DB change; use restore/forward-fix per migration runbook.

## 12. Phased delivery

1. **Evidence and skeleton:** solution, architecture tests, crawl/manifest tooling, design tokens,
   route fixtures, IaC/pipeline skeleton.
2. **Read-only public parity:** shell, content/religious pages, navigation, SEO, redirects, media,
   search projection.
3. **Calendar/prayer/social:** events/iCal, deterministic prayer/overrides/cache, feed snapshots and
   failure states.
4. **Editorial/admin:** Identity policies, versioned workflow, audit, media safety, consent config.
5. **Forms and donations:** durable submissions, Stripe sandbox, idempotent webhook/ledger.
6. **Migration/reconciliation:** crawl import, later WP adapters, dry-run/conflict reports.
7. **Release evidence:** full browser/visual/accessibility/performance/security/restore/rollback
   gates; production remains separately authorized.

## 13. Requirement traceability

| Req | Component/evidence strategy |
|---|---|
| R-01 | MVC fidelity templates + route/visual/content manifests; AC-01..05 |
| R-02 | Public site, role-based Admin, operator runbooks; AC-18..19,33..34 |
| R-03 | Bicep and immutable Development/Staging/Production promotion; AC-29..31 |
| R-04 | design tokens/template screenshots; AC-01 |
| R-05 | navigation manifest and Playwright pointer/keyboard tests; AC-02 |
| R-06 | responsive matrix; AC-03,28 |
| R-07 | snapshot-backed dynamic-region state fixtures; AC-01,11..12 |
| R-08 | explicit route manifest, redirects, feeds/downloads; AC-04,10,21..22 |
| R-09 | immutable Unicode structured religious-content fixtures/checksums; AC-06 |
| R-10 | Prayer module, DST-aware profile, overrides/snapshots; AC-07..08 |
| R-11 | Calendar module/event details/iCal parser tests; AC-09..10 |
| R-12 | Social/Media snapshot, playback/lightbox/load-more states; AC-11..12 |
| R-13 | published-only `SearchDocument`, bounded input; AC-13 |
| R-14 | versioned form definitions, durable submission/delivery, anti-abuse; AC-14 |
| R-15 | data-driven category/mode contract and Stripe sandbox; AC-15 |
| R-16 | unique provider IDs + atomic ledger transaction; AC-16 |
| R-17 | test credentials and pipeline live-key guards; AC-15..17 |
| R-18 | versioned editable legal/site content; AC-05,19,21 |
| R-19 | six policy roles and matrix; AC-18 |
| R-19A | policy-to-use-case mapping in Identity/Application; AC-18 |
| R-20 | Blob media versions, validation, alias stability; AC-20,22 |
| R-21 | EventEditor workflow and iCal regeneration; AC-09..10,18..19 |
| R-22 | donation views without card data; auditor read-only reports; AC-17..18 |
| R-23 | deny-by-default policies + append-only audit; AC-18,26,33 |
| R-24 | revision preview/publish/rollback and operations runbooks; AC-19,33..34 |
| R-25 | machine-readable crawl/SEO/media/redirect manifest; AC-21..22,35 |
| R-26 | crawl/media and later WP adapters; AC-35 |
| R-27 | redirect validator, one-hop middleware; AC-04 |
| R-28 | stable media/event/feed/donation route mappings; AC-04,22 |
| R-29 | published-only sitemap/robots generation; AC-21 |
| R-30 | revision SEO fields and JSON-LD renderers; AC-21 |
| R-31 | pre/post crawl reconciliation reports; AC-22,35 |
| R-32 | dry-run/idempotency/conflict/non-destructive import; AC-35 |
| R-33 | .NET 10/VS 2026 solution + CLI parity docs; AC-29 |
| R-34 | PR quality/security/accessibility/artifact pipeline; AC-23,26,29 |
| R-35 | checksum-addressed immutable package; AC-30 |
| R-36 | automatic Development and gated Staging pipeline; AC-30..31 |
| R-37 | Production approval/evidence stage definition; AC-31..33 |
| R-38 | Key Vault references, managed identity, scan gates; AC-17,26,29 |
| R-39 | isolated resource groups/data/config and nonprod sinks; AC-15,27,30 |
| R-40 | idempotent IaC/migrations/deploy and retained package; AC-31 |
| R-41 | semantic server rendering + automated/manual WCAG plan; AC-23 |
| R-42 | low-JS SSR, image variants, caching, performance budgets; AC-24 |
| R-43 | SQL projections/indexes, output caching, load tests; AC-24 |
| R-44 | snapshot isolation, timeouts, durable retries, alerts; AC-08,12,25 |
| R-45 | security middleware/boundary controls; AC-26 |
| R-46 | independent scan/review gate; AC-26 |
| R-47 | data minimization, retention, redaction, consent; AC-17,27 |
| R-48 | health, OTel telemetry, alerts/runbooks; AC-33 |
| R-49 | Playwright browser/device matrix and manual Safari QA; AC-28 |
| R-50 | SQL PITR + Blob protection + Staging restore; AC-32 |
| R-51 | required development/deployment/operations/editor docs; AC-34 |
| R-52 | pipeline evidence mapped to all DoD gates |
| R-53 | executable reports/checksums/deploy records, never README-only claims |
| R-54 | release evidence packet and independent Engineering Judge gate |

## 14. Acceptance-criteria traceability

| AC | Named test/evidence |
|---|---|
| 01 | `VisualParityTests` screenshot diff ≤2% per template/viewport |
| 02 | `NavigationContractTests` manifest + keyboard/pointer E2E |
| 03 | `ResponsiveLayoutTests` 320..1920 overflow/overlap checks |
| 04 | `LegacyUrlCrawlTests` status/one-hop/loop assertions |
| 05 | `ContentSnapshotTests` checksum/order/link/media comparison |
| 06 | `ReligiousContentRoundTripTests` Unicode/direction/audio fixtures |
| 07 | `PrayerScheduleTests` fixed clock, DST boundaries, profile/tolerance |
| 08 | `PrayerFallbackTests` timeout/stale-label/unavailable state |
| 09 | `PublishedEventVisibilityTests` states/timezone/canonical |
| 10 | `ICalendarContractTests` parses UID/timezone/content |
| 11 | `SocialMediaHealthyTests` snapshot/control behavior |
| 12 | `SocialMediaFailureTests` timeout/rate/blocked/empty fixtures |
| 13 | `PublicSearchSecurityTests` empty/Unicode/oversize/injection/private |
| 14 | `FormSubmissionTests` validation/duplicate/bot/atomic delivery |
| 15 | `StripeSandboxContractTests` amount/mode/decline/cancel/3DS |
| 16 | `DonationConcurrencyTests` parallel webhook/callback replay |
| 17 | `PaymentDataLeakTests` DB/log/telemetry/browser/admin inspection |
| 18 | `RoleAuthorizationMatrixTests` all identities × capabilities + audit |
| 19 | `EditorialLifecycleTests` preview/publish/unpublish/rollback |
| 20 | `MediaUploadSafetyTests` type/size/corrupt/executable/path stability |
| 21 | `SeoManifestTests` metadata/JSON-LD/sitemap |
| 22 | `LinkAssetCrawlTests` public/authenticated crawl + mixed content |
| 23 | `AccessibilityTests` axe automation + retained manual QA record |
| 24 | Lighthouse/Web Vitals runs + 25 rps/10 min load report |
| 25 | `DependencyIsolationTests` one dependency fault at a time |
| 26 | `SecurityControlTests` headers/TLS/CSRF/auth/upload/webhook/injection |
| 27 | `ConsentE2ETests` denied/grant/withdraw network and cookie assertions |
| 28 | Playwright supported matrix + manual current iOS/Android evidence |
| 29 | clean-agent restore/build/test/publish transcript and package hash |
| 30 | promotion record proving identical SHA-256 |
| 31 | induced migration/health failure rollback rehearsal |
| 32 | timed Staging database/media restore integrity report |
| 33 | synthetic trace correlation and alert/runbook evidence |
| 34 | clean developer/agent documentation rehearsal |
| 35 | repeated dry-run/apply/reconcile reports with zero destructive overwrite |

## 15. Tradeoffs, risks, and CTO-reserved decisions

**Optimized for:** fidelity, one Visual Studio solution, low operating cost, reversible delivery,
test seams, and isolation of volatile providers. **Given up:** independent module scaling,
instant global edge delivery, and a turnkey third-party CMS.

Principal risks:

1. Public crawl cannot reveal widget/form/payment configuration. Mitigation: immutable timestamped
   evidence, explicit residual-risk report, and mandatory later export reconciliation when available.
2. Pixel parity can be destabilized by third-party fonts/widgets. Mitigation: licensed local assets,
   snapshot-backed dynamic regions, and consent-safe placeholders.
3. Custom editorial workflow is more implementation than a hosted CMS. Mitigation: narrow content
   model, revision immutability, phased delivery; a hosted CMS was rejected because it adds service
   cost, contract impedance, and a second authorization surface.
4. Same-origin media streaming through App Service may become a bandwidth bottleneck. Mitigation:
   range/cache support and measure R-42/R-43; add Front Door/CDN only on evidence and CTO approval.
5. In-process durable workers may be delayed during app suspension. Mitigation: DB leases,
   on-request freshness checks, alerts, and retry on next activation; add a separate job host only
   if measured reliability requires it.

CTO-reserved recommendations:

- **RPO/RTO:** retain A-01 (24h/4h) until business impact justifies higher-cost redundancy.
- **Raw `/wp-*` endpoints:** do not preserve administration/plugin endpoints; preserve public assets
  and indexed paths, and assign explicit redirect/410 outcomes in the manifest.
- **Costed enhancements:** default to none. Approve slots/CDN/private endpoints/Defender/zone or geo
  redundancy only when security, load, or recovery evidence requires them. This is not an
  implementation blocker because the baseline design satisfies the frozen contract without them.

## 16. Amendment: deterministic T01 screenshot capture

### Verified amendment context

- **FACT:** The architecture already selects Playwright for visual/browser/accessibility evidence
  (`architecture.md:92,289,361,406,417,444` before this amendment), so using the Playwright library
  in the baseline capture tool extends an accepted pattern rather than adding a second browser
  automation stack.
- **FACT:** The current T01 tool launches an installed Edge/Chrome process and derives a smaller
  browser width plus a fractional device scale for viewports below 500px
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselineCaptureService.cs:763-777,797-821`).
- **FACT:** It renders rewritten local HTML rather than the live page, injects fixture CSS and
  template-specific width/font/layout overrides, and removes or replaces source markup
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselineCaptureService.cs:802-806,1828-1945`).
- **FACT:** Its declared allowlist is applied by HTML rewriting, not browser request interception;
  the capture project has no browser-control dependency
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselineCaptureService.cs:1947-2011`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj:1-10`).
- **FACT:** Playwright provides deterministic `ViewportSize`, `DeviceScaleFactor`, request routing,
  network events, and `ServiceWorkers = Block`; its documentation warns that opting out of viewport
  emulation is non-deterministic. Sources:
  [browser context options](https://playwright.dev/dotnet/docs/api/class-browser#browser-new-context),
  [network interception](https://playwright.dev/dotnet/docs/network), and
  [browser installation](https://playwright.dev/dotnet/docs/browsers), inspected 2026-08-15.

### Decision

**APPROVED:** replace only the T01 screenshot execution path with the `Microsoft.Playwright` .NET
library and its package-matched bundled Chromium. Do not use direct CDP: it would require custom
protocol, lifecycle, download, routing, and compatibility code that Playwright already supplies.
The HTTP crawl/import/navigation/AC-35 code remains unchanged because those gates are approved.

Playwright is **tooling-only**. It must not be referenced by Domain, Application, Infrastructure,
Web, Migration, or the deployable application artifact, and it adds no Azure/runtime service.

### Binding capture contract

1. Launch one package-matched bundled Chromium browser per capture run; reuse the browser but create
   a fresh non-persistent `BrowserContext` for every template/viewport capture.
2. Use `BrowserNewContextOptions` with:
   - `ViewportSize = { Width = requestedWidth, Height = requestedHeight }`;
   - `ScreenSize` equal to the viewport;
   - `DeviceScaleFactor = 1`;
   - `ServiceWorkers = Block`, `AcceptDownloads = false`, `IgnoreHTTPSErrors = false`;
   - `Locale = "en-US"`, `TimezoneId = "America/Los_Angeles"`, `ColorScheme = Light`,
     `ReducedMotion = Reduce`, and no permissions/storage state.
   Do not set `NoViewport`, mobile device descriptors, browser window-size flags, or fractional
   scale factors.
3. Navigate the retained representative **live HTTPS URL directly**. Do not load a `file:` copy,
   rewrite the captured HTML, replace iframe/form/media markup, inject fixtures, or add any CSS.
   Specifically remove the screenshot path's use of `CreateScreenshotFixture`,
   `CreateViewportConstraintStyle`, `SanitizeForGetOnlyScreenshot`, and resource-filter rewriting.
4. Register context-wide request interception before creating/navigating the page. Default deny:
   - allow only `GET` and `HEAD`;
   - allow the exact Husaynia HTTPS origin for document and same-origin resources;
   - allow an explicit, versioned static-host list only for necessary `stylesheet`, `font`, and
     image resources; no wildcard subdomains and no arbitrary third-party scripts;
   - abort all other methods/origins, payment/form endpoints, analytics/tracker hosts, beacon/event
     streams, downloads, and post-load document navigation;
   - set `ServiceWorkers = Block`; block WebSockets using Playwright WebSocket routing when the
     pinned version supports it, otherwise a context init script may disable only
     `WebSocket`, `EventSource`, `sendBeacon`, service-worker registration, and programmatic form
     submission. Such a script must not mutate DOM, style, geometry, text, or media state.
5. Every browser request/response/failure and WebSocket/capability attempt produces a
   `ScreenshotNetworkDecision` record containing capture key, timestamp, method, redacted URL,
   resource type, navigation flag, decision (`allow|block`), stable reason code, response status or
   failure, and policy version. Strip fragments and redact values for token/key/session/payment
   query names. Do not record headers, cookies, request bodies, credentials, or form values.
6. The allowlist is executable configuration and test data, not prose. A capture fails closed if an
   unclassified request is observed, if a blocked request is accidentally continued, or if an
   allowed host/method is absent from policy. The network-decision artifact is checksummed with the
   baseline.
7. Deterministic readiness is automation-side polling, not injected page markup:
   `DOMContentLoaded`; `document.fonts.ready`; all visible same-origin/allowed images complete;
   no in-flight allowed request; then at least three identical samples, 250ms apart, of
   `innerWidth`, `innerHeight`, `devicePixelRatio`, document scroll width/height, and body bounding
   box. Bound readiness to 30 seconds and each capture to 45 seconds. `networkidle` alone is not a
   readiness contract.
8. Capture a viewport screenshot (`FullPage = false`) with no masking and no screenshot-time
   layout stylesheet. Record DOM metrics from `EvaluateAsync`: actual viewport, DPR, document/body
   dimensions, horizontal overflow, up to 20 overflowing element descriptors, visible landmark
   counts, font readiness, incomplete image count, and readiness sample history.
9. The PNG must exactly equal the requested CSS viewport in pixels because DPR is 1. Actual
   `window.innerWidth`, `window.innerHeight`, and `devicePixelRatio` must equal requested width,
   requested height, and 1; mismatch fails the capture rather than being rescaled.
10. Capture the six approved template representatives at all six named viewports sequentially
    (36/36). No failed/tooling-unavailable row is acceptable for T01 approval. A per-capture error
    is retained, the remaining matrix may continue for diagnostics, and the command exits nonzero.
11. Do not automatically overwrite an approved baseline in PR validation. Generate into a
    run-specific staging directory, validate all gates, then require explicit reviewer promotion.

### Dependency and supply-chain constraints

- Add only `Microsoft.Playwright` to the T01 capture tool, pinned to one exact non-preview version
  and committed in its NuGet lock file. Do not use a floating version or a separately installed
  system Edge/Chrome.
- The package version and its required Chromium revision are one compatibility unit. Updating either
  requires regenerating and reviewing screenshots and network evidence.
- CI builds the tool first, then runs the generated `playwright.ps1 install --no-shell chromium`
  (and `--with-deps` only on a supported Linux agent). Cache browsers outside the repository using
  a key containing OS, architecture, T01 lock-file hash, and Playwright version. Never commit browser
  binaries.
- CI records .NET SDK, OS image, Playwright package, Chromium executable version/revision, install
  command/result, and browser-cache key in capture provenance. NuGet audit, dependency scan, secret
  scan, SBOM/provenance, and locked restore cover the package; browser downloads are permitted only
  from Playwright's configured official download endpoints during the explicit install step.
- The canonical approval capture runs on one documented Windows agent/browser/font profile.
  Cross-platform runs are diagnostic because font rasterization differs; rebaseline approval must
  use the same recorded profile or explicitly approve a profile change.

### Exact T01 task-plan amendment for the Tech Lead

Keep T01 ownership, dependencies, scope, and approved navigation/AC-35 artifacts unchanged. Replace
only its screenshot subtask and evidence clauses with:

```text
objective addition:
  Capture live representative templates through package-pinned Microsoft.Playwright Chromium at
  exact CSS viewports under a fail-closed browser network policy, without DOM/CSS rewriting.

implementation constraints:
  Add Microsoft.Playwright only to tools/Husaynia.BaselineCapture; fresh context per capture;
  exact viewport/screen and DPR=1; live HTTPS navigation; context RouteAsync default deny;
  ServiceWorkers=Block; block non-GET/HEAD, trackers, payment/forms, WebSockets and post-load
  navigation; retain redacted request decision log and DOM readiness/overflow metrics.

commands/evidence:
  dotnet restore tools/Husaynia.BaselineCapture --locked-mode
  dotnet build tools/Husaynia.BaselineCapture -c Release --no-restore
  pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
  dotnet test tests/Husaynia.BaselineCapture.Tests
  dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --max-duration-minutes 60
  Retain 36 PNGs, screenshots.json, screenshot-network-decisions.json,
  screenshot-capture-provenance.json, DOM metrics, checksums, and nonzero failure evidence.

exit criteria replacement:
  36/36 captures pass; every PNG and actual browser viewport exactly matches its named dimensions
  at DPR=1; no layout-altering injection occurred; every attempted browser request/capability has
  an allow/block decision; no disallowed method/origin/capability continued; deterministic
  readiness and overflow metrics exist; two consecutive controlled runs produce the same DOM
  metrics and reviewer-acceptable screenshots, with any dynamic pixel variance explicitly masked
  only by the later comparison tool, never by capture-time page mutation.
```

Required independent re-gates are limited to: test engineer (exact viewport/readiness/artifacts),
security engineer (fail-closed network policy/log redaction/supply chain), and code reviewer
(removal of transformed viewport/CSS path and bounded lifecycle). Navigation and AC-35 are not
reopened unless the implementation changes their approved files or outputs.

## 17. Binding ADR-009 consolidated rework A-I

This section supersedes section 16 only where it is more specific. It changes T01 capture,
comparison, verification, and baseline promotion; it does not reopen navigation hierarchy,
route/import schemas, migration decisions, or `mediaRefs`. For T01, the facts below are the current
state; section 1 records the mission's earlier pre-implementation snapshot.

### Verified current state

- **FACT:** T01 owns only `tools/Husaynia.BaselineCapture/**`,
  `tests/Husaynia.BaselineCapture.Tests/**`, and `evidence/baseline/**`
  (`task-plan.md:153-161`).
- **FACT:** the tool pins `Microsoft.Playwright` exactly `1.62.0`; its lock resolves `1.62.0`, and
  the package metadata maps Chromium revision `1234` to `151.0.7922.34`
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj:12`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/packages.lock.json:7-8`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/bin/Release/net10.0/.playwright/package/browsers.json:5-8`).
- **FACT:** current promotion validates only selected screenshot fields, deletes the approved
  destination, then copies directly into it; it has no full checksum pre/post verification,
  sibling copy, rollback, lock, or fault recovery
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/Program.cs:114-169`).
- **FACT:** current determinism passes on equal DOM metrics even when PNG hashes differ; the
  retained evidence reports five pixel-hash differences and `passed: true`
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/ScreenshotDeterminismComparer.cs:48-88`;
  `../../../HusayniaSite/evidence/baseline/screenshot-determinism.json:7-29`).
- **FACT:** current trust is derived from a caller-supplied/inventory URL and host equality. The
  crawler checks only `Host`, while screenshot policy does not reject credentials, IP literals,
  non-default ports, private/reserved resolution, DNS changes, or rebinding
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/Program.cs:73-88`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselineCaptureService.cs:1231-1264,1314-1315`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:44-100`).
- **FACT:** Chromium launch currently sets headless/channel only and does not explicitly enable
  `ChromiumSandbox`, sanitize the child environment, or verify the executable SHA-256 before launch
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:190-194`).
- **FACT:** a context is fresh per matrix row, but navigation retries reuse that row's context/page
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:250-264,422-427,527-555`).
- **FACT:** quality calculation does not include horizontal overflow, giant SVG, or loading-only
  state even though overflow is recorded
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:453-478`).
- **FACT:** the retained `event-detail/tablet-768x1024` row is marked `captured/pass` while
  `horizontalOverflow=true` and `documentScrollWidth=850` for a 768px viewport
  (`../../../HusayniaSite/evidence/baseline/screenshots.json:2865-2882`).
- **FACT:** R-06/AC-03 require no page-level horizontal overflow
  (`requirements.md:51,118`), while ADR-009 requires genuine source overflow to be recorded rather
  than hidden (`adr/ADR-009-deterministic-browser-capture.md:24-27`).

### Decision and component boundaries

Implement the smallest extension of the existing tool; add no NuGet dependency and keep
`Microsoft.Playwright` exactly `1.62.0` with Chromium `151.0.7922.34`.

| Component | Binding responsibility |
|---|---|
| `CaptureProfile` | Immutable canonical origin, six representative paths, six viewports, approved static hosts/resource types, policy version, browser version/revision/hash profile. Inventory may select content, but never expands trust. |
| `TrustedEndpointPolicy` | Validate every crawl/browser URI and redirect; resolve and pin public IPs; provide the same decision/reason codes to `HttpClient` and Playwright. |
| `PlaywrightScreenshotCapture` | Launch verified sandboxed Chromium once per run; create a fresh context and page per attempt; enforce routing, request ledger, readiness, snapshot barrier, metrics, and PNG capture. |
| `ScreenshotQualityEvaluator` | Pure, fail-closed evaluation of viewport, readiness, landmarks, loading/giant-SVG state, donation shell, network completeness, and overflow. |
| `ScreenshotDeterminismComparer` | Require the exact unique 36-key set in both runs; compare metrics and pixels; create per-key diff artifacts for changed PNGs using the existing Playwright browser/canvas, not a new image package. |
| `BaselineEvidenceValidator` | Read-only full-tree validation: required artifacts, capture lineage, hashes, schemas, trust/provenance, 36-key completeness, quality, determinism/review, and no stale/extra unchecked files. |
| `BaselinePromotionService` | Exclusive promotion lock, sibling copy, complete verification, swap, post-verify, prior-baseline restore, and crash recovery. |

Dependencies point inward: CLI/matrix runners call these components; policy, quality, ledger, and
validators remain framework-light and unit-testable. Navigation extraction, route/import manifest
construction, religious content, and `mediaRefs` remain byte-for-byte behaviorally unchanged.

The fixed representative map is:
`home=/`, `content-page=/announcements/`, `contact-form=/contact-us/`,
`donation-form=/donate-construction/`,
`event-detail=/event/%E2%9A%94%EF%B8%8F-battle-of-badr/`, and
`religious-content=/duas-dua-e-tawassul/`. The fixed static-host list is
`fonts.googleapis.com`, `fonts.gstatic.com`, `cdnjs.cloudflare.com`, and `cdn.jsdelivr.net`.
Changing either list is a reviewed capture-profile change and requires complete regeneration.

### Binding contracts

#### Canonical trust and browser process

1. The sole primary origin is `https://www.husaynia.org:443`. Reject user-info/credentials,
   IP-literal hosts, any scheme other than HTTPS, any host other than `www.husaynia.org`, and any
   effective port other than 443. `--base-url` may remain for compatibility but accepts only this
   normalized origin; otherwise exit `2`.
2. Every redirect and document/frame navigation is revalidated before continuation. Outside-origin
   redirects/navigation fail closed. Same-origin public GET/HEAD child documents may render;
   admin, authentication, form-submit, checkout, payment, and mutation endpoints remain blocked.
3. Static resources remain an exact, versioned host/resource-type allowlist. Static hosts receive
   the same credential/IP/port/DNS checks and may serve only approved stylesheet/font/image GET/HEAD
   requests. No wildcard host or inventory-discovered host is trusted.
4. Before the first request, resolve every approved hostname, reject any loopback, private,
   link-local, unspecified, multicast, CGNAT, documentation, benchmark, reserved, or otherwise
   non-public address, sort the complete address set, select one deterministic address, and record
   the set. Before each attempt, resolve again; any set change is `dns-set-changed` and fails the
   attempt/run. Pin the selected address for the run: `SocketsHttpHandler.ConnectCallback` for crawl
   traffic and Chromium host-resolver mapping for browser traffic. TLS still validates the original
   hostname; `IgnoreHTTPSErrors=false`.
5. Disable ambient proxies, cookies, default credentials, and authentication for crawl traffic.
   Launch Chromium with `ChromiumSandbox=true` and an allowlisted child environment containing only
   OS/runtime/temp variables required to start Chromium. Do not pass proxy, secret, CI, repository,
   workspace, cookie, storage-state, client-certificate, permission, or extra-header values.
   Launch from an empty per-run OS temporary working directory. Browser screenshots and comparison
   images return as bytes for .NET to write; never give Chromium a workspace output path or `file:`
   URL.
6. Before `LaunchAsync`, hash the resolved executable and match a checked-in T01-owned profile keyed
   by OS/architecture, Playwright `1.62.0`, revision `1234`, and browser
   `151.0.7922.34`. Missing/mismatched identity or SHA-256 exits `1` without launching. Provenance
   records expected/actual hash, revision, sandbox state, environment policy, and pinned endpoints.
7. Run as a non-administrative account. The browser cache is read/execute, the repository is
   read-only to the browser, and only the run staging directory is writable by the tool. Document
   this in `tools/Husaynia.BaselineCapture/README.md` and the generated evidence README.

#### Attempts, readiness, donation, and logging

1. A matrix key is `{templateKey}|{viewport}`. The expected set is exactly six approved templates
   times six approved viewports, with no duplicates or omissions.
2. One browser may be reused per run, but each of at most three attempts creates and disposes a new
   non-persistent context and page. No attempt inherits cookies, cache, frames, navigation state, or
   request ledger state from another attempt.
3. The fixed donation representative remains a safe public GET. Do not click, focus, type, submit,
   or follow payment/checkout navigation. A passing donation row requires the same-origin embedded
   form shell, stable per-key form count across controlled runs, blocked payment/mutation traffic,
   and the same fresh-attempt lifecycle as every other template.
4. `RequestLedger` registers every routed request before allow/block. Each request has exactly one
   request decision and exactly one terminal `response|failure`; response status is retained until
   `RequestFinished`, and blocked requests are terminally recorded without duplicate failure events.
   Untracked, duplicate-terminal, accidentally continued blocked, or unclassified requests fail the
   attempt.
5. Readiness remains bounded by the existing 30/45-second limits. After stable geometry/fonts/images
   and zero allowed in-flight requests, enter a snapshot barrier: drain all terminals, require a
   250ms quiescent decision window, read metrics, and ensure the ledger is still empty immediately
   before capture. If a request/capability occurs through screenshot completion, discard that PNG
   and retry with a fresh context. All attempts and terminal reasons remain in the decision log.
6. The separate whole-capture deadline defaults to 60 minutes and accepts only 1 through 90
   minutes. It bounds the complete sequential 36-row matrix plus crawl, assets, and final capture
   work without changing the per-attempt 30/45-second limits or maximum three attempts.

#### Binding overflow resolution

The binding documents **do permit and require separation of capture completeness from source-page
quality**: ADR-009 says real overflow must be observed, while R-06/AC-03 says it is not acceptable
quality.

- `status="captured"` means a complete, safely produced PNG/metrics/log record exists.
- `qualityStatus="pass"` means every quality predicate passes. Any page-level horizontal overflow
  (except a positively identified intentionally scrollable table), including
  `event-detail|tablet-768x1024`, sets `qualityStatus="fail"`. Giant SVG, loading-only state,
  incomplete request ledger, viewport mismatch, failed readiness, missing landmarks, or donation
  shell failure also fail quality.
- The matrix continues to 36 rows for diagnosis. A run with 36 captured rows and one overflow row is
  truthfully **36/36 captured, 35/36 quality-pass** and exits `1`; it must not relabel the row failed
  or hide/mutate the source.
- Promotion requires both 36/36 captured and 36/36 quality-pass. Visual-diff review may approve
  dynamic pixel variance; it may never waive overflow or another quality failure. The currently
  retained event-tablet evidence therefore cannot pass the new promotion gate.

Accepting a known overflow baseline later would be a CTO-reserved quality/reliability exception; it
is not part of this decision.

#### Pixel determinism and evidence

1. Run A is a clean full capture in a new staging directory. Run B is a separate screenshot-only
   directory derived from Run A's fixed profile/inventory; recapture must not mutate Run A.
2. Comparison first proves identical unique key sets, complete status/quality, compatible browser
   provenance, equal dimensions, and equal required DOM metrics. It then compares PNG SHA-256.
3. Zero changed PNGs passes directly. Any changed key exits `3` (`review required`) and produces an
   unmasked per-key visual diff plus raw differing-pixel count/ratio/bounds. Optional comparison-only
   masks are explicit, versioned, keyed rectangles with rationale; the unmasked diff is always
   retained. Capture-time page mutation or masking remains forbidden.
4. `screenshot-visual-review.json` names every changed key and exact A/B hashes, diff reference,
   reviewer, timestamp, `accept|reject`, and rationale. Missing/stale/rejected entries fail
   verification. Equal DOM metrics alone can never pass changed pixels.
5. Preserve every existing JSON field and frozen route/import schemas. Additive screenshot
   provenance/determinism fields and new artifacts are permitted:
   `screenshot-diffs/**`, `screenshot-visual-review.json` when needed, and
   `baseline-verification.json`. Checksums are generated last and cover every retained file except
   `checksums.sha256`.
6. Evidence is one coherent lineage: Run A summary and screenshot provenance must identify Run A;
   determinism names Run A and Run B; README states captured and quality counts separately; no stale
   artifact may predate its declared generating step or survive from an earlier staging run.
7. `verify --evidence RUN_A` is the only finalization mutation: after a read-only validation pass it
   writes/replaces `baseline-verification.json`, updates the generated README gate summary, writes
   `checksums.sha256` last, then immediately revalidates the sealed tree. Repeating `verify` with
   unchanged inputs is deterministic.

#### Transactional promotion

`promote --approved --from RUN_A --to evidence/baseline` is allowed only after read-only validation
passes. It uses this state machine under an exclusive sibling lock:

1. Recover any interrupted prior promotion, or refuse if state is ambiguous.
2. Pre-verify Run A completely: required files, unique paths, no symlink/reparse traversal, checksum
   manifest completeness, actual hashes/lengths, schemas, lineage, trust/provenance, 36/36 captured,
   36/36 quality, and accepted determinism review.
3. Copy to a new sibling temporary directory using create-new semantics. Re-enumerate and verify the
   copy byte-for-byte against the preverified source manifest.
4. Rename the existing baseline to a sibling prior-baseline directory, then rename the verified
   sibling temporary directory to `baseline`. Do not delete the prior baseline yet.
5. Re-run complete verification at the destination. Only then delete the prior baseline and release
   the lock.
6. On any failure after the prior baseline moves, move the failed candidate aside and restore the
   prior baseline. If restore itself fails, retain all recovery directories, exit `4`, and print
   exact manual recovery paths; never continue or delete recovery evidence.

This is transactional and crash-recoverable on the same volume. Windows cannot replace a non-empty
directory in one syscall, so the exclusive lock plus recoverable two-rename swap is chosen over a
new indirection/junction scheme.

CLI exit semantics are binding: `0` pass, `1` capture/verification/quality failure, `2` usage or
safety/trust refusal, `3` visual review required, `4` baseline restore failure.

### Failure, concurrency, observability, and test hooks

- Capture and recapture require new empty output directories; refuse non-empty staging instead of
  deleting unknown prior evidence. Compare may create only its declared diff/determinism outputs,
  and verify may create only the finalization files defined above. Retries are bounded and
  sequential.
- Promotion is serialized by an exclusive sibling lock. Source mutation during copy is detected by
  post-copy verification; concurrent capture never targets the approved baseline.
- Structured console events include command/run/capture key/attempt, reason code, counts, duration,
  and promotion phase. Never log headers, cookies, bodies, credentials, form values, environment, or
  unredacted sensitive query values.
- Provide internal test seams, not a new framework: `ITrustedDnsResolver`, a pure
  `ScreenshotQualityEvaluator`, a request-ledger state machine, `IPromotionFileSystem`, and
  `PromotionFaultPoint` values for before/after copy verification, backup move, swap, post-verify,
  and restore. Production composition supplies real implementations; tests supply deterministic
  fakes.

### Precise change boundary

Allowed T01 implementation changes:

- Existing files beneath `tools/Husaynia.BaselineCapture/**`, including CLI, models, IO, crawl,
  screenshot, comparison, matrix runner, project/lock files; the package version and lock resolution
  must not change.
- New focused T01 files such as `CaptureProfile.cs`, `TrustedEndpointPolicy.cs`,
  `BrowserExecutableVerifier.cs`, `ScreenshotQualityEvaluator.cs`,
  `BaselineEvidenceValidator.cs`, `BaselinePromotionService.cs`,
  `chromium-executable-sha256.json`, and tool `README.md`.
- Existing/new files beneath `tests/Husaynia.BaselineCapture.Tests/**`.
- Regenerated `evidence/baseline/**` only after all gates pass and transactional promotion succeeds.

Forbidden: solution/central package/contracts/backend/T02 paths, navigation/migration/mediaRef
behavior, prior applications, forms/payments/production, NuGet TLS policy, Git history, and
`.ai-org/active-mission.json`.

### Independent A-I gates and command order

| Rework | Independent pass criterion |
|---|---|
| A | Fault tests prove pre-copy failure leaves baseline untouched; copy/post-copy/swap/post-verify faults restore the exact prior hash tree; restore failure preserves recovery paths; concurrent promotion is refused. |
| B | Both runs contain the identical unique 36-key set; zero changed pixels or every changed key has an unmasked diff and hash-bound accepted review; DOM-only pass is impossible. |
| C | All baseline evidence is regenerated from the new lineage, README/counts are truthful, and the final checksum manifest covers the exact tree. |
| D | Unit/integration tests reject credentials, IP literals, non-443, private/reserved answers, DNS set change/rebinding, outside redirects, and outside/frame/post-load navigation. |
| E | Provenance proves sandbox `true`, exact package/revision/version, prelaunch executable SHA-256, sanitized environment, no cookies/credentials/proxy/workspace input, and least-privilege instructions. |
| F | Tests prove fresh context/page/ledger per attempt, maximum attempts/timeouts, terminal drain/snapshot barrier, and repeatable donation shell/form metrics without submission. |
| G | Quality tests make any overflow fail, include the event-tablet regression, retain `status=captured`, return nonzero, and refuse promotion. |
| H | Every request has one decision and one terminal; every capability attempt is recorded; unclassified/untracked/duplicate/missing terminal cases fail closed. |
| I | Focused tests, locked restore, Release build, two full controlled captures, inspection, comparison/review, validation, promotion fault suite, real promotion, and post-promotion artifact tests all pass independently. |

Required execution order:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore --filter "Category!=Live"
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --max-duration-minutes 60 --output evidence/staging/run-a
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- recapture-screenshots --no-submit --evidence evidence/staging/run-a --output evidence/staging/run-b
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- compare-screenshot-metrics --first-screenshots evidence/staging/run-a/screenshots.json --first-provenance evidence/staging/run-a/screenshot-capture-provenance.json --second-evidence evidence/staging/run-b --output evidence/staging/run-a/screenshot-determinism.json
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- verify --evidence evidence/staging/run-a
$env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/staging/run-a)
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- promote --approved --from evidence/staging/run-a --to evidence/baseline
$env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/baseline)
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
```

If comparison exits `3`, retain generated diffs, obtain per-key independent review, rerun comparison
with `--review evidence/staging/run-a/screenshot-visual-review.json`, then continue. If
capture/verify exits `1` because of overflow, stop: evidence may be inspected, but promotion is
forbidden.

### Tradeoffs, risks, and migration

- Optimized for trustworthy evidence, recoverable promotion, and security over capture availability
  and speed. DNS pin/change checks and fresh retries can fail during legitimate CDN/DNS rotation;
  rerun later rather than weakening trust.
- Rejected direct baseline overwrite, DOM-only determinism, capture-time masks, system browser,
  browser-sandbox opt-out, arbitrary `--base-url`, and a new image library.
- Existing baseline remains untouched until a new candidate passes. There is no data/schema
  migration and no public API change. Existing screenshot fields are backward compatible; new
  evidence is additive. Rollback before promotion is deletion of staging; promotion faults restore
  the previous tree; after a successful reviewed promotion, Git retains the prior approved evidence.

### 2026-08-17 binding promotion recovery/security amendment

This amendment supersedes only the cross-process recovery/authentication parts of **Transactional
promotion**, **Failure, concurrency, observability, and test hooks**, and gate A. The sibling-copy,
full pre/post verification, same-volume two-rename swap, same-process prior restore, and exit-code
contracts remain binding.

#### Verified current state

- **FACT:** production promotion defaults to a shared `PhysicalPromotionJournalAuthenticator`, and
  both startup recovery and same-process rollback read the persisted journal before deciding what
  to move, delete, restore, or finish
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselinePromotionService.cs:317-334,400-406,583-759,885-934`).
- **FACT:** the authenticator stores one HMAC key in the current user's Local Application Data,
  protects it with Windows DPAPI `CurrentUser`, and permits the current user in the artifact ACL
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/PromotionSecurity.cs:353-377,512-543,651-679`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/PromotionSecurity.cs:131-156,215-249`).
  This authenticates corruption by a different principal; it does not distinguish two processes or
  workspaces running as the same user.
- **FACT:** reparse checks resolve path strings and are followed later by path-based
  `Directory.Move`/`Directory.Delete`; tree enumeration rejects reparse points but does not inspect
  file link count or stable file identity
  (`../../../HusayniaSite/tools/Husaynia.BaselineCapture/CaptureIO.cs:120-144`;
  `../../../HusayniaSite/tools/Husaynia.BaselineCapture/BaselinePromotionService.cs:1643-1698,1759-1796`).
- **FACT:** the physical-authenticator test constructs the production default and therefore touches
  the real per-user key store instead of a test-owned directory
  (`../../../HusayniaSite/tests/Husaynia.BaselineCapture.Tests/BaselinePromotionServiceTests.cs:1351-1379`).
- **FACT:** current tests require authenticated cross-process auto-restore/cleanup, while separate
  tests already prove exact same-process rollback after backup/swap/post-verify faults
  (`../../../HusayniaSite/tests/Husaynia.BaselineCapture.Tests/BaselinePromotionServiceTests.cs:324-398,780-836,1525-1694`).

#### Binding decision

**APPROVED:** automatic recovery is limited to failures handled by the same
`PromoteAsync` invocation. It uses only its in-memory transaction context: source/prior manifests,
stable artifact identities, phase, and sibling names. It must not reread a persisted journal to
authorize rollback.

After a process crash or restart, **any** recognized sibling residual
(`baseline.transaction-*`, its temporary files, `baseline.prior-*`,
`baseline.candidate-*`, `baseline.failed-*`, or `baseline.failed-prior-*`) is ambiguous by
definition. Under the promotion lock, the next invocation preserves every residual, does not parse,
authenticate, quarantine, delete, rename, restore, or complete any of them, emits
`promotion-interrupted-state-manual-recovery-required` with logical sibling names, and exits `4`.
The persistent lock file alone is not transaction residue.

For ADR-009, **crash-recoverable** therefore means that the last approved tree and all recovery
artifacts are preserved for explicit operator recovery; it does not mean unattended automatic
restore. This satisfies “recover ... or refuse if state is ambiguous”: without an external trust
anchor every cross-process state in an owner-writable workspace is ambiguous, so refusal is the
required fail-closed branch.

No separate principal, service, hardware signer, package, or infrastructure change is required for
this design because persisted state grants no automatic mutation authority. If a future requirement
demands unattended cross-process recovery despite a malicious same-user writer, a separate
principal or hardware/service-backed signing authority becomes mandatory and CTO-reserved.

#### Component and contract delta

| Component | Binding change |
|---|---|
| `BaselinePromotionService` | Replace auto-recovery with a read-only residual scan. Keep same-process rollback, but pass an in-memory `PromotionTransactionContext`; never reload it from disk. |
| Promotion journal | Retain only as an untrusted, bounded, create-new, flushed diagnostic record written before the first canonical move. Its contents may aid an operator but never authorize code. |
| `PhysicalPromotionJournalAuthenticator` | Remove from production composition, including DPAPI/HMAC key, active markers, and the Local Application Data store. |
| `IPromotionFileSystem` | Add a promotion-scoped lease that pins source root and destination parent identity and owns sibling create/open/rename/delete operations. |
| File-system trust policy | Validate ownership/other-principal write access; do not recursively rewrite ACLs on pre-existing or recovered trees. Process-created files inherit/create private ACLs. |

The public CLI remains
`promote --approved --from RUN_A --to evidence/baseline`; no schema, package, deployment, or
production interface changes.

#### Binding state machine

1. Acquire the exclusive sibling lock and a stable, non-following lease on the source root and
   destination parent. Scan residual names. Any residual exits `4` without mutation.
2. Fully validate the source and, when present, enumerate the existing baseline into the in-memory
   prior manifest before any canonical move. Reject unsafe links/identity changes.
3. Create and fully verify a unique sibling candidate. Write and flush the diagnostic journal
   before moving the prior or candidate.
4. Rename the existing baseline to the sibling prior, then the verified candidate to `baseline`;
   post-verify the complete destination.
5. Through the `AfterPostVerify` fault point, including after successful destination verification,
   the transaction remains pre-commit. Any same-process exception/cancellation after the prior move
   performs cancellation-independent rollback from the in-memory context: move the failed candidate
   aside when necessary, restore the exact prior, verify its manifest/identity, then clean
   same-transaction artifacts. A safe rollback returns `1`.
6. If rollback identity is uncertain or restore/verification fails, stop mutating, preserve every
   path, emit logical recovery names, and return `4`.
7. Durable commit is marked only after `AfterPostVerify` returns and the final cancellation check
   succeeds, and before any prior deletion. After that commit boundary, prior cleanup failure
   preserves the verified destination plus remaining prior/journal and returns `4`; it does not
   roll back a verified commit. Delete the journal only after prior cleanup completes.

Exit `2` remains a pre-mutation usage/concurrency/trust refusal. Exit `4` means an incident requiring
manual operator action, including every cross-process residual state.

#### Threat and failure model

- A same-user process can directly edit an owner-writable checkout; a local HMAC cannot remove that
  authority. The security property here is that the promotion tool never elevates attacker-writable
  persisted state into an automatic restore/delete decision. Such an attacker may cause denial of
  service by leaving residual names; availability is not preferred over integrity.
- Path-string check-then-use is not compliant for promotion mutations. The physical implementation
  must open source root and destination parent without following reparse points, retain stable
  volume/file identity, and perform sibling create/open/rename/delete through pinned handles or an
  OS primitive with equivalent object-identity semantics. Identity mismatch fails closed; a later
  path-based cleanup must not follow it.
- Reject every source, existing-baseline, candidate, prior, and failed-candidate file whose hard-link
  count is not exactly one. Hash and metadata must be read from the same opened file identity.
  If the canonical Windows implementation cannot obtain stable identity/link-count metadata, exit
  `2`; another OS must provide equivalent `openat`/handle-relative guarantees or refuse promotion.
- An administrator, kernel compromise, or direct operator replacement of Git evidence remains
  outside T01. Manual recovery must use an independent authority such as reviewed Git evidence or a
  separately preserved verified staging tree, never the local journal alone.

Required structured events are phase/reason/exit plus logical destination, prior, candidate, failed,
and journal names. Do not emit absolute paths, journal contents, keys, ACLs, or user identifiers.

#### Required test changes

1. Replace every cross-process auto-restore/auto-cleanup assertion with refusal/no-mutation tests for
   each residual type, valid legacy signed journals, forged same-user journals, destination
   present/absent, and multi-artifact combinations.
2. Terminate a child promoter after prior move, after swap, and after destination verification;
   prove the next process returns `4` and leaves byte/identity snapshots unchanged.
3. Keep all same-process fault/cancellation tests and prove rollback succeeds when the diagnostic
   journal is missing, corrupt, or changed after the in-memory transaction begins.
4. Add physical Windows ancestor-replacement and hard-link tests. No operation may reach an
   attacker-selected target; pre-move detection returns `2`, and post-move uncertainty preserves
   recovery artifacts and returns `4`.
5. Remove the production-key-store test. Every test uses a unique temp parent/journal and asserts
   the real Local Application Data promotion-security path is neither created nor changed.
6. Preserve tests for full source/candidate/destination/prior manifest equality, source mutation,
   lock exclusion, cleanup failure, logical-name-only diagnostics, and exact prior restoration.

#### Tradeoffs, risks, and migration

- Optimized for integrity, smallest scope, and an implementable no-infrastructure design. The cost
  is loss of unattended crash recovery and a deliberate denial-of-service response to any residue.
- A signed per-user journal was rejected because it falsely treats same-user processes as separate
  principals. A workspace-derived key, ACL-only solution, or journal semantic validation was
  rejected for the same reason. A separate principal/service/hardware signer would support trusted
  auto-recovery but violates current scope and is unnecessary when restart behavior is refusal.
- Existing signed journals are not migrated or trusted; they trigger exit `4`. The obsolete per-user
  key store is never read or automatically deleted and may be removed manually only after confirming
  no old promoter is running. Rollback is a source/ADR revert before promotion; re-enabling
  cross-process auto-recovery would reopen the security defect.
- Manual recovery runbook: stop promoters, preserve a copy of all siblings, select the authoritative
  tree using independent reviewed evidence, restore/verify it explicitly, then remove residuals.
  T01 adds no automatic `repair` command.

### 2026-08-18 ADR-009 v3 availability and security amendment

This amendment supersedes the run-long DNS-set-equality rule and the treatment of empty-context
cookie reads as blocked capabilities. The canonical origin remains fixed at
`https://www.husaynia.org:443`; inventory cannot extend trusted hosts.

- The primary-origin answer set is immutable for a run. Any change fails
  `origin-dns-set-changed`.
- Before each fresh browser context, every approved static host is resolved. All answers must be
  public. The ordinal-first sorted address is pinned for that context's immutable DNS epoch.
  Legitimate all-public CDN rotation is recorded and allowed. If selected pins change, the
  hash-verified sandboxed browser is relaunched before the context is created.
- Request authorization revalidates that observed answers remain entirely public while the socket
  destination stays on the context pin. TLS validates the original hostname and resolver rules end
  in `MAP * ~NOTFOUND`. Empty, failed, mixed public/private, or non-public answers fail closed.
- Fresh nonpersistent contexts begin and end with zero cookies. `document.cookie`,
  `cookieStore.get()`, and `cookieStore.getAll()` reads return empty results and produce paired
  allowed capability decisions with reason `cookie-read-empty-context`. Cookie writes, Cookie
  Store mutation, outbound `Cookie` headers, imported storage, and state inheritance remain blocked.
- Evidence records the DNS epoch, host class, selected pin, complete public answer-set hash,
  observation time, rotation flag, browser instance, capture key, and pre/post validation without
  recording headers, bodies, cookie/storage values, credentials, or secrets.
- Route `Sitemap` is true only for exact canonical URLs found in retained
  `<urlset><url><loc>` evidence. Sitemap-index child locations, discovery, and endpoint-seed
  heuristics do not establish membership. Raw sitemap files are bound to successful HTTP evidence
  by saved path, length, and SHA-256.
- Visual-review JSON is case-sensitive and rejects unknown or duplicate properties. All ten fields
  are required. Masks are either both null or use version `1` with 1-64 bounded, in-image,
  positive-area rectangles and nonempty rationale. Masks never suppress unmasked diffs, quality
  failures, or independent hash-bound acceptance.

No security exception, new dependency, production change, public API change, or Azure change is
authorized. Cookie writes/transmission, weaker origin continuity, private-address tolerance, and
unpinned connections remain prohibited.
