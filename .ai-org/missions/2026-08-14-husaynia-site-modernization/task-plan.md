# Husaynia.org Replica — Dependency-Ordered Implementation Plan

Mission: `2026-08-14-husaynia-site-modernization`  
Plan status: `READY`  
Architecture authority: `architecture.md` and ADR-001..ADR-008  
Implementation target: `C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`  
Prohibited: production deployment, paid-SKU selection, secrets, real payments/forms, changes to
`Husaynia/`, git commit/push, and changes to shared `active-mission.json`.

## Frozen implementation contracts

These contracts land in T02 and may not be renegotiated by parallel tasks. A proposed breaking
change stops the affected wave and returns to the Tech Lead/Architect.

### C1 — Route manifest schema

Canonical file: `contracts/routes/route-manifest.schema.json`; captured instance:
`evidence/baseline/route-manifest.json`.

Required root fields: `schemaVersion`, `captureId`, `capturedAtUtc`, `sourceBaseUrl`, `routes`.
Each route has:

```text
routeId, legacyPath, canonicalPath, expectedStatus, redirectTarget?,
templateKey, contentKey?, indexable, sitemap, metadataKey?, assetKeys[],
dynamicRegionKeys[], evidenceRefs[], contentChecksum?
```

Rules: paths are leading-slash, host-free, decoded Unicode normalized to NFC, query strings are
listed only when contractually significant, statuses are `200|301|308|404|410`, redirects have one
target and one hop, `200` routes have no redirect target, canonical paths are unique, raw `/wp-*`
technical routes require an explicit redirect/410 decision, and `/wp-content/uploads/**` aliases
are first-class asset contracts.

### C2 — Content and module interfaces

Canonical namespace: `Husaynia.Application.Contracts`. All methods accept `CancellationToken`;
expected failures use `Result<TSuccess,TError>`, never exceptions for control flow.

```csharp
Task<Result<ContentView, ContentError>> IContentReader.GetByPathAsync(string path, CancellationToken ct);
Task<Result<RevisionView, ContentError>> IContentReader.GetPreviewAsync(ContentId id, RevisionId revisionId, PreviewToken token, CancellationToken ct);
Task<Result<DraftReceipt, ContentError>> IContentEditor.SaveDraftAsync(ContentDraft draft, RevisionId? expectedDraft, UserContext actor, CancellationToken ct);
Task<Result<PublishReceipt, PublishError>> IPublisher.PublishAsync(ContentId id, RevisionId expectedDraft, UserContext actor, CancellationToken ct);
Task<Result<PublishReceipt, PublishError>> IPublisher.UnpublishAsync(ContentId id, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
Task<Result<PublishReceipt, PublishError>> IPublisher.RollbackAsync(ContentId id, RevisionId priorRevision, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
Task<Result<EventPage, CalendarError>> IEventReader.GetBySlugAsync(string slug, CancellationToken ct);
Task<Result<IcalDocument, CalendarError>> ICalendarExporter.ExportAsync(EventSelection selection, CancellationToken ct);
Task<Result<PrayerSchedule, PrayerError>> IPrayerScheduleService.GetMonthAsync(YearMonth month, string timeZoneId, CancellationToken ct);
Task<Result<SearchPage, SearchError>> IPublicSearch.SearchAsync(string query, int page, int pageSize, CancellationToken ct);
Task<Result<FormReceipt, FormError>> IFormSubmissionService.SubmitAsync(FormSubmissionCommand command, RequestFingerprint fingerprint, CancellationToken ct);
Task<Result<CheckoutRedirect, DonationError>> IDonationService.CreateCheckoutAsync(DonationIntent intent, string idempotencyKey, CancellationToken ct);
Task<Result<WebhookReceipt, WebhookError>> IDonationService.HandleWebhookAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken ct);
Task<Result<ImportPlan, ImportError>> IImportPlanner.PlanAsync(ImportSource source, ImportMode mode, CancellationToken ct);
Task<Result<ImportReceipt, ImportError>> IImportExecutor.ApplyAsync(ImportPlanId planId, UserContext actor, CancellationToken ct);
```

`ContentItem` has stable identity and draft/published pointers. `ContentRevision` is immutable.
Rollback creates a new revision. Typed event, prayer, media, form, donation, audit, import, and job
tables remain outside generic content JSON.

### C3 — Role and permission matrix

Policy names are `SiteAdministration`, `ContentManagement`, `EventManagement`, `MediaManagement`,
`DonationOperations`, and `AuditRead`. Public registration is disabled and MFA is required for all
six privileged roles.

| Capability | SiteAdministrator | ContentEditor | EventEditor | MediaEditor | DonationOperator | ReadOnlyAuditor |
|---|---:|---:|---:|---:|---:|---:|
| users, roles, integrations, settings | RW | - | - | - | - | reports R |
| pages, announcements, religious content | RW | RW | - | - | - | audit R |
| events and iCal | RW | - | RW | - | - | audit R |
| media and aliases | RW | - | - | RW | - | audit R |
| donation status and reconciliation | RW | - | - | - | RW | reports R |
| audit and operational reports | RW | - | - | - | limited R | R |

Both endpoint and Application use-case checks are mandatory. Every allowed and denied privileged
attempt appends actor, roles, action, target, outcome, correlation ID, and UTC timestamp.

### C4 — External adapter interfaces

Application owns ports; Infrastructure owns provider SDKs:

```csharp
IPrayerSource.GetAsync(PrayerSourceRequest request, CancellationToken ct)
ISocialFeedProvider.FetchAsync(SocialFeedRequest request, CancellationToken ct)
IMediaStore.PutAsync(MediaWriteRequest request, CancellationToken ct)
IMediaStore.OpenReadAsync(MediaReadRequest request, CancellationToken ct)
IUploadSafetyValidator.ValidateAsync(UploadCandidate candidate, CancellationToken ct)
IOutboundMessageSender.SendAsync(OutboundMessage message, CancellationToken ct)
IPaymentGateway.CreateCheckoutAsync(PaymentCheckoutRequest request, string idempotencyKey, CancellationToken ct)
IPaymentGateway.VerifyWebhookAsync(ReadOnlyMemory<byte> body, string signature, CancellationToken ct)
IJobLeaseStore.TryAcquireAsync(JobKey key, WorkerIdentity worker, TimeSpan lease, CancellationToken ct)
```

Provider calls have explicit connect/overall timeouts; retries apply only to safe idempotent reads.
Public requests consume persisted prayer/social snapshots rather than live providers. Provider
diagnostics do not reach public responses.

### C5 — Migration manifest schema

Canonical files: `contracts/migration/import-manifest.schema.json` and
`contracts/migration/import-report.schema.json`.

Required root fields: `schemaVersion`, `source`, `sourceVersion`, `capturedAtUtc`, `rightsProfile`,
`candidates`. Each candidate has:

```text
sourceKey, sourceVersion, sourceChecksum, kind, sourceUri?, targetKey,
canonicalPath?, payloadRef, mediaRefs[], dependencyKeys[], expectedTargetChecksum?,
decision(create|update|skip|conflict|orphan|reject), conflictReason?
```

Dry-run is the default. Apply requires an immutable plan ID. Idempotency key is
`sourceKey + sourceVersion`; a target changed since planning becomes `conflict`, never overwrite.
Reports contain counts, decisions, unresolved dependencies, checksums, and no secret values.

### C6 — Immutable pipeline artifact contract

CI produces one directory/archive named `husaynia-site-{version}` containing:

```text
app/Husaynia.Web.zip
migrations/sql/*.sql
migrations/bundle/*
contracts/route-manifest.json
contracts/import-manifest.schema.json
sbom/sbom.spdx.json
reports/test-results/**
reports/security/**
reports/accessibility/**
release/release-manifest.json
release/SHA256SUMS
```

`release-manifest.json` fields: `schemaVersion`, `version`, `commitSha`, `builtAtUtc`, `dotnetSdk`,
`files[{path,sha256,length}]`, `databaseCompatibility`, `requiredConfigurationKeys[]`,
`prohibitedLiveConfigurationInNonProduction[]`. Development, Staging, and Production consume the
same application archive checksum; configuration is injected without rebuilding. Production is a
manual, separately authorized stage and is never run by implementation tasks.

### C7 — Shared module/composition convention

T02 owns all solution/project files and `Program.cs`. Feature tasks add an `IHusayniaModule`
implementation beneath their owned module directory. T02's composition root discovers and invokes
modules deterministically. Feature tasks may not edit `Program.cs`, project files,
`Directory.Packages.props`, shared layouts, EF migration snapshots, or another task's test folder.
EF configurations are discovered with `ApplyConfigurationsFromAssembly`; T18 alone owns generated
migrations and the model snapshot.

## Tasks

T01  Capture approved live baseline and executable route/content evidence
    owner:        frontend-specialist
    objective:    Build repeatable public crawl, screenshot, metadata, form/widget observation, media-download, checksum, and manifest capture tooling; capture the timestamped baseline without submitting forms or payments.
    files:        HusayniaSite/tools/Husaynia.BaselineCapture/**; HusayniaSite/evidence/baseline/**; HusayniaSite/tests/Husaynia.BaselineCapture.Tests/**
    depends_on:   -
    parallel_ok:  yes
    inputs/contracts: requirements R-04..R-18,R-25..R-32; C1/C5; public site and sitemap endpoints; prior app is read-only evidence.
    acceptance:   AC-01..AC-07, AC-11..AC-15, AC-21..AC-22, AC-35
    commands/evidence: `dotnet test tests/Husaynia.BaselineCapture.Tests`; `dotnet run --project tools/Husaynia.BaselineCapture -- capture --no-submit`; schema validation; retained screenshots/HTTP/SEO/media checksums.
    exit_criteria: Every discoverable sitemap/public route and owned asset is represented; named viewport captures and dynamic/form observations exist; unknown widget/export details are explicit residual risks.
    status:       BLOCKED
    rework_status: Final initial-artifact ownership now begins when `NtCreateFile` succeeds: candidate/journal post-create validation failures self-clean through the created handle, while unproven cleanup reports bounded residue at exit `4` and the next invocation refuses without mutation. The crash snapshot helper is extended-root/handle-relative, including a maintained deep OS-temp crash topology; throwing safety diagnostics cannot alter exit `2`; and `AfterPostVerify` is consistently documented as pre-commit, with durable commit after the fault/cancellation check and before prior deletion. Six new adversarial cases and both the deep-temp and canonical full 390-test non-live suites pass; strict Release builds pass with 0 warnings/errors. Exact package/browser pins remain unchanged. Official locked restore remains blocked only by external NU1900 vulnerability-index connectivity. Fresh live Run A/Run B/verify/promotion remain withheld. See `t01-adr009-rework.md`.

T02  Create solution foundation and land frozen contracts
    owner:        developer
    objective:    Create the .NET 10 solution, central package/build policy, projects, shared Result/error/value contracts, module discovery, configuration validation, test scaffolds, and checked-in contract schemas.
    files:        HusayniaSite/HusayniaSite.sln; HusayniaSite/global.json; HusayniaSite/Directory.Build.props; HusayniaSite/Directory.Packages.props; HusayniaSite/NuGet.config; HusayniaSite/src/Husaynia.Domain/Husaynia.Domain.csproj; HusayniaSite/src/Husaynia.Application/Husaynia.Application.csproj; HusayniaSite/src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj; HusayniaSite/src/Husaynia.Web/Husaynia.Web.csproj; HusayniaSite/tools/Husaynia.Migration/Husaynia.Migration.csproj; HusayniaSite/tests/Husaynia.Domain.Tests/Husaynia.Domain.Tests.csproj; HusayniaSite/tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj; HusayniaSite/tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj; HusayniaSite/tests/Husaynia.ContractTests/Husaynia.ContractTests.csproj; HusayniaSite/tests/Husaynia.E2ETests/Husaynia.E2ETests.csproj; HusayniaSite/tests/Husaynia.ArchitectureTests/Husaynia.ArchitectureTests.csproj; HusayniaSite/tests/Husaynia.SystemValidation.Tests/Husaynia.SystemValidation.Tests.csproj; HusayniaSite/src/Husaynia.Domain/Common/**; HusayniaSite/src/Husaynia.Application/Contracts/**; HusayniaSite/src/Husaynia.Web/Program.cs; HusayniaSite/src/Husaynia.Web/Composition/**; HusayniaSite/contracts/**
    depends_on:   -
    parallel_ok:  yes
    inputs/contracts: architecture sections 2-5; ADR-001; C1..C7.
    acceptance:   AC-29
    commands/evidence: `dotnet --version`; `dotnet restore --locked-mode`; `dotnet build -warnaserror`; `dotnet test`; architecture test proving dependency direction and module discovery.
    exit_criteria: Clean solution builds/tests/publishes with empty modules; contract schemas validate; all later tasks can add files without editing shared foundation.
    status:       DONE

T03  Implement persistence and transaction foundation
    owner:        database-specialist
    objective:    Provide SQL Server EF Core context, common rowversion/audit timestamps, configuration discovery, transaction/idempotency primitives, design-time tooling, and database test fixture.
    files:        HusayniaSite/src/Husaynia.Infrastructure/Persistence/Core/**; HusayniaSite/src/Husaynia.Infrastructure/Persistence/Transactions/**; HusayniaSite/tests/Husaynia.IntegrationTests/Persistence/Core/**
    depends_on:   T02
    parallel_ok:  yes
    inputs/contracts: architecture section 6; C2/C7; ADR-003/004/006/007.
    acceptance:   AC-16, AC-19, AC-29, AC-35
    commands/evidence: integration tests against disposable SQL Server/Azure SQL-compatible instance; concurrency and rollback tests; `dotnet ef dbcontext info`.
    exit_criteria: Modules can add discovered entity configurations without editing the context; transactions and optimistic concurrency are proven.
    status:       DONE — 2026-08-20; persistence, transaction, retry, concurrency, design-time, and disposable SQL validation approved.

T04  Implement Identity authorization and append-only audit
    owner:        backend-specialist
    objective:    Implement Identity account administration, six roles/policies, MFA requirement, deny-by-default endpoint/use-case authorization, and append-only allowed/denied audit events.
    files:        HusayniaSite/src/Husaynia.Domain/Identity/**; HusayniaSite/src/Husaynia.Application/Identity/**; HusayniaSite/src/Husaynia.Infrastructure/Identity/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Identity/**; HusayniaSite/tests/Husaynia.Application.Tests/Identity/**; HusayniaSite/tests/Husaynia.IntegrationTests/Identity/**
    depends_on:   T03
    parallel_ok:  yes
    inputs/contracts: C3/C7; R-19..R-24; ADR-004.
    acceptance:   AC-18, AC-26, AC-33
    commands/evidence: role matrix integration suite covering anonymous, ordinary user, and all six roles; audit correlation assertions; public-registration and MFA policy tests.
    exit_criteria: Only matrix-authorized actions succeed and every allow/deny attempt is auditable without sensitive logging.
    status:       DONE — 2026-08-21; Identity administration, six-role authorization, MFA, append-only audit, bootstrap seal, and distributed anonymous throttling independently approved.

T05  Implement immutable content and editorial lifecycle
    owner:        backend-specialist
    objective:    Implement pages, announcements, religious content, navigation, revisions, signed preview, publish/unpublish/rollback, sanitization, Unicode checksums, and SEO fields.
    files:        HusayniaSite/src/Husaynia.Domain/Content/**; HusayniaSite/src/Husaynia.Application/Content/**; HusayniaSite/src/Husaynia.Infrastructure/Content/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Content/**; HusayniaSite/tests/Husaynia.Domain.Tests/Content/**; HusayniaSite/tests/Husaynia.Application.Tests/Content/**; HusayniaSite/tests/Husaynia.IntegrationTests/Content/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C2/C3/C7; ADR-003; R-08,R-09,R-18,R-19A,R-24.
    acceptance:   AC-05, AC-06, AC-18, AC-19, AC-21
    commands/evidence: content revision/publish concurrency tests; Unicode round-trip fixtures; preview noindex/expiry tests; role tests.
    exit_criteria: Published rows are immutable, rollback creates a new revision, drafts never leak, and lifecycle/SEO data is queryable through frozen ports.
    status:       PENDING

T06  Implement events, programs, recurrence, and iCal
    owner:        backend-specialist
    objective:    Implement event/program state, categories, occurrences, timezone-aware recurrence, editor workflows, public reads, and RFC-compliant single/full-calendar iCal.
    files:        HusayniaSite/src/Husaynia.Domain/Calendar/**; HusayniaSite/src/Husaynia.Application/Calendar/**; HusayniaSite/src/Husaynia.Infrastructure/Calendar/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Calendar/**; HusayniaSite/tests/Husaynia.Domain.Tests/Calendar/**; HusayniaSite/tests/Husaynia.Application.Tests/Calendar/**; HusayniaSite/tests/Husaynia.IntegrationTests/Calendar/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C2/C3/C7; R-11,R-21; America/Los_Angeles contract.
    acceptance:   AC-09, AC-10, AC-18, AC-19
    commands/evidence: recurrence/timezone/DST tests; parse generated iCal with an independent parser; draft/past/future visibility tests.
    exit_criteria: EventEditor lifecycle and public/iCal output are deterministic and canonical.
    status:       PENDING

T07  Implement deterministic prayer schedules and fallback
    owner:        backend-specialist
    objective:    Implement configurable Snohomish profiles, DST-aware calculation/provider ports, persisted snapshots, manual overrides, stale labeling, and failure isolation.
    files:        HusayniaSite/src/Husaynia.Domain/Prayer/**; HusayniaSite/src/Husaynia.Application/Prayer/**; HusayniaSite/src/Husaynia.Infrastructure/Prayer/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Prayer/**; HusayniaSite/tests/Husaynia.Domain.Tests/Prayer/**; HusayniaSite/tests/Husaynia.Application.Tests/Prayer/**; HusayniaSite/tests/Husaynia.IntegrationTests/Prayer/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C2/C4/C7; R-10; ADR-005; approved baseline/tolerance remains a pre-release input.
    acceptance:   AC-07, AC-08, AC-18, AC-25
    commands/evidence: fixed-clock DST boundary tests, profile-hash determinism, override precedence, provider timeout/cached/unavailable tests.
    exit_criteria: Same profile/date produces the same schedule; overrides apply last; outages cannot break unrelated pages.
    status:       DONE — 2026-08-28; deterministic Prayer profiles/schedules, DST handling, snapshots, overrides, stale/failure isolation, durable refresh integration, administration, exact EF/T18 schema contract, 69-test independent gate, and final review approved. T01 live tolerance remains a pre-release fidelity input.

T08  Implement safe versioned media and stable aliases
    owner:        backend-specialist
    objective:    Implement media metadata/versioning, Blob adapter, upload safety validation, decode/re-encode, metadata stripping, stable `/wp-content/uploads/**` aliases, ETag/range/cache behavior, and archive/replace workflow.
    files:        HusayniaSite/src/Husaynia.Domain/Media/**; HusayniaSite/src/Husaynia.Application/Media/**; HusayniaSite/src/Husaynia.Infrastructure/Media/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Media/**; HusayniaSite/src/Husaynia.Web/Features/MediaDelivery/**; HusayniaSite/tests/Husaynia.Application.Tests/Media/**; HusayniaSite/tests/Husaynia.IntegrationTests/Media/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C3/C4/C7; R-20,R-26,R-28; Blob emulator/test container only.
    acceptance:   AC-18, AC-20, AC-22, AC-26
    commands/evidence: MIME/extension/magic-byte, corrupt, oversize, executable, path traversal, range, ETag, replacement/alias tests.
    exit_criteria: Only safe allowlisted files are stored/served and replacement never breaks approved stable URLs.
    status:       PENDING

T09  Implement persisted social snapshots and consent-safe feeds
    owner:        backend-specialist
    objective:    Implement provider normalization, persisted feed snapshots, refresh state/jobs, stale/empty/error states, and consent-controlled embed descriptors.
    files:        HusayniaSite/src/Husaynia.Domain/Social/**; HusayniaSite/src/Husaynia.Application/Social/**; HusayniaSite/src/Husaynia.Infrastructure/Social/**; HusayniaSite/tests/Husaynia.Application.Tests/Social/**; HusayniaSite/tests/Husaynia.IntegrationTests/Social/**
    depends_on:   T03
    parallel_ok:  yes
    inputs/contracts: C4/C7; ADR-005; R-12,R-44,R-47.
    acceptance:   AC-11, AC-12, AC-25, AC-27
    commands/evidence: healthy/timeout/rate-limit/empty/stale provider fixture tests; assertion that public read path makes no provider call.
    exit_criteria: Public feed reads are local and provider failures produce accessible state data without diagnostics leakage.
    status:       DONE — 2026-08-21; Social remediation, canonical recurring T19 job integration, liveness repair, tests, security, review, and judgment approved. Locked NuGet audit remains externally blocked by NU1900 metadata availability.

T10  Implement published-only search projection
    owner:        database-specialist
    objective:    Implement maintained `SearchDocument` projection, Azure SQL Full-Text query adapter, input/page bounds, ranking, Unicode support, and dependency-unavailable result.
    files:        HusayniaSite/src/Husaynia.Domain/Search/**; HusayniaSite/src/Husaynia.Application/Search/**; HusayniaSite/src/Husaynia.Infrastructure/Search/**; HusayniaSite/tests/Husaynia.Application.Tests/Search/**; HusayniaSite/tests/Husaynia.IntegrationTests/Search/**
    depends_on:   T03,T05,T06
    parallel_ok:  yes
    inputs/contracts: C2/C7; R-13,R-43.
    acceptance:   AC-13, AC-24, AC-25
    commands/evidence: published/private fixture tests; empty/Unicode/oversize/injection cases; query plan/index evidence; unavailable full-text state test.
    exit_criteria: Search cannot access drafts/admin/donor/form data and meets bounded query behavior.
    status:       PENDING

T11  Implement contact and pledge submission workflows
    owner:        backend-specialist
    objective:    Implement versioned form definitions, accessible validation model, durable submission plus delivery job, duplicate fingerprint, antiforgery, honeypot/rate limits, retention status, and non-public environment sinks.
    files:        HusayniaSite/src/Husaynia.Domain/Forms/**; HusayniaSite/src/Husaynia.Application/Forms/**; HusayniaSite/src/Husaynia.Infrastructure/Forms/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Forms/**; HusayniaSite/tests/Husaynia.Application.Tests/Forms/**; HusayniaSite/tests/Husaynia.IntegrationTests/Forms/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C3/C4/C7; baseline form schema from T01; R-14,R-47.
    acceptance:   AC-14, AC-18, AC-25, AC-33
    commands/evidence: required/malformed/oversize/duplicate/bot/atomic-write tests; development sink and failed delivery retry tests.
    exit_criteria: Invalid/bot submissions create no partial workflow; valid input creates exactly one auditable record/job without sensitive logs.
    status:       DONE — 2026-08-28; versioned Forms definitions/submissions, bounded admission, durable T19 delivery, stale-session authorization, privacy-safe duplicate/rate controls, CAS administration, fenced retention, exact T18 schema handoff, 468-test independent gate, security PASS, correctness APPROVED, and final judgment APPROVED. Live definitions/destinations remain blocked on T01 and production delivery remains disabled.

T12  Implement Stripe Checkout and webhook-authoritative donations
    owner:        backend-specialist
    objective:    Implement data-driven categories/modes/amount rules, sandbox Checkout, read-only success/cancel, verified webhook event idempotency, atomic donation transition and campaign ledger, operator reconciliation, and privacy-safe views.
    files:        HusayniaSite/src/Husaynia.Domain/Donations/**; HusayniaSite/src/Husaynia.Application/Donations/**; HusayniaSite/src/Husaynia.Infrastructure/Donations/**; HusayniaSite/src/Husaynia.Web/Areas/Admin/Donations/**; HusayniaSite/src/Husaynia.Web/Features/StripeWebhook/**; HusayniaSite/tests/Husaynia.Domain.Tests/Donations/**; HusayniaSite/tests/Husaynia.Application.Tests/Donations/**; HusayniaSite/tests/Husaynia.IntegrationTests/Donations/**
    depends_on:   T03,T04
    parallel_ok:  yes
    inputs/contracts: C2/C3/C4/C7; ADR-006; baseline modes/categories from T01; sandbox only.
    acceptance:   AC-15, AC-16, AC-17, AC-18, AC-25, AC-26
    commands/evidence: Stripe sandbox contract fixtures; invalid/decline/cancel/3DS tests; concurrent duplicate webhook test proving one completion/ledger entry; leak inspection.
    exit_criteria: Browser callbacks never complete payments; verified duplicate/concurrent events are harmless; no card/secret data is stored or displayed.
    status:       PENDING

T13  Build public shell, design system, content, and religious templates
    owner:        frontend-specialist
    objective:    Reproduce header/footer/navigation, design tokens, local licensed fonts/assets, responsive shell, content/announcement/religious/legal templates, audio presentation, loading/empty/error primitives, and keyboard behavior.
    files:        HusayniaSite/src/Husaynia.Web/Views/Shared/**; HusayniaSite/src/Husaynia.Web/Features/PublicContent/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/base/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/content/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/navigation/**; HusayniaSite/src/Husaynia.Web/wwwroot/assets/brand/**; HusayniaSite/tests/Husaynia.E2ETests/Shell/**; HusayniaSite/tests/Husaynia.E2ETests/Content/**
    depends_on:   T01,T05
    parallel_ok:  yes
    inputs/contracts: C1/C2/C7; approved visual/menu/content baseline; R-04..R-09,R-18.
    acceptance:   AC-01, AC-02, AC-03, AC-05, AC-06, AC-23, AC-28
    commands/evidence: Playwright screenshots at 320/390/768/1024/1440/1920; keyboard navigation tests; axe scans; religious text/audio fixtures.
    exit_criteria: Shell and static/content templates satisfy baseline tolerance and have no horizontal overflow or serious accessibility defects.
    status:       PENDING

T14  Build calendar, prayer, and social public templates
    owner:        frontend-specialist
    objective:    Build event list/detail/calendar/iCal affordances, prayer today/month/fallback UI, and announcement/photo/video social snapshot states using the shared shell.
    files:        HusayniaSite/src/Husaynia.Web/Features/PublicCalendar/**; HusayniaSite/src/Husaynia.Web/Features/PublicPrayer/**; HusayniaSite/src/Husaynia.Web/Features/PublicSocial/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/calendar/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/prayer/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/social/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/calendar/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/social/**; HusayniaSite/tests/Husaynia.E2ETests/CalendarPrayerSocial/**
    depends_on:   T06,T07,T09,T13
    parallel_ok:  yes
    inputs/contracts: C1/C2/C7; dynamic-region baseline from T01.
    acceptance:   AC-01, AC-03, AC-07..AC-12, AC-23, AC-25, AC-28
    commands/evidence: Playwright healthy/stale/empty/error fixtures; iCal download parse; responsive/axe tests.
    exit_criteria: All dynamic states remain usable, accessible, visually approved, and isolated during dependency faults.
    status:       PENDING

T15  Build media, search, forms, and donation public journeys
    owner:        frontend-specialist
    objective:    Build media gallery/lightbox/load-more, search results/states, contact/pledge forms, and configurable sandbox donation initiation/status/cancel pages.
    files:        HusayniaSite/src/Husaynia.Web/Features/PublicMedia/**; HusayniaSite/src/Husaynia.Web/Features/PublicSearch/**; HusayniaSite/src/Husaynia.Web/Features/PublicForms/**; HusayniaSite/src/Husaynia.Web/Features/PublicDonations/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/media/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/search/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/forms/**; HusayniaSite/src/Husaynia.Web/wwwroot/css/donations/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/media/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/forms/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/donations/**; HusayniaSite/tests/Husaynia.E2ETests/MediaSearchFormsDonations/**
    depends_on:   T08,T10,T11,T12,T13,T16
    parallel_ok:  yes
    inputs/contracts: C1/C2/C7; T01 baseline; consent API from T16.
    acceptance:   AC-01, AC-03, AC-11..AC-17, AC-20, AC-23, AC-27, AC-28
    commands/evidence: Playwright validation/error/duplicate/cancel/status tests; axe/responsive tests; sandbox redirect stubs; network/cookie assertions.
    exit_criteria: Named journeys and failure states work without exposing private data or loading optional tracking before consent.
    status:       PENDING

T16  Implement URL, SEO, redirect, consent, and web security controls
    owner:        backend-specialist
    objective:    Load/validate route contracts, map explicit endpoints, enforce one-hop redirects, generate sitemap/robots/RSS/canonical/OG/Twitter/JSON-LD, implement versioned consent, and apply CSP nonce/TLS/security/CSRF/error middleware.
    files:        HusayniaSite/src/Husaynia.Application/SeoRouting/**; HusayniaSite/src/Husaynia.Infrastructure/SeoRouting/**; HusayniaSite/src/Husaynia.Domain/Operations/Consent/**; HusayniaSite/src/Husaynia.Application/Operations/Consent/**; HusayniaSite/src/Husaynia.Web/Features/SeoRouting/**; HusayniaSite/src/Husaynia.Web/Middleware/**; HusayniaSite/src/Husaynia.Web/wwwroot/js/consent/**; HusayniaSite/tests/Husaynia.ContractTests/RoutesSeo/**; HusayniaSite/tests/Husaynia.IntegrationTests/WebSecurityConsent/**
    depends_on:   T01,T04,T05,T06,T08,T09,T12
    parallel_ok:  yes
    inputs/contracts: C1/C3/C7; R-27..R-30,R-45,R-47; route/metadata manifest.
    acceptance:   AC-04, AC-21, AC-22, AC-26, AC-27
    commands/evidence: route schema/loop/chain tests; SEO XML/JSON-LD validators; security-header/CSRF/CSP tests; consent deny/grant/withdraw network tests.
    exit_criteria: Every manifest route has the approved status/canonical behavior, optional scripts default off, and mandatory web controls pass.
    status:       PENDING

T17  Implement manifest-driven migration and reconciliation CLI
    owner:        migration-specialist
    objective:    Implement crawl/media source now and WordPress XML/DB/media/config adapters, canonical candidate normalization, rights checks, dry-run planning, idempotent apply, optimistic conflict handling, reconciliation, and reports.
    files:        HusayniaSite/tools/Husaynia.Migration/Program.cs; HusayniaSite/tools/Husaynia.Migration/Commands/**; HusayniaSite/tools/Husaynia.Migration/Sources/**; HusayniaSite/tools/Husaynia.Migration/Reporting/**; HusayniaSite/tools/Husaynia.Migration/appsettings*.json; HusayniaSite/src/Husaynia.Application/Migration/**; HusayniaSite/src/Husaynia.Infrastructure/Migration/**; HusayniaSite/tests/Husaynia.ContractTests/Migration/**; HusayniaSite/tests/Husaynia.IntegrationTests/Migration/**
    depends_on:   T01,T03,T05,T06,T08,T12
    parallel_ok:  yes
    inputs/contracts: C1/C5/C7; ADR-007; no authorized export is assumed available.
    acceptance:   AC-04, AC-05, AC-06, AC-21, AC-22, AC-35
    commands/evidence: schema validation; repeated dry-run/apply tests; changed-editor-target conflict test; crawl and synthetic WP fixture reconciliation reports.
    exit_criteria: Replays do not duplicate or overwrite newer edits; every candidate has an accountable decision and checksum.
    status:       PENDING

T18  Consolidate relational schema and create initial migration
    owner:        database-specialist
    objective:    Validate the complete model, keys/indexes/checks/rowversions/full-text setup, generate the initial idempotent migration bundle/script, and prove forward/app rollback compatibility.
    files:        HusayniaSite/src/Husaynia.Infrastructure/Persistence/Migrations/**; HusayniaSite/tests/Husaynia.IntegrationTests/Persistence/CompleteSchema/**
    depends_on:   T04,T05,T06,T07,T08,T09,T10,T11,T12,T17,T19
    parallel_ok:  yes
    inputs/contracts: architecture section 6; C6/C7; all module configurations.
    acceptance:   AC-16, AC-18..AC-20, AC-29, AC-31, AC-35
    commands/evidence: `dotnet ef migrations add InitialCreate`; idempotent script/bundle generation; fresh DB apply; replay; induced failure rollback/forward-fix test; index/constraint inspection.
    exit_criteria: A clean database reaches the complete schema repeatably and uniqueness/concurrency invariants are database-enforced.
    status:       PENDING

T19  Implement durable jobs, health, telemetry, redaction, and operations controls
    owner:        backend-specialist
    objective:    Implement leased DB jobs, retries/dead letters, health/readiness, correlation/OpenTelemetry, redaction, dependency/staleness metrics, retention workflows, and configuration validation.
    files:        HusayniaSite/src/Husaynia.Domain/Operations/Jobs/**; HusayniaSite/src/Husaynia.Application/Operations/Jobs/**; HusayniaSite/src/Husaynia.Application/Operations/Health/**; HusayniaSite/src/Husaynia.Application/Operations/Telemetry/**; HusayniaSite/src/Husaynia.Application/Operations/Retention/**; HusayniaSite/src/Husaynia.Infrastructure/Operations/**; HusayniaSite/src/Husaynia.Web/Features/Operations/**; HusayniaSite/tests/Husaynia.Application.Tests/Operations/**; HusayniaSite/tests/Husaynia.IntegrationTests/Operations/**
    depends_on:   T03
    parallel_ok:  yes
    inputs/contracts: C4/C7; ADR-005; R-44,R-47,R-48,R-50.
    acceptance:   AC-17, AC-25, AC-27, AC-32, AC-33
    commands/evidence: lease/concurrency/retry/dead-letter tests; health degradation tests; synthetic trace correlation; redaction tests; retention workflow tests.
    exit_criteria: Failures are isolated and observable; logs contain correlation but no prohibited data; jobs recover safely after restart.
    status:       DONE — 2026-08-21; durable jobs, retries/dead letters, health/readiness, telemetry/redaction, retention fencing, restart recovery, independent tests, security, code review, and final judgment approved.

T20  Define isolated Azure baseline with Bicep
    owner:        infrastructure-specialist
    objective:    Define parameterized Development/Staging/Production resource groups and App Service, SQL, Storage, Key Vault, App Insights/Log Analytics, identities, health, alerts, backup/lifecycle capabilities without selecting paid SKUs or provisioning.
    files:        HusayniaSite/infra/**
    depends_on:   T02
    parallel_ok:  yes
    inputs/contracts: ADR-002; architecture section 9; C6; capability parameters only.
    acceptance:   AC-30, AC-31, AC-32, AC-33
    commands/evidence: `az bicep build`; Bicep lint; non-deploying what-if/template validation where credentials permit; policy tests proving environment isolation and no literal secrets.
    exit_criteria: Templates validate for all stages, expose required capability parameters, and contain no SKU authorization or deployment execution.
    status:       DONE — 2026-08-20; parameterized inert Azure baseline independently validated with zero deployments.

T21  Implement PR and immutable promotion pipelines
    owner:        devops-sre-specialist
    objective:    Implement locked restore/build/test/analyze/scan/accessibility/artifact CI and checksum-preserving Development/Staging/manual-Production promotion definitions with explicit migration, health, rollback, and evidence gates.
    files:        HusayniaSite/pipelines/**; HusayniaSite/eng/**; HusayniaSite/.config/dotnet-tools.json
    depends_on:   T02,T20
    parallel_ok:  yes
    inputs/contracts: C6; ADR-008; R-34..R-40; production stage disabled/manual by default.
    acceptance:   AC-23, AC-26, AC-29, AC-30, AC-31
    commands/evidence: local pipeline-script dry run; artifact build and SHA verification; YAML validation; induced gate failure; secret/dependency scan configuration tests.
    exit_criteria: One artifact is built once, checksummed, and referenced unchanged across stages; failed gates stop promotion; no production action occurs.
    status:       BLOCKED — 2026-08-27; repository implementation and independent test/security/review gates pass after R12. Completion is externally blocked by fail-closed NU1900 vulnerability metadata access and CTO-authorized GitHub OIDC/Azure FIC installation. No deployment occurred.

T22  Write developer, deployment, editor, security, and operations documentation
    owner:        documentation-specialist
    objective:    Document setup, repository/module contracts, safe changes, tests, sandbox configuration, migration, Azure stages, promotion/rollback, roles/editorial flow, privacy/security, alerts, outages, restores, credential rotation, and emergency unpublish.
    files:        HusayniaSite/README.md; HusayniaSite/docs/**
    depends_on:   T13,T14,T15,T16,T17,T18,T19,T20,T21
    parallel_ok:  yes
    inputs/contracts: C1..C7; DoD Gate 8; implemented commands/configuration keys only.
    acceptance:   AC-33, AC-34
    commands/evidence: clean-agent documentation rehearsal; link checker; secret-pattern scan; role matrix diff against C3.
    exit_criteria: A new developer/operator can build, test, run, configure sandbox dependencies, diagnose named failures, and locate all constraints without secrets.
    status:       PENDING

T23  Independently validate automated and contract coverage
    owner:        test-engineer
    objective:    Map AC-01..AC-35 to executable tests/evidence, add only missing independent system-validation tests, execute all suites, and issue defects with reproducible evidence.
    files:        HusayniaSite/tests/Husaynia.SystemValidation.Tests/**; HusayniaSite/artifacts/validation/test-engineer/**
    depends_on:   T14,T15,T16,T17,T18,T19,T21
    parallel_ok:  yes
    inputs/contracts: all frozen contracts and DoD Gate 4; implementation files are read-only.
    acceptance:   AC-01..AC-35
    commands/evidence: clean restore/build/test/publish; unit/integration/contract/architecture/E2E subsets; counts/skips/failures and artifact hashes retained.
    exit_criteria: Every AC has named passing evidence or a blocking defect; unexplained skips are zero.
    status:       PENDING

T24  Perform independent security and privacy gate
    owner:        security-engineer
    objective:    Threat-model and test authorization, CSRF/CSP/headers, uploads, injection, secrets, webhook verification, dependencies, redaction, retention, and consent behavior.
    files:        HusayniaSite/artifacts/validation/security-review/**
    depends_on:   T15,T16,T18,T19,T21
    parallel_ok:  yes
    inputs/contracts: C3/C4/C6; R-45..R-47; implementation is read-only.
    acceptance:   AC-17, AC-18, AC-20, AC-26, AC-27
    commands/evidence: executed security-control tests/scans and severity-rated report.
    exit_criteria: APPROVED with zero unresolved Critical/High findings, or CHANGES_REQUIRED with exact evidence.
    status:       PENDING

T25  Perform independent principal code review
    owner:        code-reviewer
    objective:    Review the complete implementation for correctness, architecture adherence, maintainability, error handling, concurrency, performance, and public-contract compatibility.
    files:        HusayniaSite/artifacts/validation/code-review/**
    depends_on:   T15,T16,T17,T18,T19,T21
    parallel_ok:  yes
    inputs/contracts: architecture/ADRs/C1..C7; implementation is read-only.
    acceptance:   AC-04, AC-13, AC-16, AC-18..AC-22, AC-25, AC-29..AC-35
    commands/evidence: review report with exact file/line findings and APPROVED/CHANGES_REQUIRED verdict.
    exit_criteria: APPROVED with no correctness/maintainability blocker.
    status:       PENDING

T26  Execute full end-to-end, accessibility, visual, and browser QA
    owner:        qa-engineer
    objective:    Exercise all public/editor/operator journeys, fault states, WCAG manual/automated checks, pixel baselines, responsive widths, and required desktop/mobile browser matrix.
    files:        HusayniaSite/artifacts/validation/qa/**
    depends_on:   T22,T23,T24,T25
    parallel_ok:  no
    inputs/contracts: T01 approved baseline; C1/C3; DoD Gate 6; sandbox/non-public configuration only.
    acceptance:   AC-01..AC-28, AC-33, AC-34
    commands/evidence: Playwright matrix, axe reports, screenshot diffs, manual keyboard/screen-reader/device records, journey logs.
    exit_criteria: No severity-1/2 functional, visual, accessibility, security, or data-loss defect remains.
    status:       PENDING

T27  Rehearse non-production release readiness and recovery
    owner:        devops-sre-specialist
    objective:    In authorized non-production only, prove immutable artifact identity, migration preflight/failure stop, health/smoke/crawl/performance gates, backup/media restore, rollback decision, alert correlation, and release packet completeness.
    files:        HusayniaSite/artifacts/validation/release-readiness/**
    depends_on:   T22,T23,T24,T25,T26
    parallel_ok:  no
    inputs/contracts: C6; ADR-008; Development/Staging authorization and credentials supplied externally; never Production.
    acceptance:   AC-22, AC-24, AC-29..AC-35
    commands/evidence: package SHA records; 25 rps/10 minute report; induced failure transcript; timed restore integrity report; migration dry-run/reconcile report; no production command.
    exit_criteria: Development/Staging evidence passes and a production plan exists, but production remains unexecuted and separately authorized.
    status:       PENDING

T28  Independently judge the complete mission
    owner:        engineering-judge
    objective:    Verify R-01..R-54 and every applicable DoD gate from objective evidence, including residual risk if no authorized WordPress export exists.
    files:        HusayniaSite/artifacts/validation/final-judgment/**
    depends_on:   T27
    parallel_ok:  no
    inputs/contracts: all mission artifacts, C1..C7, all retained validation evidence; implementation is read-only.
    acceptance:   AC-01..AC-35
    commands/evidence: evidence-index audit and APPROVED/REJECTED judgment.
    exit_criteria: APPROVED only when every requirement/gate is proven or an exception has explicit CTO owner/expiry/risk acceptance.
    status:       PENDING

## Execution waves and validation gates

- **Wave 1 — READY NOW (smallest coherent first implementation wave):** T01 and T02 in parallel.
  Gate G1: baseline schemas validate; no form/payment was submitted; clean solution
  restore/build/test/publish succeeds; C1..C7 are checked in unchanged.
- **Wave 2:** T03, T20, T21 in parallel after T02 (T21 waits for T20).
  Gate G2: persistence fixture/concurrency tests pass; Bicep compiles/lints without provisioning;
  artifact builder emits C6 layout and checksum.
- **Wave 3:** T04, T05, T06, T07, T08, T09, T11, T12, T19 in parallel after T03; T10 starts
  after T05 and T06 complete.
  Gate G3: each module's unit/integration tests pass independently; role, idempotency, snapshot,
  upload, form, webhook, job, and redaction invariants are proven.
- **Wave 4:** T13, T16, and T17 in parallel when their dependencies are satisfied.
  Gate G4: route/SEO/migration contract suites pass and the public shell meets initial
  visual/accessibility thresholds.
- **Wave 5:** T14, T15, and T18 in parallel.
  Gate G5: the complete schema applies cleanly; all public templates/journeys pass responsive,
  axe, visual-fixture, sandbox, and dependency-failure tests; integrated build remains green.
- **Wave 6:** T22, T23, T24, and T25 in parallel.
  Gate G6: documentation rehearsal passes; independent automated, security, and code-review gates
  approve. Any failure creates a serialized remediation task owned by the implicated module, then
  reruns all invalidated gates.
- **Wave 7:** T26.
  Gate G7: full E2E/browser/WCAG/visual QA passes with no severity-1/2 defect.
- **Wave 8:** T27.
  Gate G8: authorized Development/Staging-only release/restore/rollback evidence passes; identical
  artifact checksum is proven; Production remains untouched.
- **Wave 9:** T28.
  Gate G9: independent final judgment.

Critical path: `T02 -> T03 -> T04/T05/T06/T08/T12/T19 -> T16/T17/T18 -> T15 -> T23/T24/T25
-> T26 -> T27 -> T28`. T01 gates all fidelity work and must finish before T13/T16/T17.

Collision rule: tasks in the same wave own disjoint paths. Generated EF migrations are serialized
under T18. Shared layouts are owned only by T13. Shared solution/project/composition files are
owned only by T02. Pipeline files are owned only by T21. Documentation is owned only by T22.
