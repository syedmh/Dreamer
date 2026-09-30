# Husaynia backend wave — implementation task plan

Date: 2026-08-28  
State: **IMPLEMENTATION**  
Scope: T05 Content, T06 Calendar, T07 Prayer, T08 Media, T11 Forms, T12 Donations  
Authority: `architecture.md`, `decisions.md`, and the master mission requirements/task plan  
Target: `C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`

This plan authorizes implementation and non-live validation only. It does not authorize deployment,
migration generation/application, secrets, live form submission, real payment activity, commit,
push, or history rewriting.

## 1. PX-01 external NuGet audit blocker — REQUIRED FOR PACKAGE-DEPENDENT MODULES

Current manifest evidence:

- Central package management is enabled in `Directory.Packages.props`.
- Production projects directly reference only Microsoft configuration/DI, EF Core, Identity, and
  SQL Server packages.
- `dotnet list .\HusayniaSite.sln package --include-transitive --format json --no-restore` reports:
  `HtmlSanitizer`, `Ical.Net`, `SixLabors.ImageSharp`, `Azure.Storage.Blobs`, and `Stripe.net`
  **absent**.
- `Azure.Identity` exists only as an incidental transitive dependency of
  `Microsoft.Data.SqlClient` (resolved `1.14.2` in the inspected lock state). T08 will compile
  directly against its API,
  so it must be an explicit, centrally pinned Infrastructure dependency rather than relying on an
  unrelated SQL client dependency.
- ASP.NET Core Data Protection, cryptography/HMAC, timezone handling, JSON, and HTTP primitives are
  supplied by the target framework/shared framework. T05 preview, T07, and T11 require no additional
  package solely for those capabilities.

PX-01 is currently **BLOCKED** because the external NuGet vulnerability/audit metadata required to
select and approve the package set is unavailable. NU1900 or unavailable vulnerability metadata
continues to fail closed; cached package availability is not audit completion.

Effective immediately, the current solution/project/package manifests and lock files are frozen.
No task may edit them while PX-01 is blocked. T07 Prayer and T11 Forms require no new package under
this plan, so they are authorized to proceed now in parallel against the frozen current dependency
graph. T05 Content, T06 Calendar, T08 Media, and T12 Donations remain blocked until PX-01 completes.

### PX-01 — Add and audit the backend-wave package set

Owner: **T02 foundation/package owner**  
Status: **BLOCKED — external NuGet audit metadata unavailable**. The 2026-08-28 retry resolved
`api.nuget.org` and reached TCP 443, but Schannel and .NET both received a TLS handshake failure;
the solution vulnerability command returned 11 unsuppressed `NU1900` errors.  
Parallelism: **none when resumed**; this is the only task allowed to edit manifests/locks.  
Depends on: T02 complete.  
Blocks: T05, T06, T08, T12 developer fanout and WG-01 integration.  
Does not block: T07 or T11 implementation, independent gates, or T18 handoff production.

Exact direct package placement:

| Package ID | Direct project | Purpose |
|---|---|---|
| `HtmlSanitizer` | `src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj` | Parser-based module-local T05/T06 HTML sanitizers |
| `SixLabors.ImageSharp` | `src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj` | Cross-platform full image decode/re-encode and metadata removal |
| `Azure.Storage.Blobs` | `src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj` | Official Blob client |
| `Azure.Identity` | `src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj` | Explicit managed-identity credential dependency |
| `Stripe.net` | `src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj` | Official Stripe client |
| `Ical.Net` | `tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj` | Independent parser for generated iCalendar tests only |

Do not add a direct `AngleSharp` reference unless the selected `HtmlSanitizer` public API forces
production code to compile against AngleSharp types; package types must not cross an Application
port. Do not add prayer, forms, scheduler, retry, or alternative JSON packages.

Exclusive PX-01 files:

```text
Directory.Packages.props
src\Husaynia.Infrastructure\Husaynia.Infrastructure.csproj
tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj
src\Husaynia.Infrastructure\packages.lock.json
src\Husaynia.Web\packages.lock.json
tools\Husaynia.Migration\packages.lock.json
tests\Husaynia.IntegrationTests\packages.lock.json
tests\Husaynia.E2ETests\packages.lock.json
tests\Husaynia.ArchitectureTests\packages.lock.json
tests\Husaynia.SystemValidation.Tests\packages.lock.json
```

PX-01 units:

1. **PX-01.1 Select:** select exact, stable, non-preview versions compatible with `net10.0`; record
   package ID, exact version, source, license, support status, and package hash. No range/wildcard.
   Versions are intentionally not guessed by this planning task: the package owner freezes them
   only with current NuGet metadata and vulnerability evidence.
2. **PX-01.2 Edit:** add central versions and only the two direct-project reference sets above.
3. **PX-01.3 Lock:** regenerate only the listed lock files and inspect every transitive delta.
4. **PX-01.4 Audit:** fail closed on NU1900 or unavailable vulnerability metadata; no waiver is
   implied by an offline cache.
5. **PX-01.5 Prove:** verify all six IDs have the intended direct/transitive placement and that no
   feature or composition source changed.

Validation:

```powershell
dotnet restore .\HusayniaSite.sln --force-evaluate
dotnet restore .\HusayniaSite.sln --locked-mode
dotnet list .\HusayniaSite.sln package --include-transitive
dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive
dotnet list .\HusayniaSite.sln package --deprecated --include-transitive
dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror
git diff --name-only
```

Exit: exact versions are audit-clean, locked restore/build pass, only PX-01 files changed, and
`gates\PX-01\package-evidence.md` records commands, exit codes, selected versions, hashes, lock
deltas, and zero preview packages. Only then may T05, T06, T08, and T12 start in parallel. T07 and
T11 continue independently and must not modify their implementation merely to participate in
PX-01.

## 2. Frozen implementation seams

The following are immutable for this wave:

- `src\Husaynia.Application\Contracts\**` including C2/C4 types and method signatures.
- `Program.cs`, shared DbContexts, transaction and idempotency implementations, Identity,
  Operations, Social, migrations, and the model snapshot.
- All solution/project/package manifests and lock files are frozen at their current bytes while
  PX-01 is blocked. After audit access is restored, only the PX-01 owner may change its exclusive
  file list; they freeze again immediately when PX-01 evidence is accepted.
- Provider/SDK types remain in Infrastructure. Web and downstream modules receive only Application
  DTOs/results.
- Each feature supplies a public parameterless Infrastructure `IHusayniaModule`, public
  `IHusayniaConfigurationValidator`, and a public parameterless Web admin
  `IHusayniaModule`/`IHusayniaEndpointModule`.
- EF configuration is discovered automatically; stores use `HusayniaDbContext.Set<T>()`.
- Mutable roots use `MutableEntityConfiguration<T>`; immutable records use explicit
  `IEntityTypeConfiguration<T>`.
- Admin endpoints explicitly invoke the frozen policy, derive stable denial through
  `AdministrativeCapabilityAuthorizer`, enforce MFA/antiforgery, and finalize exactly one redacted
  audit outcome. Application repeats the capability check.
- External I/O never occurs inside SQL transactions. Durable work uses T19 `IJobHandler` only.
- T07 uses target/shared-framework timezone/math and the existing T19 job contracts/infrastructure
  only. It may not add a prayer, scheduling, retry, or other package.
- T11 uses only the existing T19 `IJobHandler` path and target/shared-framework antiforgery,
  cryptography/HMAC, JSON, and HTTP primitives. It may not add a forms, messaging, retry, or other
  package.

Frozen module summary:

| Task | Existing frozen ports | Additive module ports | Policy/capability | Durable job | T18 custom objects |
|---|---|---|---|---|---|
| T05 | `IContentReader`, `IContentEditor`, `IPublisher` | `IPublishedContentCatalog`, `IContentPreviewTokenCodec` | `ContentManagement` / `PagesAnnouncementsReligiousContent` | none | `TR_ContentRevisions_Immutable` 51105; `TR_ContentNavigationRevisions_Immutable` 51106; `TR_ContentNavigationEntries_Immutable` 51107 |
| T06 | `IEventReader`, `ICalendarExporter` | `IEventEditor`, `IPublishedEventCatalog` | `EventManagement` / `EventsAndIcal` | none | `TR_CalendarEventRevisions_Immutable` 51206 |
| T07 | `IPrayerScheduleService`, `IPrayerSource` | `IPrayerAdministration` | `SiteAdministration` / `UsersRolesIntegrationsSettings` | `prayer.schedule.refresh.v1` / `prayer.schedule.refresh` | none |
| T08 | `IMediaStore`, `IUploadSafetyValidator` | `IMediaLibrary`, `IMediaEditor`, `IMediaBlobLifecycle` | `MediaManagement` / `MediaAndAliases` | `media.blob.cleanup.v1` / `media.blob.cleanup` | `TR_MediaVersions_Immutable` 51408 |
| T11 | `IFormSubmissionService`, `IOutboundMessageSender` | definition/admin, privacy-safe summary, retention, retry ports | `SiteAdministration` / `UsersRolesIntegrationsSettings` | `forms.delivery.v1` / `forms.delivery` | none |
| T12 | `IDonationService`, `IPaymentGateway` | `IDonationStatusReader`, `IDonationOperations` | `DonationOperations` / `DonationStatusAndReconciliation` | `donations.reconcile.v1` / `donations.reconcile` | `TR_DonationPaymentEvents_AppendOnly` 51612; `TR_DonationCampaignLedger_AppendOnly` 51613 |

No interface change is permitted after parallel fanout. A necessary signature change stops all
affected work and returns to architecture/technical-lead review.

## 3. Dependency graph and execution order

```text
T03 + T04 + T19
  -> T07 implementation -> T07 independent gates -> T07 T18 handoff
  -> T11 implementation -> T11 independent gates -> T11 T18 handoff
     (T07 and T11 run now in parallel on disjoint owned paths)

external NuGet audit recovery
  -> PX-01 select/edit/lock/audit/prove
     -> T05 implementation -> T05 independent gates -> T05 T18 handoff
     -> T06 implementation -> T06 independent gates -> T06 T18 handoff
     -> T08 implementation -> T08 independent gates -> T08 T18 handoff
     -> T12 implementation -> T12 independent gates -> T12 T18 handoff

PX-01 accepted package evidence + all six accepted handoffs
  -> WG-01 full backend-wave integration gate
  -> T18-ready packet

T18-ready packet + master T10 + master T17
  -> T18 schema consolidation
```

T07 and T11 do not depend on PX-01. Their dependency set is T03, T04, and T19, all already complete.
They proceed against the byte-frozen current manifests/locks. PX-01 remains ordered before every
package-dependent module and before WG-01; no package-dependent work may be speculatively started.

## 4. Exact disjoint ownership

Each owner may add/edit only the paths in its row. Existing shared files may be read but not edited.
Module-local helpers must stay in that module's roots; there is no shared backend-wave helper task.

| Task | Exclusive production roots | Exclusive test roots | Mission artifact root |
|---|---|---|---|
| T05 | `src\Husaynia.Domain\Content\**`; `src\Husaynia.Application\Content\**`; `src\Husaynia.Infrastructure\Content\**`; `src\Husaynia.Web\Areas\Admin\Content\**` | `tests\Husaynia.Domain.Tests\Content\**`; `tests\Husaynia.Application.Tests\Content\**`; `tests\Husaynia.IntegrationTests\Content\**` | `handoffs\T05\**`, `gates\T05\**` |
| T06 | `src\Husaynia.Domain\Calendar\**`; `src\Husaynia.Application\Calendar\**`; `src\Husaynia.Infrastructure\Calendar\**`; `src\Husaynia.Web\Areas\Admin\Calendar\**` | `tests\Husaynia.Domain.Tests\Calendar\**`; `tests\Husaynia.Application.Tests\Calendar\**`; `tests\Husaynia.IntegrationTests\Calendar\**` | `handoffs\T06\**`, `gates\T06\**` |
| T07 | `src\Husaynia.Domain\Prayer\**`; `src\Husaynia.Application\Prayer\**`; `src\Husaynia.Infrastructure\Prayer\**`; `src\Husaynia.Web\Areas\Admin\Prayer\**` | `tests\Husaynia.Domain.Tests\Prayer\**`; `tests\Husaynia.Application.Tests\Prayer\**`; `tests\Husaynia.IntegrationTests\Prayer\**` | `handoffs\T07\**`, `gates\T07\**` |
| T08 | `src\Husaynia.Domain\Media\**`; `src\Husaynia.Application\Media\**`; `src\Husaynia.Infrastructure\Media\**`; `src\Husaynia.Web\Areas\Admin\Media\**`; `src\Husaynia.Web\Features\MediaDelivery\**` | `tests\Husaynia.Domain.Tests\Media\**`; `tests\Husaynia.Application.Tests\Media\**`; `tests\Husaynia.IntegrationTests\Media\**` | `handoffs\T08\**`, `gates\T08\**` |
| T11 | `src\Husaynia.Domain\Forms\**`; `src\Husaynia.Application\Forms\**`; `src\Husaynia.Infrastructure\Forms\**`; `src\Husaynia.Web\Areas\Admin\Forms\**` | `tests\Husaynia.Domain.Tests\Forms\**`; `tests\Husaynia.Application.Tests\Forms\**`; `tests\Husaynia.IntegrationTests\Forms\**` | `handoffs\T11\**`, `gates\T11\**` |
| T12 | `src\Husaynia.Domain\Donations\**`; `src\Husaynia.Application\Donations\**`; `src\Husaynia.Infrastructure\Donations\**`; `src\Husaynia.Web\Areas\Admin\Donations\**`; `src\Husaynia.Web\Features\StripeWebhook\**` | `tests\Husaynia.Domain.Tests\Donations\**`; `tests\Husaynia.Application.Tests\Donations\**`; `tests\Husaynia.IntegrationTests\Donations\**` | `handoffs\T12\**`, `gates\T12\**` |

Canonical file grouping inside each root is `<Feature>Models.cs`, `<Feature>Contracts.cs`,
`<Feature>Services.cs`, `<Feature>Module.cs`, `<Feature>Configuration.cs`,
`<Feature>Persistence.cs`, `<Feature>Store.cs`, `<Feature>TransactionalAuditAppender.cs`,
`<Feature>AdminEndpointModule.cs`, and focused adapter/job/invariant files. Developers may split a
canonical file only within their assigned root and must record the final file list in the handoff.

## 5. Red-first protocol

Every unit starts by adding the named test in the task-owned test folder and executing the narrowest
filter. The first run must fail for the missing behavior, not for a broken test harness, absent
LocalDB, or live dependency. Record the command, failing test names, and expected failure in
`gates\<Task>\red-first.md`; then implement only enough to make that unit green.

Common commands, replacing `<Feature>` with `Content`, `Calendar`, `Prayer`, `Media`, `Forms`, or
`Donations`:

```powershell
dotnet test .\tests\Husaynia.Domain.Tests\Husaynia.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.<Feature>."
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.<Feature>."
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~.<Feature>."
dotnet test .\tests\Husaynia.ContractTests\Husaynia.ContractTests.csproj -c Release --no-restore
dotnet test .\tests\Husaynia.ArchitectureTests\Husaynia.ArchitectureTests.csproj -c Release --no-restore
dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror
```

All LocalDB fixtures must use guarded unique `HusayniaTxx_*` databases and delete only the exact
created database. Emulator/fixture servers bind loopback only. No test may contact a live provider.

## 6. T05 Content units

1. **T05.1 Domain red/green:** test immutable revision/navigation construction, NFC/RTL
   round-trip, pointer transitions, rollback-new-revision, and cycle/ordinal rejection; implement
   `ContentModels.cs`, `ContentNavigationModels.cs`, and value/checksum primitives.
2. **T05.2 Application red/green:** test exact C2 implementations, additive catalog/token
   interfaces, capability denial before validation/store, canonical path/SEO/checksum validation,
   draft non-leakage, and exactly-one finalization; implement module contracts/services.
3. **T05.3 Sanitizer red/green:** hostile corpus covers script/style/iframe/form, event attributes,
   active/data URLs, CSS, malformed nesting, rel hardening, Unicode, and deterministic output;
   implement a module-local `HtmlSanitizer` adapter. Persist sanitized output only.
4. **T05.4 Persistence red/green:** LocalDB tests prove unique normalized paths, immutable revision
   SQL errors 51105/51106/51107, rowversion conflicts, atomic pointer+audit, rollback on audit
   failure, published-only reads, and `Set<T>()` discovery; implement configurations, store,
   transactional audit appender, and exact invariant constants.
5. **T05.5 Web red/green:** full-host tests cover anonymous/ordinary/all-six-role/MFA matrix,
   antiforgery/body bounds, correlation, preview token tamper/expiry/future/wrong revision, and
   `noindex`; implement admin endpoint module and Data Protection codec purpose
   `Husaynia.Content.Preview.v1`.
6. **T05.6 Handoff:** run all T05/common commands and write the T18 packet.

Exit: public reads follow only the published pointer; concurrency returns 409; sanitizer package is
used without package types crossing Application; all privileged outcomes have one redacted audit.

## 7. T06 Calendar units

1. **T06.1 Domain red/green:** test immutable revisions, slug/NFC rules, local start/end, invalid
   spring times, explicit earlier/later fall ambiguity, and category identity; implement models.
2. **T06.2 Recurrence red/green:** test canonical bounded DAILY/WEEKLY/MONTHLY/YEARLY grammar,
   interval/BYDAY/BYMONTHDAY/COUNT/UNTIL bounds, leap dates, unsupported rules, horizon, maximum
   occurrence count, and deterministic occurrence identity; implement recurrence parser/materializer.
3. **T06.3 Application red/green:** test C2 reader/exporter, additive editor/catalog, capability
   check, published/past/future visibility, exact slug selection, and private/unknown
   non-disclosure; implement services/contracts.
4. **T06.4 iCal red/green:** test CRLF, 75-octet folding, escaping, stable UID, LA timezone,
   ordering, canonical URL, single/full selection, and parse the result with `Ical.Net`; implement
   the production serializer without using `Ical.Net`.
5. **T06.5 Persistence red/green:** prove pointer concurrency, occurrence uniqueness, synchronous
   publish visibility, atomic revision/occurrences/audit, trigger error 51206, and discovered
   configuration; implement module/configuration/store/invariant and module-local sanitizer.
6. **T06.6 Web red/green:** full-host role/MFA/antiforgery/audit/correlation tests; implement admin
   endpoints only. Public HTML remains T14-owned.
7. **T06.7 Handoff:** run all T06/common commands and write the T18 packet.

Exit: publish atomically materializes occurrences; generated iCal passes the independent parser;
unsupported recurrence fails explicitly.

## 8. T07 Prayer units

Authorization condition: proceed now in parallel with T11, only in the exact T07-owned paths from
section 4. No manifest, lock, solution, project, `Program.cs`, shared DbContext, migration, model
snapshot, or other task-owned edit is permitted. Use framework timezone/math and existing T19 job
infrastructure only.

1. **T07.1 Domain red/green:** test immutable profile canonical JSON/hash, activation pointer,
   snapshot batch completeness, override precedence/deactivation, and profile isolation; implement
   profiles, snapshots, overrides, and integration state.
2. **T07.2 Calculator red/green:** fixed-clock synthetic LA fixtures prove byte-identical
   profile/date output and DST handling; implement deterministic in-process `IPrayerSource`.
   Do not add Snohomish production defaults.
3. **T07.3 Application red/green:** test frozen month read, stale/complete/incomplete/missing
   behavior, latest active override, zero provider resolution/calls on reads, and
   SiteAdministrator-only administration; implement services/admin port.
4. **T07.4 Refresh persistence red/green:** prove provider call occurs before SQL, one serializable
   complete month upsert, timeout/malformed mapping, rowversion activation conflict, atomic audit,
   and discovered constraints; implement module/configuration/store/provider selection.
5. **T07.5 Job red/green:** strict payload rejects unknown/duplicate/case-wrong fields; prove
   canonical `prayer-refresh:{profileHash}:{yyyy-MM}`, duplicate/restart/retry/dead-letter behavior,
   activation enqueue, and current/next-month catch-up; implement T19 handler/coordinator and
   module-owned hosted startup registration.
6. **T07.6 Web red/green:** role/MFA/antiforgery/audit/correlation and sanitized diagnostics tests;
   implement admin endpoints.
7. **T07.7 Handoff:** run all T07/common commands and write the T18 packet.

Exit: public reads are snapshot-only, outages cannot escape the prayer boundary, and T18 receives
EF-only constraints with no custom SQL object.

## 9. T08 Media units

1. **T08.1 Domain red/green:** test immutable versions, stable alias preservation, archive state,
   randomized/content-bound storage keys, NFC path normalization, and rejected traversal/encoding/
   device/control cases; implement media models/value objects.
2. **T08.2 Safety red/green:** generated synthetic files cover MIME/extension/magic agreement,
   corrupt/truncated/polyglot/executable input, decoded pixel/dimension limits, metadata removal,
   JPEG/PNG re-encode, and bounded MP3 frame/ID3 validation; implement `IUploadSafetyValidator`
   with ImageSharp and module-local audio validation.
3. **T08.3 Blob red/green:** fake then Azurite tests prove immutable put/open/list/delete,
   key+ETag compensation, timeout/redaction, explicit emulator mode, managed identity production
   mode, and no ambient credential fallback; implement Azure Blob/Identity adapters.
4. **T08.4 Workflow persistence red/green:** fault matrix proves validate/transform/hash before Blob,
   Blob before SQL, atomic metadata/pointer/audit, SQL-failure compensation, referenced-pending
   preservation, alias-stable replacement, archive, trigger error 51408, and rowversion 409;
   implement services/configurations/store/audit/invariant.
5. **T08.5 Cleanup job red/green:** strict identifier-only payload, bounded page, DB-reference check,
   hourly key `media-cleanup:{utcHourBucket}`, duplicate/restart/retry behavior; implement T19
   handler/coordinator and startup registration.
6. **T08.6 Delivery red/green:** GET/HEAD, one range, 200/206/304/416, strong ETag, length/type,
   nosniff, disposition, cache, multi-range rejection, and provider-error redaction; implement only
   `Features\MediaDelivery`.
7. **T08.7 Admin red/green:** full-host role/MFA/antiforgery/body/audit/correlation tests; implement
   admin endpoints.
8. **T08.8 Handoff:** run all T08/common commands, Azurite suite, and write the T18 packet.

Exit: no unsafe asset is stored/served, no Blob I/O is enclosed by SQL, and stable aliases survive
replacement.

## 10. T11 Forms units

Authorization condition: proceed now in parallel with T07, only in the exact T11-owned paths from
section 4. No manifest, lock, solution, project, `Program.cs`, shared DbContext, migration, model
snapshot, or other task-owned edit is permitted. Use existing T19 `IJobHandler` and framework
antiforgery/crypto/JSON/HTTP only.

1. **T11.1 Domain red/green:** test immutable definition versions/fields, bounded field kinds,
   published pointer, retention/legal hold state, append-only attempts, and no raw admin regex;
   implement models.
2. **T11.2 Validation red/green:** synthetic `test-contact-v1`/`test-pledge-v1` cover every field
   kind, required/bounds/choices/pattern kind, unknown/oversized/malformed fields, schema
   non-disclosure, canonical payload hash, and value minimization; implement contracts/services.
3. **T11.3 Admission red/green:** full-host tests prove antiforgery and honeypot precede Application,
   keyed HMAC fingerprint only, generic bot acknowledgement, fail-closed SQL fixed-window limit,
   and no partial state; implement module-local Web admission/fingerprint code and Infrastructure
   limiter.
4. **T11.4 Submission persistence red/green:** concurrent duplicate races prove unique
   `(DefinitionVersionId, DuplicateFingerprint, DuplicateWindowStartUtc)`, same hash prior receipt,
   different hash conflict, and one transaction for submission/values/job/audit; implement
   configurations/store/audit.
5. **T11.5 Delivery job red/green:** strict IDs-only payload, persisted reload, sender outside SQL,
   append-only sanitized attempt, pickup/failure/retry/dead-letter/cancellation, and
   `forms-delivery:{submissionId:N}:{definitionVersionId:N}`; implement handler/coordinator,
   disabled and test pickup senders, and startup registration.
6. **T11.6 Admin/retention red/green:** SiteAdministrator-only definition/read/retry/retention,
   legal-hold fencing, privacy-safe summaries, role/MFA/audit matrix, and body/log/audit leak scan;
   implement admin endpoints/configuration.
7. **T11.7 Handoff:** run all T11/common commands and write the T18 packet.

Exit: invalid/bot/rate-limited input creates no submission/value/job; valid duplicate input creates
exactly one durable workflow; no form values enter jobs, logs, telemetry, or audit.

Status: **DONE — 2026-08-28.** Independent test gate passed 468/468 with zero failures/skips,
including real SQL lock-contention and post-lock authoritative-time regressions. Independent
security passed with zero Critical/High findings, correctness review approved, and independent
engineering judgment approved. The official strict build remains externally blocked before
compilation by unsuppressed NU1900 vulnerability-metadata unavailability; current-source
warning-as-error compilation passed. T01 live definitions/destinations and T18 migration remain
required before release.

## 11. T12 Donations units

1. **T12.1 Domain red/green:** test category rules, integer minor units, exact conversion,
   currency/mode/custom/preset boundaries, closed donation state machine, and immutable payment/
   ledger rows; implement models.
2. **T12.2 Checkout phase 1 red/green:** test normalized application key and canonical request hash,
   serializable T03 `donations.checkout.v1` reservation, duplicate in-progress/completed/conflict,
   local `CheckoutCreating` commit, and zero provider calls for invalid input; implement service/
   store reservation.
3. **T12.3 Stripe boundary red/green:** loopback contract fixture proves same-key Checkout replay,
   success/cancel same-origin allowlist, timeout/crash mapping, no generic POST retry, test-key
   enforcement, and redacted diagnostics; implement official Stripe adapter/configuration.
4. **T12.4 Checkout phase 2 red/green:** prove Stripe occurs outside SQL and phase 2 atomically
   attaches filtered-unique provider IDs, stores approved redirect/reference only, changes Pending,
   completes idempotency receipt, and audits; test crash between phases and resume.
5. **T12.5 Webhook red/green:** raw bounded body read once, missing/malformed/tampered/stale signature
   rejection, verified typed event only, duplicate same hash prior receipt, hash/type conflict,
   concurrent/out-of-order transitions, exactly one completion and ledger delta, unmatched event,
   and trigger errors 51612/51613; implement webhook endpoint/store/invariants.
6. **T12.6 Reconciliation/admin red/green:** strict `donation-reconcile:{providerEventId}` job,
   rowversion reconciliation conflict, privacy-safe operator view, all role/MFA/audit cells, and
   DB/log/telemetry/browser/admin leak scan; implement operations port, handler/startup, and admin
   endpoints.
7. **T12.7 Handoff:** run all T12/common commands and local Stripe fixture suite; write T18 packet.

Exit: browser callbacks never mutate payment completion; only a verified webhook can complete a
donation; replay cannot duplicate completion or campaign ledger.

## 12. Independent module gate fanout

After a developer reports green, launch independent gates in parallel. The developer may remediate
findings but may not approve a gate.

T07 and T11 gates run as soon as each module reports green; they do not wait for PX-01. There is no
gate relaxation for the early lanes. Their test and review evidence must include `git diff
--name-only` plus manifest/lock hashes or an equivalent byte comparison proving that no frozen or
forbidden file changed.

| Module | Independent test | Independent code review | Independent security | Handoff release dependency |
|---|---|---|---|---|
| T05 | required | required | required: sanitizer/preview/audit | all three pass |
| T06 | required | required | wave security coverage | test + review pass |
| T07 | required | required | wave security coverage | test + review pass |
| T08 | required | required | required: upload/Blob/delivery | all three pass |
| T11 | required | required | required: PII/fingerprint/retention | all three pass |
| T12 | required | required | required: Stripe/webhook/privacy | all three pass |

Test gate:

- rerun the module commands from a clean process;
- report exact passed/failed/skipped counts and environment;
- exercise LocalDB/emulator/loopback fixtures; unexplained skips are failure;
- prove no live endpoint, credential, payment, form destination, or production mutation.

Code-review gate:

- verify correctness, failure paths, concurrency, cancellation, idempotency, and public-port
  compatibility;
- run `git diff --name-only` and reject any ownership/forbidden-file breach;
- reject runtime DDL, SDK types outside Infrastructure, cross-module table access, or duplicated
  worker/scheduler.

Security gate where required:

- verify authorization/MFA/CSRF, injection/path/body bounds, secret/PII/payment redaction, dependency
  audit, provider allowlists, and hostile-input suites;
- Critical/High findings block handoff.

Artifacts:

```text
gates\<Task>\red-first.md
gates\<Task>\test-results.md
gates\<Task>\code-review.md
gates\<Task>\security-review.md        # T05/T08/T11/T12
handoffs\<Task>\implementation-evidence.md
handoffs\<Task>\t18-schema-handoff.md
```

## 13. T18 handoff contract

T07 and T11 may produce and independently release their T18 handoffs while PX-01 remains blocked.
Release freezes their accepted source list and handoff contents/hash. This authorizes schema
documentation only: T18 still may not generate or apply migrations, and no other owner may edit a
shared DbContext or model snapshot. The early handoffs are staged inputs, not permission to start
T18.

Each `t18-schema-handoff.md` must be exact and mechanically checkable:

1. final source file list and configuration class names;
2. every table/column with SQL type, length/precision, Unicode, nullability, default, generated
   behavior, and mutable/immutable classification;
3. PK/FK/AK/index/filtered-index/check names, columns/order/filter, and delete behavior;
4. shadow timestamps and rowversion placement;
5. each custom trigger's exact object name, exact `InstallSql`, exact `DownSql`, reserved error
   number/text, and integration test proving UPDATE/DELETE rejection;
6. additive `Up` order and exact reverse-safe `Down` order;
7. SQL 2601/2627 and concurrency mappings used by the store;
8. fixture/setup commands and real test results;
9. statement that runtime performs no `EnsureCreated`, `Migrate`, or DDL;
10. independent gate verdicts and unresolved risks.

T18 must copy the feature-owned invariant constants; it may not rename or independently recreate
them. A handoff is rejected if it says “per configuration” without enumerating the schema.

## 14. WG-01 final backend-wave integration gate

Depends on accepted PX-01 package evidence and all six accepted handoffs. Owners: independent test
engineer, code reviewer, and security engineer in parallel; engineering judge evaluates their
evidence. No feature developer self-certifies.

WG-01 may start only after PX-01 has frozen the final audited manifests/locks and T05/T06/T08/T12
have completed their gates and handoffs. It must then rebuild and retest T07 and T11 against that
final locked graph. Any resulting T07/T11 failure reopens the affected independent gates; it does
not authorize unreviewed manifest, shared-file, DbContext, or migration edits.

Commands:

```powershell
dotnet restore .\HusayniaSite.sln --locked-mode
dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror
dotnet test .\HusayniaSite.sln -c Release --no-restore --logger "trx" --results-directory .\TestResults\backend-wave
dotnet publish .\src\Husaynia.Web\Husaynia.Web.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive
git diff --name-only
rg -n "EnsureCreated|EnsureDeleted|MigrateAsync|Database\.Migrate|CREATE\s+(TABLE|TRIGGER)" .\src\Husaynia.Domain\Content .\src\Husaynia.Domain\Calendar .\src\Husaynia.Domain\Prayer .\src\Husaynia.Domain\Media .\src\Husaynia.Domain\Forms .\src\Husaynia.Domain\Donations .\src\Husaynia.Application\Content .\src\Husaynia.Application\Calendar .\src\Husaynia.Application\Prayer .\src\Husaynia.Application\Media .\src\Husaynia.Application\Forms .\src\Husaynia.Application\Donations .\src\Husaynia.Infrastructure\Content .\src\Husaynia.Infrastructure\Calendar .\src\Husaynia.Infrastructure\Prayer .\src\Husaynia.Infrastructure\Media .\src\Husaynia.Infrastructure\Forms .\src\Husaynia.Infrastructure\Donations
```

The DDL grep may match only feature-owned `*DatabaseInvariant.InstallSql/DownSql` constants; any
runtime invocation is failure. The integrated gate additionally verifies:

- C2/C4 compile-time tests remain byte-compatible;
- deterministic discovery resolves all modules, validators, handlers, and endpoint modules once;
- combined EF model builds without duplicate tables/indexes/object names;
- every module remains disabled-by-default for external integrations;
- public prayer/content/calendar/media/status reads make no provider call;
- no live form/payment/provider network activity or credentials occurred;
- no shared-file or cross-owner diff exists except the already accepted PX-01 manifest/lock delta;
- zero failed tests and zero unexplained skips.

Outputs:

```text
gates\WG-01\test-results.md
gates\WG-01\code-review.md
gates\WG-01\security-review.md
gates\WG-01\final-verdict.md
handoffs\T18\t18-wave-readiness.md
```

`t18-wave-readiness.md` lists the six exact schema packets and their hashes, gate verdicts, custom
object/error-number registry, combined model evidence, and confirms T18 is still blocked until
master tasks T10 and T17 are complete. T18 alone then owns migrations and the model snapshot.
