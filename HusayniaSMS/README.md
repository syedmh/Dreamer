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
**Refresh** to reload exactly that remembered path without opening the file chooser. If no path has
been remembered, the app explains that a successful import is required first.

CsvHelper handles quoted commas, escaped quotes (`""`), and quoted line breaks. Name and number
values are trimmed. Invalid rows remain visible but cannot be selected or sent. Blank names, blank
numbers, and invalid E.164 numbers receive specific errors. For repeated valid trimmed numbers, the
first row is eligible and later rows are marked duplicate. A malformed/unreadable file does not
replace the current grid. A header-only file successfully replaces the grid with zero contacts.
Imports above 100,000 logical records, 10 MiB per file, 256 characters per header, or 4,096
characters per field are rejected without partial replacement.

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
that could overlap setup, import, refresh, initialization, send preflight, or an active batch are
serialized; Import and Refresh cannot queue or overlap each other. Message editing and other
batch-mutating controls stay disabled throughout send preflight and the active batch. Cancel and
window processing remain available during the active batch. Repeated activation cannot queue a
second batch. There are no automatic retries: a timeout might have reached Twilio, so a retry could
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
