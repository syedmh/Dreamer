# Mission Definition of Done

- [ ] **Architecture gate:** Approved design demonstrates local-only operation, zero required runtime services, versioned portable data, safe rendering/import, and no installed build/runtime dependency.
- [ ] **Implementation gate:** FR-1 through FR-19 and NFR-1 through NFR-10 are implemented at their stated P0/P1 priority; any deferral is explicitly approved and reflected in this contract.
- [ ] **Test gate:** AC-1 through AC-18 each map to a named automated or reproducible manual test with recorded pass evidence.
- [ ] **Test gate:** Negative tests cover malformed/unsupported/oversized restore, storage failure/quota, script-shaped content, empty collection, Unicode, canceled destructive actions, browser-data loss, and stale multi-tab writes.
- [ ] **Test gate:** Backup from a populated profile restores into a clean profile with all fields and ordering preserved.
- [ ] **Performance gate:** The NFR-8 dataset meets the 2-second load and 300-ms interaction thresholds on a documented test machine.
- [ ] **Security/privacy gate:** Browser network inspection across every P0 journey shows zero outbound requests; no unresolved Critical or High findings; imported and authored script-shaped text never executes.
- [ ] **Accessibility gate:** Keyboard-only completion of all P0 journeys passes; labels, focus, headings, and WCAG 2.1 AA contrast are verified.
- [ ] **Compatibility gate:** All P0 journeys pass on current stable desktop Chrome, Edge, and Firefox, with browser versions recorded.
- [ ] **E2E/QA gate:** First-run → memory capture → guided expansion → chapter organization → restart → backup → clean-profile restore → book review → HTML/text export passes with reproducible evidence.
- [ ] **E2E/QA gate:** Offline execution, erase/cancel, invalid restore, storage failure, and stale-tab warning journeys pass.
- [ ] **Documentation gate:** A concise README explains how to open the app, local-only privacy behavior, supported browsers, backup/restore, export, data erasure, storage-loss risk, and unencrypted-backup disclosure.
- [ ] **Code review gate:** Independent reviewer returns APPROVED with no unresolved correctness or maintainability blockers.
- [ ] **Repository gate:** No application code depends on package installation, account credentials, cloud services, paid APIs, external fonts/scripts/styles, or generated build artifacts.
- [ ] **Judge gate:** Engineering Judge verifies the original mission objective and every applicable item above from actual evidence and returns APPROVED.

## Explicitly not required for Done

- **OUT OF SCOPE:** Deployment, authentication, collaboration, generative AI, media attachments, PDF/e-book publishing, encrypted backup, restore merging, and guaranteed perpetual storage.
