# BookWriter Independent E2E QA Results

**STATUS: PASS**  
**Completed:** 2026-08-12 18:07:45 -07:00

## ENVIRONMENT

- Machine: `DESKTOP-4IR2HRB`
- OS: `Microsoft Windows NT 10.0.26200.0`
- App: direct-open
  `file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/index.html`
- Edge: `151.0.4129.78`, fresh profile, CDP port `9411`
- Chrome: `151.0.7922.138`, fresh profile, CDP port `9421`
- Firefox: not installed; optional coverage was not executed
- No server, package installation, account, or credentials used
- Automation: PowerShell plus Chrome DevTools Protocol `Runtime`, `DOM`, `Input`,
  `Network`, `Emulation`, `Page`, `Browser`, and `Target` domains
- Network was set offline before reloading each app profile and remained offline
  through the P0 journeys:

```text
Network.enable
Network.emulateNetworkConditions
  {offline:true, latency:0, downloadThroughput:0, uploadThroughput:0}
Page.reload {ignoreCache:true}
```

- Browser launch pattern:

```powershell
& <browser.exe> --headless=new --no-first-run --no-default-browser-check `
  --disable-sync --disable-extensions --disable-background-networking `
  --remote-allow-origins=* --remote-debugging-port=<9411|9421> `
  --user-data-dir=<fresh-profile> --allow-file-access-from-files `
  file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/index.html
```

## SCENARIOS

| # | Scenario | Exact actions / inputs | Expected | Actual evidence | Result |
|---:|---|---|---|---|---|
| 1 | Fresh offline first launch | Opened `index.html` in fresh Edge and Chrome profiles, forced CDP offline, then reloaded. | First-run disclosure, create/restore actions, no network dependency. | Both browsers showed `Not saved yet`; first-run panel disclosed local-only storage, browser-data loss, and unencrypted backups. Edge and Chrome each recorded `0` HTTP(S) requests and `0` console/runtime errors. | PASS |
| 2 | Capture fragmented memories and expand narrative | Created three memories. Primary test memory title: `A <script> memory — 東京`; date: `Sometime around 1987`; people: `Amma, José`; places: `Seattle, घर`; themes: `Family, यात्रा`; multiline source included `<img src=x onerror=globalThis.__memoirXss=1>`. Filled sensory details and Context/Feelings prompts, then used **Create draft from memory and prompt responses**. | Every field saves; source remains unchanged; explicit draft contains prompt responses; script-shaped text stays inert. | Saved status appeared. Book view showed the literal payload and Unicode. Narrative included Context and Feelings. `globalThis.__memoirXss === 1` was `false`. | PASS |
| 3 | Chapter organization, search/filter, book view, reload | Created `Beginnings` and `Journeys`; assigned all three memories; moved `Journeys` first; moved `The red bicycle` before the script-shaped memory. Searched separately for `東京`, `Rain tapped`, `José`, `घर`, and `यात्रा`; filtered by chapter and theme; filled title, subtitle, author, dedication, and multiline preface; reloaded. | Search covers every supported field, order persists, and book view uses narrative/fallback text. | Every search returned only `A <script> memory — 東京`; chapter filter showed `2 of 3`, theme filter `1 of 3`. Reload restored 3 memories, chapter order `Journeys > Beginnings`, and memory order `The red bicycle > A <script> memory — 東京`. | PASS |
| 4 | Backup download and fidelity | From **Backup & data**, downloaded the populated backup while offline. Parsed the downloaded file independently. | Versioned portable backup preserves all fields and ordering. | `life-memoir-backup-2026-08-13.json`, 4,589 bytes, SHA-256 `229f35ae8f14fc47b79cce59f6e3580f230972ec213fc8e0588045c4a588002c`; format `dreamer.life-memoir.backup`, version `1`, 3 memories, 2 chapters. Unicode, paragraph break, script-shaped literal, and ordering checks all passed. | PASS |
| 5 | Restore into clean Chrome profile, cancel then confirm | Used CDP `DOM.setFileInputFiles` on `#restore-file` with the Edge backup. First pressed Escape on the preview; selected it again, tabbed from **Cancel restore** to **Replace memoir**, and pressed Enter. | Preview shows counts and replacement warning; cancel leaves clean state unchanged; confirmation restores full collection. | Preview: `3 memories and 2 chapters`; cancel left the storage key `null` and first-run visible. Confirm produced `Restored and saved locally`; restored data matched the backup exactly except revision, including timestamps, Unicode, fields, assignments, and order. | PASS |
| 6 | Invalid restore is atomic | With the restored collection present, selected `tests/fixtures/malformed.json`. Compared the exact local-storage string before and after. | Specific error; no preview or mutation. | `The selected backup is not valid JSON. (INVALID_JSON)`; restore dialog stayed closed; stored bytes were unchanged. | PASS |
| 7 | HTML/text export, independent open, and safety | Edited sensory text in Chrome, exported HTML and text, then opened each exported file as its own offline browser target and inspected raw bytes and rendered content. | Standalone readable files preserve all fields/order/Unicode; user markup never executes. | HTML: 4,310 bytes, SHA-256 `d4230a499554dba4820ef52b4e5352b31b167f9c14cae7bd8f403f5fe99bc078`, zero scripts, CSP `default-src 'none'`, escaped raw payload, literal rendered payload, no XSS. Text: 1,859 bytes, SHA-256 `9cf5f81618bb0729aa6db019c9b9e39c9ad74915558211a7d1c242d8652379b7`; Unicode, paragraphs, fields, and chapter order passed. | PASS |
| 8 | Chrome edit/search compatibility | In the restored fresh Chrome profile, searched `José`, edited sensory details to add `Verified in Chrome.`, and waited for autosave. | Normal authoring works in Chrome and persists. | Search returned the expected memory. UI and stored snapshot contained the identical edited sensory text; status was `Saved locally`. | PASS |
| 9 | Two-tab conflict: load and explicit overwrite | Opened two Edge tabs on the same saved profile. Tab A changed the title; Tab B observed the conflict and chose **Load stored copy**. Tab A then changed the subtitle; Tab B chose **Overwrite stored copy** and confirmed. | No silent overwrite; both conflict choices are explicit and deterministic. | Tab B showed `Save paused` and the newer-copy banner. Load stored copy received `Threads of Home — Tab A`. Overwrite confirmation explained that no merge exists; the stored subtitle reverted to Tab B's stale value, and Tab A then loaded that stored copy. | PASS |
| 10 | Erase cancel, confirm, and cross-tab erase | Added unrelated key `qa.unrelated=keep-me`. In tab B, opened erase with keyboard, pressed Escape, reopened, tabbed to confirm, and pressed Enter. Waited 700 ms for pending saves/events. | Cancel changes nothing; confirm removes only the app key; every open tab returns to first run without stale resurrection. | Cancel preserved memoir data. Confirm removed `dreamer.bookWriter.collection.v1` in both tabs, preserved `qa.unrelated`, and produced `Local data erased` / `Local data erased elsewhere`. Both tabs remained first-run after the delay. | PASS |
| 11 | Forced storage quota failure and emergency recovery | In populated Chrome, replaced persistence storage through the exposed test seam with a storage object whose `setItem` throws `QuotaExceededError`; edited the title to `Quota draft retained — 東京`; downloaded emergency backup. | On-screen draft remains, stored bytes remain intact, error is actionable, emergency backup contains draft. | Status `Save failed`; banner named `STORAGE_QUOTA`, retained the on-screen title, focused `storage-banner`, and enabled emergency backup. Stored bytes were unchanged. Emergency backup SHA-256 `7c9384d8eb789b15ed8d53ecb4765c9ccd3449abe256b2d399eae430f9e137d0` contained the unsaved draft. | PASS |
| 12 | Keyboard and focus behavior | Used CDP keyboard events only for tab traversal, creating/editing/deleting a temporary memory, Escape cancellation, and tab/Enter confirmation. Native restore file selection was supplied through CDP; restore and erase dialogs were operated by keyboard. | Controls are reachable, visibly focused, dialogs focus Cancel, Escape cancels, and focus returns. | 14 recorded focus stops; every stop matched `:focus-visible`; minimum outline was `4px`. Delete dialog initially focused `confirm-cancel`; Escape returned focus to **Delete memory**; confirmed deletion succeeded. | PASS |
| 13 | Responsive layouts at 360/768/1440 | In both Edge and Chrome, set each viewport with `Emulation.setDeviceMetricsOverride`, visited Memories, Chapters, Book view, and Backup & data, measured page overflow, and captured in-memory screenshots. | No page-level horizontal scrolling; controls remain usable. | Every browser/view/width reported page overflow `0`. At 360 px the top navigation intentionally became an internal horizontal scroller; keyboard navigation remained reachable. 768 and 1440 had no off-screen controls. Screenshot hashes were recorded for all six browser/width combinations. | PASS |
| 14 | Existing regression suite | Direct-opened `tests/browser-tests.html` with separate fresh profiles. | No regression in the 48-test suite. | Edge: `48 passed, 0 failed, 48 total.` Chrome: `48 passed, 0 failed, 48 total.` | PASS |

**Scenarios:** 14 executed, 14 passed, 0 failed.  
**Not executed:** Firefox compatibility, because Firefox is not installed.

## DEFECTS

No P0/P1 functional defect was reproduced.

## FINDINGS

1. **LOW — narrow-width navigation discoverability:** at 360 CSS px, **Book view** and
   **Backup & data** begin outside the visible portion of the horizontally scrollable top
   navigation. There is no page-level overflow and keyboard focus scrolls the navigation, so this
   does not block the responsive requirement, but a visual affordance could make the additional
   tabs more discoverable.

## EVIDENCE

- Temporary evidence root:
  `C:\Users\syedhu\AppData\Local\Temp\bookwriter-qa-20260812-e2e`
- Edge backup:
  `edge-downloads\life-memoir-backup-2026-08-13.json`
- Chrome exports:
  `chrome-downloads\life-memoir-2026-08-13.html`
  and `chrome-downloads\life-memoir-2026-08-13.txt`
- Chrome emergency backup:
  `chrome-emergency\life-memoir-backup-2026-08-13.json`
- Edge screenshot SHA-256 values at 360/768/1440:
  `05728f86d4a717a20a9309606ce33b5286904a0c7d3c6fc9ba68209fec55b7f2`,
  `c0691d6f94fd0a6ca577deacd0a3bba7d46102e105ee50ddafdeab9aa75d8d9a`,
  `d160f697840061038373c21a5fda9104a0baf1a644d8a78ad7928f2da3c5fd82`
- Chrome screenshot SHA-256 values at 360/768/1440:
  `26f02d54caab50543df0ca7e13d296099f8251d45e03e2c25fdcca3cd0ba48e4`,
  `823de940528967ffcd2bd603157292053dbc52b8267cb762c125326060b75701`,
  `7a3457e9b95c45bf41822608fcf725806f671d15c604af0716d9cff1e18ad807`
- Outbound HTTP(S) requests: Edge `0`, Chrome `0`
- Console/runtime errors: Edge `0`, Chrome `0`

## COVERAGE GAPS

- Firefox was not installed, so it was not executed.
- Storage quota failure was forced through the application's exposed persistence test seam in a
  real Chrome page. Browser-policy-denied storage on initial boot was not separately executed.
- Focus behavior was measured in automated headless browsers; no separate human visual contrast
  audit was performed in this QA pass.

## VERDICT

**PASS.** The complete P0 journey passed with real browser execution:

`first run -> capture fragments -> prompts/narrative -> chapter organization -> reload -> backup
-> clean Chrome restore -> book review -> HTML/text export`

Offline operation, invalid restore atomicity, two-tab conflict choices, cross-tab erase, full erase,
quota-failure recovery, keyboard focus, responsive layouts, and Edge/Chrome regression suites also
passed. No blocking defect was found.

---

STATUS:          PASS  
SUMMARY:         All 14 executed E2E scenarios passed in fresh Edge and Chrome profiles.  
WORK_COMPLETED:  Executed primary, portability, resilience, offline, keyboard, responsive, cross-tab,
and regression journeys without modifying application code.  
EVIDENCE:        The QA result above and local artifacts under
`C:\Users\syedhu\AppData\Local\Temp\bookwriter-qa-20260812-e2e`.  
ARTIFACTS:       `.ai-org/missions/2026-08-12-life-book-writer/qa-results.md`  
FINDINGS:        Low-risk 360 px navigation discoverability note; no functional defect.  
RISKS:           Firefox and browser-policy-denied storage boot remain unexecuted; downloaded
backups are intentionally unencrypted.  
BLOCKERS:        None.  
NEXT_ACTION:     Proceed to final mission judgment; optionally add Firefox and human visual
accessibility evidence when that environment is available.
