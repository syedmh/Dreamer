# HusayniaSMS Requirements

## 1. Grounding and classification

- **FACT F-01:** The mission is to create a C# Windows desktop application that imports contacts from CSV, displays them in a selectable grid, and sends SMS through Twilio to selected contacts or all valid contacts. Evidence: `mission.md:3-5`.
- **FACT F-02:** The application target is Windows Forms on `net8.0-windows` and must be usable from Visual Studio. Evidence: `mission.md:7-9`; `decisions.md:3-6`.
- **FACT F-03:** `HusayniaSMS/` was empty at mission start; this is a greenfield application. Evidence: `mission.md:11-15`.
- **FACT F-04:** The approved technology constraints include the official Twilio SDK, an established CSV parser, Windows DPAPI, and validation that never sends a real SMS. Evidence: `decisions.md:3-6`.
- **FACT F-05:** Sibling projects, unrelated working-tree changes, and the legacy `.ai-org/active-mission.json` must remain untouched. Evidence: `mission.md:16-18`; `decisions.md:8-11`.
- **OPEN QUESTION:** None. The CTO-provided defaults are resolved and are not subject to re-escalation for this mission.

## 2. Assumptions used to make the contract falsifiable

- **ASSUMPTION A-01:** “Conservative E.164” means accepting only `+` followed by 8–15 digits total, with the first digit non-zero (`^\+[1-9]\d{7,14}$`). No punctuation, extensions, national-format conversion, or automatic normalization is performed.
- **ASSUMPTION A-02:** Contact `Name` and `Number` values are trimmed; both are required. Header matching is case-insensitive after trimming and tolerates a UTF-8 BOM.
- **ASSUMPTION A-03:** A message is valid when it contains at least one non-whitespace character and is no longer than 1,600 Unicode characters. The exact text entered, including intentional leading/trailing whitespace, is sent after validation.
- **ASSUMPTION A-04:** On a structurally valid CSV, invalid rows remain visible with an error and are ineligible for selection or sending. A structurally invalid CSV does not replace the currently displayed contact set.
- **ASSUMPTION A-05:** Repeated valid phone numbers in one import are treated as duplicates: the first occurrence is eligible and later occurrences are visible but invalid, preventing accidental duplicate sends.
- **ASSUMPTION A-06:** Cancellation is cooperative: it prevents additional recipients from starting, but cannot recall a request already accepted or in flight.
- **ASSUMPTION A-07:** Account SID and sender number may be stored in per-user settings as non-secret configuration; only the auth token requires DPAPI protection. Imported contacts and message text are not persisted by the application.
- **ASSUMPTION A-08:** A newly successful import replaces the current grid only after the entire CSV has been parsed; canceling file selection leaves the current grid unchanged.

## 3. Functional and non-functional requirements

### R-01 — Application baseline

- **REQUIREMENT R-01:** Provide a buildable C# Windows Forms desktop solution under `HusayniaSMS/`, targeting `net8.0-windows`, that starts without requiring Twilio credentials merely to open the UI.

**Acceptance criteria**

- **REQUIREMENT AC-01:** **Given** the documented prerequisites are installed, **when** the documented restore/build command is run from a clean checkout, **then** the solution restores and builds with zero errors.
- **REQUIREMENT AC-02:** **Given** no saved settings exist, **when** the application starts, **then** its main window opens without an unhandled exception and identifies that Twilio setup is incomplete.

### R-02 — Local Twilio setup

- **REQUIREMENT R-02:** Allow the user to enter, validate, save, and reload a Twilio Account SID, auth token, and sender number in per-user local settings.

**Acceptance criteria**

- **REQUIREMENT AC-03:** **Given** a missing Account SID, blank auth token, or sender number invalid under A-01, **when** the user attempts to save or send, **then** the application identifies each offending field, starts no send, and makes no Twilio API call.
- **REQUIREMENT AC-04:** **Given** an Account SID not matching `AC` followed by 32 hexadecimal characters, **when** setup is validated, **then** it is rejected locally with a field-specific error.
- **REQUIREMENT AC-05:** **Given** valid setup values, **when** the user saves them and restarts the application under the same Windows user, **then** the Account SID, sender number, and usable auth token are restored.

### R-03 — Auth-token protection and recovery

- **REQUIREMENT R-03:** Protect the saved Twilio auth token with Windows DPAPI using current-user scope; never persist or display the token in plaintext after it has been saved.

**Acceptance criteria**

- **REQUIREMENT AC-06:** **Given** a token is saved, **when** the per-user settings files are inspected, **then** the literal token value is absent and the stored protected value cannot be decrypted under a different Windows user context.
- **REQUIREMENT AC-07:** **Given** a saved protected token is corrupt, copied from another Windows user, or otherwise cannot be decrypted, **when** settings load, **then** the application does not crash, treats the token as missing, tells the user to re-enter it, and cannot send.
- **REQUIREMENT AC-08:** **Given** a saved token is shown in the UI after reload, **when** the user views setup, **then** the control masks the value and no status, exception, log, or result view reveals it.

### R-04 — Robust CSV import

- **REQUIREMENT R-04:** Import contacts from a user-selected CSV using an established CSV parsing library that correctly handles quoted fields, embedded commas, escaped quotes, and quoted line breaks.

**Acceptance criteria**

- **REQUIREMENT AC-09:** **Given** a CSV with `Name` and `Number` headers and valid rows, **when** it is imported, **then** every row is represented once with the parsed name and number.
- **REQUIREMENT AC-10:** **Given** names containing commas, escaped double quotes, or quoted line breaks, **when** the CSV is imported, **then** those names are parsed as single field values rather than split into extra rows or columns.
- **REQUIREMENT AC-11:** **Given** file selection is canceled, **when** the chooser closes, **then** the current contacts and selection remain unchanged.
- **REQUIREMENT AC-12:** **Given** a missing file, unreadable file, malformed quoting, inconsistent record structure that prevents reliable parsing, or absent required header, **when** import is attempted, **then** no partial replacement occurs, the existing grid remains unchanged, and a non-secret diagnostic identifies the file-level problem.
- **REQUIREMENT AC-13:** **Given** an empty CSV containing valid headers only, **when** imported, **then** the grid becomes empty, the UI reports zero contacts, and sending is disabled.

### R-05 — Row validation and selectable grid

- **REQUIREMENT R-05:** Display imported contacts in a grid that exposes selection, name, number, validation state, validation error, and latest send result; only valid, non-duplicate rows are eligible for selection or sending.

**Acceptance criteria**

- **REQUIREMENT AC-14:** **Given** rows with a blank name, blank number, or number not matching A-01, **when** import completes, **then** each row remains visible, is marked invalid with a specific reason, cannot be selected, and is excluded from every send scope.
- **REQUIREMENT AC-15:** **Given** two or more rows have the same trimmed valid number, **when** import completes, **then** only the first occurrence is eligible and each later occurrence is marked as a duplicate and excluded.
- **REQUIREMENT AC-16:** **Given** a mixture of valid and invalid rows, **when** the user selects all or clears selection, **then** select-all affects only eligible rows and clear-selection leaves no eligible row selected.

### R-06 — Message validation

- **REQUIREMENT R-06:** Validate the message before confirmation or sending and provide a visible character count and actionable validation error.

**Acceptance criteria**

- **REQUIREMENT AC-17:** **Given** an empty, whitespace-only, or greater-than-1,600-character message, **when** the user attempts to send, **then** no confirmation is shown, no batch starts, no Twilio call occurs, and the message error states the allowed bounds.
- **REQUIREMENT AC-18:** **Given** a message of 1–1,600 characters containing at least one non-whitespace character, **when** all other prerequisites are satisfied, **then** message validation permits confirmation and the unchanged message text is supplied to each send request.

### R-07 — Explicit send scope and confirmation

- **REQUIREMENT R-07:** Provide separate, unambiguous actions for sending to selected eligible contacts and sending to all eligible contacts. Every batch requires explicit user confirmation before any Twilio request.

**Acceptance criteria**

- **REQUIREMENT AC-19:** **Given** no eligible contact is selected, **when** “Send Selected” is invoked, **then** no confirmation or send starts and the UI explains that at least one valid contact must be selected.
- **REQUIREMENT AC-20:** **Given** one or more eligible contacts are selected, **when** “Send Selected” is invoked, **then** confirmation identifies the selected scope and exact recipient count; canceling confirmation produces zero Twilio calls.
- **REQUIREMENT AC-21:** **Given** valid contacts exist regardless of current selection, **when** “Send All Valid” is invoked, **then** confirmation explicitly says all valid contacts and gives the exact eligible count; canceling confirmation produces zero Twilio calls.
- **REQUIREMENT AC-22:** **Given** a confirmation is accepted, **when** sending begins, **then** the recipient snapshot is the confirmed eligible set; later UI selection changes cannot silently add recipients to that batch.

### R-08 — Asynchronous batch sending and duplicate-submit prevention

- **REQUIREMENT R-08:** Send asynchronously through Twilio without blocking the Windows UI, permit only one active batch, and prevent repeated clicks or key activation from creating duplicate batches.

**Acceptance criteria**

- **REQUIREMENT AC-23:** **Given** a fake sender that delays each response, **when** a batch is active, **then** the window continues to repaint and process its cancel/close controls without an unhandled cross-thread exception.
- **REQUIREMENT AC-24:** **Given** a confirmed batch is active, **when** any send action is clicked or activated repeatedly, **then** exactly one batch exists, each confirmed recipient is submitted at most once by that batch, and send/import/setup controls that could mutate the batch are disabled or otherwise rejected until it settles.

### R-09 — Twilio integration contract

- **REQUIREMENT R-09:** Use the official Twilio .NET SDK pinned to an exact stable package version. Each request must use the configured sender number, the validated recipient number, and the validated message.

**Acceptance criteria**

- **REQUIREMENT AC-25:** **Given** package manifests are inspected, **when** Twilio dependencies are reviewed, **then** the official Twilio package has an exact version with no wildcard/floating range and no unofficial SMS transport is used.
- **REQUIREMENT AC-26:** **Given** a confirmed batch using a recording fake, **when** sends are captured, **then** there is one request per attempted recipient and each contains the configured sender, that row’s recipient number, and the unchanged validated message.

### R-10 — Per-recipient outcomes and partial failure

- **REQUIREMENT R-10:** Record and display an outcome for every confirmed recipient, preserve successful outcomes when other recipients fail, and present final totals for succeeded, failed, and canceled/not-started recipients.

**Acceptance criteria**

- **REQUIREMENT AC-27:** **Given** all fake Twilio calls succeed, **when** the batch completes, **then** every recipient shows success with a non-secret provider identifier when available and the summary counts equal the confirmed recipient count.
- **REQUIREMENT AC-28:** **Given** one or more recipient-specific Twilio rejections or network failures while other calls succeed, **when** the batch settles, **then** successful rows remain successful, failed rows show sanitized actionable errors, unaffected recipients continue to be attempted, and final totals reconcile to the confirmed count.
- **REQUIREMENT AC-29:** **Given** a batch-wide authentication/configuration failure, **when** it is detected, **then** the application stops starting additional sends, preserves completed outcomes, marks remaining recipients not sent, identifies setup as the likely corrective action, and does not crash.
- **REQUIREMENT AC-30:** **Given** a Twilio exception or network error contains credentials or sensitive request detail, **when** it is displayed or recorded, **then** auth tokens and other secrets are redacted.

### R-11 — Cancellation and window closing

- **REQUIREMENT R-11:** Allow an active batch to be canceled safely and prevent accidental application closure from obscuring in-progress results.

**Acceptance criteria**

- **REQUIREMENT AC-31:** **Given** an active delayed batch, **when** the user requests cancellation, **then** no additional recipient starts after cancellation is observed, in-flight work may settle, completed results remain visible, remaining rows are marked canceled/not sent, and totals reconcile.
- **REQUIREMENT AC-32:** **Given** a batch is active, **when** the user closes the window, **then** the application warns that sends may be in flight and offers to stay or cancel remaining work; choosing stay leaves the batch running, and choosing cancel-and-close follows AC-31 before shutdown completes.

### R-12 — Error containment and repeatability

- **REQUIREMENT R-12:** Expected file, settings, validation, DPAPI, network, and Twilio failures must be handled without process termination; after a batch settles, the user can correct data/setup and start a new independently confirmed batch.

**Acceptance criteria**

- **REQUIREMENT AC-33:** **Given** any expected failure covered by AC-03, AC-07, AC-12, AC-14, AC-17, or AC-28–AC-32, **when** it occurs, **then** the UI remains usable and no unhandled exception terminates the process.
- **REQUIREMENT AC-34:** **Given** a prior batch completed, failed, or was canceled, **when** the user corrects prerequisites and confirms a new batch, **then** the new batch can run once and its results are distinguishable from stale outcomes.

### R-13 — No-real-send testability

- **REQUIREMENT R-13:** Automated and QA validation must use fakes/test doubles and be incapable of sending a real SMS or requiring live Twilio credentials.

**Acceptance criteria**

- **REQUIREMENT AC-35:** **Given** the complete automated test suite runs on a machine with no Twilio credentials and no network access, **when** it exercises success, delay, rejection, exception, cancellation, and partial-failure cases, **then** all tests can pass and zero real Twilio request is made.
- **REQUIREMENT AC-36:** **Given** test source and configuration are inspected, **when** sender dependencies are reviewed, **then** send assertions are made against fakes/recording doubles, no production credential is present, and tests have no fallback path to the live Twilio API.

### R-14 — User documentation and sample data

- **REQUIREMENT R-14:** Include a README and safe sample CSV sufficient for a new user/developer to build, configure, import, validate, and exercise the application without a real send.

**Acceptance criteria**

- **REQUIREMENT AC-37:** **Given** the README, **when** followed from a clean checkout, **then** it identifies prerequisites, build/run commands, required CSV headers and quoting behavior, E.164/message rules, selection and confirmation behavior, settings/DPAPI limitations, cancellation/partial-failure behavior, and the no-real-send test command.
- **REQUIREMENT AC-38:** **Given** the sample CSV, **when** imported, **then** it demonstrates the required headers and at least one quoted-name case, contains only fictitious/non-routable example data, and is not used by tests to contact Twilio.

## 4. Explicit exclusions

- **OUT OF SCOPE OOS-01:** Telemetry, analytics, crash reporting services, or collection of usage/contact/message data.
- **OUT OF SCOPE OOS-02:** Any backend service, database, cloud-hosted contact store, account system, or multi-user synchronization.
- **OUT OF SCOPE OOS-03:** Installer creation, packaging, deployment, code signing, publishing, or store distribution.
- **OUT OF SCOPE OOS-04:** Git commits, branches, pull requests, repository cleanup, `.gitignore` changes, or modification of the legacy active-mission file.
- **OUT OF SCOPE OOS-05:** Modification of sibling projects or unrelated working-tree content.
- **OUT OF SCOPE OOS-06:** Receiving SMS, delivery-receipt polling, scheduling, templates, contact editing/storage, CSV export, opt-out management, or regulatory/compliance workflow beyond explicit confirmation.
- **OUT OF SCOPE OOS-07:** Real Twilio sends during development, automated testing, independent validation, or QA.
- **OUT OF SCOPE OOS-08:** Architecture and implementation prescriptions beyond the approved platform, dependency, security, and externally testable constraints above.
