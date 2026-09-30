# Life Memoir Writer manual validation checklist

## Recording rules

Run from a fresh browser profile unless a step says otherwise. Open `index.html` through `file://`,
disable or disconnect networking, keep developer tools open where requested, and record the exact
browser version, machine, timestamp, and PASS/FAIL. Do not mark a row PASS from code inspection
alone.

Implementation-session environment:

- Machine: `DESKTOP-4IR2HRB`
- OS: `Microsoft Windows NT 10.0.26200.0`
- Edge: `151.0.4129.78`
- Chrome: `151.0.7922.138`
- Firefox: not installed in the implementation environment
- Automated direct-open runner, 2026-08-12: Edge PASS; Chrome PASS
- Interactive checklist status: **NOT RUN** (requires a human-controlled GUI session)

## Common test data

Use one memory containing:

- title: `A <script> memory — 東京`
- source text: two paragraphs including `<img src=x onerror=alert(1)>`
- date: `Sometime around 1987`
- people: `Amma, José`
- places: `Seattle, घर`
- themes: `Family, यात्रा`
- sensory details and prompt responses containing Unicode and line breaks

Create at least two chapters and three memories for ordering checks. Before destructive or restore
tests, note the exact local-storage value for `dreamer.bookWriter.collection.v1`.

## Acceptance-criteria procedures

### AC-1 — first run and local-only disclosure

1. Clear only the application storage key and open `index.html` with networking disabled.
2. Confirm the first-run panel explains local-only storage, loss risk, and unencrypted backups.
3. Confirm **Create my first memory** and **Restore a backup** are present.
4. In the Network panel, confirm zero requests.

Expected: first-run state is usable offline and no outbound request appears.  
Result: **NOT RUN interactively**.

### AC-2 — fidelity after restart

1. Enter all common test data, wait for **Saved locally**, close the tab, and reopen `index.html`.
2. Compare every field, paragraph break, Unicode character, and timestamp-visible behavior.

Expected: every authored value returns unchanged.  
Result: **NOT RUN interactively**; automated domain/persistence fidelity tests PASS in Edge/Chrome.

### AC-3 — search fields

Search separately for terms found only in title, source text, people, places, and themes.

Expected: each matching memory appears in stable order and nonmatches do not.  
Result: **NOT RUN interactively**; automated search test PASS in Edge/Chrome.

### AC-4 — chapter and memory order after restart

Move chapters and memories up/down, assign a memory to another chapter, wait for save, and reopen.

Expected: chapter order, assignment, and within-chapter order remain exactly as chosen.  
Result: **NOT RUN interactively**; automated ordering test PASS in Edge/Chrome.

### AC-5 — chapter deletion

Cancel chapter deletion once, then confirm it.

Expected: cancel changes nothing; confirmation removes only the chapter and appends its memories to
Unassigned memories.  
Result: **NOT RUN interactively**; automated deletion regression test PASS in Edge/Chrome.

### AC-6 — prompt and narrative independence

Answer, skip, and revisit prompts. Edit narrative manually. Use the explicit narrative-draft action,
cancel replacement once, then confirm it.

Expected: prompt work never changes source text; narrative changes only by typing or explicit
confirmed action.  
Result: **NOT RUN interactively**; automated fallback/composition tests PASS in Edge/Chrome.

### AC-7 — complete book view

Open Book view with memories in chapters and Unassigned memories, including one blank narrative.

Expected: all memories appear in persisted order; blank narratives use source memory text.  
Result: **NOT RUN interactively**; automated book assembly test PASS in Edge/Chrome.

### AC-8 — save success and forced failure

1. Make an edit and observe Unsaved → Saving → Saved.
2. In a disposable profile, use developer tools to make local storage unavailable or exceed quota,
   then edit again.

Expected: text remains visible; error names the stable condition and directs the author to retry or
download an emergency backup.  
Result: **NOT RUN interactively**; automated unavailable/quota tests PASS in Edge/Chrome.

### AC-9 — populated backup to clean profile

Download a backup, open a clean compatible browser profile, restore it, and compare every supported
field, timestamp, assignment, and order.

Expected: source and restored collections match except for the local persistence revision advanced
by the confirmed save.  
Result: **NOT RUN interactively**; automated serialization/inspection/commit fidelity test PASS.

### AC-10 — restore rejection

Try `malformed.json` (intentionally invalid JSON), `wrong-format.json`, `unsupported-version.json`,
`invalid-reference.json`, `duplicate-id.json`, a wrong MIME type, and a file over 10 MiB.

Expected: a specific stable error appears and the pre-test local-storage bytes remain identical.  
Result: **NOT RUN interactively**; automated malformed/type/version/size/reference tests PASS.

### AC-11 — restore preview and cancel

Choose `valid-backup.json`, verify counts and replacement warning, cancel, compare storage, then
repeat and confirm.

Expected: cancel changes nothing; confirm performs one replacement write before the UI changes.  
Result: **NOT RUN interactively**; automated preview preparation/one-write test PASS.

### AC-12 — safe durable exports

Restore `script-shaped.json`, export HTML and text, then open both offline.

Expected: Unicode, paragraphs, and order survive; script-shaped values display literally; no script
or event handler executes; HTML contains `default-src 'none'`.  
Result: **NOT RUN interactively**; automated HTML/text escaping tests PASS in Edge/Chrome.

### AC-13 — every P0 journey offline

With network disabled, run create/edit/delete, prompts, narrative, chapter organization, book view,
backup, restore, export, and erase.

Expected: all journeys work and Network shows zero outbound requests.  
Result: **NOT RUN interactively**.

### AC-14 — keyboard-only operation

Without a pointing device, complete create/edit/delete, chapter organization, backup, restore,
export, erase-cancel, and erase-confirm. Verify visible focus and dialog Escape/cancel behavior.

Expected: every control is reachable, labeled, visibly focused, and operable; dialogs retain focus.  
Result: **NOT RUN interactively**.

### AC-15 — performance dataset

Open `browser-tests.html` and record the NFR-8 timing line for 500 memories, 50 chapters, and roughly
500,000 characters.

Expected: load < 2000 ms; search, reorder, and save each < 300 ms.  
Result: automated threshold test PASS in Edge/Chrome; exact final timings belong in implementation
evidence.

### AC-16 — stale-tab warning

Open two tabs from the same saved collection. Edit/save tab A, then edit tab B.

Expected: tab B pauses before overwrite and offers **Load stored copy** or explicitly confirmed
**Overwrite stored copy**; no silent write or merge occurs.  
Result: **NOT RUN interactively**; automated revision and storage-event tests PASS.

### AC-17 — erase cancel and confirm

Record the app key and an unrelated local-storage key. Cancel erase, then confirm erase.

Expected: cancel changes nothing; confirm removes only the app key and returns to first run; the
unrelated key and downloaded files remain.  
Result: **NOT RUN interactively**; automated application-key-only erase test PASS.

### AC-18 — storage cleared or unavailable

Clear the app key and reopen; separately deny local storage and reopen.

Expected: cleared storage shows honest first run; unavailable storage pauses editing, makes no
recovery promise, and explains restore/backup options.  
Result: **NOT RUN interactively**; automated storage-unavailable test and headless first-run boot
PASS.

## Responsive, compatibility, and privacy matrix

Repeat the P0 journey at 360, 768, and 1440 CSS pixels in current stable Edge, Chrome, and Firefox.
Check for page-level horizontal scrolling, clipped controls, contrast, focus visibility, and
reduced-motion behavior.

| Browser | Version | 360 px | 768 px | 1440 px | Offline/network | Keyboard | Result |
|---|---:|---|---|---|---|---|---|
| Edge | 151.0.4129.78 | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN |
| Chrome | 151.0.7922.138 | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN |
| Firefox | not installed | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NOT RUN | BLOCKED BY ENVIRONMENT |
