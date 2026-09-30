# T07 Prayer implementation evidence

Date: 2026-08-28  
Status: implementation and developer validation complete; independent test/code-review gates pending.

## Delivered behavior

- Immutable canonical profile versions with SHA-256 settings hashes and configurable coordinates,
  method JSON, provider kind, algorithm version, effective date, and fixed
  `America/Los_Angeles`.
- Offset prayer keys are lower-canonicalized before hashing; Unicode/case-colliding duplicate keys
  are rejected.
- Deterministic framework-only solar calculator with LA DST handling and canonical six-prayer
  schedules.
- Persisted complete-month snapshots, active-profile isolation, stale labeling, missing/incomplete
  unavailable results, and latest-active manual overrides applied last.
- Disabled-by-default allowlisted HTTPS provider, explicit connect/overall timeout, bounded strict
  response parsing, and sanitized timeout/malformed/unavailable mapping.
- Public `IPrayerScheduleService` reads only local persistence and never resolves/calls a provider.
- SiteAdministrator/MFA/antiforgery/correlation enforcement, explicit existing policy evaluation,
  Application capability recheck, and exactly-one redacted audit ownership.
- Activation commits the active profile, durable refresh intent, and one audit before enqueue.
  Enqueue success returns `scheduled`; enqueue failure returns committed `pending` state and startup
  catch-up recovers from the durable intent without reporting a false activation failure.
- Profile creation computes its return projection before mutation/audit commit, so no post-commit
  read can convert committed success into a Web failure or a second audit.
- External timestamps beyond five-minute clock skew are rejected; near-future timestamps are
  clamped, and any future persisted timestamp is conservatively stale.
- Prayer-owned EF configuration explicitly makes every mutable Prayer rowversion non-null and names
  both integration-state FK indexes; no shared persistence helper was changed.
- Serializable monthly refresh upsert after provider completion; rowversion activation/deactivation;
  mutation and authorized store audit committed together.
- T19 `IJobHandler` only: strict payload, canonical monthly idempotency key, startup definition and
  catch-up registration, duplicate restart behavior, and dead-letter repair. No scheduler or worker
  was added.
- No public HTML; T14 remains owner.

## Final owned source files

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

## Final owned test files

```text
tests/Husaynia.Domain.Tests/Prayer/PrayerCalculatorTests.cs
tests/Husaynia.Domain.Tests/Prayer/PrayerDomainTests.cs
tests/Husaynia.Application.Tests/Prayer/PrayerApplicationTests.cs
tests/Husaynia.Application.Tests/Prayer/PrayerRefreshJobTests.cs
tests/Husaynia.IntegrationTests/Prayer/PrayerAdminEndpointTests.cs
tests/Husaynia.IntegrationTests/Prayer/PrayerConfigurationAndModelTests.cs
tests/Husaynia.IntegrationTests/Prayer/PrayerProviderIsolationTests.cs
tests/Husaynia.IntegrationTests/Prayer/PrayerStoreTests.cs
```

## Validation summary

69 tests passed, 0 failed, 0 skipped across focused Prayer, frozen contract, and architecture suites.
The strict Web project and full solution builds passed with 0 warnings and 0 errors. Exact commands
are retained in `../../gates/T07/test-results.md`.

## Frozen-file evidence

No source/project/package/solution/lock/Program/shared-context/migration file was edited by T07.
Current SHA-256 values for mechanically checkable comparison:

```text
3CF56DAE041AB57B3ADEFC3CFA6543762A97A16C38ED46404C0F9EB9D3056BEB Directory.Build.props
C8E2637A32B9DF519F1582E74F8B7B940D12696B35CBAE3CF74689BBADB784AF Directory.Packages.props
DEB68FDCDF73CA5A9B4862010D170EBC117339887AA0AA72D22A04ADE7437942 global.json
9029BECC5E5A01EC1FBBED213C7F811F8550B25C4FF641F10CC8E19AEEBF0B1D NuGet.config
38CBF385A72275A727771D0CCC23B91D5A615D2AF532E106B08941592DBAD734 HusayniaSite.sln
CBAB68257FBE06AC257A6DBA945AD7DD68E09EA117EB1A29FF31C99D82CCA9EB src/Husaynia.Domain/Husaynia.Domain.csproj
47AFD6CC2A6C3AAF25A8099637D10D71147643896567231F13C5B39CF55B004C src/Husaynia.Application/Husaynia.Application.csproj
9DDBFCAC8717FF1A9F5A0D0152CC3066D056DD9E71A2ED7700FFD53A4B2DE407 src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj
8E0757F08B79BC2562931251029BEA829B01D1881296B784DB8CD27E283FDBB5 src/Husaynia.Web/Husaynia.Web.csproj
2241481B7E74DCDCD600303146B730ECC12CBD8E49AA55755EF95EE499FE229A src/Husaynia.Domain/packages.lock.json
2C6120BCF585B0F4A2FDACF9AA7E716F1CA0795406E03946F17091E59205EF7D src/Husaynia.Application/packages.lock.json
C6F0DAFE1344A3B1AFD798DBA99954BE1FADD2C319C83071824D655EE4D9B728 src/Husaynia.Infrastructure/packages.lock.json
D449D8CCDBA6F7E828AC07187B23419907861B1BE21C3D929DDA09052E352E3C src/Husaynia.Web/packages.lock.json
```

## Known gates and residual risk

- T01 live Snohomish baseline values and accepted display tolerance remain deliberately unresolved;
  no production profile defaults were invented.
- External NuGet vulnerability service access initially produced NU1900. Locked assets were refreshed
  with audit disabled solely to permit `--no-restore` compilation; the external dependency-audit
  evidence gate remains outside T07.
- Independent test and code-review verdicts are pending. This document does not self-approve them.
- No deployment, migration, commit, push, or history rewrite occurred.
