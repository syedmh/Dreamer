# Husaynia backend feature wave architecture

Date: 2026-08-28  
Status: **IMPLEMENTATION-READY WITH NAMED PREREQUISITES**  
Scope: T05 Content, T06 Calendar, T07 Prayer, T08 Media, T11 Forms, T12 Donations  
Authority: the master mission at `../2026-08-14-husaynia-site-modernization/`

This artifact freezes the seams for six disjoint feature implementations. It does not authorize
production deployment, migration generation/application, package/project edits by feature owners,
secrets, live form submissions, real payments, git commit/push, or edits to shared composition or
persistence roots.

## 1. Binding source facts

The design is grounded in the current source, not the pre-implementation architecture snapshot:

- `src/Husaynia.Web/Program.cs` loads the Application, Infrastructure, and Web assemblies, validates
  every discovered `IHusayniaConfigurationValidator`, registers every `IHusayniaModule`, and maps
  `IHusayniaEndpointModule` instances. Feature work therefore requires no `Program.cs` edit.
- `HusayniaCompositionExtensions` discovers public concrete parameterless modules and validators in
  deterministic assembly/type order. Web endpoint modules are mapped in deterministic type order.
- `HusayniaDbContext` discovers every Infrastructure `IEntityTypeConfiguration<T>` with
  `ApplyConfigurationsFromAssembly`. Stores use `Set<T>()`; no feature adds `DbSet` properties or
  edits a shared context.
- `MutableEntityConfiguration<T>` supplies shadow `CreatedAtUtc`, `UpdatedAtUtc`, and SQL rowversion.
  Mutable roots use it; immutable records configure their own UTC creation fields and never receive
  an update method.
- T03 supplies `IHusayniaUnitOfWork`, serializable isolation, bounded retry only for explicitly
  idempotent database work, and `IIdempotencyStore`. Application-layer feature stores hide those
  Infrastructure types behind module-owned ports.
- T04 supplies the exact six-role/capability matrix, `AdministrativeCapabilityAuthorizer`,
  `IIdentityAuditFinalizer`, append-only `AuditEvent`, antiforgery, MFA-enforced policies, and
  fail-closed auditing. The existing authorization middleware metadata is Identity-internal, so
  new modules must not depend on or edit it.
- T19 supplies `IJobHandler`, scoped handler resolution, leased durable jobs, retries/dead letters,
  correlation, redaction, health probes, and a worker. Feature modules register handlers and job
  definitions; they do not create a second scheduler or worker.
- T09 proves the registration pattern: Infrastructure module, discovered EF configurations,
  `TryAddEnumerable` for `IJobHandler`/health probes, strict bounded payloads, persisted-state-derived
  idempotency, and public reads that never resolve or call live providers.
- Current central/project files contain EF Core, Identity, MVC testing, and xUnit only. They do not
  currently reference a production HTML sanitizer, image decoder/transcoder, Azure Blob SDK,
  Stripe SDK, or independent iCalendar parser.

## 2. Frozen cross-wave rules

### 2.1 No shared-file edits

Feature owners may add files only below their master task paths. They must not edit:

- `Program.cs`, any `*.csproj`, `Directory.Packages.props`, package lock files, solution/build files;
- `HusayniaDbContext`, `HusayniaIdentityDbContext`, shared persistence/transaction classes;
- `src/Husaynia.Application/Contracts/**` (C2/C4 signatures stay byte-for-byte compatible);
- T04 Identity files, T19 Operations files, T09 Social files;
- `src/Husaynia.Infrastructure/Persistence/Migrations/**` or any EF model snapshot;
- another feature's source/test folder or shared public layouts.

Additive interfaces needed by downstream tasks live under the owning module namespace, for example
`Husaynia.Application.Content`, never in the frozen shared contract files.

### 2.2 Module registration

Each feature uses exactly these discovered composition seams:

1. A public parameterless Infrastructure `<Feature>Module : IHusayniaModule` registers Application
   services, module stores, adapters, handlers, health probes, `TimeProvider` with `TryAdd`, and
   options read from `IConfiguration`.
2. A public parameterless Infrastructure `<Feature>ConfigurationValidator :
   IHusayniaConfigurationValidator` returns sanitized failures and never throws for malformed input.
3. A public parameterless Web `<Feature>AdminEndpointModule : IHusayniaModule,
   IHusayniaEndpointModule` registers only Web concerns and maps explicit admin endpoints.
4. T08 also owns a Web `MediaDeliveryEndpointModule`; T12 also owns a Web
   `StripeWebhookEndpointModule` (these may be combined with their admin module if kept in the same
   owned directory).
5. Feature Web modules rely on T04's existing authentication, policy, antiforgery, and cookies.
   They must not reconfigure those services.

Configuration is disabled-by-default when it would require an external dependency or a secret.
An absent disabled section is valid so existing hosts/tests do not break. If `Enabled=true`, every
required value is validated at startup and a missing adapter fails closed.

### 2.3 HTTP ownership

This wave maps JSON/admin/technical endpoints only:

- `/admin/content/**` — T05;
- `/admin/calendar/**` — T06;
- `/admin/prayer/**` — T07;
- `/admin/media/**` and `/wp-content/uploads/{**path}` — T08;
- `/admin/forms/**` — T11;
- `/admin/donations/**` and `/webhooks/stripe` — T12.

T13/T14/T15 own public HTML journeys. T16 owns route-manifest/SEO/redirect/security middleware.
Feature services expose stable read ports to them; this wave does not add public content, calendar,
prayer, form, donation, success, or cancel pages.

## 3. Authorization and audit contract

### 3.1 Capability mapping

| Module | Administrative capability | Policy | Full-write roles |
|---|---|---|---|
| T05 Content | `PagesAnnouncementsReligiousContent` | `ContentManagement` | SiteAdministrator, ContentEditor |
| T06 Calendar | `EventsAndIcal` | `EventManagement` | SiteAdministrator, EventEditor |
| T07 Prayer | `UsersRolesIntegrationsSettings` | `SiteAdministration` | SiteAdministrator |
| T08 Media | `MediaAndAliases` | `MediaManagement` | SiteAdministrator, MediaEditor |
| T11 Forms | `UsersRolesIntegrationsSettings` | `SiteAdministration` | SiteAdministrator |
| T12 Donations | `DonationStatusAndReconciliation` | `DonationOperations` | SiteAdministrator, DonationOperator |

ReadOnlyAuditor receives only the read/audit/report access already present in C3. Forms bodies are
not an audit report and are not exposed to ReadOnlyAuditor. No module adds a role, policy, capability,
or implicit authenticated-user privilege.

### 3.2 Endpoint plus Application checks

Every admin endpoint explicitly calls `IAuthorizationService.AuthorizeAsync(User, policyName)` and
also evaluates `AdministrativeCapabilityAuthorizer` to derive the stable denial code. This is the
endpoint-layer policy check. The Application use case independently checks the required capability
from the actor/UserContext before validation or store access. Do not attach or duplicate T04's
internal endpoint metadata and do not edit its middleware.

The Web module constructs the actor from the authenticated principal, frozen roles, MFA claims
(`amr=mfa` or `husaynia.mfa=true`), and `ICorrelationContext`. It begins correlation from
`X-Correlation-ID`/`TraceIdentifier`, emits the response correlation ID, validates antiforgery on
all browser mutations, bounds JSON before use, and never trusts role/MFA/body values supplied by the
client.

### 3.3 Exactly-one audit owner

Action names are lowercase dotted constants and target IDs are bounded or SHA-256 represented.
`PrivilegedAttemptOutcome.Denied` means policy/authentication/MFA denial. An authorized validation,
conflict, dependency failure, or success uses `Allowed` plus `details.result` and optional sanitized
`details.errorCode`.

| Outcome boundary | Sole owner |
|---|---|
| Endpoint authentication/policy/MFA denial | module Web endpoint admission using `IIdentityAuditFinalizer` |
| Antiforgery/malformed/oversized transport input | module Web endpoint handler using finalizer |
| Application role/capability denial or command validation | module Application service using finalizer |
| Authorized store not-found/conflict/failure/read/success | module Infrastructure store |
| Exception before another owner finalizes | module Web exception wrapper using finalizer |

Authorized store outcomes stage an `AuditEvent` in the same `HusayniaDbContext` transaction as the
feature mutation. A module-local Infrastructure audit appender must reproduce `EfAuditWriter`'s
sorted role/detail JSON and `ISensitiveDataRedactor` behavior but must not call `SaveChanges`; the
store/UoW commits feature state and audit together. If the audit insert fails, the mutation rolls
back. After rollback of a persistence exception, the module attempts one sanitized failure audit
through `IAuditWriter`; if that cannot persist, return `*_audit_unavailable`/503 and never claim
success. No layer retries an audit or catches finalization failure to create a second audit.

Once a privileged outcome is selected, finalization uses a fresh bounded token independent of
`RequestAborted` (five seconds maximum). Logs/audit details never contain bodies, HTML, form values,
raw email/phone/IP, fingerprints, payment payloads, filenames supplied by users, tokens, secrets,
provider diagnostics, or blob identifiers containing credentials.

Frozen action prefixes are `content.*`, `calendar.*`, `prayer.*`, `media.*`, `forms.*`, and
`donations.*`. Representative required actions are listed in each module section.

## 4. Persistence, transaction, and T18 handoff

### 4.1 Entity configuration discovery

- Every mutable aggregate root derives its configuration from `MutableEntityConfiguration<T>`.
- Immutable revisions/events/ledger rows use ordinary `IEntityTypeConfiguration<T>` and explicit
  `CreatedAtUtc`/`OccurredAtUtc` columns.
- Every key, alternate key, index, filtered unique index, check constraint, FK delete behavior,
  max length, Unicode choice, precision, and table/object name is explicit and deterministic.
- All externally supplied text is NFC-normalized before hashing/persistence where Unicode is
  allowed. Machine keys/slugs/codes use ordinal ASCII rules.
- Stores use `AsNoTracking` for reads and never expose EF entities outside Infrastructure.

### 4.2 Transaction rules

- No prayer provider, outbound sender, Blob, or Stripe call runs inside a SQL transaction.
- Ordinary atomic feature writes use the T03 unit of work at `ReadCommitted`.
- Publish pointer changes, duplicate-fingerprint reservation, media current-version swap, and Stripe
  webhook/ledger processing use `Serializable` or an equivalent SQL lock plus unique constraints.
- T03 retry mode is used only when every external effect is absent and unique/idempotency fences make
  replay safe. A provider write is never automatically retried by the unit of work.
- Expected concurrency maps to the frozen 409 behavior. The submitted rowversion/draft revision is
  mandatory; last-write-wins is prohibited.
- Database uniqueness is authoritative. Catch SQL 2601/2627, clear/detach the losing tracked entity,
  reload the winner, compare the canonical request/payload hash, and return prior receipt or
  payload-conflict.

### 4.3 T18-only schema delivery

Feature tasks add entity/configuration classes and executable fixture coverage only. **T18 alone**:

- creates/edits `Persistence/Migrations/**` and the model snapshot;
- generates idempotent SQL/bundle artifacts;
- applies module-owned custom invariant SQL in migration `Up/Down`;
- validates the combined model.

Each feature completion packet must include a `t18-schema-handoff.md` in that feature mission
artifact containing exact tables, columns, PK/FK/AK/index/check/filtered-index names, custom SQL,
error numbers, additive/rollback sequence, and the source configuration/constants that T18 must use.
Where this architecture requires an immutable-table trigger, the feature owns an
`<Feature>DatabaseInvariant.InstallSql/DownSql` constant and integration tests; runtime startup must
never execute it.

Reserved custom objects:

| Task | Exact custom database objects reserved for T18 |
|---|---|
| T05 | `TR_ContentRevisions_Immutable`, `TR_ContentNavigationRevisions_Immutable`, `TR_ContentNavigationEntries_Immutable` |
| T06 | `TR_CalendarEventRevisions_Immutable` |
| T07 | none; EF constraints only |
| T08 | `TR_MediaVersions_Immutable` |
| T11 | none; EF constraints only |
| T12 | `TR_DonationPaymentEvents_AppendOnly`, `TR_DonationCampaignLedger_AppendOnly` |

Triggers reject UPDATE/DELETE with module-reserved SQL errors 51105, 51106, 51107, 51206, 51408,
51612, and 51613 respectively. Feature handoffs must freeze exact text before T18 starts; T18 must
not rename or recreate them independently.

## 5. Durable-job contract

Feature jobs use T19 unchanged:

- strict bounded JSON; unknown/duplicate/case-wrong fields fail;
- payload contains identifiers/generation only, never content, PII, payment data, credentials, or
  HTML;
- public reads never enqueue or invoke a provider;
- handlers are scoped and registered with
  `TryAddEnumerable(ServiceDescriptor.Scoped<IJobHandler, THandler>())`;
- a module-owned Web hosted startup service registers definitions before requests are served, using
  one fresh scope and stable startup correlation; it does not add a second worker;
- recurring keys derive from persisted state or a stable due generation, not process/job-instance
  GUIDs; enqueue conflicts compare exact payloads;
- handlers let T19 own timeout, cancellation, lease renewal, retry, backoff, dead-letter, and metrics.

Frozen definitions:

| Module | Definition key | Handler name | Idempotency key |
|---|---|---|---|
| Prayer | `prayer.schedule.refresh.v1` | `prayer.schedule.refresh` | `prayer-refresh:{profileHash}:{yyyy-MM}` |
| Media | `media.blob.cleanup.v1` | `media.blob.cleanup` | `media-cleanup:{utcHourBucket}` |
| Forms | `forms.delivery.v1` | `forms.delivery` | `forms-delivery:{submissionId:N}:{definitionVersionId:N}` |
| Donations | `donations.reconcile.v1` | `donations.reconcile` | `donation-reconcile:{providerEventId}` |

T05/T06 publish synchronously so a successful response makes the new public projection and calendar
occurrences immediately queryable; they do not introduce jobs.

## 6. External provider/blob/Stripe ports

Frozen C4 interfaces and DTOs in `Husaynia.Application.Contracts` are not edited:
`IPrayerSource`, `IMediaStore`, `IUploadSafetyValidator`, `IOutboundMessageSender`, and
`IPaymentGateway`. All provider SDK/client types remain in Infrastructure and never cross these
ports. Additive lifecycle/admin ports live in the owning module directory.

- Connect and overall timeouts are explicit and startup-validated. Automatic retries are limited to
  safe idempotent reads; Blob upload, outbound delivery, Checkout creation, and webhook handling are
  not hidden behind generic retries.
- Provider errors map to bounded machine codes. Public responses never include response bodies,
  stack traces, endpoints, account/container names, request IDs, signatures, keys, or SDK messages.
- Production endpoints are fixed/allowlisted HTTPS and credential-free in configuration values.
  Emulator/loopback endpoints are accepted only under an explicit test environment/mode.
- Disabled adapters return `IntegrationError("not_configured", ...)` and cause only that operation
  to be unavailable.

### Shared dependency prerequisite PX-01

Because feature owners cannot edit project files and the current projects lack required libraries,
a serialized **T02 foundation/package-owner** prerequisite must land before affected modules can pass
their final production-adapter gates. PX-01 alone may update central/project/package-lock files to
supply audited, exact, non-preview dependencies for:

1. deterministic HTML parsing/sanitization (T05);
2. an independent RFC 5545 parser in tests (T06);
3. cross-platform image decode/re-encode/metadata stripping plus official Azure Blob/identity clients
   (T08);
4. the official Stripe client (T12).

PX-01 adds references only, no feature code or composition edits. Feature branches compile against
module ports/fakes until the prerequisite is available and must not substitute regex HTML cleaning,
hand-written image decoding, ambient credentials, shell tools, browser automation, or unreviewed raw
provider clients. NU1900/audit connectivity is an external gate, not permission to skip auditing.

## 7. T01 live-baseline block and safe fixtures

T01 remains blocked: existing `evidence/baseline` is not an approved promotable live baseline. It may
be read as unapproved observation, not treated as runtime defaults or fidelity proof. Master facts
still prove route/content categories, contact markup observation, uploaded image/audio existence,
and dynamic/widget uncertainty, but not hidden behavior.

Implementation may proceed with fixtures only when they are:

- synthetic, deterministic, clearly named `test-*`, and created in test-owned LocalDB/emulator/fake
  provider state;
- free of live credentials, real destinations, real donations, real submissions, and production
  provider calls;
- testing architectural invariants rather than asserting unapproved public values.

Do **not** seed or infer live form destinations/success text, pledge fields, donation amounts/modes,
recurrence rules, prayer coordinates/method/tolerance/times, retention periods, or enabled media
formats from the blocked capture. The observed contact field list may be retained only as an
observation/parser fixture; it cannot become a production `FormDefinition`. Production form
versions, donation categories/modes, prayer profile, and provider enablement remain empty/disabled
until approved evidence or authorized export reconciliation arrives.

Safe fixture set:

- Content: synthetic Arabic/NFC/RTL, transliteration, English, unsafe HTML, checksum and concurrency.
- Calendar: invented `.test` events around LA spring/fall DST, explicit ambiguous-time resolution,
  finite daily/weekly/monthly recurrence, and RFC escaping/folding.
- Prayer: synthetic calculation profile and fixed clock; no assertion against Husaynia live values.
- Media: generated tiny safe image/audio bytes, corrupt/truncated/polyglot/executable/path traversal,
  fake Blob/Azurite container, and no imported live asset.
- Forms: generic `test-contact-v1`/`test-pledge-v1` schemas with non-real destinations and pickup sink.
- Donations: `test-general` sandbox category, fake provider IDs/signatures, declined/cancelled/3DS
  contract responses, and zero live network/payment activity.

T01 later supplies data/config/seed inputs; it must not require a domain/schema redesign.

## 8. T05 Content module

### 8.1 Frozen contracts

Implement C2 unchanged: `IContentReader`, `IContentEditor`, `IPublisher`. Add under
`Husaynia.Application.Content`:

```csharp
public interface IPublishedContentCatalog
{
    Task<Result<PublishedContentDocument, ContentError>> GetByIdAsync(ContentId id, CancellationToken ct);
    Task<Result<IReadOnlyList<PublishedContentIndexEntry>, ContentError>> ListAsync(
        string? kind, CancellationToken ct);
}

public interface IContentPreviewTokenCodec
{
    PreviewToken Issue(ContentId contentId, RevisionId revisionId, DateTimeOffset expiresAtUtc);
    Result<ContentPreviewGrant, ContentError> Validate(PreviewToken token);
}
```

`PublishedContentDocument` freezes kind, canonical path, title, summary, sanitized body HTML,
structured JSON, SEO title/description/canonical/robots/OG fields, checksum, published timestamp,
and media keys. `PublishedContentIndexEntry` freezes content ID, kind, path, title, summary,
searchable plain text, checksum, and published timestamp for T10/T16/T17. No downstream module reads
Content tables directly.

The production codec is Web-owned and uses ASP.NET Core Data Protection with purpose
`Husaynia.Content.Preview.v1`; the payload binds content ID, revision ID, issued/expiry UTC, and
purpose. It rejects expiry, tampering, ID mismatch, future issuance, and unknown version. Preview
responses are non-indexable and never change publication state.

### 8.2 Model and lifecycle

Tables: `ContentItems`, `ContentRevisions`, `ContentNavigationMenus`,
`ContentNavigationRevisions`, `ContentNavigationEntries`.

- `ContentItem` is mutable and rowversioned. Kind is exact enum
  `Page|Announcement|Religious|Legal`. `CanonicalPathNormalized` is unique.
- `ContentRevision` is immutable; `(ContentItemId, Version)` is unique. It contains sanitized output,
  canonical structured/SEO JSON, searchable plain text, SHA-256 checksum, creator, and UTC creation.
- save-draft always creates a new immutable revision and atomically moves `DraftRevisionId` using
  `expectedDraft`; it never edits a revision.
- publish requires the exact draft pointer, moves `PublishedRevisionId`, retains the draft pointer,
  and returns rowversion. Unpublish clears only the published pointer. Rollback copies a selected
  prior revision into a new revision and publishes that new revision.
- public reads join only the published pointer. Missing, draft-only, and archived content all return
  the same not-found error.
- sanitizer output, not raw input, is persisted. The allowlist excludes script/style/iframe/form,
  event attributes, active URLs, `javascript:`/`data:` except approved media references, CSS, and
  unknown elements. Links are normalized; `rel` is hardened. Regex-only sanitation is forbidden.
- checksum input is versioned canonical UTF-8 over NFC title/summary/sanitized HTML/structured JSON/
  SEO JSON/media keys. The checksum version is stored.
- navigation has the same draft/publish/rollback pointer model. Entries are immutable per navigation
  revision; parent cycles, duplicate ordinals, and non-canonical internal targets are rejected.

Required actions: `content.draft.save`, `content.publish`, `content.unpublish`, `content.rollback`,
`content.preview.issue`, `content.navigation.publish`.

Config: `Content:Preview:Lifetime` defaults to 15 minutes and must be 1..60 minutes;
`Content:Sanitization:Profile` must be `editorial-v1`.

### 8.3 Transactions/tests/dependencies

Draft/publish/unpublish/rollback/navigation pointer changes plus audit use one transaction.
Concurrency loser returns 409; no automatic merge. Required Domain/Application/LocalDB/Web tests:
immutable revision and trigger, rollback-new-revision, draft non-leakage, path uniqueness, NFC/RTL
round-trip, checksum determinism, sanitizer hostile corpus, SEO projection, token tamper/expiry/
wrong-revision/noindex, all role/MFA/allow-deny audit cells, cancellation-safe audit, and complete
T18 handoff. T10/T13/T16/T17 depend only on the published catalog/C2 ports. PX-01 sanitizer is a
final-gate dependency; T01 data is not.

## 9. T06 Calendar module

### 9.1 Frozen contracts

Implement C2 unchanged: `IEventReader`, `ICalendarExporter`. Add under
`Husaynia.Application.Calendar`:

```csharp
public interface IEventEditor
{
    Task<Result<EventDraftReceipt, CalendarError>> SaveDraftAsync(
        EventDraft draft, Guid? expectedDraftRevisionId, UserContext actor, CancellationToken ct);
    Task<Result<EventPublishReceipt, CalendarError>> PublishAsync(
        Guid eventId, Guid expectedDraftRevisionId, UserContext actor, CancellationToken ct);
    Task<Result<EventPublishReceipt, CalendarError>> UnpublishAsync(
        Guid eventId, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
    Task<Result<EventPublishReceipt, CalendarError>> RollbackAsync(
        Guid eventId, Guid priorRevisionId, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
}

public interface IPublishedEventCatalog
{
    Task<Result<IReadOnlyList<PublishedEventOccurrence>, CalendarError>> ListAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
```

T10/T14/T16 consume the catalog/C2 ports, never Calendar tables.

### 9.2 Model, recurrence, and iCal

Tables: `CalendarEvents`, `CalendarEventRevisions`, `CalendarCategories`,
`CalendarEventRevisionCategories`, `CalendarOccurrences`.

- `CalendarEvent` is mutable/rowversioned with unique normalized slug and draft/published pointers.
- revisions are immutable typed rows: title, summary, sanitized description, local start/end,
  IANA timezone, all-day flag, location, recurrence, ambiguity resolution, SEO/canonical data.
- initial timezone support is exactly `America/Los_Angeles`. Invalid local times are rejected.
  Ambiguous times require explicit `EarlierOffset|LaterOffset`; silent platform-default choice is
  forbidden.
- recurrence grammar is a validated canonical RFC subset: non-recurring or
  `FREQ=DAILY|WEEKLY|MONTHLY|YEARLY`, bounded `INTERVAL`, optional `BYDAY/BYMONTHDAY`, and `COUNT` or
  `UNTIL`. Unsupported baseline rules return `unsupported_recurrence`; they are not approximated.
- publishing materializes deterministic occurrences for the configured horizon, keyed uniquely by
  revision and local occurrence identity. A successful publish makes occurrences/public detail
  visible in the same transaction. Past published detail remains readable; list windows return only
  published occurrences in the requested bounds.
- empty `EventSelection.Slugs` means the full published calendar; non-empty means the exact unique
  requested published slugs. Unknown/private requested slugs return not-found rather than leaking
  state.
- iCal is UTF-8 `text/calendar; charset=utf-8`, CRLF, 75-octet folding, RFC escaping, stable
  `{eventId:N}@husaynia.org` UID, `PRODID`, `VERSION:2.0`, `CALSCALE:GREGORIAN`, canonical URL,
  description/location, and LA timezone semantics. Export order is UTC start then UID.

Required actions: `calendar.event.draft.save`, `calendar.event.publish`,
`calendar.event.unpublish`, `calendar.event.rollback`, `calendar.category.manage`.

Config: `Calendar:TimeZoneId=America/Los_Angeles`, `OccurrenceHorizonMonths` 1..36 (default 24),
`MaximumOccurrencesPerRevision` 1..10000 (default 5000).

### 9.3 Tests/dependencies

Required tests cover immutable revisions/trigger, pointer concurrency, draft/published/past/future
visibility, slug/NFC, spring invalid time, fall ambiguity both choices, recurrence boundaries/leap
calendar, occurrence uniqueness, deterministic republish, full/single iCal, Unicode escaping and
line folding, independent parser validation, role/MFA/audit matrix, and T18 handoff. PX-01's
independent parser is a test gate. T01 may later add recurrence fixtures but does not block the
bounded generic implementation.

## 10. T07 Prayer module

### 10.1 Frozen contracts

Implement C2/C4 unchanged: `IPrayerScheduleService`, `IPrayerSource`. Add under
`Husaynia.Application.Prayer` an `IPrayerAdministration` for creating/activating profiles and
saving/deactivating overrides, all with `UserContext`, expected rowversion, and cancellation.
No public/admin caller invokes a concrete calculator/provider.

### 10.2 Model and algorithm

Tables: `PrayerProfiles`, `PrayerSnapshots`, `PrayerOverrides`, `PrayerIntegrationStates`.

- profiles are immutable versioned rows containing provider kind, latitude/longitude, canonical
  method JSON, algorithm version, `America/Los_Angeles`, effective date, and SHA-256 `ProfileHash`.
  Activation is a rowversioned pointer/state transition, not an in-place profile edit.
- the default source is a deterministic in-process calculated `IPrayerSource`; an optional external
  source may replace it only through configuration and the same port.
- snapshot rows are unique by `(ProfileHash, Date)`, contain base calculated/provider values,
  generation/source UTC, and validity. A monthly read never mixes profile hashes.
- overrides are retained records with date, prayer key, local time, reason, active state, actor, and
  rowversion. They apply last at read time; latest valid active override wins deterministically.
- public `GetMonthAsync` accepts only the frozen timezone, reads local persisted snapshots, applies
  overrides, and makes zero provider resolutions/calls. A complete old snapshot returns
  `IsStale=true`; no complete snapshot returns `unavailable`. Outage cannot throw through unrelated
  pages.
- refresh computes/fetches outside SQL, then serializably upserts one complete profile/month batch.
  Same profile/date input must produce byte-identical canonical values and hash.

Required actions: `prayer.profile.create`, `prayer.profile.activate`, `prayer.override.save`,
`prayer.override.deactivate`, `prayer.snapshot.refresh` (administrative/manual invocation).

Config: `Prayer:TimeZoneId` must be `America/Los_Angeles`; `SnapshotMaxAge` 1 hour..7 days (default
36 hours); `GenerateMonthsAhead` 1..6 (default 2); external provider disabled by default and, when
enabled, requires allowlisted HTTPS endpoint and bounded 1..60 second timeout. No Snohomish
coordinates/method/tolerance are supplied as defaults while T01 is blocked.

### 10.3 Jobs/tests/dependencies

`prayer.schedule.refresh.v1` registers at startup. Activation enqueues active-profile months;
startup ensures current/next configured months by canonical keys. Handler validates strict
profileHash/year/month payload, calls the provider outside transactions, persists atomically, and
lets T19 retry/dead-letter.

Tests cover profile-hash canonicalization, fixed-clock determinism, LA DST dates, override
precedence/deactivation, complete/incomplete/stale/missing snapshots, timeout/unavailable/malformed,
zero public provider calls, job duplicate/restart/retry behavior, profile change isolation,
SiteAdministrator-only write and audit, no secret diagnostics, and T18 handoff. T01 baseline values
and tolerance remain a pre-release fidelity gate, not an implementation blocker.

## 11. T08 Media module

### 11.1 Frozen contracts

Implement C4 unchanged: `IMediaStore`, `IUploadSafetyValidator`. Add under
`Husaynia.Application.Media`:

```csharp
public interface IMediaLibrary
{
    Task<Result<MediaDeliveryDescriptor, MediaError>> GetByStablePathAsync(
        string stablePath, CancellationToken ct);
}

public interface IMediaEditor
{
    Task<Result<MediaMutationReceipt, MediaError>> UploadAsync(
        MediaUploadCommand command, UserContext actor, CancellationToken ct);
    Task<Result<MediaMutationReceipt, MediaError>> ReplaceAsync(
        Guid assetId, MediaUploadCommand command, RowVersion expectedVersion,
        UserContext actor, CancellationToken ct);
    Task<Result<MediaMutationReceipt, MediaError>> ArchiveAsync(
        Guid assetId, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
}

public interface IMediaBlobLifecycle : IMediaStore
{
    Task<Result<IReadOnlyList<PendingMediaObject>, IntegrationError>> ListPendingAsync(
        DateTimeOffset olderThanUtc, CancellationToken ct);
    Task<Result<bool, IntegrationError>> DeleteAsync(
        string storageKey, string expectedETag, CancellationToken ct);
}
```

### 11.2 Model, upload, and delivery

Tables: `MediaAssets`, `MediaVersions`, `MediaAliases`, `MediaBlobObjects`.

- assets are mutable/rowversioned; versions are immutable; aliases have unique normalized same-origin
  leading-slash paths. Replacement creates a new version and swaps the pointer; aliases never
  change. Archive is explicit state, not deletion.
- internal storage keys are randomized/content-bound and never derived from a supplied path. Blob
  objects are immutable; overwrite is prohibited.
- stable paths reject decoding ambiguity, backslash, control/NUL, query/fragment, encoded slash,
  dot segment, traversal, device names, and non-NFC input. `/wp-content/uploads/**` preserves the
  approved path string while lookup uses a separate normalized key.
- upload order: bound stream -> safety validation -> image decode/re-encode/metadata strip when
  applicable -> SHA-256 -> stage pending Blob -> SQL transaction creates blob/version/asset/alias
  plus audit -> best-effort clear pending state. No SQL transaction encloses Blob I/O.
- if SQL fails, delete the exact pending object by storage key+ETag. If cleanup fails or a crash
  leaves pending state, the cleanup job lists old pending objects and deletes only after a DB
  reference check. A referenced object is preserved even if the pending marker was not cleared.
- default enabled allowlist after PX-01 is JPEG/PNG images and MP3 audio. SVG/HTML/XML, executables,
  scripts, archives, office macros, and polyglots are rejected. PDF/document enablement remains off
  until an approved content-disarm/scan policy exists; if later enabled it is attachment-only.
- image claimed MIME, extension, magic bytes, successful full decode, dimensions, and decoded pixel
  limit must agree. Stored images are re-encoded and metadata-free. Audio validates bounded ID3/
  MPEG frames and is never rendered as active content.
- delivery maps GET/HEAD only. It supports a single byte range, 200/206/304/416, strong version ETag,
  `Accept-Ranges: bytes`, exact content length/type, nosniff, safe content disposition, and short
  cache for stable aliases. Multi-range is rejected. Provider diagnostics never enter the response.

Required actions: `media.asset.upload`, `media.asset.replace`, `media.asset.archive`,
`media.alias.add`.

Config: `Media:Enabled=false` by default; enabled Blob mode requires exact account endpoint/container,
managed-identity credential mode, and bounded connect/operation timeouts. Test emulator mode is
explicit. Size/pixel limits are validated and capped; values cannot disable safety checks.

### 11.3 Jobs/tests/dependencies

`media.blob.cleanup.v1` runs a bounded page per execution and schedules the next UTC-hour generation.
Tests cover every allowlist mismatch, corrupt/truncated/polyglot/executable, decompression bomb/
dimensions, metadata stripping, randomized storage key, SQL/Blob fault compensation, crash/pending
cleanup, replacement alias stability, archive result, traversal/encoding, GET/HEAD/range/ETag/cache,
provider timeout, role/MFA/audit, and T18 handoff. Use fake store plus Azurite/test container; no live
Storage account. PX-01 image and Blob clients are blocking final dependencies.

## 12. T11 Forms module

### 12.1 Frozen contracts

Implement C2/C4 unchanged: `IFormSubmissionService`, `IOutboundMessageSender`. Add module-owned read/
admin ports for active definitions, privacy-safe submission summaries, retention status, and audited
manual delivery retry. Public T15 posts only through `IFormSubmissionService`.

### 12.2 Model and submission transaction

Tables: `FormDefinitions`, `FormDefinitionVersions`, `FormFields`, `FormSubmissions`,
`FormSubmissionValues`, `FormDeliveryAttempts`, `FormRateLimits`.

- definition versions and fields are immutable. An enabled `FormDefinition` points to one published
  version. Unknown/unpublished forms return not-found without schema disclosure.
- field kinds are a bounded generic set (`Text|Email|Telephone|TextArea|Decimal|Choice|Consent`).
  Validation rules are versioned data: label, required, bounds, pattern kind, choices, order, privacy
  class. Raw regex supplied by admins is prohibited.
- Web computes a keyed HMAC fingerprint from normalized remote address, form key, and canonical
  payload hash using `Forms:FingerprintKey`; only the 32-byte result is passed/stored. Honeypot and
  antiforgery run before Application submission. Bot rejection is a generic acknowledgement with
  no record/job.
- rate limiting is a Forms-owned SQL fixed window per form/fingerprint, serialized with
  `UPDLOCK,HOLDLOCK`. Failure is fail-closed and creates no partial submission.
- duplicate window start is deterministically bucketed. Unique
  `(DefinitionVersionId, DuplicateFingerprint, DuplicateWindowStartUtc)` is authoritative. Same hash
  returns the original receipt; same fingerprint key with a different canonical payload is conflict.
- one serializable transaction validates the active version, reserves duplicate identity, persists
  one submission/value set, enqueues one `forms.delivery.v1` job, and appends audit/operational
  metadata. Invalid/bot/rate-limited input leaves no submission/value/job.
- values are minimized and bounded. Secrets/attachments/payment data are unsupported. Logs,
  telemetry, durable payload, and privileged audit contain no values. Retention uses status and T19
  retention fencing; legal hold blocks deletion/anonymization.

The non-public sender modes are `Disabled` and a Development/Integration pickup sink with a
configured test-only directory/destination key. Staging/Production real-provider selection remains
external; the Application port is frozen. A disabled sender makes the job retry/dead-letter without
losing the submission.

Required actions: `forms.definition.publish`, `forms.submission.read`, `forms.delivery.retry`,
`forms.retention.change`.

Config: `Forms:Enabled=false` default; when enabled require 32-byte base64 fingerprint key, duplicate
window 1 minute..7 days, rate limit 1..1000 and window <=1 hour, retention within policy, and an
explicit non-live delivery mode outside Production.

### 12.3 Job/tests/dependencies

The strict delivery payload contains only submission/definition-version IDs. The handler reloads
persisted data, creates an append-only attempt, calls sender outside SQL, and records the sanitized
outcome; T19 owns retry/dead-letter. Tests cover version selection, every field type/boundary,
malformed/oversized/unknown fields, antiforgery, honeypot, distributed rate limit, duplicate races,
atomic submission+job, pickup/failure/retry/dead-letter, cancellation, retention/legal hold,
privacy/log/audit leak inspection, SiteAdministrator-only admin, and T18 handoff. T01 later replaces
test definitions; no current live destination/pledge schema is invented.

## 13. T12 Donations module

### 13.1 Frozen contracts

Implement C2/C4 unchanged: `IDonationService`, `IPaymentGateway`. Add under
`Husaynia.Application.Donations`:

```csharp
public interface IDonationStatusReader
{
    Task<Result<DonationStatusView, DonationError>> GetByCheckoutReferenceAsync(
        string checkoutReference, CancellationToken ct);
}

public interface IDonationOperations
{
    Task<Result<DonationOperationsView, DonationError>> GetAsync(
        Guid donationId, UserContext actor, CancellationToken ct);
    Task<Result<DonationReconciliationReceipt, DonationError>> ReconcileAsync(
        Guid donationId, RowVersion expectedVersion, UserContext actor, CancellationToken ct);
}
```

T15 uses create/status only. Browser success/cancel routes remain read-only and T15-owned.

### 13.2 Model and Checkout flow

Tables: `DonationCategories`, `Donations`, `DonationPaymentEvents`, `DonationCampaignLedger`.

- categories are rowversioned data with unique normalized slug, currency, allowed mode set,
  min/max/preset/custom amount rules, enabled state, receipt/anonymity flags, and optional campaign
  key. No live category/mode/amount seed exists while T01 is blocked.
- money is integer minor units plus uppercase ISO currency; decimal-to-minor conversion is checked
  and exact. Negative/zero/overflow/mismatched currency/mode/custom amounts fail before persistence.
- donations have unique application idempotency key and request hash; provider Checkout Session and
  PaymentIntent IDs have filtered unique indexes. Status transitions are a closed state machine:
  `CheckoutCreating -> Pending -> Completed|Failed|Cancelled|Expired`, with no browser-controlled
  transition to Completed.
- Checkout phase 1 serializably reserves the T03 idempotency scope `donations.checkout.v1`, creates
  or reloads the local `CheckoutCreating` donation, and commits. The Stripe call occurs outside SQL
  with the exact same idempotency key. Phase 2 atomically attaches the returned provider checkout ID,
  stores only the approved redirect/reference, changes to Pending, completes the idempotency receipt,
  and audits. A retry with the same key/hash resumes/replays through Stripe's idempotency; a different
  hash is conflict. Concurrent in-progress callers return a bounded in-progress result, not a second
  mutation.
- success/cancel URIs must be same-origin approved routes; arbitrary caller hosts are rejected.

### 13.3 Webhook authority and reconciliation

`POST /webhooks/stripe` is anonymous, antiforgery-exempt, and signature-mandatory. It reads the exact
raw bounded body once, rejects missing/malformed/oversized signature/body generically, invokes
`VerifyWebhookAsync`, and only then processes typed data. Raw body/signature are never logged/stored.

A serializable transaction:

1. inserts unique provider event ID with event type and payload hash;
2. on duplicate, returns the prior receipt only when hash/type match, otherwise conflict;
3. loads donation by approved provider IDs;
4. applies the allowed event transition;
5. inserts at most one `DonationCampaignLedger` row keyed by provider event ID when completion first
   occurs;
6. updates donation rowversion and commits.

Browser callback, refresh, duplicate/concurrent webhook, out-of-order failure after completion, and
reconciliation cannot produce a second completion/ledger delta. Unknown verified events are retained
as privacy-safe unmatched outcomes and may enqueue `donations.reconcile.v1`; they do not increment a
campaign. Ledger totals are summed from immutable rows or updated only in this transaction.

Admin views expose amount/category/status/timestamps/provider reference suffixes only: no card data,
secret, signature, receipt URL token, raw payload, full donor contact, or provider diagnostics.
Required actions: `donations.status.read`, `donations.reconcile`, `donations.category.manage`.

Config: `Donations:Enabled=false` default. Enabled mode requires Stripe test/sandbox key, webhook
secret, fixed `https://api.stripe.com` endpoint, same-origin return base, currency/amount bounds, and
connect/operation timeouts. Live key prefixes are rejected in Development/Integration/Staging and in
all automated tests. No automatic POST retry is configured outside explicit same-key application
replay.

### 13.4 Tests/dependencies

Tests cover amount/currency/mode/category rules, custom/preset boundaries, idempotency key/hash races,
provider timeout/crash between phases/retry, redirect allowlist, signature timestamp/tamper/body
bounds, decline/cancel/expired/3DS fixtures, browser callback non-mutation, duplicate/concurrent/
out-of-order webhook, unique completion/ledger, unmatched reconciliation job, rowversion admin
conflict, all roles/MFA/audit, and DB/log/telemetry/browser/admin leak inspection. Use local contract
server/fakes and Stripe test fixtures only; no real charge or live network. PX-01 Stripe client is a
blocking final dependency; T01 controls production categories/modes only.

## 14. Disjoint ownership and downstream handoffs

| Task | Exclusive source/test ownership in this wave | Downstream stable consumers |
|---|---|---|
| T05 | Domain/Application/Infrastructure/Admin `Content/**`; matching test `Content/**` | T10, T13, T16, T17, T18 |
| T06 | Domain/Application/Infrastructure/Admin `Calendar/**`; matching test `Calendar/**` | T10, T14, T16, T17, T18 |
| T07 | Domain/Application/Infrastructure/Admin `Prayer/**`; matching test `Prayer/**` | T14, T18 |
| T08 | Domain/Application/Infrastructure/Admin `Media/**`, `Features/MediaDelivery/**`; matching tests | T13, T15, T16, T17, T18 |
| T11 | Domain/Application/Infrastructure/Admin `Forms/**`; matching tests | T15, T18, T19 retention |
| T12 | Domain/Application/Infrastructure/Admin `Donations/**`, `Features/StripeWebhook/**`; matching tests | T15, T16, T17, T18 |

No shared helper file is created across modules. Small admission/audit/normalization helpers are
module-local to preserve parallel ownership. Cross-module behavior occurs only through frozen
Application ports, T19 job contracts, T04 authorization/audit contracts, and T03 persistence
semantics.

T18 starts only after all six handoffs plus T10/T17 are complete. T10 starts after T05/T06 and uses
published catalogs, not table access. T13/T14/T15 may begin against deterministic port fakes when
their required backend contracts compile, but fidelity acceptance remains blocked on T01.

## 15. Required validation per feature and merge wave

Each feature owner must retain commands and real pass counts:

1. Domain tests in its owned folder.
2. Application tests in its owned folder.
3. Integration tests in its owned folder against disposable guarded LocalDB; T08 also Blob emulator,
   T12 local Stripe contract fixture.
4. Web policy/MFA/antiforgery/audit tests using the composed full host.
5. `Husaynia.ContractTests` to prove C2/C4 unchanged.
6. `Husaynia.ArchitectureTests` and a module-local discovery/resolution test.
7. Strict Release build with warnings as errors.

The merge wave then runs the full non-live solution suite. Required negative evidence includes no
provider call from public reads, no runtime DDL/migration, no feature edit to forbidden shared files,
no real form/payment submission, no live provider credential/network use, zero unexplained skips,
and no sensitive values in logs/audits/errors.

Independent code review and test-engineer gates evaluate each module before T18 consumes its model.
T08/T12 also require independent security review; T05 sanitizer, T11 PII, and T12 payment boundaries
are security-blocking even if a broader gate is scheduled later.

## 16. Risks and explicit non-claims

- PX-01 is a real dependency discovered from current project files. Feature owners must not bypass it
  through unsafe home-grown sanitization/image processing or project-file edits.
- `HusayniaDbContext` and `HusayniaIdentityDbContext` currently build models from the same
  Infrastructure assembly, but feature stores standardize on `HusayniaDbContext`; they do not couple
  to the Identity context.
- Transactional feature audit uses the shared physical `IdentityAuditEvents` table through the base
  context's discovered configuration. T18 must prove one combined model and T04 append-only trigger.
- Forms delivery may be accepted by a provider and then time out; T19 retries can duplicate an
  external message unless the selected provider supplies idempotency. This is observable and must be
  documented; it does not duplicate the durable submission/job.
- Prayer baseline/tolerance, live form/donation definitions, exact recurrence inventory, and live
  media allowlist are intentionally not claimed. T01/export reconciliation remains the authority.
- No production deployment, migration, paid service, secret, live transaction, commit, push, or
  history rewrite is authorized by this architecture.
