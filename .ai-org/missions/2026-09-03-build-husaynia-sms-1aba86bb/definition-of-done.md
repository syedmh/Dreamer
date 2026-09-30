# HusayniaSMS Mission-Specific Definition of Done

- **REQUIREMENT:** All gates are mandatory unless explicitly marked not applicable. Evidence must come from executed commands, inspected artifacts, or reproducible observations; agent assertions alone are insufficient.

## Gate 1 — Scope and build baseline (Developer, independently verified by Test Engineer)

- [ ] **REQUIREMENT DoD-01:** `HusayniaSMS/` contains a Visual Studio-compatible C# Windows Forms solution targeting `net8.0-windows`; clean restore/build succeeds with zero errors. Evidence: recorded command/output proving **AC-01**.
- [ ] **REQUIREMENT DoD-02:** First-run launch with no settings opens safely and shows incomplete setup. Evidence: automated or QA observation proving **AC-02**.
- [ ] **REQUIREMENT DoD-03:** Exact stable versions are pinned for the official Twilio SDK and established CSV parser; package-lock/manifests contain no floating version for them. Evidence: manifest inspection proving **AC-25**.

## Gate 2 — Independent functional test gate (Test Engineer)

- [ ] **REQUIREMENT DoD-04:** Setup validation tests prove missing/malformed Account SID, blank token, and invalid sender behavior with zero send calls (**AC-03–AC-05**).
- [ ] **REQUIREMENT DoD-05:** DPAPI tests or Windows-user-scope probes prove ciphertext-at-rest, same-user reload, decryption-failure recovery, masking, and secret redaction (**AC-06–AC-08**, **AC-30**). If a second-user execution is unavailable, a deterministic DPAPI-scope test plus documented manual reproduction steps is required.
- [ ] **REQUIREMENT DoD-06:** CSV tests cover valid rows; case-insensitive/trimmed headers and BOM; quoted commas; escaped quotes; quoted line breaks; canceled selection; missing/unreadable files; malformed quoting/records; missing headers; and header-only empty input (**AC-09–AC-13**).
- [ ] **REQUIREMENT DoD-07:** Grid/validation tests cover blank fields, malformed/short/long E.164 values, duplicate numbers, mixed valid/invalid rows, select-all-valid, and clear-selection (**AC-14–AC-16**).
- [ ] **REQUIREMENT DoD-08:** Message boundary tests cover empty, whitespace-only, 1 character, exactly 1,600 characters, and 1,601 characters, including preservation of valid message text (**AC-17–AC-18**).
- [ ] **REQUIREMENT DoD-09:** Scope/confirmation tests prove empty selected-send rejection, selected-recipient count, all-valid count independent of selection, confirmation cancellation with zero calls, and immutable confirmed recipient snapshots (**AC-19–AC-22**).
- [ ] **REQUIREMENT DoD-10:** Async/concurrency tests with delayed fakes prove UI responsiveness and that repeated click/key activation produces one batch and at most one submission per confirmed row (**AC-23–AC-24**).
- [ ] **REQUIREMENT DoD-11:** Recording-fake tests prove exact sender/recipient/message mapping and one request per attempted recipient (**AC-26**).
- [ ] **REQUIREMENT DoD-12:** Batch result tests cover all-success, recipient rejection, network exception, mixed partial failure, batch-wide authentication failure, reconciled totals, sanitized errors, and a corrected follow-up batch (**AC-27–AC-30**, **AC-33–AC-34**).
- [ ] **REQUIREMENT DoD-13:** Cancellation/closing tests with controllable delayed fakes prove no new starts after cancellation is observed, preservation of completed results, canceled/not-sent statuses, reconciled totals, stay-open behavior, and cancel-and-close behavior (**AC-31–AC-32**).
- [ ] **REQUIREMENT DoD-14:** The full automated suite passes with no live credentials and with network access unavailable or blocked; test inspection proves there is no live Twilio fallback (**AC-35–AC-36**). Evidence records test command, pass/fail/skip counts, and confirms no real sends.

## Gate 3 — Independent security gate (Security Engineer)

- [ ] **REQUIREMENT DoD-15:** Review confirms the auth token is DPAPI-protected with current-user scope, absent from plaintext settings, masked in UI, and redacted from errors/results.
- [ ] **REQUIREMENT DoD-16:** Review confirms malformed CSV and provider error text cannot expose secrets or execute content, and no production credential, API key, or real recipient data is committed.
- [ ] **REQUIREMENT DoD-17:** Security Engineer returns **APPROVED** with no unresolved Critical or High finding. Medium/Low findings are documented with disposition.

## Gate 4 — Independent code review (Code Reviewer)

- [ ] **REQUIREMENT DoD-18:** Reviewer verifies implementation matches `requirements.md`, has one authoritative validation path per input type, prevents duplicate active batches, and handles asynchronous UI updates/cancellation without unsafe cross-thread access.
- [ ] **REQUIREMENT DoD-19:** Reviewer verifies sibling projects, unrelated changes, and `.ai-org/active-mission.json` were not modified by this mission.
- [ ] **REQUIREMENT DoD-20:** Code Reviewer returns **APPROVED** with no unresolved correctness or compatibility defect that violates an acceptance criterion.

## Gate 5 — Realistic no-send QA (QA Engineer)

- [ ] **REQUIREMENT DoD-21:** From a clean user profile, QA follows the README to build/start, observes incomplete first-run setup, saves valid test setup without a live token, restarts, and verifies safe DPAPI/settings behavior (**AC-01–AC-08**).
- [ ] **REQUIREMENT DoD-22:** QA imports the supplied sample and purpose-built malformed CSV files, verifies quoted parsing, invalid-row visibility, duplicate handling, selection controls, and non-destructive file-level failures (**AC-09–AC-16**, **AC-38**).
- [ ] **REQUIREMENT DoD-23:** QA executes selected-send and all-valid journeys against a fake sender, verifies explicit counts and cancel/no-call behavior, attempts repeated sends, and observes per-row/final outcomes (**AC-17–AC-30**).
- [ ] **REQUIREMENT DoD-24:** QA exercises cancel, close/stay, and cancel-and-close during delayed fake sends; the UI remains responsive and results reconcile (**AC-23**, **AC-31–AC-34**).
- [ ] **REQUIREMENT DoD-25:** QA provides reproducible evidence that no real SMS was sent and no live Twilio credential was needed (**AC-35–AC-36**).

## Gate 6 — Documentation and scope audit (Documentation Specialist or QA)

- [ ] **REQUIREMENT DoD-26:** README content is checked against actual commands and behavior and covers every item in **AC-37**.
- [ ] **REQUIREMENT DoD-27:** The sample CSV is importable, demonstrates quoting, and uses only fictitious/non-routable example data (**AC-38**).
- [ ] **REQUIREMENT DoD-28:** Repository diff/status evidence shows only authorized `HusayniaSMS/` implementation content and mission evidence changed; exclusions **OOS-01–OOS-08** remain absent.

## Gate 7 — Final engineering judgment (Engineering Judge)

- [ ] **REQUIREMENT DoD-29:** Judge traces every **AC-01–AC-38** to named executed evidence and rejects missing, skipped without justification, contradictory, or assertion-only proof.
- [ ] **REQUIREMENT DoD-30:** Judge confirms Test, Security, Code Review, QA, documentation, and scope gates are complete and independently approved.
- [ ] **REQUIREMENT DoD-31:** Judge returns **APPROVED** only if no acceptance criterion is unproven, no blocking defect remains, no real-send validation occurred, and no out-of-scope file was modified.

## Not applicable to this mission

- **OUT OF SCOPE / NOT APPLICABLE:** Installer/deployment, backend availability, production telemetry/monitoring, data migration, rollback deployment, and live Twilio delivery verification are not release gates because they are explicitly excluded in `requirements.md`.
