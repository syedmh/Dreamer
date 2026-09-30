# T07 Prayer exact T18 schema handoff

Date: 2026-08-28  
Owner: T07 Prayer  
Migration authority: T18 only  
Custom database objects: **none; EF constraints only**

## 1. Final production/config/entity/invariant source inventory

This is the complete final T07 production source list:

```text
src/Husaynia.Domain/Prayer/PrayerCalculator.cs
src/Husaynia.Domain/Prayer/PrayerCanonicalJson.cs
src/Husaynia.Domain/Prayer/PrayerModels.cs
src/Husaynia.Application/Prayer/PrayerContracts.cs
src/Husaynia.Application/Prayer/PrayerRefreshJobs.cs
src/Husaynia.Application/Prayer/PrayerServices.cs
src/Husaynia.Infrastructure/Prayer/PrayerConfiguration.cs
src/Husaynia.Infrastructure/Prayer/PrayerModule.cs
src/Husaynia.Infrastructure/Prayer/PrayerPersistence.cs
src/Husaynia.Infrastructure/Prayer/PrayerSources.cs
src/Husaynia.Infrastructure/Prayer/PrayerStore.cs
src/Husaynia.Infrastructure/Prayer/PrayerTransactionalAuditAppender.cs
src/Husaynia.Web/Areas/Admin/Prayer/PrayerAdminEndpointModule.cs
src/Husaynia.Web/Areas/Admin/Prayer/PrayerAdminEndpoints.cs
src/Husaynia.Web/Areas/Admin/Prayer/PrayerRefreshJobStartupService.cs
```

Entity definitions are in `PrayerModels.cs`. EF configurations are in `PrayerPersistence.cs`.
T07 has no invariant SQL/constants file and owns no custom trigger.

Configuration class names:

```text
Husaynia.Infrastructure.Prayer.PrayerProfileConfiguration
Husaynia.Infrastructure.Prayer.PrayerSnapshotConfiguration
Husaynia.Infrastructure.Prayer.PrayerOverrideConfiguration
Husaynia.Infrastructure.Prayer.PrayerIntegrationStateConfiguration
```

`PrayerSnapshotConfiguration`, `PrayerOverrideConfiguration`, and
`PrayerIntegrationStateConfiguration` derive from `MutableEntityConfiguration<T>`.
Each of those three Prayer-owned configurations explicitly reapplies shadow `RowVersion` with
`IsRequired()`; T18 must preserve the resulting non-null `rowversion`.
`PrayerProfileConfiguration` is an explicit immutable-row configuration.

## 2. Exact tables and columns

No column has a SQL default. `CreatedAtUtc`/`UpdatedAtUtc` shadow values are assigned by the shared
context before save. Shadow `RowVersion` is generated on add/update and is a concurrency token.

### `PrayerProfiles` — immutable profile version

| Column | SQL type | Unicode | Null | Generated |
|---|---|---:|---:|---|
| `Id` | `uniqueidentifier` | n/a | no | never |
| `ProviderKind` | `varchar(32)` | no | no | no |
| `Latitude` | `decimal(9,6)` | n/a | no | no |
| `Longitude` | `decimal(9,6)` | n/a | no | no |
| `MethodJson` | `nvarchar(4000)` | yes | no | no |
| `AlgorithmVersion` | `varchar(64)` | no | no | no |
| `TimeZoneId` | `varchar(64)` | no | no | no |
| `EffectiveFrom` | `date` | n/a | no | no |
| `ProfileHash` | `char(64)` fixed | no | no | no |
| `CreatedBy` | `nvarchar(256)` | yes | no | no |
| `CreatedAtUtc` | `datetimeoffset(7)` | n/a | no | no |

### `PrayerSnapshots` — mutable generated/provider cache row

| Column | SQL type | Unicode | Null | Generated |
|---|---|---:|---:|---|
| `Id` | `uniqueidentifier` | n/a | no | never |
| `ProfileHash` | `char(64)` fixed | no | no | no |
| `Date` | `date` | n/a | no | no |
| `ValuesJson` | `varchar(512)` | no | no | no |
| `GeneratedAtUtc` | `datetimeoffset(7)` | n/a | no | no |
| `Source` | `varchar(64)` | no | no | no |
| `IsValid` | `bit` | n/a | no | no |
| `CreatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `UpdatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `RowVersion` | `rowversion` shadow | n/a | no | add/update |

### `PrayerOverrides` — retained mutable override row

| Column | SQL type | Unicode | Null | Generated |
|---|---|---:|---:|---|
| `Id` | `uniqueidentifier` | n/a | no | never |
| `ProfileHash` | `char(64)` fixed | no | no | no |
| `Date` | `date` | n/a | no | no |
| `PrayerKey` | `varchar(32)` | no | no | no |
| `LocalTime` | `time(0)` | n/a | no | no |
| `Reason` | `nvarchar(500)` | yes | no | no |
| `EffectiveRevision` | `bigint` | n/a | no | no |
| `IsActive` | `bit` | n/a | no | no |
| `CreatedBy` | `nvarchar(256)` | yes | no | no |
| `DeactivatedBy` | `nvarchar(256)` | yes | yes | no |
| `DeactivatedAtUtc` | `datetimeoffset(7)` | n/a | yes | no |
| `CreatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `UpdatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `RowVersion` | `rowversion` shadow | n/a | no | add/update |

### `PrayerIntegrationStates` — mutable activation/provider state

| Column | SQL type | Unicode | Null | Generated |
|---|---|---:|---:|---|
| `Id` | `varchar(32)` | no | no | never |
| `ActiveProfileHash` | `char(64)` fixed | no | yes | no |
| `RefreshIntentProfileHash` | `char(64)` fixed | no | yes | no |
| `RefreshIntentCreatedAtUtc` | `datetimeoffset(7)` | n/a | yes | no |
| `LastAttemptAtUtc` | `datetimeoffset(7)` | n/a | yes | no |
| `LastSuccessAtUtc` | `datetimeoffset(7)` | n/a | yes | no |
| `LastFailureAtUtc` | `datetimeoffset(7)` | n/a | yes | no |
| `LastErrorCode` | `varchar(100)` | no | yes | no |
| `LastSource` | `varchar(64)` | no | yes | no |
| `LastGeneratedMonth` | `date` | n/a | yes | no |
| `CreatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `UpdatedAtUtc` | `datetimeoffset(7)` shadow | n/a | no | application-managed |
| `RowVersion` | `rowversion` shadow | n/a | no | add/update |

## 3. Exact keys, indexes, checks, and foreign keys

### `PrayerProfiles`

- PK `PK_PrayerProfiles`: (`Id`).
- AK `AK_PrayerProfiles_ProfileHash`: (`ProfileHash`).
- Index `IX_PrayerProfiles_EffectiveFrom`: (`EffectiveFrom`), non-unique, no filter.
- Check `CK_PrayerProfiles_ProviderKind`:
  `[ProviderKind] IN ('local','external')`
- Check `CK_PrayerProfiles_Latitude`:
  `[Latitude] >= -90 AND [Latitude] <= 90`
- Check `CK_PrayerProfiles_Longitude`:
  `[Longitude] >= -180 AND [Longitude] <= 180`
- Check `CK_PrayerProfiles_TimeZoneId`:
  `[TimeZoneId] = 'America/Los_Angeles'`

### `PrayerSnapshots`

- PK `PK_PrayerSnapshots`: (`Id`).
- AK `AK_PrayerSnapshots_ProfileHash_Date`: (`ProfileHash`, `Date`).
- Index `IX_PrayerSnapshots_ProfileHash_GeneratedAtUtc`:
  (`ProfileHash`, `GeneratedAtUtc`), non-unique, no filter.
- FK `FK_PrayerSnapshots_PrayerProfiles_ProfileHash`:
  (`ProfileHash`) -> `PrayerProfiles` alternate key (`ProfileHash`), `ON DELETE NO ACTION`
  (`DeleteBehavior.Restrict`).

### `PrayerOverrides`

- PK `PK_PrayerOverrides`: (`Id`).
- AK `AK_PrayerOverrides_ProfileHash_Date_PrayerKey_Revision`:
  (`ProfileHash`, `Date`, `PrayerKey`, `EffectiveRevision`).
- Index `IX_PrayerOverrides_ActiveLookup`:
  (`ProfileHash`, `Date`, `PrayerKey`, `IsActive`, `EffectiveRevision`), non-unique, no filter.
- Check `CK_PrayerOverrides_EffectiveRevision`: `[EffectiveRevision] > 0`.
- FK `FK_PrayerOverrides_PrayerProfiles_ProfileHash`:
  (`ProfileHash`) -> `PrayerProfiles` alternate key (`ProfileHash`), `ON DELETE NO ACTION`
  (`DeleteBehavior.Restrict`).

### `PrayerIntegrationStates`

- PK `PK_PrayerIntegrationStates`: (`Id`).
- Index `IX_PrayerIntegrationStates_ActiveProfileHash`:
  (`ActiveProfileHash`), non-unique, no filter.
- Index `IX_PrayerIntegrationStates_RefreshIntentProfileHash`:
  (`RefreshIntentProfileHash`), non-unique, no filter.
- FK `FK_PrayerIntegrationStates_PrayerProfiles_ActiveProfileHash`:
  nullable (`ActiveProfileHash`) -> `PrayerProfiles` alternate key (`ProfileHash`),
  `ON DELETE NO ACTION` (`DeleteBehavior.Restrict`).
- FK `FK_PrayerIntegrationStates_PrayerProfiles_RefreshIntentProfileHash`:
  nullable (`RefreshIntentProfileHash`) -> `PrayerProfiles` alternate key (`ProfileHash`),
  `ON DELETE NO ACTION` (`DeleteBehavior.Restrict`).

There are no filtered indexes.

## 4. Custom SQL objects

None. T07 owns no trigger, `InstallSql`, `DownSql`, reserved SQL error number, runtime DDL, or
custom migration SQL.

## 5. Required T18 migration order

Additive `Up` order:

1. `PrayerProfiles` with PK, AK, checks, then `IX_PrayerProfiles_EffectiveFrom`.
2. `PrayerIntegrationStates` with PK and both FKs, then
   `IX_PrayerIntegrationStates_ActiveProfileHash` and
   `IX_PrayerIntegrationStates_RefreshIntentProfileHash`.
3. `PrayerSnapshots` with PK/AK/FK, then
   `IX_PrayerSnapshots_ProfileHash_GeneratedAtUtc`.
4. `PrayerOverrides` with PK/AK/check/FK, then `IX_PrayerOverrides_ActiveLookup`.

Exact reverse-safe `Down` order:

1. drop `PrayerOverrides`;
2. drop `PrayerSnapshots`;
3. drop `PrayerIntegrationStates`;
4. drop `PrayerProfiles`.

No data backfill or destructive conversion is owned by T07.

## 6. Store conflict/concurrency mapping

- SQL 2601/2627 while creating a profile maps to `profile_conflict`.
- SQL 2601/2627 while saving an override maps to `override_conflict`.
- Snapshot complete-month upsert runs at `Serializable`; an unexpected database uniqueness/write
  failure maps to `persistence_failure` and never claims success.
- Activation and override deactivation compare submitted rowversion and map stale values or
  `DbUpdateConcurrencyException` to `concurrency_conflict` (HTTP 409).
- Saving an override for a non-active profile maps to `profile_not_active` (HTTP 409).
- Feature mutation plus authorized `IdentityAuditEvents` insert uses one
  `HusayniaDbContext` transaction. Audit insert failure rolls the mutation back; a bounded
  post-rollback failure audit is attempted through `IAuditWriter`; its failure maps to
  `prayer_audit_unavailable`/503.

## 7. Fixture and verification evidence

Disposable fixture setup:

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer." --nologo
```

Result: 16 passed, 0 failed, 0 skipped. The fixture uses guarded unique `HusayniaT07_*` LocalDB
databases, calls `EnsureCreated` only in test setup, and deletes only the exact guarded database.
Production runtime performs no `EnsureCreated`, `EnsureDeleted`, `Migrate`, or DDL.

Model discovery assertions cover all four configuration classes, exact table/AK names, checks, and
rowversion placement. Store tests cover persisted complete batches, profile isolation, override-last,
inactive-profile rejection, stale fallback, rowversion conflict, and atomic audit rollback.

## 8. Gates and unresolved risks

- Developer validation: passed; see `implementation-evidence.md` and `../../gates/T07/test-results.md`.
- Independent test gate: pending.
- Independent code-review gate: pending.
- T01 approved live Snohomish profile/baseline/tolerance: pending pre-release; no defaults supplied.
- T18 must not generate/apply a migration until required independent gates accept this handoff.
