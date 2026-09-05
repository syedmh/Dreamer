# HusayniaSMS

HusayniaSMS is a Windows Forms desktop utility for importing contacts from CSV, reviewing
validation results, selecting recipients, and sending a confirmed batch through Twilio. It targets
`net8.0-windows` and can be opened in Visual Studio as `HusayniaSMS.sln`.

## Prerequisites

- Windows 10 or later.
- Visual Studio 2022 with the **.NET desktop development** workload, or .NET SDK `10.0.400`.
- A Twilio account, Account SID, auth token, and either an SMS-capable From phone number or a
  configured Messaging Service SID only when using production mode. No credential is required to
  build, test, or use safe-demo mode.

## Restore, build, test, and run

From this directory:

```powershell
dotnet restore .\HusayniaSMS.sln
dotnet build .\HusayniaSMS.sln -c Release --no-restore
dotnet test .\HusayniaSMS.sln -c Release --no-build --no-restore --logger "console;verbosity=normal"
dotnet run --project .\src\HusayniaSMS.WinForms\HusayniaSMS.WinForms.csproj
```

The normal connected restore above is recommended because it retains NuGet vulnerability auditing.
For an intentionally offline or network-isolated restore, when the required packages are already
available locally, use:

```powershell
dotnet restore .\HusayniaSMS.sln --ignore-failed-sources -p:NuGetAudit=false
```

Then run the same `--no-restore` build and `--no-build --no-restore` test commands above. Disabling
`NuGetAudit` is only an offline fallback; do not use it instead of connected vulnerability auditing.

The no-argument run is **production mode**. It uses the official Twilio SDK and can send live SMS
only after valid setup and an explicit confirmation.

For a network-free, fail-closed demonstration:

```powershell
dotnet run --project .\src\HusayniaSMS.WinForms\HusayniaSMS.WinForms.csproj -- --safe-demo
dotnet run --project .\src\HusayniaSMS.WinForms\HusayniaSMS.WinForms.csproj -- --safe-demo --scenario=mixed
dotnet run --project .\src\HusayniaSMS.WinForms\HusayniaSMS.WinForms.csproj -- --safe-demo --scenario=auth-failure
dotnet run --project .\src\HusayniaSMS.WinForms\HusayniaSMS.WinForms.csproj -- --safe-demo --scenario=delayed
```

Safe-demo scenarios are `all-success` (the default), `mixed`, `auth-failure`, and `delayed`. The
window always displays **SAFE DEMO — NO SMS WILL BE SENT**. Unknown, duplicate, or malformed
arguments stop at startup; they never fall back to production.

## Twilio setup

- Account SID: `AC` followed by exactly 32 hexadecimal characters.
- Sender mode: choose exactly one visible option:
  - **From phone number** — the sender value must match `^\+[1-9]\d{7,14}$`: `+` followed by
    8–15 digits total, with a non-zero first digit.
  - **Messaging Service SID** — the sender value must match `^MG[0-9A-Fa-f]{32}$`: uppercase
    `MG` followed by exactly 32 hexadecimal characters.
- Recipient numbers use the same conservative E.164 phone-number format. Spaces, punctuation,
  extensions, lowercase `mg`, and automatic normalization are rejected.
- Auth token: required and treated as opaque text.

Changing sender mode keeps the single visible sender value so a cross-mode mismatch produces an
actionable validation error instead of silently discarding text. The app sends with exactly one
Twilio option: `From` for **From phone number**, or `MessagingServiceSid` for
**Messaging Service SID**. It never submits both and never treats an `MG...` SID as a phone number.

For safe-demo UI practice, values may be syntactically valid fakes. Either of these sender choices
can be saved and exercised without a provider call:

- Account SID: `AC0123456789abcdef0123456789ABCDEF`
- From phone number: `+12025550100`
- Messaging Service SID: `MG0123456789abcdef0123456789ABCDEF`
- Token: `safe-demo-only`

The token is protected with Windows DPAPI `CurrentUser` and stored only as base64 ciphertext.
Production settings are in `%LOCALAPPDATA%\HusayniaSMS\settings.v1.json`; safe-demo settings are
isolated in `%LOCALAPPDATA%\HusayniaSMS\SafeDemo\settings.v1.json`. Account SID, selected sender
mode, and the one active sender value are non-secret configuration. The selected mode and value are
restored after restart. The last successfully imported CSV path is also stored there as non-secret
configuration so **Refresh** survives an application restart. CSV contents, contacts, selections,
messages, and send outcomes are never persisted.

For backward compatibility, schema version 1 and the existing `settings.v1.json` file remain in
place. The historical JSON property `senderNumber` is the single sender-value slot for either mode,
and new setup saves add `senderMode` as `fromPhoneNumber` or `messagingServiceSid`. Only a missing
or `null` legacy `senderMode` defaults to **From phone number**. Unknown, numeric, malformed, or
undefined modes are treated as corrupt settings and require setup review; they never silently
default. Loading legacy settings does not rewrite the file or re-encrypt the protected token.
Saving setup preserves the remembered CSV path, and saving a CSV path preserves the selected
mode/value and exact protected token ciphertext unless the token is explicitly replaced.

DPAPI normally prevents another Windows user from decrypting a copied settings file, but it does not
protect against software already running as the same Windows user. A Windows profile reset, copied
file, corrupt JSON, or corrupt ciphertext may make the token unavailable. The application then
opens safely, masks or clears the token field, reports that setup is incomplete, and requires the
token to be re-entered. To reset settings, close the app and delete the applicable directory above.
To manually verify user scope, save a test token under one Windows account, copy the JSON file to a
second account's matching settings directory, and confirm that the second account is asked to
re-enter the token.

## CSV format and validation

Import `samples\contacts.sample.csv` to see safe fictitious examples. A CSV must contain exactly one
`Name` header and one `Number` header. Header matching is case-insensitive, ignores surrounding
spaces, and tolerates a UTF-8 BOM. Additional uniquely named columns are ignored.

Use **Import CSV** to browse for a file. The chosen path is remembered only after the complete CSV
has imported successfully and the local settings update succeeds; canceling the chooser, a CSV
failure, or a settings-write failure leaves the previous remembered path and grid unchanged. Use
**Refresh** to reload the current document path, or the remembered path after restart, without
opening the file chooser. If no path is available, use Import CSV or save a new document first.

## Add, edit, delete, and explicitly save contacts

Contact cells remain read-only. Use the modal contact editor instead:

- **Add Contact** opens a blank Name/Number dialog and is available before any CSV is loaded. The
  new contact is appended in memory and highlighted, but it is not written to disk automatically.
- **Edit Contact** requires exactly one highlighted grid row. A Name-only change preserves that
  row's checked-recipient state and latest send result. Changing Number clears that row's check and
  stale result.
- **Delete Selected** acts on highlighted rows, not checked recipients. It supports multiple
  highlighted rows and always asks for confirmation with the exact count; **No** is the default.
- **Save CSV** explicitly persists the current visual order. The first save of an untitled document
  uses **Save As**. Deleting every row and saving produces a header-only CSV.

Checked boxes continue to control SMS recipient selection. Blue/highlighted rows independently
control Edit Contact and Delete Selected. Highlighting a row never checks it for sending, and
checking a row never selects it for editing or deletion.

The editor trims surrounding whitespace and control characters from both fields, requires a Name
and Number, applies the existing E.164 rule, and rejects a number already used by another row while
excluding the row currently being edited. Names whose normalized first character is `=`, `+`, `-`,
or `@` are rejected rather than silently altered, preventing saved contact names from becoming
spreadsheet formulas. Imported invalid and duplicate rows remain visible so they can be repaired or
deleted. The complete document is revalidated after every add, edit, or delete. **Save CSV** is
blocked until every remaining row is valid and unique, but valid in-memory rows can still be checked
and sent before saving.

An asterisk in the window title and **Unsaved changes.** in contact status indicate a dirty
in-memory document. Before Import, Refresh, or Exit, the app offers **Save / Discard / Cancel**.
Save must complete before the pending action continues. Discard does not clear the current document
until the replacement CSV loads successfully, so a canceled chooser or failed load leaves local
changes intact. Exit first settles an active send, then applies the same dirty guard.

CsvHelper handles quoted commas, escaped quotes (`""`), and quoted line breaks. Name and number
values are trimmed. Invalid rows remain visible but cannot be selected or sent. Blank names, blank
numbers, and invalid E.164 numbers receive specific errors. For repeated valid trimmed numbers, the
first row is eligible and later rows are marked duplicate. A malformed/unreadable file does not
replace the current grid. A header-only file successfully replaces the grid with zero contacts.
Imports above 100,000 logical records, 10 MiB per file, 256 characters per header, or 4,096
characters per field are rejected without partial replacement.

Saved files always use strict UTF-8 without a BOM, exact `Name,Number` headers, normal CsvHelper
quoting, and the grid's current visual order. Contact ordinals, checks, highlights, and send results
are session-only and never enter CSV bytes. Save enforces the same 100,000-record, 10 MiB serialized
UTF-8, and 4,096-character field limits before touching the destination.

CSV saving is conflict-safe and fail-closed. The app compares the loaded file's length, UTC
last-write time, and SHA-256 immediately before committing a sibling temporary file. The temporary
file receives the existing destination's protected access rules, or a protected current-user-only
access rule for a new destination, before contact bytes are written. Existing files are committed
only with atomic replacement; new files use a non-overwriting atomic move. The exact saved version
is computed from the flushed temporary bytes and timestamp before that final commit, so no
cancelable fingerprint read occurs after the destination changes. The app never truncates the
original, deletes it before moving, or silently overwrites an external change.

- If the file changed externally, choose **Reload External**, **Overwrite This Version**,
  **Save As**, or **Cancel**.
- If it was deleted externally, choose **Recreate**, **Save As**, or **Cancel**.
- If a Save As target appeared, choose **Overwrite This Version**, **Choose Another**, or
  **Cancel**.
- If access is denied, the target is read-only, an I/O failure occurs, or Atomic replacement is
  unavailable on the filesystem/share, choose **Save As** or **Cancel**.

Every overwrite is tied to the version the user explicitly chose; another external change prompts
again. Any canceled or failed save leaves the in-memory rows, current path/version, checks,
highlights, results, and dirty state unchanged. If the CSV save succeeds but saving its remembered
path in settings fails, the document remains clean and usable at the new path for this session; a
warning explains that Refresh after restart might not remember it. A temporary-file cleanup problem
is also surfaced with a safe warning without changing an already committed save into a failure.

A successful Refresh transactionally replaces the grid and clears prior send results. Checked
recipients are preserved when the refreshed CSV still contains an eligible row with the same
validated phone number; the status reports how many selections were retained. Any Refresh failure,
including a missing/inaccessible file, malformed or invalid UTF-8 CSV, or size-limit rejection,
preserves the current contacts, checked selections, displayed results, and remembered path.

## Selection, messages, confirmation, and results

- **Select All Valid** checks only eligible rows; **Clear Selection** clears them.
- **Send Selected** uses only checked eligible rows.
- **Send All Valid** uses every eligible row, independent of current checks.
- A selected send with no eligible checked row is rejected before confirmation.
- Every accepted batch shows its scope and exact count and requires explicit confirmation.
- The confirmed recipient list is an immutable snapshot; later selection changes cannot add a
  recipient.
- A message must contain a non-whitespace Unicode scalar value and contain at most 1,600 Unicode
  scalar values. The visible counter uses the same rule. Valid text, including intentional leading
  or trailing whitespace, is sent unchanged.

Recipients are attempted sequentially, with one request in flight and one active batch. Interactions
that could overlap setup, Import, Refresh, Add/Edit/Delete dialogs, Save CSV, initialization, send
preflight, or an active batch are serialized; duplicate mutations do not queue. Message editing and
all contact mutation/persistence controls stay disabled throughout send preflight and the active
batch. Cancel and window processing remain available during the active batch. The confirmed
recipient/message/credential snapshot is immutable even if local contacts are later edited after
settlement. There are no automatic retries: a timeout might have reached Twilio, so a retry could
duplicate an SMS.

Production mode uses the official Twilio SDK and may create billable live messages after
confirmation. Verify the selected sender mode and value in Twilio before sending, especially after
switching between a From phone number and a Messaging Service SID. Safe-demo supports both modes,
uses deterministic fake responses, creates no live Twilio client, and has no fallback to production.

Recipient-specific rejection, provider, rate-limit, and network failures are sanitized and recorded
per row while unaffected recipients continue. Authentication or sender-configuration failures stop
new starts and mark remaining recipients not sent. Final totals always reconcile confirmed,
succeeded, failed, and canceled/not-started recipients. Network-unknown results should be checked in
the provider console before a newly confirmed manual retry. An unexpected transport exception is
treated as delivery-ambiguous, stops later recipients from starting, preserves completed results,
and requires provider-state verification before retrying.

Canceling stops new recipients after cancellation is observed; the one request already in flight may
settle. Closing during a batch offers to stay open or cancel remaining work and close after normal
settlement. After any completed, failed, or canceled batch, correct the setup/data and explicitly
confirm a new batch.

## Safety and privacy

Automated tests use handwritten fake transports and never construct the production Twilio transport,
read environment credentials, require network access, or send a real SMS. The sample uses reserved
fictional NANP `555-01xx` numbers. The application has no telemetry, database, backend, or persistent
log. Status and result output contains only locally authored diagnostics, safe provider categories
and codes, batch counts/IDs, and provider message SID on success; raw provider errors, auth tokens,
message text, and CSV record content are not logged.
