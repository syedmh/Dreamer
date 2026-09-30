# Definition of Done — Husaynia Tabruk Requirements Mission

- [ ] **REQUIREMENT — Product contract gate:** R-1 through R-19 and NFR-1 through NFR-3 are approved as the MVP contract with no blocking product question remaining.
- [ ] **REQUIREMENT — Access gate:** Named tests prove approved/invited-member access, administrator assignment/revocation of Food Incharge access, dual-control/no-self-assignment administrator governance with five-minute step-up, expired/disabled account denial, and record-level authorization.
- [ ] **REQUIREMENT — Signup gate:** Named tests prove individual, household, and team requests require a named primary contact, accept only active adult member references plus a bounded unnamed-participant count, reject non-member/minor names, remain pending until Food Incharge approval, and do not create duplicate active records under retry/concurrency.
- [ ] **REQUIREMENT — Workflow gate:** Named tests prove approve, decline, waitlist, cancel, post-deadline override, and waitlist reassignment states remain consistent between the primary-contact view and Food Incharge roster.
- [ ] **REQUIREMENT — Capacity/closure gate:** Named tests prove a closed date/category rejects new requests without deleting existing requests, commitments, waitlist entries, or thread history.
- [ ] **REQUIREMENT — Communication gate:** A transition-level matrix proves ordinary thread read/post only for approved primary contacts and the active managing Food Incharge; denial for every other signup state; grant on waitlisted-to-approved; immediate revocation on withdrawal, cancellation, date cancellation, disable, organization change, or Food Incharge revocation; queued-post replay denial; sender/timestamp display; in-app notifications; and correct behavior when push is disabled or fails.
- [ ] **REQUIREMENT — Privacy gate:** Tests prove direct member contact details and non-member participant names are never collected/exposed; public or unrelated users cannot access rosters, participant composition, or threads; ordinary administrators cannot read threads; and moderation reads require purpose-scoped permission, recent step-up, reason/purpose/case ID, and successful immutable audit.
- [ ] **REQUIREMENT — Abuse-control gate:** Contract/integration tests prove message/report/signup payload and pagination limits, account/organization quotas, duplicate-report behavior, notification suppression after rejection, `413 payload_too_large`, and `429 rate_limited` with `Retry-After`.
- [ ] **REQUIREMENT — Reliability gate:** Failure-path tests cover connectivity loss, retry, duplicate submit, concurrent approval/reassignment, notification failure, date cancellation, and a category closing during submission without false success or duplicate state changes.
- [ ] **REQUIREMENT — Accessibility gate:** AC-18 passes for publish, signup, approval, cancellation, roster, waitlist, and thread journeys using screen reader, logical focus, non-color status cues, and 200% text size.
- [ ] **REQUIREMENT — Terminology gate:** Public-facing requirements, tests, documentation, and release copy use “Tabruk”; “Tabarruk” appears only in an intentional glossary or legacy-reference note.
- [ ] **REQUIREMENT — Marketplace boundary gate:** R-23 and A-4 explicitly define the initial marketplace as quote requests only, with payments outside the app and no payment credentials stored; changing this boundary requires a separately approved payments scope.
- [ ] **REQUIREMENT — Traceability gate:** AC-1 through AC-18 each map to at least one named test and test evidence records the executed command, result, and relevant environment.
- [ ] **REQUIREMENT — Quality gates:** Independent Test Engineer and QA reports pass; focused security re-review confirms all High/Medium architecture findings are remediated and the implementation security review has no unresolved Critical/High findings; code review is APPROVED; Engineering Judge is APPROVED.
- [ ] **REQUIREMENT — Documentation gate:** User/admin guidance documents member eligibility, Food Incharge assignment, approval and waitlist states, primary-contact responsibility, cancellation deadline/override, hidden contact details, thread/push behavior, and marketplace payment boundary.
- [ ] **REQUIREMENT — T8 production migration safety gate:** Checked-in owner `psql` scripts are the
      sole production Up/Down interface; one session advisory lock spans concurrent DDL and
      history-last finalization; schema-qualified/OID and schema/default-ACL attestation fails
      closed; production Down deletes only corrective history while preserving the index,
      validated constraint, and least privilege; EF is disposable/local-only; the seven-artifact
      manifest and real PostgreSQL 18.6 matrix pass; T9 remains blocked until independent
      test/security/review and Engineering Judge approval.

## Explicit exclusions

- **OUT OF SCOPE:** This requirements update does not select architecture, estimate work, implement code, deploy software, or claim that future implementation/test/security/QA gates have passed.
- **OUT OF SCOPE:** In-app money movement and stored payment credentials are not required for the initial marketplace release.
- **ASSUMPTION — NON-BLOCKING:** Retention, any future named-minor/non-member collection, prior-template reuse, scale targets, and supported iOS versions may be resolved before their affected production or later-phase gates without reopening the approved MVP product workflow. MVP itself does not accept non-member participant names.
