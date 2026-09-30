# Task Plan — CSV Contact Editing and Conflict-Safe Explicit Save

Date: 2026-09-04  
Status: **JUDGMENT — T13-R1 independent test, code review, and QA gates passed; final judgment pending**
Mission: `2026-09-03-build-husaynia-sms-1aba86bb`  
Primary implementer: one Developer; all application-writing tasks are serial.

## Focused rework after independent grid-interaction review

T12-R2.1  Preserve highlighted rows during checkbox input and batch controller-driven checkbox updates
    owner:        developer
    objective:    Fix checkbox/highlight coupling and remove per-row controller callbacks during
                  Select All Valid and Clear Selection.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.Designer.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactEditingWinFormsTests.cs
    depends_on:   T12-R1.4
    parallel_ok:  no
    exit_criteria: Displayed native mouse input on a checkbox preserves the exact previous
                  highlighted-row set for none, one, and multiple rows; Edit/Delete enablement is
                  unchanged; non-checkbox clicks continue to select normally; user checkbox
                  changes synchronize once; 5,000-row Select All/Clear each synchronize once with
                  linear work; existing eligibility/send/editing behavior remains green.
    status:       DONE — a checkbox-aware DataGridView preserves and silently restores prior row
                  highlights around mouse and Space processing, while MainForm batches checkbox
                  value writes and performs one controller synchronization after each bulk update.

T12-R2.2  Developer self-validation for grid-interaction rework
    owner:        developer
    objective:    Prove the fixes are deterministic, linear, credential-free, and regression-safe.
    files:        none
    depends_on:   T12-R2.1
    parallel_ok:  no
    exit_criteria: Focused MainForm/dialog/controller tests pass; native checkbox-click regressions
                  pass at least 20 repetitions; 5,000-row Select All/Clear proves one synchronization
                  per operation; blocked-proxy/no-TWILIO Release build has 0 warnings/errors; full
                  suite is greater than 263 with 0 failed/skipped; format and diff checks pass.
    status:       DONE — focused suite passed 97/97; three native checkbox-click regressions passed
                  20/20 repetitions each (60/60 executions); 5,000-row Select All/Clear passed with
                  one checked-recipient read per operation in 0.901 seconds; blocked-proxy/no-TWILIO
                  Release build reported 0 warnings/errors; full suite passed 268/268 with 0
                  failed/skipped; format and scoped diff checks passed.

T12-R2.3  Independent test, security, and code-review gate reruns
    owner:        test-engineer + security-engineer + code-reviewer
    depends_on:   T12-R2.2
    status:       PENDING

T12-R2.4  QA and final judgment
    owner:        qa-engineer + engineering-judge
    depends_on:   T12-R2.3
    status:       PENDING

### T12-R2 self-validation evidence

- Pre-fix displayed checkbox regression failed because the native mouse-down selected the clicked
  row; the 5,000-row bulk regression observed exactly 5,000 checked-recipient reads.
- Focused displayed MainForm, ContactDialog, and MainController suite: **97 passed, 0 failed,
  0 skipped**.
- Three displayed native checkbox-click regressions: **20/20 repetitions each**, **60/60 total**.
- 5,000-row Select All/Clear regression: **1/1 passed**; each operation caused exactly one
  checked-recipient read/controller synchronization and the executed regression completed in
  **0.901 seconds**.
- With all `TWILIO_*` variables removed and HTTP/HTTPS/ALL proxies blocked at
  `http://127.0.0.1:9`, restore succeeded, the Release build reported **0 warnings, 0 errors**, and
  the full suite passed **268/268 with 0 failed and 0 skipped**.
- `dotnet format .\HusayniaSMS.sln --verify-no-changes --no-restore` and
  `git diff --check -- HusayniaSMS` passed.

## Focused rework after independent T12.10/T12.11 failures

T12-R1.1  Remediate CSV commit ordering, output limits, canonical conflict reload, progress settlement, UI coverage, formula names, and temp-file privacy
    owner:        developer
    objective:    Implement every CTO-requested remediation without weakening existing behavior or tests.
    files:        HusayniaSMS/src/**; HusayniaSMS/tests/**; HusayniaSMS/README.md only as required
    depends_on:   T12.10, T12.11
    parallel_ok:  no
    exit_criteria: Commit is the final outcome transition using an exact temp fingerprint; no
                  post-commit failure/cancellation I/O; serialized/drained progress before
                  settlement; canonical local conflict target after Save As chains; save record
                  and actual UTF-8 byte limits with boundary tests; formula-prefix names rejected
                  through import/dialog/save/writer tests; missing controller/conflict/all-12-button
                  displayed STA tests added; residual cleanup failure surfaced without changing a
                  committed success; destination-equivalent or owner-restricted temp ACL is
                  implemented with a deterministic seam/test if reliable using existing Windows/
                  .NET APIs, otherwise exact investigation evidence is recorded.
    status:       DONE — CSV commit is the final outcome transition with precommit exact-temp
                  fingerprint/version; writer record/UTF-8 byte limits, formula-name rejection,
                  canonical conflict reload, serialized/drained progress, safe cleanup warnings,
                  secure temp ACLs, and missing controller/conflict/displayed-STA regressions are
                  implemented.

T12-R1.2  Developer self-validation
    owner:        developer
    objective:    Prove the rework is clean, deterministic, credential-free, and scoped.
    files:        none
    depends_on:   T12-R1.1
    parallel_ok:  no
    exit_criteria: Focused suites pass; formerly flaky name-only preservation passes 100/100;
                  blocked-proxy/no-TWILIO restore succeeds; Release build is 0 warnings/errors;
                  full suite is greater than 209 with 0 failed/skipped; connected vulnerability
                  audit is attempted; sample hash, legacy-reference scan, and restricted diff pass.
    status:       DONE — Blocked-proxy/no-TWILIO restore succeeded; Release build 0 warnings/errors;
                  focused contact-editing suite passed 163/163; formerly flaky name-only preservation
                  passed 100/100; full suite passed 258/258 with 0 failed/skipped; connected NuGet
                  audit found no vulnerable packages; format, sample hash, legacy/live-provider
                  scans, and restricted diff checks passed.

T12-R1.3  Independent test gate rerun
    owner:        test-engineer
    depends_on:   T12-R1.2
    status:       PENDING

T12-R1.4  Independent security and code-review gate reruns
    owner:        security-engineer + code-reviewer
    depends_on:   T12-R1.2
    status:       PENDING

T12-R1.5  QA and final judgment
    owner:        qa-engineer + engineering-judge
    depends_on:   T12-R1.3, T12-R1.4
    status:       PENDING

### T12-R1 self-validation evidence

Executed from `HusayniaSMS/` on Windows with .NET SDK 10.0.400:

- Cleared all `TWILIO_*` variables and set `HTTP_PROXY`, `HTTPS_PROXY`, and `ALL_PROXY` to
  `http://127.0.0.1:9`; offline restore succeeded.
- `dotnet build .\HusayniaSMS.sln -c Release --no-restore`:
  **0 warnings, 0 errors**.
- Focused validation filter covering contact validation/draft/store/document/controller/dialog/
  displayed WinForms tests: **163 passed, 0 failed, 0 skipped**.
- `EditRequiresExactlyOneHighlightAndNameOnlyPreservesCheckAndResult`: **100/100 passed**.
- Full blocked-proxy/no-TWILIO Release suite: **258 passed, 0 failed, 0 skipped**.
- Connected audited restore plus
  `dotnet list .\HusayniaSMS.sln package --vulnerable --include-transitive --no-restore`:
  no vulnerable packages reported for Core, WinForms, or Tests.
- `dotnet format .\HusayniaSMS.sln --verify-no-changes --no-restore`: pass.
- Sample SHA-256:
  `CCF74771A7331CA0E20C88F5855F153BD3CC7672AF20D61455FBE46366F91162`.
- Legacy CSV importer/ambiguous selection references: 0. Disabled tests: 0. Live Twilio/provider
  construction or endpoint references in tests: 0. `git diff --check -- HusayniaSMS`: pass.

## Authority and current evidence

The CTO decisions and accepted `architecture.md` / ADR-011 through ADR-013 are frozen. Implement
modal Add/Edit, exact-count default-No Delete, explicit Save CSV, invalid/duplicate visibility with
save blocking, ordered in-memory identity, conflict-safe atomic persistence, and dirty
Save/Discard/Cancel guards. No database, service, package, schema, deployment, telemetry,
Git-history, sibling-project, or `.ai-org/active-mission.json` change is authorized.

- Core rows are immutable and keyed by `ImportOrdinal`; duplicates are assigned in input order
  (`HusayniaSMS/src/HusayniaSMS.Core/Contacts/ContactContracts.cs:3-30`,
  `ContactValidation.cs:14-53`).
- CSV is load-only through `IContactCsvImporter`; it has no path version or save result
  (`HusayniaSMS/src/HusayniaSMS.Core/Contacts/CsvImportContracts.cs:3-23`).
- The adapter already enforces strict UTF-8 input and 10 MiB / 100,000-record / 256-header /
  4,096-field limits (`.../Infrastructure/Csv/CsvHelperContactCsvImporter.cs:10-170`).
- `MainController` owns rows, checked ordinals, remembered path, and one non-queuing gate; send
  snapshots are copied in visual order (`.../Presentation/MainController.cs:11-45,127-239,
  275-369,411-477`).
- The view exposes checkbox state as `SelectedOrdinals`; highlighted rows have no contract
  (`.../Presentation/PresentationContracts.cs:31-47`, `.../Forms/MainForm.cs:24-34`).
- The grid is read-only/full-row-select and the common DPI-safe sizing policy covers eight buttons
  (`.../Forms/MainForm.Designer.cs:64-87,146-180,222-231`).
- Closing is guarded only during a send and the post-settlement bypass skips other guards
  (`.../Forms/MainForm.cs:276-286`, `.../Presentation/MainController.cs:381-407`).

## Frozen contracts

Signatures, enum values, semantics, and ownership below are fixed. If one must change, stop,
centrally amend this plan, and re-brief before changing another consumer.

### A. Core contact validation — `HusayniaSMS.Core.Contacts`

```csharp
public sealed record ContactDraft(string? Name, string? Number);

public sealed record ContactDraftValidation(
    string Name,
    string Number,
    IReadOnlyList<ContactErrorCode> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IContactDraftValidator
{
    ContactDraftValidation Validate(
        ContactDraft draft,
        IReadOnlyList<ContactRow> currentRows,
        int? editingOrdinal);
}

public sealed class ContactDraftValidator : IContactDraftValidator
{
    public ContactDraftValidator(IPhoneNumberValidator phoneNumberValidator);
    public ContactDraftValidation Validate(
        ContactDraft draft,
        IReadOnlyList<ContactRow> currentRows,
        int? editingOrdinal);
}

public static class ContactValidationMessages
{
    public static string GetMessage(ContactErrorCode code);
}
```

- Trim both fields; both are required; Number uses the existing E.164 validator unchanged.
- Draft duplicate comparison is `StringComparer.Ordinal` over trimmed numbers that independently
  pass E.164 validation; exclude only `editingOrdinal`. Add passes `null`.
- A draft cannot duplicate another row even when that other row is currently marked duplicate or
  has a name error. Full-document validation retains the existing ordered first-valid-wins rule.
- Draft and row validators share one internal field-validation helper; no second regex or
  normalization path.
- Every mutation rebuilds the complete ordered document through `IContactRowValidator.Validate`.
- Exact messages:
  `Name is required.`;
  `Number is required.`;
  `Number must be in E.164 format, for example +15550100100.`;
  `Another contact already uses this number.`

### B. Core CSV store contract

Keep `CsvImportStatus` as the typed load status until final legacy cleanup.

```csharp
public sealed record ContactCsvVersion(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256Hex);

public sealed record ContactCsvLoadResult(
    CsvImportStatus Status,
    string? FullPath,
    IReadOnlyList<ContactRow> Rows,
    ContactCsvVersion? Version,
    string? SafeDiagnostic);

public enum ContactCsvSaveStatus
{
    Saved = 0,
    InvalidDocument = 1,
    ConflictModified = 2,
    ConflictDeleted = 3,
    TargetExists = 4,
    AccessDenied = 5,
    IoFailure = 6,
    AtomicReplaceUnavailable = 7
}

public sealed record ContactCsvSaveRequest(
    string Path,
    IReadOnlyList<ContactRow> Rows,
    ContactCsvVersion? ExpectedVersion);

public sealed record ContactCsvSaveResult(
    ContactCsvSaveStatus Status,
    string? FullPath,
    ContactCsvVersion? SavedVersion,
    ContactCsvVersion? CurrentVersion,
    string? SafeDiagnostic);

public interface IContactCsvStore
{
    Task<ContactCsvLoadResult> LoadAsync(
        string path, CancellationToken cancellationToken);
    Task<ContactCsvSaveResult> SaveAsync(
        ContactCsvSaveRequest request, CancellationToken cancellationToken);
}
```

- `FullPath` is canonical after successful canonicalization. Path errors map to `IoFailure` without
  raw exception text.
- SHA-256 is uppercase 64-character `Convert.ToHexString` over exact bytes. Record equality is the
  concurrency comparison.
- Successful load returns ordered rows with ordinals 1..N, path, and version. Failures return no
  rows/version and diagnostics never include contact values.
- `SavedVersion` is non-null only for `Saved`. `CurrentVersion` is required for observed
  `ConflictModified`/`TargetExists` and null for `ConflictDeleted`.
- Save defensively revalidates through `IContactRowValidator`; invalid/duplicate input returns
  `InvalidDocument` before opening a target or temp file.

### C. WinForms CSV filesystem seam and platform behavior

```csharp
internal readonly record struct ContactCsvFileMetadata(
    long Length, DateTimeOffset LastWriteTimeUtc);

internal sealed class AtomicReplaceUnavailableException : IOException
{
    public AtomicReplaceUnavailableException(
        string message, Exception? innerException = null);
}

internal interface IContactCsvFileSystem
{
    string GetFullPath(string path);
    bool FileExists(string fullPath);
    ContactCsvFileMetadata GetMetadata(string fullPath);
    Stream OpenRead(string fullPath);
    Stream CreateSiblingTemporaryFile(
        string destinationFullPath, out string temporaryFullPath);
    Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken);
    void CommitNew(string temporaryFullPath, string destinationFullPath);
    void ReplaceExisting(string temporaryFullPath, string destinationFullPath);
    void DeleteFile(string fullPath);
}

internal sealed class WindowsContactCsvFileSystem : IContactCsvFileSystem;

public sealed class CsvHelperContactCsvStore : IContactCsvStore
{
    public CsvHelperContactCsvStore(IContactRowValidator rowValidator);
    internal CsvHelperContactCsvStore(
        IContactRowValidator rowValidator,
        IContactCsvFileSystem fileSystem);
}
```

- Load preserves all current limits/statuses and accepts strict UTF-8 with/without BOM.
- Save writes exact `Name,Number`, visual order, invariant CsvHelper quoting, strict UTF-8 without
  BOM, and header-only output for zero rows.
- Temp is a unique sibling `.<destination>.<guid>.tmp`, `CreateNew`, `FileShare.None`,
  asynchronous + `WriteThrough`; flush is `FlushAsync` then `FileStream.Flush(true)`.
- Existing target: only `File.Replace(temp, destination, null, true)`. New target: only
  `File.Move(temp, destination, overwrite: false)`. Never truncate, delete-then-move, or
  overwrite-move an existing destination.
- `PlatformNotSupportedException`, `NotSupportedException`, and Windows errors
  `ERROR_INVALID_FUNCTION (1)`, `ERROR_NOT_SAME_DEVICE (17)`, or `ERROR_NOT_SUPPORTED (50)` become
  `AtomicReplaceUnavailable`; other access/IO failures retain their typed status.
- Compare expected fingerprint before temp creation and immediately before commit:
  expected+missing -> `ConflictDeleted`; expected+different -> `ConflictModified`;
  no-expected+existing -> `TargetExists`. A raced appearance during new-file commit is
  re-fingerprinted as `TargetExists`.
- Every non-success best-effort deletes only the owned temp. Cleanup failure never changes the
  primary result or destination.

### D. Ordered in-memory document — WinForms Presentation

```csharp
internal sealed class ContactDocumentState
{
    public IReadOnlyList<ContactRow> Rows { get; }
    public string? Path { get; }
    public ContactCsvVersion? Version { get; }
    public bool IsDirty { get; }
    public int NextOrdinal { get; }

    public static ContactDocumentState CreateUntitled();
    public static ContactDocumentState FromLoaded(
        string fullPath,
        ContactCsvVersion version,
        IReadOnlyList<ContactRow> rows);
    public ContactDocumentState Add(
        ContactDraftValidation draft,
        IContactRowValidator rowValidator);
    public ContactDocumentState Edit(
        int ordinal,
        ContactDraftValidation draft,
        IContactRowValidator rowValidator);
    public ContactDocumentState Delete(
        IReadOnlySet<int> ordinals,
        IContactRowValidator rowValidator);
    public ContactDocumentState MarkSaved(
        string fullPath,
        ContactCsvVersion version);
}
```

- Untitled is empty/clean, next ordinal 1. Add appends/increments. Edit preserves ordinal/index.
  Delete never renumbers or lowers `NextOrdinal`; delete-all is valid/dirty.
- Loaded documents are clean and next ordinal is max+1, or 1 for header-only.
- Add/Edit require valid drafts. Each mutation fully revalidates and marks dirty. Only load/save
  marks clean. Ordinals never enter CSV bytes.

### E. View and dialog contracts

```csharp
public sealed record ContactGridRowViewModel(
    int ImportOrdinal,
    bool IsCheckedRecipient,
    bool CanCheckRecipient,
    string Name,
    string Number,
    string ValidationText,
    RecipientSendState? SendState,
    string? ProviderMessageId,
    string? SafeCode,
    string? SafeMessage);

public sealed record ContactDocumentViewState(
    string DisplayName,
    bool IsDirty,
    IReadOnlyList<ContactGridRowViewModel> Rows,
    IReadOnlySet<int> CheckedRecipientOrdinals,
    IReadOnlySet<int> HighlightedContactOrdinals);

public sealed record MainInteractionState(
    bool IsBatchActive,
    bool CanEditSetup,
    bool CanSaveSetup,
    bool CanImport,
    bool CanRefresh,
    bool CanAddContact,
    bool CanEditContact,
    bool CanDeleteContacts,
    bool CanSaveCsv,
    bool CanChangeSelection,
    bool CanEditMessage,
    bool CanSendSelected,
    bool CanSendAllValid,
    bool CanCancel);

public interface IMainView
{
    string MessageText { get; }
    IReadOnlyList<int> CheckedRecipientOrdinals { get; }
    IReadOnlyList<int> HighlightedContactOrdinals { get; }
    SetupInput ReadSetupInput();
    void RenderSettings(SettingsDescriptor descriptor, string savedTokenPlaceholder);
    void RenderDocumentState(ContactDocumentViewState state);
    void ApplyCheckedRecipients(IReadOnlySet<int> checkedOrdinals);
    void FocusContact(int ordinal);
    void RenderMessageValidation(MessageValidationResult result);
    void SetInteractionState(MainInteractionState state);
    void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt);
    void ApplyRecipientProgress(RecipientProgress progress);
    void EndBatch(BatchSummary summary);
    void ShowSafeStatus(string message);
    void ShowSafeError(string message);
    void CloseAfterControllerApproval();
}

public enum ContactDialogMode { Add = 0, Edit = 1 }
public sealed record ContactDialogRequest(
    ContactDialogMode Mode,
    ContactDraft InitialDraft,
    IReadOnlyList<ContactRow> CurrentRows,
    int? EditingOrdinal);
public sealed record ContactDialogResult(string Name, string Number);

public enum PendingAction { Import = 0, Refresh = 1, Exit = 2 }
public enum UnsavedChangesChoice { Save = 0, Discard = 1, Cancel = 2 }
public enum ExternalCsvConflictKind { Modified = 0, Deleted = 1, TargetExists = 2 }
public enum ExternalCsvConflictChoice
{
    ReloadExternal = 0,
    OverwriteThisVersion = 1,
    Recreate = 2,
    SaveAs = 3,
    ChooseAnother = 4,
    Cancel = 5
}
public enum SaveFailureChoice { SaveAs = 0, Cancel = 1 }

public interface IUserDialogs
{
    string? SelectCsvPath();
    bool ConfirmSend(SendScope scope, int recipientCount, bool isSafeDemo);
    CloseDuringBatchChoice ConfirmCloseDuringBatch();
    ContactDialogResult? ShowContactDialog(
        ContactDialogRequest request,
        IContactDraftValidator validator);
    bool ConfirmDeleteContacts(int count);
    UnsavedChangesChoice ConfirmUnsavedChanges(PendingAction action, bool canSave);
    string? SelectCsvSavePath(string? suggestedPath);
    ExternalCsvConflictChoice ResolveExternalCsvConflict(
        ExternalCsvConflictKind kind, string fileName);
    SaveFailureChoice ResolveSaveFailure(
        ContactCsvSaveStatus status, string fileName);
}
```

- Grid fields remain read-only; `FullRowSelect`, `MultiSelect=true`. Checks control sending;
  highlighted rows control Edit/Delete.
- One modal `ContactDialog`: Name/Number max 4,096; inline `ErrorProvider`; OK disabled until valid;
  Enter accepts only valid data; Escape cancels; return values are trimmed.
- Delete states exact count and uses Yes/No with No as default.
- Title is `Husaynia SMS — {DisplayName}` with final `*` while dirty. DisplayName is filename or
  `Unsaved contacts`. Status gives total/valid/invalid and appends `Unsaved changes.` only when
  dirty; no contact values.
- All 12 buttons use the existing AutoSize/GrowAndShrink, minimum-height 36, padding 12/6 policy.

### F. Controller semantics and serialization

```csharp
public void CheckedRecipientsChanged();
public void GridHighlightChanged();
public void AddContact();
public void EditContact();
public void DeleteSelectedContacts();
public Task SaveContactsAsync();
public Task RequestCloseAsync();
```

- Initialization, setup save, import, Refresh, Add/Edit/Delete dialogs, Save, send preflight, and
  active send use the existing non-queuing gate. Dialogs stay inside it. Repeated activation never
  queues.
- Controller owns checked ordinals, highlighted ordinals, and per-ordinal send results. Progress
  updates model and view. Import/Refresh/send-start clear all results.
- Add is unchecked/no-result/highlighted. Name-only Edit preserves check/result/highlight.
  Number-changing Edit clears that row's check/result. Delete removes both and highlights the
  nearest survivor. Full revalidation unchecks every newly ineligible row.
- Successful Import clears checks/highlights/results. Preserve current behavior: the loaded
  candidate replaces the document only after the canonical path is successfully remembered; a
  settings failure retains the complete old document.
- Refresh uses document path, otherwise initialized remembered path. Success preserves eligible
  checks by exact number, clears results, and preserves one highlight only when that number exists.
  Any failure preserves the complete old state.
- Save is enabled only when mutable, dirty, and all rows eligible. Defensive invalid invocation
  focuses the first invalid row and performs no store call.
- First save is Save As (`ExpectedVersion=null`); same-path save uses document version. Canceled or
  failed save changes no state.
- Modified choices: Reload External / Overwrite This Version / Save As / Cancel.
  Deleted: Recreate / Save As / Cancel.
  TargetExists: Overwrite This Version / Choose Another / Cancel.
  Overwrite retries only after explicit choice against `CurrentVersion`; Recreate expects no target;
  another conflict re-prompts, never auto-overwrites.
- AccessDenied, IoFailure, AtomicReplaceUnavailable: Save As / Cancel only.
- Reload External explicitly discards local edits and uses Refresh replacement semantics.
- On `Saved`, update runtime path/version and mark clean before `SaveLastCsvPathAsync`. If settings
  persistence then fails, CSV remains saved, current document remains clean, current-session
  Refresh uses the saved path, and UI warns only that restart may not remember it. This counts as a
  successful save for a pending action.
- Dirty Import/Refresh/Exit use Save/Discard/Cancel. Save must succeed before continuation. Discard
  authorizes replacement but does not clear first; canceled chooser/load failure keeps the dirty
  document. With invalid rows `canSave=false`; a defensive Save choice focuses the first invalid
  row and cancels the pending action.
- Close ordering: active-batch prompt first; Stay aborts; Cancel-and-close cancels and awaits normal
  settlement. Send releases the interaction gate before signaling `_batchSettled`, then dirty guard
  runs. Close during another non-batch interaction is refused.
- MainForm cancels every unapproved `FormClosing`. `CloseAfterControllerApproval` sets a one-shot
  flag and calls `Close`; the next event consumes it. Gate-owned private save/guard methods prevent
  recursive public Save or Close calls.

## Developer tasks

T12.1  Add Core CSV contracts and canonical contact draft validation
    owner:        developer
    objective:    Land frozen Core types without breaking current callers; share field validation.
    files:        HusayniaSMS/src/HusayniaSMS.Core/Contacts/ContactContracts.cs
                  HusayniaSMS/src/HusayniaSMS.Core/Contacts/ContactValidation.cs
                  HusayniaSMS/src/HusayniaSMS.Core/Contacts/ContactCsvStoreContracts.cs (new)
                  HusayniaSMS/tests/HusayniaSMS.Tests/Core/ContactValidationTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Core/ContactDraftValidatorTests.cs (new)
    depends_on:   -
    parallel_ok:  no
    exit_criteria: Core Release build succeeds. Tests prove trim/required/E.164 boundaries, exact
                  messages, ordinal duplicate comparison, duplicate against an otherwise-invalid
                  row, self exclusion, Add null ordinal, and unchanged ordered collection behavior.
                  Keep `IContactCsvImporter` temporarily for compatibility.
    validate:     dotnet build .\src\HusayniaSMS.Core\HusayniaSMS.Core.csproj -c Release --no-restore
                  dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ContactValidationTests|FullyQualifiedName~ContactDraftValidatorTests"
    status:       DONE — Core Release build 0 warnings/errors; focused validation suite 16/16.

T12.2  Implement conflict-safe CsvHelper store and filesystem seam
    owner:        developer
    objective:    Implement load/version/save, no-BOM output, comparisons, and Windows atomic commit.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Infrastructure/Csv/CsvHelperContactCsvStore.cs (new)
                  HusayniaSMS/src/HusayniaSMS.WinForms/Infrastructure/Csv/ContactCsvFileSystem.cs (new)
                  HusayniaSMS/tests/HusayniaSMS.Tests/Csv/CsvHelperContactCsvStoreTests.cs (new)
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/ThrowingStream.cs (new)
    depends_on:   T12.1
    parallel_ok:  no
    exit_criteria: Tests prove every current load case plus canonical path/version; exact header and
                  header-only; no BOM; Unicode/comma/quote/CRLF/4,096 round trips; invalid preflight
                  leaves target untouched; new/same-version save; modified/deleted/appeared
                  conflicts; fingerprint changes; access/IO/missing-dir; write/flush/replace
                  failures; atomic-unavailable mapping; original-byte equality; temp cleanup.
    validate:     dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CsvHelperContactCsvStoreTests"
    status:       DONE — Focused CsvHelper store suite 18/18, including exact bytes, versions,
                  conflicts, failure preservation, cleanup, and cancellation.

T12.3  Implement pure ordered contact document state
    owner:        developer
    objective:    Encapsulate path/version/dirty/next-ordinal invariants and full revalidation.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Presentation/ContactDocumentState.cs (new)
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactDocumentStateTests.cs (new)
    depends_on:   T12.1
    parallel_ok:  no
    exit_criteria: Tests prove clean untitled/load, Add append, Edit in place, Delete gap/delete-all,
                  monotonic non-reuse, dirty/MarkSaved, immutable prior state, full revalidation.
    validate:     dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ContactDocumentStateTests"
    status:       DONE — Focused document-state suite 7/7, including immutable mutation,
                  monotonic ordinals, full revalidation, delete-all, and saved transitions.

T12.4  Add typed presentation/dialog contracts and modal editor
    owner:        developer
    objective:    Land view/dialog shapes, test doubles, prompts, and accessible Add/Edit modal.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Presentation/PresentationContracts.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.cs (new)
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/WinFormsUserDialogs.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeMainView.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeUserDialogs.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactDialogTests.cs (new)
    depends_on:   T12.1
    parallel_ok:  no
    exit_criteria: Solution builds. STA tests prove Add/Edit requests, live inline field errors,
                  duplicate/self behavior, 4,096 limits, disabled OK, valid Enter, Cancel/Escape,
                  trimmed return, exact delete count/default No, and typed conflict/failure choices.
    validate:     dotnet build .\HusayniaSMS.sln -c Release --no-restore
                  dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ContactDialogTests"
    status:       DONE — Solution Release build 0 warnings/errors; focused ContactDialog STA suite
                  4/4 covers validation, self exclusion, accessibility, limits, and shared sizing.

T12.5  Add controller mutations, highlight separation, and result preservation
    owner:        developer
    objective:    Adopt document state; implement Add/Edit/Delete and independent check/highlight APIs.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Presentation/MainController.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/MainControllerTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeMainView.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeUserDialogs.cs
    depends_on:   T12.3, T12.4
    parallel_ok:  no
    exit_criteria: Tests prove Add-before-load/cancel; exact-one Edit; multi-highlight Delete exact
                  count/default No/delete-all; ordering/highlighting; check-highlight independence;
                  name-only preservation; number/delete clearing; ineligible deselection; unchanged
                  immutable send scope/order/count.
    validate:     dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MainControllerTests"
    status:       DONE — Controller-focused suite covers Add/Edit/Delete identity, independent
                  highlighting/checking, preservation and clearing rules, delete-all, and send snapshots.

T12.6  Implement save, conflicts, dirty guards, and close settlement
    owner:        developer
    objective:    Migrate controller to the store and implement all save/guard/close paths.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Presentation/MainController.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/MainControllerTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeMainView.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeUserDialogs.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/TestDoubles/FakeContactCsvStore.cs (new)
    depends_on:   T12.2, T12.5
    parallel_ok:  no
    exit_criteria: Tests cover dirty/save enablement and invalid focus; first/same-path save;
                  check/highlight/result preservation; remembered-path success and partial failure;
                  all modified/deleted/target-exists choices and repeated conflict; access/IO/atomic
                  Save As; all Import/Refresh/Exit Save/Discard/Cancel branches; failed candidate
                  after Discard; delayed click rejection; close during interaction; batch settle then
                  dirty guard; exactly one approved close; no recursive save/close.
    validate:     dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MainControllerTests"
    status:       DONE — Controller save/dirty/conflict/close behavior implemented with typed store
                  outcomes, explicit retry choices, settings partial-success semantics, and gate serialization.

T12.7  Integrate MainForm and remove the legacy importer
    owner:        developer
    objective:    Wire buttons/grid events/rendering/close, compose store, remove old boundary.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.Designer.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Program.cs
                  HusayniaSMS/src/HusayniaSMS.Core/Contacts/CsvImportContracts.cs (delete)
                  HusayniaSMS/src/HusayniaSMS.WinForms/Infrastructure/Csv/CsvHelperContactCsvImporter.cs (delete)
                  HusayniaSMS/tests/HusayniaSMS.Tests/Csv/CsvHelperContactCsvImporterTests.cs (delete)
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/MainControllerTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactEditingWinFormsTests.cs (new)
    depends_on:   T12.6
    parallel_ok:  no
    exit_criteria: No legacy interface/result/ambiguous selection reference remains. STA tests prove
                  12 shared-size buttons, labels, multi-highlight/check independence, enablement,
                  dirty title/status, invalid focus, modal wiring, disabled mutation/save/import/
                  Refresh during delayed operations/send, Cancel during send, one approved close.
    validate:     rg "IContactCsvImporter|CsvImportResult|SelectedOrdinals|CsvHelperContactCsvImporter" .\src .\tests
                  dotnet build .\HusayniaSMS.sln -c Release --no-restore
                  dotnet test .\tests\HusayniaSMS.Tests\HusayniaSMS.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ContactEditingWinFormsTests|FullyQualifiedName~MainControllerTests|FullyQualifiedName~CsvHelperContactCsvStoreTests"
    status:       DONE — Legacy importer and SelectedOrdinals references removed; store composed;
                  four actions and grid events wired; focused controller/store/dialog/STA suite 76/76.

T12.8  Update README
    owner:        developer
    objective:    Document editing, persistence guarantees/recovery, and safe-demo practice.
    files:        HusayniaSMS/README.md
    depends_on:   T12.7
    parallel_ok:  no
    exit_criteria: Covers four actions, checked-vs-highlighted, validation/save blocking, dirty/first
                  Save As/guards, UTF-8/header/quoting/order/header-only, conflicts, read-only/deleted/
                  atomic recovery, path partial success, Refresh/result/send serialization, safe demo.
                  Sample SHA256 remains
                  CCF74771A7331CA0E20C88F5855F153BD3CC7672AF20D61455FBE46366F91162.
    validate:     (Get-FileHash -Algorithm SHA256 .\samples\contacts.sample.csv).Hash
                  Select-String .\README.md -Pattern 'Add Contact','Edit Contact','Delete Selected','Save CSV','Unsaved changes','Reload External','Atomic'
    status:       DONE — README covers four actions, highlighted-vs-checked semantics, dirty guards,
                  exact CSV/atomic/conflict/recovery behavior, Save As, invalid rows, and serialization;
                  sample SHA256 unchanged.

T12.9  Run Developer self-validation and handoff
    owner:        developer
    objective:    Prove a clean credential-free/network-blocked Release baseline.
    files:        none
    depends_on:   T12.8
    parallel_ok:  no
    exit_criteria: Restore succeeds; Release build 0 warnings/errors; full suite 0 failed/0 skipped
                  and total >152; no TWILIO_* credentials/provider sockets/real send; scoped diff only
                  authorized files; sample unchanged; report focused/full counts and original-byte
                  assertions for every forced save failure.
    validate:     dotnet restore .\HusayniaSMS.sln
                  dotnet build .\HusayniaSMS.sln -c Release --no-restore
                  Get-ChildItem Env:TWILIO_* | Remove-Item -ErrorAction SilentlyContinue
                  $env:HTTP_PROXY='http://127.0.0.1:9'; $env:HTTPS_PROXY='http://127.0.0.1:9'; $env:ALL_PROXY='http://127.0.0.1:9'
                  dotnet test .\HusayniaSMS.sln -c Release --no-build --no-restore --logger "console;verbosity=normal"
                  git --no-pager diff -- HusayniaSMS
    status:       DONE — Restore succeeded; Release build 0 warnings/errors; credential-free
                  blocked-proxy full suite passed 204/204 with 0 failed/skipped; focused
                  controller/store suite passed 76/76; legacy references absent; sample SHA256
                  unchanged; scoped status/diff checked.

## Independent gates

T12.10  Independent automated-test gate
    owner:        test-engineer
    objective:    Execute full suite and map every T12 branch to named evidence.
    files:        HusayniaSMS/tests/HusayniaSMS.Tests/** (only if independent gaps are found)
                  .ai-org/missions/2026-09-03-build-husaynia-sms-1aba86bb/test-results.md
    depends_on:   T12.9
    parallel_ok:  yes
    exit_criteria: Network-blocked full suite passes 0 failed/skipped and >152; report maps Core,
                  store, document, controller, STA, original-preservation, conflicts, close, no-send.
    status:       PENDING

T12.11  Independent security/privacy gate
    owner:        security-engineer
    objective:    Audit paths, PII diagnostics, temp files, replacement, overwrite, Twilio/DPAPI.
    files:        .ai-org/missions/2026-09-03-build-husaynia-sms-1aba86bb/security-review.md
    depends_on:   T12.9
    parallel_ok:  yes
    exit_criteria: No unresolved Critical/High; no contact/message/token/raw-exception logging, no
                  unsafe delete-then-move, temp cleanup scoped, overwrite requires versioned consent.
    status:       PENDING

T12.12  Independent code-review gate
    owner:        code-reviewer
    objective:    Review frozen contracts, state invariants, commit behavior, serialization, compatibility.
    files:        .ai-org/missions/2026-09-03-build-husaynia-sms-1aba86bb/code-review.md
    depends_on:   T12.9
    parallel_ok:  yes
    exit_criteria: APPROVED; no stale importer, recursive close/save, identity drift, silent overwrite/
                  data loss, backward-compatibility defect, or application scope violation.
    status:       PENDING

T12.13  Windows safe-demo QA
    owner:        qa-engineer
    objective:    Exercise actual Release UI with temporary CSVs and no credentials/network/provider.
    files:        .ai-org/missions/2026-09-03-build-husaynia-sms-1aba86bb/qa-results.md
    depends_on:   T12.10, T12.11, T12.12
    parallel_ok:  no
    exit_criteria: At least 8/8 scenarios pass: add-before-load Save As; invalid/duplicate repair;
                  checked-number deselection; name-only result preservation; multi-highlight default-No
                  delete; delete-all header-only; dirty Import/Refresh/Exit; external modified/deleted/
                  target-exists; read-only/atomic Save As recovery; delayed safe send disables all
                  mutation/save/import/Refresh controls. TCP/UDP zero, no SMS, clean exit/cleanup.
    status:       PENDING

T12.14  Final judgment
    owner:        engineering-judge
    objective:    Judge the original mission plus frozen contact-editing extension from evidence.
    files:        .ai-org/missions/2026-09-03-build-husaynia-sms-1aba86bb/final-verdict.md
    depends_on:   T12.10, T12.11, T12.12, T12.13
    parallel_ok:  no
    exit_criteria: APPROVED only if every frozen behavior and gate is proven, compatibility/scope
                  hold, and no real send occurred.
    status:       PENDING

## Execution waves

`wave 1: T12.1 -> wave 2: T12.2 -> wave 3: T12.3 -> wave 4: T12.4 -> wave 5: T12.5 -> wave 6: T12.6 -> wave 7: T12.7 -> wave 8: T12.8 -> wave 9: T12.9 -> wave 10: T12.10, T12.11, T12.12 (parallel) -> wave 11: T12.13 -> wave 12: T12.14`

T12.2 and T12.3 are file-disjoint but remain serial because one Developer owns all application
changes. Only independent gates fan out.

## Risks and replan triggers

1. `File.Replace` may be unavailable on a share/filesystem: typed failure + Save As only.
2. Final hash-to-replace TOCTOU cannot be eliminated without locking; recheck and do not overclaim.
3. Save resolution/pending action/close can recurse: private gate-owned core methods are mandatory.
4. Ordinal renumber/reuse can attach checks/results to the wrong person.
5. Highlight and checkbox APIs must remain separate; `SelectedOrdinals` blocks T12.7.
6. Save must defensively revalidate; button enablement is not sufficient.
7. Settings failure after CSV success must not redirty or misreport the data save.
8. If controller tasks require broad churn, extract private pure policy helpers without changing
   frozen public contracts.
9. Four new buttons must pass minimum-window/DPI STA evidence.
10. Stop if another session begins editing an assigned HusayniaSMS file.

## T13 — ADR-014 Visual Studio Designer compatibility and dialog clipping

T13.1  Implement and self-validate designer-compatible forms
    owner:        developer
    objective:    Apply the accepted architecture.md/ADR-014 form split, designer serialization,
                  ContactDialog error-icon gutter/accessibility fix, project nesting, and tests.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.Designer.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.resx
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.Designer.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/RecipientDataGridView.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/HusayniaSMS.WinForms.csproj
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactDialogTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactEditingWinFormsTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/FormDesignerCompatibilityTests.cs
                  this task plan and this session's state file
    constraints:  Preserve all CSV/Twilio/controller behavior; no provider sends; do not touch
                  sibling projects, unrelated files, git history, or active-mission.json.
    exit_criteria: Blocked-proxy/no-TWILIO Release build has 0 warnings/errors; full suite passes
                  with more than 268 tests and no skipped tests; focused dialog/layout/native-grid/
                  5,000-row/progress regressions pass; design-time MSBuild and project metadata
                  inspection pass; installed Visual Studio validation attempted and limitations
                  reported honestly; sample unchanged; scoped format/diff checks pass.
    evidence:     Visual Studio 18.9.2 discovered. Blocked-proxy/no-TWILIO Release build passed
                  with 0 warnings/errors; full suite passed 278/278 with 0 failed/skipped; focused
                  designer/dialog/native-grid/5,000-row/progress/CSV suite passed 77/77; VS MSBuild
                  DesignTimeBuild rebuild passed 0 warnings/errors; evaluated items show both base
                  files SubType=Form and correct Designer/resx DependentUpon metadata; devenv build
                  succeeded 2/2 projects. Displayed 120-DPI minimum-size probes passed at system,
                  12pt, and 16pt fonts with 24x24 icons fully inside the 597x244 client area.
                  dotnet format and tracked/untracked whitespace checks passed; sample SHA256 stayed
                  CCF74771A7331CA0E20C88F5855F153BD3CC7672AF20D61455FBE46366F91162; no app
                  process/provider send remained. An isolated Visual Studio 18.9.2 DTE instance
                  opened both MainForm.cs and ContactDialog.cs with the designer view kind and
                  closed them without saving; no application/designer-specific ActivityLog errors
                  were recorded. Interactive cosmetic edit/save/undo remains for independent QA.
    status:       DONE

## T13-R1 — Remaining interactive Visual Studio Designer QA remediation

T13-R1.1  Fix and self-validate round-trip warnings, grid selection, and live dialog inspection
    owner:        developer
    objective:    Close the three failures recorded in qa-results.md without changing runtime
                  contact/grid/send semantics.
    files:        HusayniaSMS/src/HusayniaSMS.WinForms/Forms/MainForm.Designer.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/ContactDialog.Designer.cs
                  HusayniaSMS/src/HusayniaSMS.WinForms/Forms/RecipientDataGridView.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/FormDesignerCompatibilityTests.cs
                  HusayniaSMS/tests/HusayniaSMS.Tests/Presentation/ContactEditingWinFormsTests.cs
                  this task plan and this session state file
    exit_criteria: Both components fields use conventional explicit null initializers; the existing
                  grid is a public top-level designer-constructible control and is selectable by
                  component name; real VS 18.9.2 cosmetic save/build/restore round trips pass for
                  both forms with exact final hashes; live Release Add Contact inspection is
                  completed by a stable native route or honestly supplemented by an in-process
                  Release-assembly modal probe; final build/tests/layout/no-send checks pass.
    status:       DONE — pre-fix regressions failed 3/7 as expected. Both designer component fields
                  now use `= null;`; RecipientDataGridView is public, top-level, sealed, has the
                  same parameterless constructor/behavior, and is hidden from IntelliSense. In the
                  isolated HsmsQA1aba86bbNoScale Visual Studio 18.9.2 profile, MainForm exposed 44
                  components including `contactsGrid:HusayniaSMS.WinForms.Forms.RecipientDataGridView`;
                  committing that selection produced the grid property model. GridColor was changed
                  WindowFrame -> Silver through Properties, saved, and design-time rebuilt with 0
                  warnings/errors. ContactDialog Text was changed Add Contact -> QA TEMP Contact
                  Dialog through Properties, saved, and design-time rebuilt with 0 warnings/errors.
                  Both forms were reverted and their exact six pre-round-trip SHA-256 values restored
                  without git reset/checkout; the final clean design-time build again passed 0/0.
                  The actual Release safe-demo app exposed enabled UIA `addContactButton` with Name
                  `Add Contact`; a physical native click opened the real modal. Because cross-process
                  UIA did not expose ErrorProvider descriptions/icons, a temporary in-process probe
                  referencing the built Release assemblies displayed the same runtime dialog and
                  measured both exact required errors, 24x24 icons, MiddleRight/padding 4, and icon
                  rectangles fully inside the 597x253 client at 120 DPI. Release build passed 0/0;
                  full suite 279/279; focused designer/dialog/native-grid/progress/CSV suite 70/70;
                  format/diff/sample/no-TWILIO/process/temp cleanup checks passed.

T13-R1.2  Independent test, code-review, and interactive QA reruns
    owner:        test-engineer + code-reviewer + qa-engineer
    depends_on:   T13-R1.1
    status:       DONE — test gate passed 279/279 plus focused/repeated/designer/runtime probes;
                  code review APPROVED; final independent QA passed 9/9 with real VS 18.9.2
                  round trips, actual Release modal validation, 120-DPI system/12pt/16pt
                  icon/layout evidence, exact byte restoration, and no provider traffic.

T13-R1.3  Independent final judgment
    owner:        engineering-judge
    depends_on:   T13-R1.2
    status:       PENDING
T13.2  Independent automated-test and code-review gates
    owner:        test-engineer + code-reviewer
    depends_on:   T13.1
    exit_criteria: Independent full suite/layout probes pass and code review APPROVED.
    status:       PENDING

T13.3  Independent Visual Studio/UI QA gate
    owner:        qa-engineer
    depends_on:   T13.2
    exit_criteria: Both forms open with View Designer, nest correctly, round-trip a harmless
                  cosmetic edit, and display unclipped validation affordances at required metrics.
    status:       DONE — final independent QA passed 9/9 on 2026-09-05; see qa-results.md.

T13.4  Independent judgment
    owner:        engineering-judge
    depends_on:   T13.2, T13.3
    exit_criteria: APPROVED against the accepted designer-compatibility objective and evidence.
    status:       PENDING
