# T01 ADR-009 A-I rework execution plan

Date: 2026-08-15  
Status: BLOCKED — TOP-LEVEL CAPTURE FAILURE DIAGNOSTICS NOW FAIL CLOSED AND PASS 306 NON-LIVE TESTS; OFFICIAL NUGET AUDIT REMAINS ENVIRONMENT-BLOCKED; FRESH LIVE RUN A/RUN B/VERIFY/PROMOTION WITHHELD  
Scope owner: T01 only

## Objective and boundaries

Implement the binding ADR-009 A-I amendment as a fail-closed capture, comparison, verification, and
promotion pipeline. One developer owns the complete production-code, test-code, and tool-documentation
change so shared T01 files are never edited by parallel implementers. Independent validators are
read-only over source; only the test engineer owns generated staging and approved evidence.

Allowed implementation boundary:

- `HusayniaSite/tools/Husaynia.BaselineCapture/**`
- `HusayniaSite/tests/Husaynia.BaselineCapture.Tests/**`
- Generated `HusayniaSite/evidence/staging/run-a/**` and `run-b/**`
- `HusayniaSite/evidence/baseline/**` only during the final approved promotion task
- This plan and the T01-only note in the parent `task-plan.md`

Forbidden:

- `.ai-org/active-mission.json`
- T02, solution-wide, central-package, contracts, backend, prior-application, forms, payments,
  production, infrastructure, and Git-history changes
- Navigation hierarchy, route/import schemas, migration behavior, or `mediaRefs` changes
- Any `NuGet.config`/TLS-policy change or new package
- Any method other than safe public GET/HEAD

Read-only invariants:

- `Microsoft.Playwright` remains exactly `1.62.0`.
- The lock remains resolved to `1.62.0`.
- Chromium remains revision `1234`, browser version `151.0.7922.34`.
- Existing route/import fields are preserved; screenshot/evidence fields may only be additive.

Architecture section 17 is the narrow T01 exception to the general C7 ownership rule: only the
tool-local `tools/Husaynia.BaselineCapture/Program.cs` and tool project may change. T02-owned
solution files, central package files, and `src/Husaynia.Web/Program.cs` remain forbidden.

## Current-state evidence driving the rework

- T01 owns only the capture tool, its tests, and baseline evidence
  (`task-plan.md:153-163`).
- Promotion deletes the approved destination and copies directly into it
  (`HusayniaSite/tools/Husaynia.BaselineCapture/Program.cs:114-169`).
- Capture and recapture delete known output directories instead of refusing non-empty staging
  (`HusayniaSite/tools/Husaynia.BaselineCapture/BaselineCaptureService.cs:158-170`;
  `HusayniaSite/tools/Husaynia.BaselineCapture/ScreenshotMatrixRunner.cs:70-74`).
- Pixel changes do not participate in the current pass result
  (`HusayniaSite/tools/Husaynia.BaselineCapture/ScreenshotDeterminismComparer.cs:48-88`).
- Retained comparison evidence has five changed PNG pairs and still says `passed=true`
  (`HusayniaSite/evidence/baseline/screenshot-determinism.json:7-29`).
- Browser launch lacks the binding sandbox, environment, and executable-hash controls
  (`HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:190-194`).
- Navigation retries reuse one context/page and ledger
  (`HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:250-264,422-427,527-555`).
- Quality omits overflow, giant-SVG, loading-only, and ledger-completeness predicates and couples
  `status` to quality (`HusayniaSite/tools/Husaynia.BaselineCapture/PlaywrightScreenshotCapture.cs:453-478`).
- The retained event-detail tablet row is `captured/pass` with page overflow and a scroll width of
  850 for a 768 viewport (`HusayniaSite/evidence/baseline/screenshots.json:2865-2900`).
- Existing artifact tests require every retained row to be quality-pass and accept any nonnegative
  changed-pixel count (`HusayniaSite/tests/Husaynia.BaselineCapture.Tests/BaselineArtifactTests.cs:190-191,307-312`).

## Frozen implementation interfaces

These contracts are fixed before dispatch. The developer may add private helpers but must not alter
the public shapes, exit semantics, file names, or reason semantics below without returning to the
Tech Lead.

### 1. Approved capture profile

`CaptureProfile.cs` owns the only trust/configuration source:

```csharp
public sealed record BrowserIdentityProfile(
    string OperatingSystem,
    string Architecture,
    string PlaywrightVersion,
    string ChromiumRevision,
    string ChromiumVersion,
    string ExecutableSha256);

public sealed record CaptureViewport(string Name, int Width, int Height);
public sealed record StaticResourceRule(string Host, IReadOnlySet<string> ResourceTypes);

public sealed record CaptureProfile(
    string PolicyVersion,
    Uri PrimaryOrigin,
    IReadOnlyDictionary<string, Uri> Representatives,
    IReadOnlyList<CaptureViewport> Viewports,
    IReadOnlyList<StaticResourceRule> StaticResources,
    IReadOnlyList<BrowserIdentityProfile> BrowserIdentities)
{
    public static CaptureProfile Approved { get; }
}
```

`Approved` is immutable and contains exactly:

- origin `https://www.husaynia.org:443`
- the six representative paths and six viewports named in architecture section 17
- static hosts `fonts.googleapis.com`, `fonts.gstatic.com`, `cdnjs.cloudflare.com`,
  `cdn.jsdelivr.net`
- Playwright `1.62.0`, Chromium revision `1234`, browser `151.0.7922.34`

Inventory chooses retained crawl content but cannot add representatives, origins, static hosts, or
resource types.

### 2. Canonical endpoint trust

`TrustedEndpointPolicy.cs` owns common crawl/browser decisions:

```csharp
public interface ITrustedDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed record EndpointRequest(
    Uri Uri,
    string Method,
    string ResourceType,
    bool IsNavigation,
    bool IsMainFrame);

public sealed record EndpointDecision(
    bool Allowed,
    string ReasonCode,
    IPAddress? PinnedAddress);

public sealed class TrustedEndpointPolicy
{
    public static Task<TrustedEndpointPolicy> CreateAsync(
        CaptureProfile profile,
        ITrustedDnsResolver resolver,
        CancellationToken cancellationToken);

    public ValueTask<EndpointDecision> AuthorizeAsync(
        EndpointRequest request,
        CancellationToken cancellationToken);

    public Task AssertDnsSetsUnchangedAsync(CancellationToken cancellationToken);
    public SocketsHttpHandler CreateCrawlHandler();
    public IReadOnlyList<string> ChromiumHostResolverRules { get; }
}
```

Reason codes are stable lower-kebab-case. Credentials, IP literals, HTTP, non-443, wrong hosts,
private/reserved answers, DNS-set changes, rebinding, outside redirects/navigation, unapproved
resource types, mutations, authentication, admin, form, checkout, and payment endpoints fail closed.
The selected address is the ordinal-first address from the sorted complete public set. TLS continues
to validate the original hostname.

### 3. Browser identity and launch

`BrowserExecutableVerifier.cs` exposes:

```csharp
public sealed record BrowserLaunchVerification(
    string ExecutablePath,
    string ExpectedSha256,
    string ActualSha256,
    IReadOnlyDictionary<string, string> ChildEnvironment,
    string WorkingDirectory,
    bool ChromiumSandbox);

public static class BrowserExecutableVerifier
{
    public static BrowserLaunchVerification Verify(
        CaptureProfile profile,
        string executablePath,
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        string emptyRunTempDirectory,
        bool processIsElevated);
}
```

Missing identity, wrong package/revision/version/hash, an elevated process, a non-empty working
directory, or a disallowed environment value refuses launch. `ChromiumSandbox` is always `true`.
The child environment is an explicit OS/runtime/temp allowlist and excludes proxies, credentials,
secrets, CI/repository/workspace variables, cookies, storage state, client certificates,
permissions, and extra headers.

### 4. Request ledger and attempt lifecycle

`RequestLedger.cs` is a thread-safe state machine:

```csharp
public enum RequestTerminalKind { Response, Failure }

public sealed record RequestLedgerSnapshot(
    bool Passed,
    int DecisionCount,
    int TerminalCount,
    int AllowedInFlightCount,
    IReadOnlyList<string> ReasonCodes);

public sealed class RequestLedger
{
    public void RegisterDecision(string requestId, ScreenshotNetworkDecision decision);
    public void RecordResponse(string requestId, int statusCode);
    public void RecordFinished(string requestId);
    public void RecordFailure(string requestId, string reasonCode, string? failure);
    public Task AwaitSnapshotBarrierAsync(TimeSpan quiescentWindow, CancellationToken cancellationToken);
    public RequestLedgerSnapshot Complete();
}
```

Every routed request is registered before allow/block. An allowed response becomes one `response`
terminal only when `RequestFinished` confirms completion; its status is retained. An allowed failure
becomes one `failure` terminal. A blocked decision is immediately represented as one policy failure
terminal; Playwright's later blocked-request notification must not create a duplicate. Missing,
duplicate, untracked, accidentally-continued, unclassified, or post-barrier activity makes
`Complete()` fail.

Each of at most three attempts creates and disposes a fresh non-persistent context, page, and ledger.
A late request/capability through screenshot completion discards the PNG and starts a fresh attempt.

### 5. Completeness and quality

`ScreenshotQualityEvaluator.cs` is pure:

```csharp
public sealed record ScreenshotQualityInput(
    string TemplateKey,
    int RequestedWidth,
    int RequestedHeight,
    ScreenshotRecord Screenshot,
    RequestLedgerSnapshot Ledger,
    int? ControlledRunDonationFormCount);

public sealed record ScreenshotQualityResult(
    string QualityStatus,
    IReadOnlyList<string> ReasonCodes);

public static class ScreenshotQualityEvaluator
{
    public static ScreenshotQualityResult Evaluate(ScreenshotQualityInput input);
}
```

`status="captured"` means the PNG, metrics, decision log, and provenance were safely completed.
Quality does not alter capture status. `qualityStatus="pass"` requires every binding predicate.
Any page-level overflow (`documentScrollWidth > actualViewportWidth`) always fails quality. A table
exception applies only when a positively identified table scroll container itself is bounded inside
the viewport; it never waives document/page overflow. The current
`event-detail|tablet-768x1024` row is therefore `captured/fail`.

Existing `screenshotCount` and `successfulScreenshotCount` remain and represent total rows and
capture-complete rows. Add `qualityPassScreenshotCount`. Add `qualityReasonCodes` to each screenshot.
The 36-row matrix always finishes when safe to continue; any capture or quality failure exits `1`.

### 6. Determinism and visual review

`ScreenshotDeterminismComparer.cs` keeps the command name but expands its contract:

```csharp
public static Task<int> CompareAsync(
    string runAEvidenceDirectory,
    string runBEvidenceDirectory,
    string outputPath,
    string? visualReviewPath,
    CancellationToken cancellationToken);
```

It requires identical unique approved 36-key sets, compatible browser/profile provenance,
`captured/pass` rows, equal dimensions, and equal required DOM metrics. Equal PNG hashes return `0`.
Changed PNGs generate unmasked `screenshot-diffs/<safe-key>.png` artifacts plus raw differing-pixel
count, ratio, and bounds, then return `3` until every changed key has a hash-bound accepted review.
Missing, stale, rejected, or hash-mismatched review returns `1`. DOM equality never overrides pixel
changes. Optional comparison-only masks are versioned/keyed rectangles with rationale; unmasked
diffs remain mandatory.

`screenshot-visual-review.json` entries contain exactly: key, run-A hash, run-B hash, unmasked diff
path, optional mask version/rectangles, reviewer, reviewed UTC, decision `accept|reject`, rationale.

### 7. Evidence validation/finalization

`BaselineEvidenceValidator.cs` exposes:

```csharp
public sealed record BaselineFileManifestEntry(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record BaselineValidationResult(
    bool Passed,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<BaselineFileManifestEntry> Files);

public sealed class BaselineEvidenceValidator
{
    public Task<BaselineValidationResult> ValidateReadOnlyAsync(
        string evidenceDirectory,
        CancellationToken cancellationToken);

    public Task<BaselineValidationResult> FinalizeAsync(
        string evidenceDirectory,
        CancellationToken cancellationToken);
}
```

Read-only validation checks required artifacts, unique paths, no symlink/reparse traversal, schemas,
lineage, profile/trust/browser provenance, exact key sets, captured and quality counts,
determinism/review, file lengths/hashes, and no extra unchecked file. `FinalizeAsync` is the only
finalization mutation: validate, write/replace `baseline-verification.json`, update only the README
gate/count section, write `checksums.sha256` last, then revalidate. An unchanged already sealed tree
is not rewritten, making repeated verify deterministic.

### 8. Transactional promotion

`BaselinePromotionService.cs` owns:

```csharp
public enum PromotionFaultPoint
{
    BeforeCopy,
    AfterCopyBeforeVerify,
    AfterCopyVerify,
    AfterBackupMove,
    AfterSwap,
    AfterPostVerify,
    BeforeRestore,
    AfterRestore
}

public interface IPromotionFileSystem
{
    IDisposable AcquireExclusiveLock(string lockPath);
    IReadOnlyList<BaselineFileManifestEntry> EnumerateTree(string root);
    Task CopyTreeCreateNewAsync(string source, string destination, CancellationToken cancellationToken);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path);
    bool DirectoryExists(string path);
}

public sealed class BaselinePromotionService
{
    public Task<int> PromoteAsync(
        string source,
        string destination,
        CancellationToken cancellationToken);
}
```

Promotion uses sibling lock, candidate-copy, copy verification, prior-baseline rename, swap,
destination post-verification, and automatic restore. Source mutation is detected. Ambiguous crash
state is preserved and refused. Restore failure returns `4` and prints retained recovery paths.
Lock contention is safety refusal `2`; other validation/copy/swap failures return `1`.

### 9. CLI and output semantics

Frozen commands:

```text
capture --no-submit --output RUN_A [--base-url https://www.husaynia.org/]
recapture-screenshots --no-submit --evidence RUN_A --output RUN_B
compare-screenshot-metrics --first-screenshots RUN_A/screenshots.json
  --first-provenance RUN_A/screenshot-capture-provenance.json
  --second-evidence RUN_B --output RUN_A/screenshot-determinism.json
  [--review RUN_A/screenshot-visual-review.json]
verify --evidence RUN_A
promote --approved --from RUN_A --to evidence/baseline
```

`capture` and `recapture-screenshots` require a nonexistent or empty output directory and never
delete prior evidence. Run B reads Run A but never mutates it. Compare writes only its declared
determinism/diff files. Verify writes only finalization files. Exit codes are: `0` pass, `1`
capture/quality/verification failure, `2` usage or safety/trust refusal, `3` visual review required,
`4` restore failure.

## A-I ownership map

One developer owns the union below in T01-R01. No other implementation task edits these files.
Production file names in this table are relative to
`HusayniaSite/tools/Husaynia.BaselineCapture/`; test file names are relative to
`HusayniaSite/tests/Husaynia.BaselineCapture.Tests/`.

| Rework | Exact production files | Exact test files | Required result |
|---|---|---|---|
| A | `Program.cs`, `CaptureIO.cs`, `Models.cs`, new `BaselineEvidenceValidator.cs`, new `BaselinePromotionService.cs` | new `BaselineEvidenceValidatorTests.cs`, new `BaselinePromotionServiceTests.cs`, `RobotsPolicyTests.cs` | Transactional, locked, recoverable promotion with complete pre/post verification. |
| B | `Program.cs`, `Models.cs`, `ScreenshotDeterminismComparer.cs`, `CaptureIO.cs` | new `ScreenshotDeterminismComparerTests.cs`, `BaselineArtifactTests.cs` | Exact unique 36-key comparison; changed pixels require retained diff and accepted hash-bound review. |
| C | `BaselineCaptureService.cs`, `ScreenshotMatrixRunner.cs`, `CaptureIO.cs`, `Models.cs`, new `BaselineEvidenceValidator.cs`, new tool `README.md` | `BaselineArtifactTests.cs`, new `BaselineEvidenceValidatorTests.cs` | Coherent lineage, truthful separate counts, deterministic finalization, exact checksum coverage. |
| D | `Program.cs`, `BaselineCaptureService.cs`, `PlaywrightScreenshotCapture.cs`, new `CaptureProfile.cs`, new `TrustedEndpointPolicy.cs` | `CaptureLogicTests.cs`, new `TrustedEndpointPolicyTests.cs`, `PlaywrightLiveCaptureTests.cs` | Canonical-origin/public-DNS pinning and fail-closed redirect/navigation policy shared by crawl and browser. |
| E | `PlaywrightScreenshotCapture.cs`, `Models.cs`, `Husaynia.BaselineCapture.csproj`, new `BrowserExecutableVerifier.cs`, new `chromium-executable-sha256.json`, new tool `README.md` | new `BrowserExecutableVerifierTests.cs`, `CaptureLogicTests.cs` | Exact package/revision/version/hash, sandbox, sanitized environment, empty temp cwd, least privilege. |
| F | `PlaywrightScreenshotCapture.cs`, `ScreenshotMatrixRunner.cs`, `Models.cs`, new `RequestLedger.cs` | new `ScreenshotAttemptLifecycleTests.cs`, `PlaywrightLiveCaptureTests.cs` | Fresh attempt state, bounded retry/readiness, snapshot barrier, safe stable donation observation. |
| G | `Program.cs`, `PlaywrightScreenshotCapture.cs`, `ScreenshotMatrixRunner.cs`, `Models.cs`, new `ScreenshotQualityEvaluator.cs`, new `BaselineEvidenceValidator.cs` | new `ScreenshotQualityEvaluatorTests.cs`, `BaselineArtifactTests.cs`, `PlaywrightLiveCaptureTests.cs` | Completeness and quality separated; any page overflow is retained, quality-fail, nonzero, non-promotable. |
| H | `PlaywrightScreenshotCapture.cs`, `Models.cs`, new `RequestLedger.cs`, new `TrustedEndpointPolicy.cs` | new `RequestLedgerTests.cs`, `CaptureLogicTests.cs`, `PlaywrightLiveCaptureTests.cs` | Exactly one decision and terminal per request; every capability attempt recorded; malformed ledger fails. |
| I | All files above; `packages.lock.json` and `NuGet.config` are read-only invariants | All existing/new T01 tests | Locked restore, Release build, focused tests, controlled captures, review, verify, fault suite, promotion, post-tests. |

The single developer also owns updates to existing `ScreenshotMatrixRunner.cs`,
`BaselineCaptureService.cs`, `CaptureIO.cs`, `Models.cs`, `Program.cs`, and all listed tests needed to
integrate the frozen contracts. `RobotsPolicy.cs`, the test project file, `packages.lock.json`, and
`NuGet.config` are not expected to change. No documentation or test-writing fan-out is authorized:
tool README and tests are coupled to the frozen CLI/evidence contracts and remain with the developer.

## Dependency-ordered tasks

T01-R00  Freeze ADR-009 A-I interfaces and execution plan
    owner:        tech-lead
    objective:    Publish the contracts, ownership map, dependency graph, DoD, tests, and gate order without modifying shared mission state.
    files:        `.ai-org/missions/2026-08-14-husaynia-site-modernization/t01-adr009-rework.md`; T01 note only in `.ai-org/missions/2026-08-14-husaynia-site-modernization/task-plan.md`
    depends_on:   -
    parallel_ok:  no
    exit_criteria: This file exists; the parent plan has only the terse T01 rework note; `.ai-org/active-mission.json` is untouched.
    status:       DONE

T01-R01  Implement and self-test ADR-009 A-H
    owner:        developer
    objective:    Implement all frozen capture, trust, launch, ledger, quality, comparison, validation, and transactional-promotion contracts with focused negative and fault tests.
    files:        `HusayniaSite/tools/Husaynia.BaselineCapture/{Program.cs,BaselineCaptureService.cs,CaptureIO.cs,Models.cs,PlaywrightScreenshotCapture.cs,ScreenshotDeterminismComparer.cs,ScreenshotMatrixRunner.cs,Husaynia.BaselineCapture.csproj,CaptureProfile.cs,TrustedEndpointPolicy.cs,BrowserExecutableVerifier.cs,RequestLedger.cs,ScreenshotQualityEvaluator.cs,BaselineEvidenceValidator.cs,BaselinePromotionService.cs,chromium-executable-sha256.json,README.md}`; `HusayniaSite/tests/Husaynia.BaselineCapture.Tests/{CaptureLogicTests.cs,BaselineArtifactTests.cs,PlaywrightLiveCaptureTests.cs,RobotsPolicyTests.cs,TrustedEndpointPolicyTests.cs,BrowserExecutableVerifierTests.cs,RequestLedgerTests.cs,ScreenshotQualityEvaluatorTests.cs,ScreenshotAttemptLifecycleTests.cs,ScreenshotDeterminismComparerTests.cs,BaselineEvidenceValidatorTests.cs,BaselinePromotionServiceTests.cs}`
    depends_on:   T01-R00
    parallel_ok:  no
    exit_criteria: Locked restores and Release build pass; all `Category!=Live` tests pass including every matrix row below; pins/lock/NuGet policy are unchanged; no forbidden path changed; developer does not write staging or approved evidence.
    status:       BLOCKED — route sitemap evidence-integrity remediation passes 27 focused route tests and 281/281 full non-live tests; strict current-source Release builds pass; required locked restores remain blocked by external NuGet NU1900 vulnerability-index connectivity.

T01-R02  Independently validate candidate captures
    owner:        test-engineer
    objective:    Execute locked restore/build/focused tests and two clean controlled captures, then compare them without promoting.
    files:        `HusayniaSite/evidence/staging/run-a/**` except `screenshot-visual-review.json`; `HusayniaSite/evidence/staging/run-b/**`; source and test files read-only
    depends_on:   T01-R01
    parallel_ok:  yes
    exit_criteria: Focused tests pass; Run A and Run B are distinct clean directories with identical unique 36-key sets; both are 36/36 captured and 36/36 quality-pass; comparison exits `0`, or the expected `3` with complete unmasked diffs and activates T01-R05; no promotion occurs.
    status:       BLOCKED — two clean 36-key run attempts and comparison are retained, but approved-host DNS answer rotation caused fail-closed `dns-set-changed` rows (best Run A 3/36 captured/quality; Run B 1/36).

T01-R03  Security review the trust and promotion surface
    owner:        security-engineer
    objective:    Independently audit canonical trust, DNS pin/rebinding defense, sandbox/hash/environment controls, redaction, GET/HEAD safety, evidence validation, and recovery behavior.
    files:        read-only review of T01-R01 files and focused test results
    depends_on:   T01-R01
    parallel_ok:  yes
    exit_criteria: APPROVED with zero unresolved Critical/High findings; any finding identifies exact file/line and returns implementation to T01-R01.
    status:       BLOCKED — independent security review was not executed; official R01 restore/build evidence remains blocked by external NuGet TLS failure.

T01-R04  Code-review the consolidated implementation
    owner:        code-reviewer
    objective:    Verify architectural adherence, backward-compatible evidence fields, bounded lifecycle, error/exit semantics, test strength, and absence of forbidden scope changes.
    files:        read-only review of T01-R01 files and focused test results
    depends_on:   T01-R01
    parallel_ok:  yes
    exit_criteria: APPROVED with exact evidence; CHANGES_REQUIRED returns implementation to T01-R01 and invalidates affected validation.
    status:       BLOCKED — the latest code review found the omitted route `Sitemap` semantic comparison; the defect and the broader frozen-route omission class are remediated, and an independent code-review regate is pending.

T01-R05  Review changed screenshot pixels
    owner:        frontend-specialist
    objective:    If and only if comparison exits `3`, inspect every retained unmasked diff and create a hash-bound accept/reject review without waiving quality defects.
    files:        `HusayniaSite/evidence/staging/run-a/screenshot-visual-review.json` only
    depends_on:   T01-R02
    parallel_ok:  no
    exit_criteria: Every changed key has exact A/B hashes, diff reference, reviewer, timestamp, decision, and rationale; no overflow or other quality failure is accepted. Mark N/A when comparison exits `0`.
    status:       BLOCKED — current comparison exits `1` with no pixel-diff review eligible; await complete quality-passing Run A and Run B.

T01-R06  Seal, promote, and post-validate evidence
    owner:        test-engineer
    objective:    Rerun comparison with any review, finalize Run A, run artifact tests against staging, transactionally promote, and rerun artifact tests against the approved baseline.
    files:        `HusayniaSite/evidence/staging/run-a/baseline-verification.json`; gate/count section of its `README.md`; its `checksums.sha256`; `HusayniaSite/evidence/baseline/**`; source and test files read-only
    depends_on:   T01-R02,T01-R03,T01-R04,T01-R05
    parallel_ok:  no
    exit_criteria: Compare returns `0`; verify passes twice without changing the sealed tree; all staging artifact tests pass; real promotion succeeds under lock; destination full-tree hash equals the verified source; all post-promotion artifact tests pass; no recovery directory remains after success.
    status:       BLOCKED — capture, security, code-review, and conditional visual-review gates are incomplete; no verify or promotion is permitted.

T01-R07  Judge the T01 ADR-009 rework
    owner:        engineering-judge
    objective:    Decide whether every mission-specific DoD item is proven by executed evidence rather than agent claims.
    files:        read-only review of implementation, gate reports, staging lineage, and promoted baseline
    depends_on:   T01-R06
    parallel_ok:  no
    exit_criteria: APPROVED only when every DoD item below has reproducible evidence; otherwise REJECTED with the failed item and required T01-R01 rework.
    status:       BLOCKED — sealing/promotion and all independent upstream gates remain incomplete.

## Execution waves

`wave 0: T01-R00 (done) -> wave 1: T01-R01 -> wave 2: T01-R02, T01-R03, T01-R04
(parallel) -> wave 3: T01-R05 (conditional; N/A on zero changed pixels) -> wave 4: T01-R06
-> wave 5: T01-R07`

Any failed implementation, test, security, or review gate returns to the same T01-R01 developer.
After rework, rerun every invalidated downstream task; do not patch generated evidence manually.

## Mission-specific Definition of Done

1. Only the allowed T01 files changed; `.ai-org/active-mission.json`, T02, solution/contracts/backend,
   prior apps, production, and Git history are untouched.
2. Playwright is exactly `1.62.0`; Chromium is revision `1234` and version `151.0.7922.34`; no
   package or NuGet/TLS-policy change occurred.
3. Crawl and browser traffic share canonical-origin, public-DNS, pinning, redirect, method, and
   resource decisions; all trust and rebinding negative tests pass.
4. Chromium is hash-verified before launch, sandboxed, non-elevated, launched from an empty temp
   directory with a sanitized environment and no ambient proxy/credential/cookie/workspace input.
5. Every attempt uses a fresh context/page/ledger; readiness, terminal drain, 250ms quiescence, late
   activity discard, retry limits, and donation non-interaction are proven.
6. Every request has exactly one decision and one terminal; every blocked capability is recorded;
   untracked, duplicate, missing, unclassified, or accidentally continued activity fails closed.
7. Both controlled runs have the exact unique 36 keys. Completeness and quality are separate:
   overflow remains `captured/fail`, exits `1`, and cannot be promoted. Promotion candidate is
   36/36 captured and 36/36 quality-pass.
8. Changed pixels cannot pass on DOM metrics. Every changed key has an unmasked diff and valid
   accepted hash-bound review, or comparison remains exit `3`/`1`.
9. Verification proves one coherent lineage and exact sealed-tree checksums with no stale, extra,
   symlink/reparse, or unchecked file; repeated verify is deterministic.
10. Promotion fault tests prove exact prior-tree preservation/restoration, lock exclusion,
    source-mutation detection, crash recovery, and exit `4` recovery preservation.
11. Locked restore, Release build, focused tests, two controlled captures, comparison/review,
    staging verification/tests, real promotion, and post-promotion tests all pass.
12. Independent security review and code review approve; the engineering judge approves the
    evidence-backed result.

## Rework and fault-injection test matrix

| ID | Area | Injection/case | Expected assertion |
|---|---|---|---|
| A01 | Promotion | Validator fails before copy | Exit `1`; approved baseline hash tree unchanged; no backup move. |
| A02 | Promotion | Fault before copy | Baseline unchanged; lock released; candidate path retained only when needed for diagnosis. |
| A03 | Promotion | Fault after copy, before copy verification | Baseline unchanged; incomplete sibling candidate cannot be promoted. |
| A04 | Promotion | Source mutates during copy | Copy verification fails; approved baseline unchanged. |
| A05 | Promotion | Fault after copy verification | Approved baseline unchanged; verified candidate is not mistaken for approved. |
| A06 | Promotion | Fault after backup move | Exact prior tree restored; failed candidate preserved/removed according to unambiguous state. |
| A07 | Promotion | Fault after swap | Candidate moved aside; exact prior tree restored. |
| A08 | Promotion | Destination post-verification fails | Exit `1`; exact prior tree restored; corrupt candidate retained outside `baseline`. |
| A09 | Promotion | Restore operation fails | Exit `4`; baseline/prior/candidate recovery paths retained and printed; nothing deleted. |
| A10 | Promotion | Second concurrent promoter | Second process gets `promotion-lock-held`, exits `2`, and changes no tree. |
| A11 | Promotion | Interrupted-state recovery | Unambiguous prior state recovers; multiple/ambiguous states are preserved and refused. |
| A12 | Promotion | Symlink/reparse/path escape/duplicate path | Preflight fails before copy; baseline unchanged. |
| B01 | Determinism | Missing, extra, or duplicate key in either run | Exit `1`; exact approved 36-key set reported. |
| B02 | Determinism | Provenance, dimension, metric, status, or quality mismatch | Exit `1`; no visual acceptance can override it. |
| B03 | Determinism | Equal PNG hashes | Exit `0`; no review file required. |
| B04 | Determinism | One or more changed PNG hashes | Unmasked diff and raw statistics retained; exit `3` without review. |
| B05 | Determinism | Missing/stale/rejected/wrong-hash review | Exit `1`; changed key and reason reported. |
| B06 | Determinism | Accepted exact hash-bound review | Exit `0` only when all nonvisual gates already pass. |
| C01 | Evidence | Required artifact missing/empty or extra unchecked file | Read-only validation fails. |
| C02 | Evidence | Checksum missing, extra, duplicated, malformed, or wrong | Validation fails with exact relative path. |
| C03 | Evidence | Capture/determinism lineage mismatch or stale artifact | Validation fails; no finalization/promotion. |
| C04 | Evidence | Verify repeated on unchanged sealed tree | Byte-identical `baseline-verification.json`, README gate section, and checksums. |
| D01 | Trust | Credentials, IP literal, HTTP, wrong host, or non-443 | Safety refusal `2`; no request/browser launch. |
| D02 | Trust | Loopback/private/link-local/unspecified/multicast/CGNAT/documentation/benchmark/reserved IPv4/IPv6 | Stable reason code; request refused. |
| D03 | Trust | DNS answer set changes before an attempt | `dns-set-changed`; attempt/run fails. |
| D04 | Trust | Rebinding after authorization | Pinned connect remains on selected public address; mismatch fails. |
| D05 | Trust | Outside redirect, frame navigation, or post-load navigation | Blocked and terminally logged; capture quality fails if completeness is affected. |
| E01 | Browser | Missing OS/arch hash profile or executable hash mismatch | Exit `1` before `LaunchAsync`. |
| E02 | Browser | Wrong package/revision/version | Exit `1`; provenance records expected/actual identity. |
| E03 | Browser | Elevated process, sandbox false, dirty cwd, proxy/secret/workspace env | Safety refusal before launch. |
| E04 | Browser | Approved launch | Provenance proves sandbox, hash, environment policy, temp cwd, and pinned endpoints. |
| F01 | Attempts | First/second navigation attempt fails | Next attempt has new context, page, cookies/cache, frames, and empty ledger. |
| F02 | Attempts | Fourth attempt requested or timeout exceeded | Never occurs; max 3 and 10/30/45-second bounds hold. |
| F03 | Snapshot | Request terminal missing or decision arrives during 250ms window | PNG discarded; fresh retry; all attempts retained. |
| F04 | Snapshot | Request/capability occurs during screenshot | PNG discarded; attempt fails/retries. |
| F05 | Donation | Form count changes between controlled runs or shell absent | Donation row quality-fails; no click/focus/type/submit/payment request. |
| G01 | Quality | Any document/page horizontal overflow | `status=captured`, `qualityStatus=fail`, reason `horizontal-overflow`, run exits `1`. |
| G02 | Quality | Current event-detail 768x1024 regression fixture | Captured evidence retained; quality fail; validator/promotion refuse it. |
| G03 | Quality | Bounded internal scrollable table with no document overflow | Does not create page-overflow failure; positive identification is retained. |
| G04 | Quality | Giant SVG, loading-only, viewport/DPR/PNG mismatch, missing landmarks, unreadiness, incomplete ledger | Corresponding stable quality reason; nonzero run. |
| H01 | Ledger | Allowed response then finish | Exactly one response terminal with retained status; in-flight reaches zero. |
| H02 | Ledger | Allowed request failure | Exactly one failure terminal. |
| H03 | Ledger | Blocked request plus Playwright failure callback | Exactly one policy failure terminal, never duplicate. |
| H04 | Ledger | Untracked/duplicate/missing/unclassified/continued-blocked request | Attempt fails closed with stable reason. |
| H05 | Capabilities | WebSocket/EventSource/sendBeacon/service-worker/form/download attempt | Every attempt blocked, redacted, correlated, and retained. |
| I01 | Scope/pins | Forbidden file or dependency/pin change | Gate fails before live capture. |
| I02 | Full flow | All required commands complete | Only then may Run A be sealed and promoted. |

Tests must tighten or add coverage; existing assertions may not be removed, skipped, broadened, or
converted into weaker smoke checks. Live-site volatility is not a reason to waive trust, quality,
or determinism.

## Gate commands and stop conditions

Run from `HusayniaSite` in this order:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode
$lock = Get-Content tools/Husaynia.BaselineCapture/packages.lock.json -Raw | ConvertFrom-Json
if ($lock.dependencies.'net10.0'.'Microsoft.Playwright'.resolved -ne '1.62.0') { exit 1 }
if (-not (Select-String -Path tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -Pattern 'Version="\[1\.62\.0\]"' -Quiet)) { exit 1 }
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore --filter "Category!=Live"

dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --max-duration-minutes 60 --output evidence/staging/run-a
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- recapture-screenshots --no-submit --evidence evidence/staging/run-a --output evidence/staging/run-b
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- compare-screenshot-metrics --first-screenshots evidence/staging/run-a/screenshots.json --first-provenance evidence/staging/run-a/screenshot-capture-provenance.json --second-evidence evidence/staging/run-b --output evidence/staging/run-a/screenshot-determinism.json
```

If comparison exits `3`, stop. T01-R05 writes the independent review, then rerun:

```powershell
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- compare-screenshot-metrics --first-screenshots evidence/staging/run-a/screenshots.json --first-provenance evidence/staging/run-a/screenshot-capture-provenance.json --second-evidence evidence/staging/run-b --output evidence/staging/run-a/screenshot-determinism.json --review evidence/staging/run-a/screenshot-visual-review.json
```

Continue only on comparison exit `0` and approved security/code reviews:

```powershell
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- verify --evidence evidence/staging/run-a
$sealedFiles = @(
  'evidence/staging/run-a/baseline-verification.json',
  'evidence/staging/run-a/README.md',
  'evidence/staging/run-a/checksums.sha256'
)
$sealedBefore = $sealedFiles | ForEach-Object { (Get-FileHash -Algorithm SHA256 $_).Hash }
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- verify --evidence evidence/staging/run-a
$sealedAfter = $sealedFiles | ForEach-Object { (Get-FileHash -Algorithm SHA256 $_).Hash }
if (Compare-Object $sealedBefore $sealedAfter) { exit 1 }
$env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/staging/run-a)
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- promote --approved --from evidence/staging/run-a --to evidence/baseline
$env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/baseline)
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
```

Stop without promotion on any exit `1`, `2`, or unreviewed `3`. Exit `4` is an incident: preserve
every printed recovery path and do not retry or delete anything until the prior baseline is manually
confirmed.

## T01-R01 implementation evidence — 2026-08-16 UTC

- Implemented ADR-009 A-I only beneath the T01 tool/test paths. Added canonical profile and DNS
  pinning, prelaunch Chromium hash/sandbox/environment verification, fresh attempt contexts/pages,
  terminal request ledger, pure quality evaluation, pixel-diff/review enforcement, deterministic
  evidence finalization, and locked recoverable promotion. Playwright remains exactly `1.62.0`;
  Chromium remains revision `1234`, version `151.0.7922.34`, Windows x64 executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- Required locked restore/build commands were executed without changing NuGet/TLS/audit policy.
  Both tool restore attempts and the Release `-warnaserror` build failed with `NU1900` because
  `https://api.nuget.org/v3/index.json` returned a TLS `HandshakeFailure`; the test project itself
  restored in 206 ms but its tool project reference remained blocked. Exact `dotnet test` commands
  consequently failed at the same audit gate.
- A direct pinned-reference C# compile plus `dotnet vstest` was used only as a diagnostic fallback,
  not as a substitute for the blocked gate: the final `Category!=Live` rerun passed 113/113 in
  26 seconds; the final full-suite rerun, including live DNS-pinned capture, passed 114/114 in
  28 seconds. Fault coverage includes
  invalid/pre-copy/copy/source-mutation/backup/swap/post-verify/restore/lock promotion paths,
  stale visual review, exact key sets, reserved DNS, sanitized launch, ledger terminality,
  overflow, donation count, and non-empty output refusal.
- `playwright.ps1 install --no-shell chromium` exited `0`. Exact pins were rechecked:
  project `[1.62.0]`, tool lock requested/resolved `[1.62.0, 1.62.0]`/`1.62.0`, and test lock
  transitive `1.62.0`.
- Retained run-specific evidence (no promotion):
  - `evidence/baseline-runs/adr009-20260816T0601Z-blocked-dns/`: 6 partial sitemap/robots files from
    the first immediate `dns-set-changed` refusal.
  - `evidence/baseline-runs/adr009-20260816T0620Z-run-a/`: 819 files, 180/180 route responses,
    617/619 assets, 0/36 captured and 0/36 quality-pass, 660.7 seconds; all screenshot attempts
    refused after approved static-host DNS answers rotated.
  - `evidence/baseline-runs/adr009-20260816T0645Z-run-a/`: 182 files, exact 36 keys, 3/36 captured
    and quality-pass, 716.8 seconds; later rows failed `dns-set-changed`. Its comparison names
    Run B and fails with 35 capture/quality differences.
  - `evidence/baseline-runs/adr009-20260816T0645Z-run-b/`: 8 files, exact 36 keys, 1/36 captured
    and quality-pass, 112.6 seconds; remaining rows failed `dns-set-changed`.
- `verify` on the best Run A exited `1`; staging artifact validation failed 5/18 tests and passed
  13/18, as required for an incomplete candidate. No `baseline-verification.json` was written and
  no promotion command was run. `evidence/baseline` and `.ai-org/active-mission.json` remain
  untouched.

## Risks and sequencing decisions

- **Collision risk:** eliminated for implementation by one developer owning all source/tests/docs.
  Validators do not edit source. The visual reviewer owns only one reserved review file.
- **Sequencing risk:** promotion is impossible before candidate tests, security review, code review,
  and any visual review. Generated baseline evidence is last.
- **Live DNS/content risk:** legitimate rotation or page variance may block the run. Rerun later;
  never weaken public-address, quality, or review rules.
- **Browser-profile risk:** the canonical Windows/x64 executable hash must be populated from the
  package-matched installed browser and independently verified. Missing profiles fail closed.
- **Current source-page risk:** the known event-tablet overflow will intentionally block promotion
  until the public source is no longer overflowing. Captured diagnostic evidence remains useful but
  is not approvable.

## First independent-gate defect remediation — 2026-08-16 UTC

Status: IMPLEMENTATION AND DIRECT GATES PASS; OFFICIAL/INDEPENDENT GATES BLOCKED; NO PROMOTION.

- Remediated all first-gate findings in T01-owned source/tests: Playwright driver and Chromium now
  start only after browser identity/hash/elevation/temp preflight and inside one serialized scrubbed
  process environment/temp CWD; the behavioral child probe proves no ambient `PATH`,
  `NODE_OPTIONS`, proxy, secret, or workspace inheritance.
- Evidence verification now parses, CRC-checks, bounded-decompresses, hashes, measures, and validates
  every actual PNG; recomputes quality; requires exact approved tuples and complete per-attempt
  request ledgers; and rejects corrupt/reused/stale/extra PNGs, stale diff files, malformed/empty
  seals, orphan terminals, missing ledgers, and incomplete verification coverage.
- Pixel comparison independently validates every Run A/Run B PNG before changed-key selection and
  rejects stale metadata hashes. Its local decode browser is network-denied.
- Promotion preserves/refuses ambiguous directory or file-shaped crash artifacts. Interrupted and
  post-swap restore/verification failures return `4` and report retained recovery paths.
- The approved profile is deeply immutable. Address tests cover IPv4/IPv6 private, link-local,
  mapped/compatible, NAT64, site-local, documentation, benchmark, ORCHID, transition, multicast,
  and reserved ranges. Browser egress adds DNS deny-all, DoH/QUIC/WebTransport/WebRTC/direct-socket
  blocking and terminal capability ledger evidence.
- Response evidence retains only the explicit safe-header allowlist. Query names/values, signed
  credential patterns, malformed/double-encoded names, fragments, and relative `Location` values
  are centrally rejected or redacted. Raw exception messages are no longer persisted.
- Permanent behavioral/fault regressions cover fresh context/page/ledger isolation, late activity,
  event-detail tablet overflow, malformed seal, orphan terminal, mutable profile, stale PNG hash,
  stale allowed diff, restore failure, and ambiguous/file-shaped recovery.

Executed evidence from `HusayniaSite`:

- Official locked restores, without audit/TLS suppression:
  - `dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode`
    -> exit `1`, `NU1900`, vulnerability service index `https://api.nuget.org/v3/index.json`
    unavailable (6.29 s).
  - `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode`
    -> exit `1`, the same `NU1900` for tool and test projects (6.39 s).
- Final official Release warnings-as-errors attempts:
  - `dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror`
    -> exit `1`, only `NU1900` before compilation (0 warnings, 1 error, 0.89 s).
  - `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror`
    -> exit `1`, only `NU1900` before compilation (0 warnings, 1 error, 1.30 s).
- Existing-cache diagnostic compilation used direct Roslyn with .NET 10 references, cached
  Playwright/xUnit assemblies, `/warnaserror+`, and only `/nowarn:1701` for the diagnostic
  netstandard assembly-unification warning. It did not suppress NuGet audit and passed.
- Focused newest hardening regressions: 5/5 passed in 9 s; bounded option regressions: 5/5 passed
  in 37 ms.
- Final current-source full suite:
  `dotnet vstest tests/Husaynia.BaselineCapture.Tests/bin/Release/net10.0/Husaynia.BaselineCapture.Tests.dll --TestAdapterPath:<cached-xunit-adapter>`
  -> 160 passed, 0 failed, 0 skipped in 5 m 9 s, including the live browser capture.
- `playwright.ps1 install --no-shell chromium` exited `0`. Installed Chromium SHA-256 is
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- Three short DNS probes resolved all five approved hosts each round to public A/AAAA addresses.
  CDN answer rotation remained visible and fail-closed behavior was not weakened.
- Pins remain Playwright `1.62.0`, Chromium revision `1234`, version `151.0.7922.34`; no
  `NuGetAudit`, `NoWarn`, or failed-source suppression exists in the T01 projects.

Gate verdicts:

- Direct test gate: PASS (160/160 final current-source tests).
- Direct security audit: PASS, 0 Critical / 0 High unresolved; official dependency vulnerability
  lookup remains environmentally unavailable.
- Direct code review: APPROVED after rework.
- Independent test/security/code-review agents: BLOCKED by the session's maximum subagent depth;
  each requested gate returned `Maximum sub-agent depth of 4 reached`.
- Independent engineering-judge agent: BLOCKED by the same maximum subagent depth.
- Release judgment: BLOCKED because official NuGet-audited compilation and genuinely independent
  gates are not reproducible in this session. No full two-run recapture, seal, promotion, baseline
  mutation, or production action was performed.

## Latest independent-gate root-cause remediation — 2026-08-16

Status: IMPLEMENTATION AND SAFE CURRENT-SOURCE TESTS PASS; OFFICIAL RESTORE/BUILD AND INDEPENDENT
REGATES PENDING; NO LIVE CAPTURE OR PROMOTION.

- Evidence finalization now fails closed on required root fields and schema versions, complete
  decision/terminal semantics (including mandatory response status), exact raw/asset reference
  inventory, every tree PNG (including asset PNGs), exact checksum/verification coverage, and a
  36-key determinism hash inventory that binds the retained Run-A PNG hashes to determinism and
  visual review.
- URL evidence removes absolute and network-path user-info and redacts absolute/relative Location
  query values. The production `TrustedEndpointPolicy` now shares the canonical sensitive-endpoint
  classifier and blocks GET/HEAD form-plugin, registration/signup, OAuth, password reset,
  login/account, checkout, and payment endpoint classes.
- Promotion now returns incident exit `4` while preserving every ambiguous recovery artifact.
  Production CLI promotion accepts only the canonical repository `evidence/baseline` destination;
  the service remains path-flexible for deterministic fault tests. Once the destination post-verify
  commits, prior cleanup failure retains the verified destination and remaining prior recovery path
  and never rolls back from a possibly partial prior deletion.
- Browser attempts behaviorally reject request/context/DOM cookie use, bound capability console
  messages by allowlist/count/size/URL length with fixed malformed failure, use tested async
  resource lifetimes, and discard PNG bytes when post-barrier activity invalidates the ledger.
  Sitemap, route, and asset discovery counts are bounded before task-array allocation. Comparison
  validates PNGs within a per-run aggregate bound and loads changed pairs incrementally.
- Permanent regressions cover missing route schema, response terminal without status, stale Run-A
  hash binding, stale/malformed asset PNGs, absolute/network-path user-info redaction, ambiguous
  exit `4`, verified-destination preservation on prior cleanup failure, canonical CLI destination,
  production endpoint classes, cookie/capability bounds, discovery/PNG aggregate limits, fresh
  lifecycle identity/disposal, and late-activity PNG discard.

Executed evidence from `HusayniaSite`:

- Final direct cached-reference compilation used .NET SDK `10.0.400` Roslyn, .NET 10 reference
  assemblies, cached Playwright/xUnit assemblies, `/warnaserror+`, and only diagnostic
  `/nowarn:1701` for the known netstandard assembly-unification warning. Tool and test compilation
  each exited `0`; this is existing-cache diagnostic evidence, not an audited restore substitute.
- Final focused production-policy/lifecycle/sanitizer gate:
  `dotnet vstest tests/Husaynia.BaselineCapture.Tests/bin/Release/net10.0/Husaynia.BaselineCapture.Tests.dll --TestAdapterPath:<cached-xunit-adapter> --TestCaseFilter:"FullyQualifiedName~ScreenshotAttemptLifecycleTests|FullyQualifiedName~CaptureLogicTests|FullyQualifiedName~TrustedEndpointPolicyTests"`
  -> 108 passed, 0 failed, 0 skipped in 244 ms.
- Final full safe current-source gate (the binding no-live-traffic constraint excludes the one
  `Category=Live` test):
  `dotnet vstest tests/Husaynia.BaselineCapture.Tests/bin/Release/net10.0/Husaynia.BaselineCapture.Tests.dll --TestAdapterPath:<cached-xunit-adapter> --TestCaseFilter:"Category!=Live"`
  -> 183 passed, 0 failed, 0 skipped in 6 m 16 s.
- Official locked restores were attempted without suppression:
  - `dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode`
    -> exit `1`, only `NU1900`; NuGet vulnerability service index unavailable (6.41 s).
  - `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode`
    -> exit `1`, only `NU1900` for tool/test; vulnerability service index unavailable (6.6 s).
- Official Release warnings-as-errors builds were attempted without suppression:
  - `dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror`
    -> exit `1`, only `NU1900` before compilation (0 warnings, 1 error, 0.51 s).
  - `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror`
    -> exit `1`, only `NU1900` before compilation (0 warnings, 1 error, 0.51 s).
- Pins rechecked: project `[1.62.0]`, lock requested/resolved
  `[1.62.0, 1.62.0]` / `1.62.0`, Chromium revision `1234`, browser version
  `151.0.7922.34`; no `NuGetAudit`, `NoWarn`, or failed-source suppression was added.
- No live capture, third-party traffic, evidence sealing, staging mutation, baseline promotion,
  production action, Git operation, or approved-baseline mutation was performed. Latest independent
  test/security/code-review gates must rerun against this current source before any promotion.

## Final repository-defect remediation and independent gates — 2026-08-16

Status: ALL IN-SCOPE CODE/TEST/SECURITY/REVIEW AND RELEASE BUILD GATES PASS; OFFICIAL LOCKED
RESTORE REMAINS EXTERNALLY BLOCKED BY `NU1900`; NO CAPTURE, SEAL, BASELINE MUTATION, OR PROMOTION.

Final root-cause remediations include:

- Promotion rejects reparse/junction traversal on source, destination, parent, lock, interrupted
  recovery, candidate/prior/failed paths, copy parents, moves, and deletes. A destination-junction
  regression proves the external target remains untouched.
- Evidence validation requires the exact OS child-environment key allowlist, exact approved browser
  executable path/hash, canonical `husaynia-browser-{N}` temp CWD, complete contiguous attempt
  history, valid attempt reasons, and a successful final main-frame response.
- Route generation fails closed for missing, temporary, unsuccessful, or unsupported statuses
  rather than converting them to `200`. Route semantics are independently rebound to retained HTTP,
  metadata, asset, dynamic-region, and import evidence.
- Import validation allowlists supported candidate kinds and binds source kind/key/URI, target key,
  payload reference, and checksum to the exact source record. Optional `mode` retains the frozen
  `dry-run` default and plan IDs require canonical UUID form.
- Comparison validates the approved representative tuple and full isolation provenance for both
  runs, verifies both actual PNG trees/hashes before changed-key selection, and requires each run's
  browser hashes to equal the approved pinned identity.
- Shared assets retain a bounded one-to-many source-page association so every referencing route and
  import candidate receives the correct media relationship.
- Validator/comparer/promotion walkers and robots evaluation are bounded. Robots matching no longer
  uses regex, and sitemap XML now uses `XmlReader` with DTD/entity processing prohibited, no
  resolver, and a document-character limit.
- Analyzer-policy defects were corrected without suppressions or package/policy changes.

Final executed evidence from `HusayniaSite`:

- Current-source SDK Roslyn compilation of all tool sources and all test sources using .NET SDK
  `10.0.400`, .NET 10 reference assemblies, the SDK
  `analysislevel_10_recommended.globalconfig`, both .NET analyzers, `/warnaserror+`, and only the
  SDK-normal assembly-unification exclusions `CS1701/CS1702`: tool exit `0`; tests exit `0`.
- Final focused regressions:
  - promotion junction, provenance forgery, hidden attempt history, route-status failure, shared
    assets, and forged browser identity: 12/12 passed in 35.8 s;
  - unsupported import kind and forged Run-B representative/provenance: 4/4 passed in 30.8 s;
  - bounded DTD-free sitemap parser: 2/2 passed in 0.65 s.
- Final full current-source safe suite:
  `dotnet vstest tests/Husaynia.BaselineCapture.Tests/bin/Release/net10.0/Husaynia.BaselineCapture.Tests.dll --TestAdapterPath:<cached-xunit-adapter> --TestCaseFilter:"Category!=Live"`
  -> 229 passed, 0 failed, 0 skipped in 6 m 44 s.
- Browser process-isolation/verifier suite: 4/4 passed. The behavioral child launched through the
  production isolation scope saw only the allowlist and temp CWD, with no `PATH`, `NODE_OPTIONS`,
  proxy, secret, or workspace inheritance.
- `playwright.ps1 install --no-shell chromium` exited `0`. Installed Chromium SHA-256 exactly
  matched `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- Official locked restores were attempted without suppression:
  - tool restore -> exit `1`, only `NU1900`, vulnerability service index unavailable (6.59 s);
  - test restore -> exit `1`, only `NU1900` for tool/test (6.87 s).
- Final official Release warnings-as-errors builds were attempted without suppression against the
  current source and existing restored assets:
  - tool build -> exit `0`, 0 warnings, 0 errors, 2.40 s;
  - test build -> exit `0`, 0 warnings, 0 errors, 3.49 s.
- Focused tests from those final Release binaries (sitemap DTD, unsupported import kind, forged
  Run-B URL, and browser verifier/isolation) passed 8/8 in 12.96 s.

Independent final gates:

- Test gate: PASS. It independently rebuilt/executed current source and reported the full non-live
  suite green plus the focused promotion/provenance/attempt/route/hash/shared-asset regressions.
- Code-review gate: APPROVED after unsupported import kinds and Run-B representative identity were
  fixed and regression-tested; no High/Critical correctness defects remain.
- Security gate: APPROVED with 0 Critical / 0 High. Its sole final Medium sitemap entity-expansion
  finding was subsequently remediated with DTD prohibition and two passing parser regressions.
- Exact pins remain Playwright `1.62.0`, Chromium revision `1234`, browser
  `151.0.7922.34`. No audit/TLS suppression, new NuGet package, production/T02 change, Git-history
  operation, live two-run capture, evidence seal, promoted-baseline mutation, or promotion occurred.

Release judgment remains `BLOCKED` solely because the required official NuGet-audited locked
restore cannot reach the vulnerability service. Release warnings-as-errors compilation is clean.
Re-run locked restores when the service is reachable; do not promote as part of this rework.

## Second code-review remediation — 2026-08-16

Status: BOTH REMAINING HIGH FINDINGS REMEDIATED; FOCUSED/FULL NON-LIVE AND RELEASE BUILD GATES
PASS; OFFICIAL LOCKED RESTORE REMAINS BLOCKED BY `NU1900`; CODE-REVIEW REGATE PENDING; NO
PROMOTION.

This section supersedes the prior final-remediation claims only where the latest independent code
review found them incorrect:

- Run B comparison now performs screenshot-only evidence validation before any deterministic pass.
  It requires `capture-summary.json`, screenshots, canonical network policy, complete request and
  terminal history for every retained attempt, full browser/trust provenance, and checksums. It
  revalidates approved representative URLs/viewports, actual PNG metadata, final-attempt ledger
  completeness, recomputed quality, and rejects unexpected/stale Run-B files.
- Permanent Run-B negative regressions cover a missing ledger, malformed JSON, orphaned decisions,
  missing terminals, attempt history hidden beyond `AttemptCount`, a ledger changed after checksum
  generation, and matching forged quality claims in both runs. Existing forged Run-B URL,
  provenance, PNG-hash, and review regressions remain active.
- The unrelated rework-1 route/import behavior was restored to the retained pre-ADR contract:
  route status uses the observed redirect/status and defaults an unavailable response to `200`;
  route `assetKeys` and import `mediaRefs` remain associated with the asset's first retained
  `SourcePage`, not every later discovery reference. Validator/schema checks were not weakened.
- Frozen-contract tests bind route statuses, route `assetKeys`, and content/religious
  `mediaRefs` to `evidence/baseline-runs/adr009-20260816T0645Z-run-a`, plus an isolated shared-asset
  regression proving a later referencing page does not acquire the first page's retained
  association.

Executed evidence from `HusayniaSite`:

- Regression proof before production fixes:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ScreenshotDeterminismComparerTests|FullyQualifiedName~CaptureLogicTests"`
  -> 16 failed / 66 passed / 82 total in 31 s. The failures included every new Run-B ledger/quality
  refusal plus the frozen route-status/shared-asset assertions; one changed-PNG fixture path was
  corrected before the final green run.
- Final focused gate, same command with `--no-build` -> 82 passed, 0 failed, 0 skipped in 54 s.
- Final full safe gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build --filter "Category!=Live"`
  -> 240 passed, 0 failed, 0 skipped in 7 m 24 s.
- Final current-source Release builds:
  - tool `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 0.62 s;
  - tests `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 1.15 s.
- Official locked restores, without project/configuration changes:
  - tool restore -> exit `1`, `NU1900`, vulnerability service index unavailable in 5.99 s;
  - test restore -> exit `1`, the same `NU1900` for tool/test in 5.89 s.
  A cache-only restore was used only to regenerate `obj` for compilation after recording the
  official failure; it is not an audit pass and no NuGet/TLS/audit setting was committed.
- Exact pins/hash rechecked: project `[1.62.0]`; lock requested/resolved
  `[1.62.0, 1.62.0]` / `1.62.0`; Chromium revision `1234`; browser
  `151.0.7922.34`; installed Windows x64 executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2` exactly matches the
  checked-in profile. Browser verifier/isolation tests pass 4/4.
- No safe-public live request, full capture, seal, visual review, baseline/candidate evidence
  mutation, promotion, production action, Git operation, T02/shared mission change, or package/pin
  change occurred. The previous security approval remains 0 Critical / 0 High; code review must
  rerun against this source before any later capture or promotion gate.

## Final evidence-lineage remediation — 2026-08-16

Status: FINAL DISTINCT-RUN AND EXACT-MEDIAREF GAPS REMEDIATED; FOCUSED/FULL NON-LIVE AND
CURRENT-SOURCE RELEASE BUILD GATES PASS; OFFICIAL LOCKED RESTORE REMAINS BLOCKED BY `NU1900`; NO
LIVE CAPTURE, SEAL, PROMOTION, OR EVIDENCE MUTATION.

Root causes and fixes:

- Comparison validated each Run B locally but never related Run A and Run B capture IDs or
  timestamps. It now reads both summaries, requires nonempty distinct IDs, enforces
  `started <= captured <= completed` within each run, requires Run B captured/completed timestamps
  to be strictly later than Run A, and records both runs' started/completed timestamps in the
  checksummed determinism document.
- Finalization previously checked only Run B's captured timestamp. It now binds Run A's recorded
  start/capture/completion to the retained summary/provenance, re-enforces the same distinct-run and
  ordering rules for both runs, requires comparison after Run B completion, and requires the
  additive lineage fields in `screenshot-determinism.json`.
- Import validation accepted any existing candidate key in `mediaRefs`. It now recomputes the exact
  frozen content/religious association from retained `media-inventory.json` first-discovery
  `SourcePage` values using the approved selector, requires exact ordered equality, requires empty
  refs for other candidate kinds, and keeps route asset validation independent of candidate claims.

Executed evidence from `HusayniaSite`:

- Regression proof before production fixes:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ScreenshotDeterminismComparerTests|FullyQualifiedName~BaselineEvidenceValidatorTests" --logger "console;verbosity=minimal"`
  -> 11 failed / 47 passed / 58 total in 6 m 2 s. Failures reproduced copied Run A, duplicate IDs,
  equal/earlier captured/completed times, Run A provenance outside its summary, and missing/extra
  first-discovery `mediaRefs`. A cache-only `-p:NuGetAudit=false` restore regenerated `obj` after
  the unsuppressed restore path reported `NU1900`; no project/configuration setting changed.
- Final focused lineage/media gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build --filter "FullyQualifiedName~CopiedRunA|FullyQualifiedName~SameCaptureId|FullyQualifiedName~SameControlledRun|FullyQualifiedName~EmptyRun|FullyQualifiedName~RunBCapturedAt|FullyQualifiedName~RunBCompletedAt|FullyQualifiedName~RunAProvenance|FullyQualifiedName~FirstDiscoveryMediaRef|FullyQualifiedName~FirstDiscoveryReligiousMediaRef" --logger "console;verbosity=minimal"`
  -> 16 passed, 0 failed, 0 skipped in 1 m 24 s.
- Final full safe gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> 255 passed, 0 failed, 0 skipped in 8 m 18 s.
- Final current-source Release warnings-as-errors builds, after the official restore attempts and a
  cache-only asset refresh:
  - tool `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 1.49 s;
  - tests `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 0.82 s.
- Official locked restores, without project/configuration changes:
  - tool restore -> exit `1`, only `NU1900`, vulnerability service index unavailable in 6.26 s;
  - test restore -> exit `1`, the same `NU1900` for tool/test in 5.97 s.
- Exact pins/hash remained unchanged: project SHA-256
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`, lock SHA-256
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`, Playwright
  `[1.62.0]`, lock requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`, Chromium revision `1234`,
  browser `151.0.7922.34`, and installed executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- No live request, capture, visual review, candidate/baseline evidence write, seal, promotion,
  production/T02/contract/package/TLS/active-mission change, or Git operation occurred.

## Visual-review lineage remediation — 2026-08-16

Status: REVIEW TIMESTAMPS ARE NOW BOUND TO RUN B COMPLETION IN COMPARISON AND FINAL SEALING;
FOCUSED/FULL NON-LIVE AND STRICT CURRENT-SOURCE RELEASE GATES PASS; OFFICIAL LOCKED RESTORE REMAINS
BLOCKED BY `NU1900`; NO LIVE CAPTURE, EVIDENCE MUTATION, SEAL, OR PROMOTION.

Root cause and fix:

- A nondefault UTC `ReviewedUtc` was accepted whenever it was not in the future, even when it
  predated the reviewed Run B and its generated diff evidence.
- Comparison and final validation now require
  `RunBCompletedAtUtc <= ReviewedUtc <= utcNow`, preserving equality at the lower bound and
  rejecting pre-Run-B and future timestamps.
- No new generated timestamp was added. Existing `ComparedAtUtc` is recomputed after diff creation
  on the review-validation invocation, so requiring `ReviewedUtc >= ComparedAtUtc` would reject the
  normal two-step compare/review/compare workflow and make verification self-defeating.
- Regression coverage proves comparison and sealing both reject one tick before Run B completion,
  accept equality and a later valid UTC timestamp, and retain future-timestamp rejection.

Executed evidence from `HusayniaSite`:

- Red regression proof before the production fix:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~ReviewedUtc" --logger "console;verbosity=minimal"`
  -> exit `1`, 2 failed / 14 passed / 16 total in 2 m 19 s. The failures were the new comparison
  and final-sealing cases with `ReviewedUtc` one tick before `RunBCompletedAtUtc`.
- Green focused gate, exact same command:
  -> exit `0`, 16 passed / 0 failed / 0 skipped in 2 m 13 s.
- Full non-live serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 299 passed / 0 failed / 0 skipped in 14 m 7 s.
- Strict current-source Release builds:
  - tool `dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror`
    -> exit `0`, 0 warnings, 0 errors, 0.59 s;
  - tests `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror`
    -> exit `0`, 0 warnings, 0 errors, 0.82 s.
- Unsuppressed locked restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable; both projects failed in
  6.2 s. No NuGet/TLS/audit setting was changed.
- Exact dependency pins remained unchanged before/after: SDK file SHA-256
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`; central package file
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`; capture project
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`; capture lock
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`; test project
  `6f8a88b0fc2d3baec178ac52d2af0beadd973fd698ea603943fbcd242a735a60`; test lock
  `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`. Playwright remains
  requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`.
- No live request, capture, visual review, candidate/baseline evidence write, seal, promotion,
  production/T02/contract/package/TLS/active-mission change, or Git operation occurred.

## Frozen route generator parity remediation — 2026-08-16

Status: VALIDATOR NOW MATCHES THE FROZEN PRE-ADR ROUTE PRODUCER FOR SITEMAP AND STATUS FIELDS;
FOCUSED AND FULL NON-LIVE SERIAL GATES PASS TWICE; STRICT RELEASE BUILDS PASS; OFFICIAL LOCKED
RESTORE REMAINS BLOCKED BY `NU1900`; CODE-REVIEW REGATE PENDING; NO LIVE CAPTURE OR PROMOTION.

This section supersedes the earlier route sitemap remediation record below where its validator
reconstruction claims differ.

Root cause and fix:

- The frozen producer marks every non-query discovered route as `sitemap=true` except the exact
  `PublicEndpointSeeds`; the validator instead used direct retained sitemap membership. This
  incorrectly rejected `/` and `/events/` when retained sitemap XML also listed them.
- The frozen producer computes `expectedStatus` as
  `redirect?.Status ?? record?.Status ?? 200`; the validator substituted `-1` for unavailable or
  selected unsupported observations. It now calls the frozen producer status helper.
- The validator now reconstructs sitemap from the exact normalized public-endpoint seed exclusion
  while still parsing retained sitemap XML for evidence validity. Unavailable HTTP observations
  retain route status `200` for manifest parity but independently block approval through
  `http-route-capture-incomplete:<url>`.
- Producer/validator regressions bind `/`, `/events/`, `/feed/`, and `/page-sitemap.xml` to the
  retained pre-ADR run, including missing-status defaults and seed exclusion. A general retained
  manifest parity test rejects any route semantic mismatch. Synthetic route fixtures now use the
  same producer sitemap semantics.

Executed evidence from `HusayniaSite`:

- Regression proof before the validator fix:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~RetainedProducer" --logger "console;verbosity=minimal"`
  -> 0 passed / 2 failed / 2 total in 1 s; both failures contained
  `route-manifest-semantic-mismatch`.
- Final focused serial gate, run twice with the same command:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~RouteManifest|FullyQualifiedName~RouteSitemap|FullyQualifiedName~PublicEndpointSeedSitemap|FullyQualifiedName~EveryFrozenRouteField|FullyQualifiedName~RetainedProducer" --logger "console;verbosity=minimal"`
  -> 29 passed / 0 failed / 0 skipped in 2 m 34 s, then 29/0/0 in 2 m 27 s.
- Full non-live serial gate, run twice with the same command:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> 283 passed / 0 failed / 0 skipped in 12 m 25 s, then 283/0/0 in 14 m 29 s.
- Strict current-source Release builds:
  - tool `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 0.65 s;
  - tests `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 0.86 s.
- Official locked test/tool restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable, both projects failed in
  7.44 s. A command-line `NuGetAudit=false --ignore-failed-sources` locked cache refresh completed
  in 160 ms only to regenerate build assets; no repository configuration changed.
- Exact pins and frozen generator remained unchanged: SDK `10.0.400`; capture project SHA-256
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`; lock SHA-256
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`; generator SHA-256
  `ca5ea3a49894fd5e886785ebaa94530a36dcca57142630026d60eadab221abe0`; Playwright requested/resolved
  `[1.62.0, 1.62.0]` / `1.62.0`; Chromium revision `1234`; browser `151.0.7922.34`.
- No live request, capture, visual review, candidate/baseline evidence write, seal, promotion,
  production/T02/contract/package/TLS/active-mission change, or Git operation occurred.

## Route sitemap evidence-integrity remediation — 2026-08-16

Status: REMAINING ROUTE SITEMAP DEFECT REMEDIATED; EVERY FROZEN ROUTE FIELD HAS AN INDEPENDENT
MUTATION REGRESSION; FOCUSED/FULL NON-LIVE AND STRICT CURRENT-SOURCE RELEASE GATES PASS; OFFICIAL
LOCKED RESTORE REMAINS BLOCKED BY `NU1900`; NO LIVE CAPTURE, SEAL, PROMOTION, OR EVIDENCE MUTATION.

Root cause and fix:

- `BaselineEvidenceValidator` reconstructed status, redirect, template/content, indexability,
  metadata, assets, dynamic regions, evidence refs, and content checksum, but omitted
  `RouteEntry.Sitemap` for HTTP-backed routes. A route could therefore flip `sitemap` and be sealed.
- Route validation now builds a complete expected route manifest independently from retained
  non-query HTTP records, metadata, media, forms/dynamic-region evidence, direct sitemap
  `<sitemap>/<url>` location evidence, and retained sitemap crawl records. It compares the exact
  expected/actual route set and every frozen field: route ID, legacy/canonical path, status,
  redirect, template/content, indexable, sitemap, metadata, asset keys, dynamic-region keys,
  evidence refs, and content checksum. Asset-alias expectations remain independently reconstructed.
- Tests include the reported false-to-true sitemap reproducer, a retained-membership true-to-false
  regression, and a 14-case table-driven mutation suite. The redirect-target case first constructs
  and seals an independently valid retained redirect route so it tests only that field.

Executed evidence from `HusayniaSite`:

- Before the production fix:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RouteSitemapCannot|FullyQualifiedName~EveryFrozenRouteField" --logger "console;verbosity=minimal"`
  -> exit `1`, 4 failed / 12 passed / 16 total in 2 m 12 s. The reported false-to-true sitemap
  reproducer and the table's `sitemap` case failed because `FinalizeAsync` returned `Passed=true`;
  the same run exposed two test-harness setup defects, which were corrected before green validation.
- Final focused route gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build --filter "FullyQualifiedName~RouteManifest|FullyQualifiedName~RouteSitemap|FullyQualifiedName~EveryFrozenRouteField" --logger "console;verbosity=minimal"`
  -> 27 passed, 0 failed, 0 skipped in 2 m 25 s.
- Final full safe gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --blame-crash --blame-crash-dump-type mini --results-directory %TEMP%\husaynia-route-final-full --logger "console;verbosity=minimal"`
  -> 281 passed, 0 failed, 0 skipped in 11 m 43 s.
- Strict current-source Release builds:
  - tool `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 1.77 s;
  - tests `dotnet build ... -c Release --no-restore -warnaserror` -> exit `0`, 0 warnings,
    0 errors, 1.28 s.
- Official locked restores, without project/configuration changes:
  - tool restore -> exit `1`, only `NU1900`, vulnerability service index unavailable in 6.18 s;
  - test restore -> exit `1`, the same `NU1900` for tool/test in 6.47 s.
  A cache-only locked restore with command-line `NuGetAudit=false` refreshed existing `obj` assets
  in 178 ms after recording the official failures; it is not an audit pass.
- Exact pins/hash remain unchanged: .NET SDK `10.0.400`; project SHA-256
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`; lock SHA-256
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`; Playwright requested/resolved
  `[1.62.0, 1.62.0]` / `1.62.0`; Chromium revision `1234`; installed Windows x64 executable
  SHA-256 `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- No live request, capture, visual review, candidate/baseline evidence write, seal, promotion,
  production/T02/contract/package/TLS/active-mission change, or Git operation occurred.

## Top-level timeout/cancellation diagnostic evidence remediation — 2026-08-16

Status: IMPLEMENTATION AND NON-LIVE GATES PASS; INCOMPLETE RUNS NOW RETAIN COHERENT FAIL-CLOSED
DIAGNOSTICS AND REMAIN UNSEALABLE/UNPROMOTABLE; FRESH LIVE CAPTURE REMAINS WITHHELD.

Root cause and fix:

- `BaselineCaptureService.CaptureAsync` performed all coherent evidence serialization only after the
  browser/crawl/asset work completed and used the already-cancelled global token for those writes.
  A deadline or caller cancellation therefore escaped before `capture-summary.json`,
  `screenshots.json`, network decisions/provenance/policy, residual risk, README, and checksums were
  produced, leaving only raw fragments.
- Capture and screenshot-recapture now use a bounded independent local finalization token after an
  output directory has been created. Every retained JSON/README/checksum write is atomic and
  `checksums.sha256` is written last.
- Failed runs retain a summary with exact retained counts, duration, stable sanitized failure reason
  and stage; a complete unique 36-key screenshot matrix; completed rows unchanged; explicit failed
  rows for unattempted/cancelled keys; all collected screenshot network decisions; browser
  provenance/policy when a session existed; otherwise explicit tooling-failure provenance/policy
  that the validator rejects; and an incomplete-capture residual risk plus truthful diagnostic
  README.
- Diagnostic summaries/provenance/policy carry additive failure status fields. The validator and
  CLI complete-set gate explicitly reject diagnostic status even if counts are forged to 36/36.
  No determinism artifact or `baseline-verification.json` seal is created.
- Crawl cancellation now propagates rather than being converted to a normal request failure.
  Existing non-empty output is still refused before any mutation, and CLI exception output remains
  type-only (`command-failed:TaskCanceledException`) so exception messages, environment values,
  paths, credentials, and secrets are not retained.

Executed evidence from `HusayniaSite`:

- Regression proof before the fix:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~CancellationBeforeBrowserRetainsClosedDiagnosticEvidence" --logger "console;verbosity=minimal"`
  -> exit `1`, 0 passed / 1 failed / 1 total; all 16 required diagnostic artifacts were missing.
- Final deterministic diagnostic gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "FullyQualifiedName~CaptureFailureDiagnosticsTests" --logger "console;verbosity=minimal"`
  -> 7 passed / 0 failed / 0 skipped in 684 ms. Coverage includes cancellation before browser,
  global-deadline cancellation, mid-matrix cancellation, route-crawl error, asset-work error,
  screenshot-recapture cancellation, validator rejection, nonzero CLI exit, checksum completeness,
  sensitive-message exclusion, and non-empty output preservation.
- Final full non-live serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> 306 passed / 0 failed / 0 skipped in 14 m 53 s.
- Strict current-source Release builds:
  - capture tool -> exit `0`, 0 warnings, 0 errors, 0.62 s;
  - capture tests -> exit `0`, 0 warnings, 0 errors, 0.80 s.
- Official locked audited restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable, both projects failed in
  6.12 s. A command-line `NuGetAudit=false --ignore-failed-sources` locked cache refresh completed
  in 181 ms only to regenerate build assets; no repository package/TLS/audit setting changed.
- Exact pins remain unchanged: SDK file SHA-256
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`; central package file
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`; capture project
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`; capture lock
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`; test project
  `6f8a88b0fc2d3baec178ac52d2af0beadd973fd698ea603943fbcd242a735a60`; test lock
  `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`.
  Playwright remains requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`; Chromium revision remains
  `1234`; browser version remains `151.0.7922.34`.
- QA evidence was read-only context. No live request, capture, visual review, candidate/baseline
  evidence write, seal, promotion, production/T02/contract/package/TLS/active-mission change, or
  Git operation occurred.

## Diagnostic failure-path and promotion-recovery remediation — 2026-08-16

Status: ALL REPORTED MEDIUM/LOW FINDINGS REMEDIATED; 316 NON-LIVE TESTS AND STRICT
CURRENT-SOURCE RELEASE BUILDS PASS; OFFICIAL LOCKED RESTORE REMAINS BLOCKED ONLY BY EXTERNAL
`NU1900`; NO LIVE CAPTURE OR PROMOTION.

Root causes and fixes:

- Browser session startup and mid-matrix exceptions were swallowed by `CaptureScreenshotsAsync`,
  which appended a full failure matrix to retained rows and allowed normal finalization. Exceptions
  now reach the common failed finalizer; completed keys remain unique, only missing keys are filled,
  failure stage/reason are retained, and the command propagates nonzero.
- Playwright treated caller cancellation inside an attempt as an internal timeout and returned
  before the matrix owner could retain partial decisions. Attempt orchestration now distinguishes
  caller cancellation from internal timeout, attaches the partial row and ordered decisions to the
  propagated cancellation, and both capture and recapture owners retain them before finalization.
- Browser provenance serialized absolute executable and temporary working-directory paths.
  Provenance now retains stable browser identity/hash values, sanitized environment keys, and
  explicit outside-workspace/empty-temp/sanitized-environment attestations only.
- Interrupted recovery trusted a sole `baseline.prior-*` tree by byte manifest alone. Recovery now
  requires transaction-shaped names, semantically validates the prior before moving it, validates
  semantics and bytes after the move with safety-critical completion independent of later caller
  cancellation, and quarantines invalid or poisoned prior evidence instead of making it canonical.
- Maintained route-crawl and asset-capture caller-cancellation regressions now verify coherent
  failed diagnostics, exact unique 36-key completion, stage/reason retention, and checksums.

Executed evidence from `HusayniaSite`:

- Red regressions before production fixes:
  - browser startup/mid-matrix and interrupted-prior poisoning focused gate -> exit `1`,
    4 failed / 2 passed / 6 total in 6 s;
  - in-attempt decision-retention regression -> exit `1`, 1 failed / 1 total in 203 ms;
  - sentinel provenance regression -> exit `1`, 1 failed / 1 total in 55 ms.
- Focused diagnostic/promotion/provenance gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~CaptureFailureDiagnosticsTests|FullyQualifiedName~BaselinePromotionServiceTests|FullyQualifiedName~BrowserExecutableVerifierTests|FullyQualifiedName~ForgedBrowserEnvironmentAndWorkspaceProvenance" --logger "console;verbosity=minimal"`
  -> exit `0`, 41 passed / 0 failed / 0 skipped in 2 m 53 s.
- Full non-live current-source gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 316 passed / 0 failed / 0 skipped in 14 m 3 s.
- Final strict Release builds:
  - tool -> exit `0`, 0 warnings / 0 errors in 0.59 s;
  - tests -> exit `0`, 0 warnings / 0 errors in 0.83 s.
- Unsuppressed locked audited restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable; both projects failed in
  6.58 s. A command-line `NuGetAudit=false --ignore-failed-sources` locked cache refresh completed
  in 178 ms only to regenerate build assets; no repository package/TLS/audit setting changed.
- Exact pins remained unchanged: SDK file SHA-256
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`; central package file
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`; capture project
  `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`; capture lock
  `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`; test project
  `6f8a88b0fc2d3baec178ac52d2af0beadd973fd698ea603943fbcd242a735a60`; test lock
  `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`.
  SDK remains `10.0.400`; Playwright remains requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`.
- No live request, capture, visual review, candidate/baseline evidence write, seal, promotion,
  production/T02/contract/package/TLS/active-mission change, or Git operation occurred.

## Final Windows residue case-variation remediation — 2026-08-17

Status: IMPLEMENTATION, FOCUSED/FULL NON-LIVE TESTS, AND STRICT CURRENT-SOURCE BUILDS PASS;
OFFICIAL LOCKED RESTORE REMAINS BLOCKED ONLY BY EXTERNAL `NU1900`; NO LIVE CAPTURE OR PROMOTION.

- Root cause: the CLI accepted Windows case-equivalent `evidence/Baseline`, retained that spelling
  when opening the promotion lease, and the physical residue scan used ordinal case-sensitive
  comparisons for `prior`, `candidate`, `failed`, visible journal, and temporary journal names.
- The CLI now returns the repository-cased canonical `evidence/baseline` path after platform-
  appropriate validation. The Windows lease independently matches every transaction residue prefix
  and the temporary-journal `.tmp` suffix with `OrdinalIgnoreCase`.
- Regression coverage uses destination `BaSeLiNe`, mixed-case residue spellings, every non-empty
  journal/prior/candidate/failed combination, destination present/absent, visible/temporary journal
  variants, and six near-prefix negatives. Every positive case requires exit `4` and an identical
  handle-derived byte/identity snapshot excluding only the persistent lock.

Executed evidence from `HusayniaSite`:

- Before the production fix, the focused reproduction exited `1`: 30 mixed-case combinations
  failed with expected exit `4` versus actual exit `1`; five near-prefix controls passed.
- Final focused gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "FullyQualifiedName~EveryCrossProcessResidueCombinationIsPreserved|FullyQualifiedName~NearPromotionResiduePrefixesAreIgnored|FullyQualifiedName~CanonicalBaselineDestinationUsesRepositoryCasing" --logger "console;verbosity=minimal"`
  -> exit `0`, 67 passed / 0 failed / 0 skipped in 1 s (2.72 s command duration).
- Full non-live gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 427 passed / 0 failed / 0 skipped in 22 m 20 s (22 m 22.52 s command duration).
- Official locked audited restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable; all three projects failed
  after 6.45 s (8.19 s command duration). A command-line-only
  `NuGetAudit=false --ignore-failed-sources` locked cache refresh completed in 193 ms; no repository
  package, audit, source, or TLS setting changed.
- Strict Release builds after the cache refresh:
  - tool -> exit `0`, 0 warnings / 0 errors in 0.76 s;
  - tests and crash harness -> exit `0`, 0 warnings / 0 errors in 1.58 s.
- Exact pins remain Playwright requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`, Chromium revision
  `1234`, browser version `151.0.7922.34`, and executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`. No `NuGetAudit`,
  `NoWarn`, or failed-source suppression was added.
- Protected-path check found zero writes since task start beneath `evidence`, `src`, or `contracts`;
  `.ai-org/active-mission.json` retained its 2026-08-16 timestamp; repository transaction residue
  count was zero. No live request, capture, evidence mutation, seal, promotion, production/T02
  change, package/TLS policy change, or Git operation occurred.

## Whole-capture duration contract remediation — 2026-08-17

Status: IMPLEMENTATION, FOCUSED/FULL NON-LIVE TESTS, AND STRICT CURRENT-SOURCE BUILDS PASS;
OFFICIAL LOCKED RESTORE REMAINS BLOCKED ONLY BY EXTERNAL `NU1900`; NO LIVE CAPTURE OR PROMOTION.

- Root cause: the CLI imposed a 25-minute whole-capture default and maximum even though the
  sequential 36-row matrix can consume up to 27 minutes at the unchanged 45-second per-capture
  bound, before crawl, assets, and final capture work.
- The whole-capture deadline now defaults to 60 minutes and accepts only 1 through 90 minutes.
  Invalid values report
  `capture-option-out-of-range:--max-duration-minutes:accepted-range=1..90`; accepted values flow
  unchanged into `BaselineCaptureService` and its linked deadline token.
- ADR-009's per-attempt readiness/capture limits remain 30/45 seconds with at most three attempts.
  Deadline cancellation still uses independent diagnostic finalization and remains unsealable and
  unpromotable.
- The tool help, tool README, and binding architecture/T01 commands now document the 60-minute
  default/full-run invocation.

Executed evidence from `HusayniaSite`:

- Before the contract fix, the new focused regression gate exited `1` with four `CS0117` errors
  because the CLI had no testable `CreateCaptureOptions` contract seam.
- Final focused gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "FullyQualifiedName~CaptureDuration|FullyQualifiedName~CaptureOptionsCannotExceedSafetyBounds|FullyQualifiedName~DeadlineCancellationRetainsStableFailureEvidence|FullyQualifiedName~ScreenshotTimeoutsAreBoundedByAdr009" --logger "console;verbosity=minimal"`
  -> exit `0`, 15 passed / 0 failed / 0 skipped in 242 ms.
- Full non-live gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 435 passed / 0 failed / 0 skipped in 43 m 27 s.
- Strict Release builds:
  - tool -> exit `0`, 0 warnings / 0 errors in 0.66 s;
  - tests and crash harness -> exit `0`, 0 warnings / 0 errors in 1.42 s.
- Official locked audited restore:
  `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index unavailable; all three projects failed
  after 7.38 s. A command-line-only `NuGetAudit=false --ignore-failed-sources` locked cache refresh
  completed in 177 ms; no repository package, audit, source, or TLS setting changed.
- CLI help exited `0` and printed
  `--max-duration-minutes MINUTES (accepted 1..90; default 60)`.
- Exact pins remain Playwright requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`, Chromium revision
  `1234`, browser version `151.0.7922.34`, and executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`; the tool/test lock hashes
  remain `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3` and
  `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`.
- Protected-path check found zero writes since task start beneath `evidence`, `src`, or `contracts`;
  `.ai-org/active-mission.json` retained its pre-task timestamp. No live request, capture, evidence
  mutation, seal, promotion, production/T02 change, package/TLS policy change, or Git operation
  occurred.

## ADR-009 v3 binding rework amendment — 2026-08-18

Status: APPROVED FOR IMPLEMENTATION; supersedes the prior D03 run-long static-host equality rule,
the H05 blocked-empty-cookie-read rule, and stale T01-R01/R02 status claims.

Independent recovery proved 445/445 non-live tests pass, but fresh Run A/Run B both retained 0/36
captures because live pages performed harmless cookie reads in empty contexts and approved Google
Fonts/jsDelivr answer sets rotated. Code review also proved route Sitemap provenance was still
inferred and visual-review masks were permissive.

Binding changes:

1. Advance policy to `adr-009-v3`.
2. Keep the primary-origin answer set immutable for the run.
3. Create an immutable public-address pin epoch per fresh context. Approved static-host answer sets
   may rotate only when every answer is public; changed pins require a verified-browser relaunch.
   Reauthorization and pre/post checks cannot change the active pin.
4. Allow only empty-result `document.cookie` and Cookie Store reads, paired with allowed
   `cookie-read-empty-context` capability terminals. Cookie writes, outbound Cookie headers,
   nonzero cookie checkpoints, imported/exported state, and inherited storage remain failures.
5. Add DNS epoch/pin/answer-hash/rotation/browser-context evidence without secret values.
6. Reconstruct route Sitemap exclusively from retained `<urlset><url><loc>` evidence bound by HTTP
   status, saved path, length, and SHA-256. Sitemap-index locations and heuristics do not count.
7. Parse visual reviews case-sensitively with unknown members rejected. Require all ten fields and
   validate nullable mask pairing, version `1`, 1-64 positive in-bounds rectangles, rationale,
   hashes, timestamps, key, and diff path. Masks cannot waive unmasked evidence or quality.
8. Add red-first negative tests for all changed semantics, run strict build and all non-live tests,
   then rerun entirely fresh Run A/Run B directories. Old failed staging directories are diagnostic
   evidence only and cannot be reused, sealed, reviewed, or promoted.
9. Rerun independent security and code-review gates. Only 36/36 captured and quality-pass runs may
   advance to comparison, conditional visual review, deterministic verification, atomic promotion,
   post-promotion tests, and final judgment.

No CTO exception is required. Cookie writes/transmission, weaker origin continuity, private-address
tolerance, unpinned connects, package changes, baseline mutation before gates, and production work
remain forbidden.

## T01-R01 ADR-009 v3 implementation rework — 2026-08-19 UTC

Status: CODE COMPLETE AND READY FOR INDEPENDENT TEST/SECURITY/CODE-REVIEW REGATES; OFFICIAL
UNSUPPRESSED LOCKED AUDIT RESTORE REMAINS EXTERNALLY BLOCKED BY `NU1900`; NO LIVE CAPTURE,
EVIDENCE MUTATION, SEAL, REVIEW, PROMOTION, OR PRODUCTION ACTION.

Implemented the approved v3 amendment only in the T01 tool/tests:

- Added immutable `TrustedHostClass`, `DnsPinBinding`, and `TrustedContextNetworkPolicy` contracts.
  The primary-origin set remains run-immutable. Every attempt creates a new context epoch
  immediately before context creation; approved static hosts may rotate only through complete
  public sets; the ordinal-first address is selected; a changed pin map disposes and relaunches the
  verified sandboxed browser. Authorization and pre-navigation/pre-screenshot/post-screenshot DNS
  checks validate current public answers without altering the active pin. Provenance binds epoch,
  answer-set hash, rotation, selected pin, browser instance, context, capture key, attempt, and
  checkpoint times.
- Empty `document.cookie`, Cookie Store `get()`, and `getAll()` reads now produce one allowed
  decision and one `cookie-read-empty-context` terminal. Writes/deletes remain
  `capability-cookie-write-blocked`; outbound Cookie headers and nonzero context-cookie checkpoints
  remain failures. No storage state or cookie values are imported, exported, inherited, or logged.
- Producer and validator independently derive route `Sitemap` only from exact normalized canonical
  `<urlset><url><loc>` values. Sitemap-index locations do not count. Only successful retained raw
  sitemap HTTP records participate, and validator binding requires exact saved path, length, and
  SHA-256.
- Added a dedicated case-sensitive visual-review parser. It requires exactly ten entry properties,
  rejects unknown/duplicate properties recursively, validates key/hashes/path/timestamp/decision and
  bounded text, and accepts masks only as a null pair or version `1` with 1-64 positive in-image
  rectangles and bounded nonblank rationale. Masks remain supplemental and cannot suppress the
  unmasked diff, quality, or acceptance gates.
- Advanced the policy to `adr-009-v3`, updated operator documentation, and added DNS rotation,
  origin-change, mixed-answer, immutable-pin, empty-cookie-terminal, exact sitemap/index/provenance,
  strict JSON, duplicate-field, mask-pair, and rectangle-bound regressions.

Red/green evidence from `HusayniaSite`:

- Initial combined focused integration run after adding v3 expectations:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "FullyQualifiedName~TrustedEndpointPolicyTests|FullyQualifiedName~ScreenshotAttemptLifecycleTests|FullyQualifiedName~ScreenshotDeterminismComparerTests|FullyQualifiedName~BaselineEvidenceValidatorTests" --logger "console;verbosity=minimal"`
  -> exit `1`, 80 failed / 88 passed / 168 total in 9 m 19 s. Failures exposed the prior inferred
  sitemap fixtures and old origin DNS reason.
- Strict-review integration red:
  `dotnet test ... --filter "FullyQualifiedName~ScreenshotDeterminismComparerTests"`
  -> exit `1`, 9 failed / 23 passed / 32 total in 1 m 27 s; current run fixtures lacked the new
  epoch/browser/context associations. After fixture and parser integration, the exact command
  passed 32/32 in 2 m 21 s.
- Validator integration red:
  `dotnet test ... --filter "FullyQualifiedName~BaselineEvidenceValidatorTests"`
  -> exit `1`, 3 failed / 80 passed / 83 total in 9 m 48 s; failures identified one retained
  inferred-sitemap redirect fixture and two strict-parser reason expectations. The focused
  remediation command passed 22/22 in 3 m 2 s.
- Final sitemap success/index/provenance focus:
  `dotnet test ... --filter "FullyQualifiedName~RouteSitemap|FullyQualifiedName~SitemapIndexLocation|FullyQualifiedName~SitemapRawBytes|FullyQualifiedName~PublicEndpointSeedSitemap"`
  -> exit `0`, 4 passed / 0 failed / 0 skipped in 1 m 13 s.
- Final full current-source safe serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 456 passed / 0 failed / 0 skipped in 33 m 10 s.
- Final strict current-source Release build:
  `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror --nologo -v:minimal`
  -> exit `0`, tool, crash harness, and tests built with 0 warnings / 0 errors in 4.67 s.
  The immediately preceding explicit tool-only strict build also exited `0`, 0 warnings /
  0 errors in 0.69 s (0.96 s measured command duration).

Official unsuppressed locked audit restore was attempted after implementation:

- `dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index
  `https://api.nuget.org/v3/index.json` unavailable; project failed in 6.47 s
  (7.51 s command duration).
- `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only the same `NU1900` for tool, tests, and crash harness; restore failed in
  5.98 s (7.43 s command duration).
- A command-line-only `NuGetAudit=false --ignore-failed-sources` locked cache refresh completed in
  191 ms solely to regenerate existing build assets. No repository audit/source/TLS setting,
  package, or lock content changed.

Immutable/protected verification:

- Playwright remains requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`; Chromium remains revision
  `1234`, version `151.0.7922.34`, with executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- Final SHA-256 values: `global.json`
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`;
  `Directory.Packages.props`
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`;
  capture project `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`;
  capture lock `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`;
  test lock `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`;
  executable-profile file
  `ba0bcba67acf062411321438e1fe15022b86c1fa04f88e700ae0053289a521f1`.
  Restore retouched lock-file timestamps only; hashes/content remained unchanged.
- Write-time scan from task start found zero writes beneath `evidence`, `src`, or `contracts`.
  `.ai-org/active-mission.json` was not accessed or modified. No live request/capture, staging or
  baseline write, visual acceptance, sealing, verification, promotion, Git operation, package
  change, production action, or T02/shared-mission change occurred.

T01-R01 is ready for independent test, security, and code-review regates against this source.
The external `NU1900` audit-index outage remains a recorded environment blocker and is not
suppressed as an official gate.

## T01-R01 ADR-009 v3 independent-regate remediation — 2026-08-20 UTC

Status: DONE FOR ALL SIX CONSOLIDATED SOURCE/TEST FINDINGS; OFFICIAL UNSUPPRESSED LOCKED AUDIT
RESTORE REMAINS EXTERNALLY BLOCKED BY `NU1900`. NO LIVE CAPTURE, EVIDENCE MUTATION, SEAL, REVIEW,
PROMOTION, PRODUCTION CHANGE, PACKAGE CHANGE, OR GIT OPERATION OCCURRED.

Root causes and remediation:

- Capture and recapture retained the session provenance object before attempts, while
  `RecordDnsEpoch` replaced it after each attempt. Both finalizers now read the session's final
  provenance in `finally`, including cancellation/failure paths. Integrated service and recapture
  diagnostics prove the final epoch is retained.
- `RecordDnsEpoch` replaced run-level resolver rules with the latest epoch while run-level DNS
  answers remained the initial run identity. Run-level answers/rules now remain immutable and
  internally consistent; each epoch retains its own selected bindings. A rotation-through-final-
  evidence test proves the initial run identity stays at `.34` while the retained second epoch
  selects `.35`.
- Independent validation now enforces exact host classes and host sets, sorted unique complete
  public answer sets and hashes, immutable primary-origin answers, truthful rotation flags,
  chronological unique epochs, exact screenshot-key/attempt/context associations, unique context
  IDs, and browser-instance change whenever the selected pin map changes. Request pins are checked
  against their referenced epoch rather than the initial run answer set. Negative tests cover host
  reclassification, origin rotation, changed pins without relaunch, epoch reordering, and orphan
  associations; a positive rotation test validates each epoch independently.
- Browser resolver transitions now use a production seam exercised with fake sessions. Identical
  maps reuse the active session; changed maps dispose it, create a replacement, and clean up the
  replacement exactly once.
- The exact production capability injection is executed in pinned Chromium without network
  navigation. It proves `document.cookie == ""`, Cookie Store `get() == null`, `getAll() == []`,
  three allowed read decisions with three paired terminals, and blocked document/store
  set/delete operations. This exposed and fixed Cookie Store mutation logs incorrectly treating
  the cookie name as a URL; mutations now emit the current location and remain
  `capability-cookie-write-blocked`.
- The production strict review parser/validator matrix now covers zero and 65 rectangles,
  unsupported versions, both null-pair mismatches, negative coordinates, zero/negative dimensions,
  right/bottom overflow, blank/oversized rectangle rationale, and recursive
  duplicate/unknown/case-variant properties, while retaining exact-edge valid coverage.

Red/green and verification evidence from `HusayniaSite`:

- Red-first focused regressions:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~CancellationMidMatrixPreservesCompletedRow|FullyQualifiedName~ScreenshotRecaptureCancellation|FullyQualifiedName~DnsEpochProvenanceEnforcesIndependentContextInvariants|FullyQualifiedName~VisualReviewParserRejectsStrictMaskMatrix" --logger "console;verbosity=minimal"`
  -> exit `1`, 7 failed / 12 passed / 19 total in 45 s. Failures reproduced zero retained final
  epochs and acceptance of origin rotation, changed pins without relaunch, reordered epochs,
  reclassified hosts, and orphan epochs.
- Exact production cookie proof initially failed 1/2 because Cookie Store set/delete emitted their
  cookie-name argument as the capability URL. After the production script fix, the same focused
  command passed 2/2 in 1 s.
- Final consolidated focus:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~CancellationMidMatrixPreservesCompletedRow|FullyQualifiedName~ScreenshotRecaptureCancellation|FullyQualifiedName~CancellationAfterDnsRotation|FullyQualifiedName~DnsEpochProvenance|FullyQualifiedName~PublicStaticRotationKeepsInitialRunIdentity|FullyQualifiedName~ChangedResolverMapRelaunchesBrowser|FullyQualifiedName~ProductionCapabilityInjectionAllowsOnlyEmptyCookieReads|FullyQualifiedName~VisualReviewParser" --logger "console;verbosity=minimal"`
  -> exit `0`, 27 passed / 0 failed / 0 skipped in 55 s.
- Surrounding validator/comparer/diagnostic/policy/lifecycle suites:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~BaselineEvidenceValidatorTests|FullyQualifiedName~ScreenshotDeterminismComparerTests|FullyQualifiedName~CaptureFailureDiagnosticsTests|FullyQualifiedName~TrustedEndpointPolicyTests|FullyQualifiedName~ScreenshotAttemptLifecycleTests" --logger "console;verbosity=minimal"`
  -> exit `0`, 219 passed / 0 failed / 0 skipped in 12 m 59 s.
- Strict Release build:
  `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror --nologo -v:minimal`
  -> exit `0`, 0 warnings / 0 errors in 1.58 s.
- Full safe serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 477 passed / 0 failed / 0 skipped in 18 m 39 s.

Official unsuppressed locked audit restore:

- `dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode --force-evaluate`
  -> exit `1`; only `NU1900`, vulnerability service index
  `https://api.nuget.org/v3/index.json` unavailable; failed in 7.85 s.
- `dotnet restore tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj --locked-mode --force-evaluate`
  -> exit `1`; only the same `NU1900` for tool, tests, and crash harness; failed in 7.45 s.
- A command-line-only `NuGetAudit=false --ignore-failed-sources` locked restore was used before
  development testing solely to refresh generated assets. No repository NuGet/audit/TLS policy,
  package, project, or lock content changed.

Immutable/protected verification:

- Playwright remains requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`; Chromium remains revision
  `1234`, version `151.0.7922.34`, executable SHA-256
  `409805a16d6416087e6b2f778df1cf8f7bbb267d6b99f6b5bb0a618eace234f2`.
- SHA-256 values remained: `global.json`
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`;
  `Directory.Packages.props`
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`;
  capture project `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`;
  capture lock `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`;
  test lock `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`;
  executable profile
  `ba0bcba67acf062411321438e1fe15022b86c1fa04f88e700ae0053289a521f1`.
- A write-time scan covering the work interval returned no files beneath `evidence`, `src`, or
  `contracts`. No live network/capture command was run.

## T01-R01 ADR-009 v3 browser-launch transition rework — 2026-08-20 UTC

Status: DONE; SHARED SECURITY/CODE-REVIEW BROWSER-LAUNCH TRANSITION FINDING CLOSED. OFFICIAL
UNSUPPRESSED LOCKED AUDIT RESTORE REMAINS EXTERNALLY BLOCKED BY `NU1900`. NO LIVE CAPTURE,
EVIDENCE/PROTECTED-PATH MUTATION, PACKAGE CHANGE, OR GIT OPERATION OCCURRED.

Implementation:

- First-context rotation now compares each epoch-one selected pin with the immutable bootstrap pin
  rather than a null prior context. `Rotated` is true exactly when that host's selected pin changes;
  complete public answer-set provenance remains independently validated.
- Added bounded additive `BrowserLaunchEvidence` for every verified browser instance:
  `browserInstanceId`, `launchedAtUtc`, canonical SHA-256 of the exact ordered Chromium resolver
  rules, optional previous instance, and reason. The bootstrap launch is always retained, and each
  resolver-map relaunch is recorded before context creation.
- Every epoch is independently bound to exactly one unique launch. The validator reconstructs the
  exact resolver rules from the epoch selected pins, verifies the launch hash and launch-before-
  context ordering, and rejects missing, duplicate, unused, late, or hash-mismatched launches.
- The lifecycle rule is now biconditional from bootstrap through every epoch: a selected map change
  requires a new browser instance and `resolver-map-changed` launch linked to the prior instance;
  an unchanged map requires reuse of the same instance. This rejects both stale-browser use and
  unexplained relaunch.
- Production browser creation is exercised without network navigation and proves bootstrap launch
  evidence uses the verified browser instance, launch time, and exact bootstrap resolver hash.
- Added first-context bootstrap comparison, invalid lifecycle, resolver hash, missing/duplicate
  launch, late launch, incorrect rotation, unchanged-bootstrap, and correctly relaunched
  rotated-bootstrap coverage. Existing changed-pin/same-browser coverage remains active.

Red/green and verification:

- Red-first launch validation:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~FirstContextComparesSelectedPinsAgainstBootstrapBindings|FullyQualifiedName~BrowserLaunchEvidenceRejectsInvalidLifecycleTransitions|FullyQualifiedName~BootstrapUnchangedAndBootstrapRotatedLaunchesAreAccepted" --logger "console;verbosity=minimal"`
  -> exit `1`, 8 failed / 1 passed / 9 total in 1 m 20 s. The old validator accepted missing,
  duplicate, late, hash-mismatched, and unexplained launch evidence and rejected a correct
  bootstrap-to-first-context rotation.
- Complete browser-launch focus:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~FirstContextComparesSelectedPinsAgainstBootstrapBindings|FullyQualifiedName~BrowserLaunchEvidence|FullyQualifiedName~BootstrapUnchangedAndBootstrapRotated|FullyQualifiedName~PublicStaticRotationKeepsInitialRunIdentity|FullyQualifiedName~DnsEpochProvenance|FullyQualifiedName~ChangedResolverMapRelaunchesBrowser|FullyQualifiedName~CaptureCreationRecordsBootstrapBrowserLaunchEvidence" --logger "console;verbosity=minimal"`
  -> exit `0`, 17 passed / 0 failed / 0 skipped in 2 m 42 s.
- Final launch validation subset after the last ordering check:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~BrowserLaunchEvidenceRejectsInvalidLifecycleTransitions|FullyQualifiedName~BootstrapUnchangedAndBootstrapRotatedLaunchesAreAccepted|FullyQualifiedName~CaptureCreationRecordsBootstrapBrowserLaunchEvidence" --logger "console;verbosity=minimal"`
  -> exit `0`, 9 passed / 0 failed / 0 skipped in 1 m 11 s.
- Surrounding validator/comparer/DNS/browser/diagnostic suites:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~BaselineEvidenceValidatorTests|FullyQualifiedName~ScreenshotDeterminismComparerTests|FullyQualifiedName~TrustedEndpointPolicyTests|FullyQualifiedName~BrowserExecutableVerifierTests|FullyQualifiedName~CaptureFailureDiagnosticsTests" --logger "console;verbosity=minimal"`
  -> exit `0`, 226 passed / 0 failed / 0 skipped in 14 m 59 s.
- Strict Release build:
  `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror --nologo -v:minimal`
  -> exit `0`, 0 warnings / 0 errors in 1.62 s.
- Full safe serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 487 passed / 0 failed / 0 skipped in 27 m 28 s.

Official unsuppressed locked audit restore:

- Tool restore exited `1` after 10.23 s with only `NU1900`: vulnerability service index
  `https://api.nuget.org/v3/index.json` unavailable.
- Test restore exited `1` after 9.36 s with only the same `NU1900` for tool, tests, and crash
  harness. No audit failure was suppressed for the official gate.

Immutable/protected verification:

- Playwright/Chromium pins and executable hash remain unchanged.
- SHA-256 values remain: `global.json`
  `deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`;
  `Directory.Packages.props`
  `270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`;
  capture project `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`;
  capture lock `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`;
  test lock `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`;
  executable profile
  `ba0bcba67acf062411321438e1fe15022b86c1fa04f88e700ae0053289a521f1`.
- Work-interval scan returned no writes beneath `evidence`, `src`, or `contracts`.

## T01-R01 browser-launch causal chronology fix — 2026-08-20 UTC

Status: DONE. Replacement launches now require
`predecessor.launchedAtUtc <= replacement.launchedAtUtc <= epoch.createdAtUtc`; equality with the
predecessor is valid. The check resolves the predecessor by `browserInstanceId` and does not rely
on launch-array order. A causal violation fails with the stable
`browser-launch-chronology-invalid` reason.

Red/green evidence:

- Red-first:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~ReplacementBrowserLaunchCannotPredatePredecessorLaunch|FullyQualifiedName~BootstrapUnchangedAndBootstrapRotatedLaunchesAreAccepted" --logger "console;verbosity=minimal"`
  -> exit `1`, 1 failed / 1 passed / 2 total in 23 s. A correct resolver-map replacement with a
  launch timestamp 1 ms before its referenced predecessor was incorrectly accepted.
- Final chronology/lifecycle focus:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~ReplacementBrowserLaunchCannotPredatePredecessorLaunch|FullyQualifiedName~BootstrapUnchangedAndBootstrapRotatedLaunchesAreAccepted|FullyQualifiedName~BrowserLaunchEvidenceRejectsInvalidLifecycleTransitions" --logger "console;verbosity=minimal"`
  -> exit `0`, 9 passed / 0 failed / 0 skipped in 1 m 16 s. The rotated-bootstrap positive uses
  launch time exactly equal to its predecessor and passes.
- Focused validator/lifecycle suites:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~BaselineEvidenceValidatorTests|FullyQualifiedName~TrustedEndpointPolicyTests" --logger "console;verbosity=minimal"`
  -> exit `0`, 157 passed / 0 failed / 0 skipped in 10 m 56 s.
- Strict Release build:
  `dotnet build tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore -warnaserror --nologo -v:minimal`
  -> exit `0`, 0 warnings / 0 errors in 1.17 s.
- Full safe serial gate:
  `dotnet test tests/Husaynia.BaselineCapture.Tests/Husaynia.BaselineCapture.Tests.csproj -c Release --no-restore --no-build -m:1 --filter "Category!=Live" --logger "console;verbosity=minimal"`
  -> exit `0`, 488 passed / 0 failed / 0 skipped in 21 m 30 s.

Official unsuppressed locked restores were attempted. The tool restore exited `1` after 8.96 s and
the test restore exited `1` after 8.28 s; the only failure was the existing external `NU1900`
vulnerability-index unavailability at `https://api.nuget.org/v3/index.json`.

Immutable SHA-256 values remained unchanged: `global.json`
`deb68fdcdf73ca5a9b4862010d170ebc117339887aa0aa72d22a04ade7437942`;
`Directory.Packages.props`
`270123f5742917ef35c2d459e040458f888ddf3809fe96f8531c3f20d1f05585`;
capture project `48356df325c6a69c4b529584dd1c131c1992f08764787a375aaa2c933f75d580`;
capture lock `9d737d7bfb7ffe174866ac35ffb42a3fc2f02f5bcd77acc571b8142e265575f3`;
test lock `f2b22d39aca04cbd07867a20d6cc2509013305b56fa4fa6c6ed2922d930831ac`;
executable profile `ba0bcba67acf062411321438e1fe15022b86c1fa04f88e700ae0053289a521f1`.
An exact task-start scan (`LastWriteTime > 2026-08-19T21:52:06-07:00`) returned no files beneath
`evidence`, `src`, or `contracts`. No live capture/network command, evidence mutation, package
change, protected-path write, or Git operation occurred.
