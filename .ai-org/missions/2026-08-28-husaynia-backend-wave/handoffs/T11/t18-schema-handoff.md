# T11 Forms exact T18 schema handoff

Date: 2026-08-28  
Schema owner after acceptance: T18 only  
Default schema: `dbo`  
Custom trigger/SQL objects: **none**

## 1. Source list and configuration classes

Authoritative model source:

- `src/Husaynia.Infrastructure/Forms/FormsEntityConfigurations.cs`

Configuration classes:

- `FormDefinitionConfiguration`
- `FormDefinitionVersionConfiguration`
- `FormFieldConfiguration`
- `FormSubmissionConfiguration`
- `FormSubmissionValueConfiguration`
- `FormDeliveryAttemptConfiguration`
- `FormRateLimitConfiguration`

Authoritative entities:

- `src/Husaynia.Domain/Forms/FormEntities.cs`

Store mappings that consume SQL 1205/2601/2627 and rowversion conflicts:

- `src/Husaynia.Infrastructure/Forms/EfFormsStore.cs`
- `src/Husaynia.Infrastructure/Forms/SqlFormsRateLimiter.cs`

## 2. Exact tables and columns

No column has a SQL default unless generated behavior is stated.

### `FormDefinitions` — mutable aggregate root

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `Key` | `varchar(100)` | no | no |
| `Title` | `nvarchar(200)` | no | no |
| `PublishedVersionId` | `uniqueidentifier` | yes | no |
| `IsEnabled` | `bit` | no | no |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `UpdatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `RowVersion` | `rowversion` | no | database on add/update |

### `FormDefinitionVersions` — immutable

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `DefinitionId` | `uniqueidentifier` | no | no |
| `Version` | `int` | no | no |
| `DestinationKey` | `varchar(100)` | no | no |
| `TemplateKey` | `varchar(100)` | no | no |
| `ConsentVersion` | `nvarchar(100)` | yes | no |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | application timestamp |

### `FormFields` — immutable

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `DefinitionVersionId` | `uniqueidentifier` | no | no |
| `Key` | `varchar(100)` | no | no |
| `Label` | `nvarchar(200)` | no | no |
| `Kind` | `int` | no | enum `0..6` |
| `Required` | `bit` | no | no |
| `MinimumLength` | `int` | yes | no |
| `MaximumLength` | `int` | yes | no |
| `MinimumValue` | `decimal(19,4)` | yes | no |
| `MaximumValue` | `decimal(19,4)` | yes | no |
| `PatternKind` | `int` | no | enum `0..2` |
| `ChoicesJson` | `nvarchar(max)` | no | max-length metadata `22000` |
| `Order` | `int` | no | no |
| `PrivacyClass` | `int` | no | enum `0..2` |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | application timestamp |

### `FormSubmissions` — mutable aggregate root

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `DefinitionVersionId` | `uniqueidentifier` | no | no |
| `DuplicateFingerprint` | `binary(32)` | no | keyed HMAC |
| `CanonicalPayloadHash` | `binary(32)` | no | SHA-256 |
| `DuplicateWindowStartUtc` | `datetimeoffset(7)` | no | deterministic bucket |
| `AcceptedAtUtc` | `datetimeoffset(7)` | no | application timestamp |
| `RetentionEligibleAtUtc` | `datetimeoffset(7)` | no | application timestamp |
| `ConsentVersion` | `nvarchar(100)` | yes | no |
| `RetentionStatus` | `int` | no | enum `0..2` |
| `HasLegalHold` | `bit` | no | no |
| `AnonymizedAtUtc` | `datetimeoffset(7)` | yes | no |
| `DeliveryJobInstanceId` | `uniqueidentifier` | yes | T19 receipt |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `UpdatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `RowVersion` | `rowversion` | no | database on add/update |

### `FormSubmissionValues` — retention-mutable value rows

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `SubmissionId` | `uniqueidentifier` | no | no |
| `FieldId` | `uniqueidentifier` | no | no |
| `FieldKey` | `varchar(100)` | no | no |
| `Value` | `nvarchar(4000)` | no | emptied only by fenced anonymization |
| `PrivacyClass` | `int` | no | enum `0..2` |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | application timestamp |

### `FormDeliveryAttempts` — append-only attempt identity with one finalization update

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `uniqueidentifier` | no | client |
| `SubmissionId` | `uniqueidentifier` | no | no |
| `JobInstanceId` | `uniqueidentifier` | no | T19 identifier |
| `AttemptNumber` | `int` | no | T19 attempt number |
| `StartedAtUtc` | `datetimeoffset(7)` | no | application timestamp |
| `CompletedAtUtc` | `datetimeoffset(7)` | yes | no |
| `Outcome` | `int` | no | enum `0..3` |
| `ErrorCode` | `varchar(100)` | yes | bounded machine code |
| `ProviderReceiptHash` | `binary(32)` | yes | SHA-256 only |
| `CreatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `UpdatedAtUtc` | `datetimeoffset(7)` | no | shadow, application timestamp |
| `RowVersion` | `rowversion` | no | database on add/update |

### `FormRateLimits` — mutable SQL fixed-window partition

| Column | SQL type | Null | Generated |
|---|---|---:|---|
| `Id` | `bigint` | no | identity |
| `FormKey` | `varchar(100)` | no | no |
| `ClientFingerprint` | `binary(32)` | no | keyed HMAC |
| `WindowStartedAtUtc` | `datetimeoffset(7)` | no | no |
| `WindowEndsAtUtc` | `datetimeoffset(7)` | no | no |
| `RequestCount` | `int` | no | no |
| `RetainUntilUtc` | `datetimeoffset(7)` | no | no |

## 3. Exact keys, indexes, checks, and foreign keys

### Primary keys

- `PK_FormDefinitions` (`Id`)
- `PK_FormDefinitionVersions` (`Id`)
- `PK_FormFields` (`Id`)
- `PK_FormSubmissions` (`Id`)
- `PK_FormSubmissionValues` (`Id`)
- `PK_FormDeliveryAttempts` (`Id`)
- `PK_FormRateLimits` (`Id`)

### Foreign keys

- `FK_FormDefinitions_PublishedVersion`: `FormDefinitions.PublishedVersionId` ->
  `FormDefinitionVersions.Id`; optional; delete `Restrict`.
- `FK_FormDefinitionVersions_Definition`: `FormDefinitionVersions.DefinitionId` ->
  `FormDefinitions.Id`; delete `Restrict`.
- `FK_FormFields_DefinitionVersion`: `FormFields.DefinitionVersionId` ->
  `FormDefinitionVersions.Id`; delete `Cascade`.
- `FK_FormSubmissions_DefinitionVersion`: `FormSubmissions.DefinitionVersionId` ->
  `FormDefinitionVersions.Id`; delete `Restrict`.
- `FK_FormSubmissionValues_Submission`: `FormSubmissionValues.SubmissionId` ->
  `FormSubmissions.Id`; delete `Cascade`.
- `FK_FormSubmissionValues_Field`: `FormSubmissionValues.FieldId` -> `FormFields.Id`;
  delete `Restrict`.
- `FK_FormDeliveryAttempts_Submission`: `FormDeliveryAttempts.SubmissionId` ->
  `FormSubmissions.Id`; delete `Cascade`.

`DeliveryJobInstanceId` and `JobInstanceId` are T19 contract identifiers, intentionally not
cross-module EF foreign keys.

### Unique indexes

- `UX_FormDefinitions_Key` (`Key`)
- `UX_FormDefinitionVersions_DefinitionId_Version` (`DefinitionId`, `Version`)
- `UX_FormFields_DefinitionVersionId_Key` (`DefinitionVersionId`, `Key`)
- `UX_FormFields_DefinitionVersionId_Order` (`DefinitionVersionId`, `Order`)
- `UX_FormSubmissions_DefinitionVersionId_Fingerprint_Window`
  (`DefinitionVersionId`, `DuplicateFingerprint`, `DuplicateWindowStartUtc`)
- `UX_FormSubmissions_DeliveryJobInstanceId` (`DeliveryJobInstanceId`) with filter
  `[DeliveryJobInstanceId] IS NOT NULL`
- `UX_FormSubmissionValues_SubmissionId_FieldId` (`SubmissionId`, `FieldId`)
- `UX_FormDeliveryAttempts_SubmissionId_JobInstanceId_AttemptNumber`
  (`SubmissionId`, `JobInstanceId`, `AttemptNumber`)
- `UX_FormRateLimits_FormKey_ClientFingerprint` (`FormKey`, `ClientFingerprint`)

### Non-unique indexes

- `IX_FormSubmissions_Retention`
  (`RetentionStatus`, `RetentionEligibleAtUtc`, `HasLegalHold`)
- `IX_FormRateLimits_RetainUntilUtc` (`RetainUntilUtc`)

### Check constraints

- `CK_FormDefinitions_PublishedWhenEnabled`:
  `[IsEnabled] = 0 OR [PublishedVersionId] IS NOT NULL`
- `CK_FormDefinitionVersions_Version`: `[Version] > 0`
- `CK_FormFields_Order`: `[Order] BETWEEN 1 AND 100`
- `CK_FormFields_Kind`: `[Kind] BETWEEN 0 AND 6`
- `CK_FormFields_PatternKind`: `[PatternKind] BETWEEN 0 AND 2`
- `CK_FormFields_PrivacyClass`: `[PrivacyClass] BETWEEN 0 AND 2`
- `CK_FormFields_LengthBounds`:
  `([MinimumLength] IS NULL OR [MinimumLength] >= 0) AND ([MaximumLength] IS NULL OR [MaximumLength] BETWEEN 1 AND 4000) AND ([MinimumLength] IS NULL OR [MaximumLength] IS NULL OR [MinimumLength] <= [MaximumLength])`
- `CK_FormFields_ValueBounds`:
  `[MinimumValue] IS NULL OR [MaximumValue] IS NULL OR [MinimumValue] <= [MaximumValue]`
- `CK_FormSubmissions_RetentionEligible`:
  `[RetentionEligibleAtUtc] > [AcceptedAtUtc]`
- `CK_FormSubmissions_RetentionStatus`: `[RetentionStatus] BETWEEN 0 AND 2`
- `CK_FormSubmissionValues_PrivacyClass`: `[PrivacyClass] BETWEEN 0 AND 2`
- `CK_FormDeliveryAttempts_AttemptNumber`: `[AttemptNumber] > 0`
- `CK_FormDeliveryAttempts_Outcome`: `[Outcome] BETWEEN 0 AND 3`
- `CK_FormDeliveryAttempts_Completion`:
  `([Outcome] = 0 AND [CompletedAtUtc] IS NULL) OR ([Outcome] <> 0 AND [CompletedAtUtc] IS NOT NULL)`
- `CK_FormDeliveryAttempts_Receipt`:
  `([Outcome] = 1 AND [ProviderReceiptHash] IS NOT NULL) OR ([Outcome] <> 1 AND [ProviderReceiptHash] IS NULL)`
- `CK_FormRateLimits_RequestCount`: `[RequestCount] > 0`
- `CK_FormRateLimits_Window`: `[WindowEndsAtUtc] > [WindowStartedAtUtc]`
- `CK_FormRateLimits_Retention`: `[RetainUntilUtc] >= [WindowEndsAtUtc]`

## 4. Custom SQL and reserved errors

T11 has no immutable-table trigger and no `FormsDatabaseInvariant` SQL. T18 must add no Forms
trigger, stored procedure, runtime DDL, or reserved custom SQL error. Runtime rate limiting invokes
the built-in `sys.sp_getapplock`; this is not a migration object.

## 5. Additive Up and reverse-safe Down order

Recommended `Up`:

1. Create `FormDefinitions` columns/PK/check/index, deferring
   `FK_FormDefinitions_PublishedVersion`.
2. Create `FormDefinitionVersions` with `FK_FormDefinitionVersions_Definition`.
3. Add `FK_FormDefinitions_PublishedVersion`.
4. Create `FormFields`.
5. Create `FormSubmissions`.
6. Create `FormSubmissionValues`.
7. Create `FormDeliveryAttempts`.
8. Create `FormRateLimits`.

Exact reverse-safe `Down`:

1. Drop `FormRateLimits`.
2. Drop `FormDeliveryAttempts`.
3. Drop `FormSubmissionValues`.
4. Drop `FormSubmissions`.
5. Drop `FormFields`.
6. Drop `FK_FormDefinitions_PublishedVersion`.
7. Drop `FormDefinitionVersions`.
8. Drop `FormDefinitions`.

## 6. Store conflict mappings

- SQL `2601`/`2627` on the duplicate fence: clear tracking, reload winner, fixed-time compare
  `CanonicalPayloadHash`; same hash returns prior receipt, different hash returns
  `duplicate_conflict`/409.
- SQL `1205` or an auto-completed deadlock transaction: bounded retry; no external effect exists
  inside the submission transaction.
- Rate limiter serializes each HMAC partition with transaction-scoped `sys.sp_getapplock` and
  `UPDLOCK,HOLDLOCK`; SQL `1205/2601/2627` uses bounded retry; exhaustion fails closed.
- EF rowversion conflict in retention finalization maps to `retention_conflict`/409.
- Definition publish requires the current `FormDefinitions.RowVersion` for an existing definition;
  empty is accepted only for create-if-absent. The original shadow rowversion is assigned as EF's
  concurrency original. Mismatch maps to `definition_conflict`/409 before a new version is inserted.
- Every retention mutation requires the current `FormSubmissions.RowVersion`; mismatch maps to
  `retention_conflict`/409 before status, hold, value, or audit mutation.
- HTTP definition and retention success responses expose the refreshed rowversion as a quoted
  base64 ETag. Request DTOs carry the expected 8-byte rowversion.

## 7. T19 retention and requeue audit dependencies

No duplicate Forms lease/fence columns are added. Destructive retention depends on the existing T19
`OperationsRetentionRuns`, `OperationsRetentionRunItems`, and `OperationsRetentionHolds` schema.
Under one serializable transaction and target application lock, the sink locks the exact run/item
with `UPDLOCK,HOLDLOCK`, validates `RunId`, current `LeaseToken`, unexpired/incomplete run,
target/subject/due binding, and `Applying`, then checks active wildcard/subject holds. It locks the
submission with `UPDLOCK,HOLDLOCK`, requires `Eligible`, due timestamp equality, no entity legal
hold, and a valid rowversion, then anonymizes. T19's existing stable callback changes the item from
`Applying` to `Applied`; an already-anonymized current permit is idempotent so crash recovery can
finish that callback. The sink captures one authoritative UTC value after the target application
lock is acquired and uses it for lease-expiry, hold effective-window, due, and mutation decisions.

Manual delivery retry continues to use existing T19 `OperationsJobInstances` and
`OperationsJobAudit`. The durable requeue reason is a 71-character `sha256:` fingerprint, never raw
admin text. The T04 `retry_authorized` audit is finalized before invoking requeue; audit failure
therefore leaves the job `DeadLettered` and creates no T19 operational audit. No compensation claim,
new Forms audit/retry table, or cross-module foreign key is required.

## 8. Fixture and executed evidence

Fixture setup uses disposable LocalDB through existing test infrastructure and `EnsureCreated` only
inside tests. Production/runtime source never invokes it.

Executed focused results:

- Domain: 3 passed, 0 failed, 0 skipped.
- Application: 7 passed, 0 failed, 0 skipped.
- Forms Integration/Web: 50 passed, 0 failed, 0 skipped.
- Architecture: 10 passed, 0 failed, 0 skipped.
- Frozen C2/C4 contracts: 3 passed, 0 failed, 0 skipped.
- Relevant Identity cookie/security regressions: 17 passed, 0 failed, 0 skipped.
- Relevant T19 retention/job regressions: 71 passed, 0 failed, 0 skipped.
- Current source isolated compiler warning-as-error diagnostic: passed.
- Official strict `--no-restore` build: blocked before compilation by cached external `NU1900`.
- Final duplicate/rate race repetition: 8 passed across four consecutive combined runs.

## 9. Runtime/migration statement and gates

Runtime performs no `EnsureCreated`, `EnsureDeleted`, `Migrate`, migration application, `CREATE
TABLE`, or `CREATE TRIGGER`. T11 created no migration or snapshot.

Independent gates are complete: 468 tests passed with zero failures/skips, security passed with zero
Critical/High findings, correctness review approved, and final engineering judgment approved. T01
live schema/destination evidence remains blocked; production definitions and delivery stay
empty/disabled.
