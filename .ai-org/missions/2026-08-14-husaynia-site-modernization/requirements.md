# Husaynia.org Replica — Frozen Requirements Contract

**Mission:** `2026-08-14-husaynia-site-modernization`  
**Status:** `PASS — architecture may proceed. CTO autopilot decisions D-01..D-04 resolve all blocking requirements ambiguities; production release remains subject to the named evidence gates.`  
**Evidence snapshot:** Public site inspected 2026-08-14/15; prior application inspected read-only.

## 1. Objective and users

- **REQUIREMENT R-01:** Rebuild `https://www.husaynia.org/` as a maintainable Microsoft Visual Studio solution that preserves the public site's observable visual design, content presentation, navigation, public URLs, responsive behavior, metadata, integrations, downloads, and functional journeys.
- **REQUIREMENT R-02:** The solution shall serve public visitors, donors, community members seeking programs/prayer information, content editors, site administrators, and deployment/operators.
- **REQUIREMENT R-03:** The solution shall be deployable through repeatable Azure-targeted stages without choosing a final Azure SKU in this contract.

## 2. Evidence-backed current-state inventory

### Live public site

- **FACT F-01:** The sitemap index exposes page, event, post, and post-archive sitemaps. Evidence: `https://www.husaynia.org/sitemap.xml`.
- **FACT F-02:** The page sitemap lists Home, Overview, Prayer Timings, Photos, Build Husaynia, Donate, Announcements, Programs, Videos, Islamic Calendar, Contact, Terms, Privacy, Fundraiser, Pledge, and category-specific donation pages. Evidence: `https://www.husaynia.org/page-sitemap.xml`.
- **FACT F-03:** The post sitemap lists the Duas index plus Dua Faraj, Dua Kumayl, Dua Nudba, Dua Tawassul, Hadith al-Kisa, Monday Rites, and Ziarat-e-Ashura. Evidence: `https://www.husaynia.org/post-sitemap.xml`.
- **FACT F-04:** The event sitemap exposes individual `/event/{slug}/` pages, including recurring religious-calendar entries and `majlis-e-aza-muharram-2026`. Evidence: `https://www.husaynia.org/tribe_events-sitemap.xml`.
- **FACT F-05:** The homepage currently presents recent Facebook-sourced announcements/videos, including Arbaeen 2026 content. Evidence: `https://www.husaynia.org/`.
- **FACT F-06:** Public media includes the Husaynia logo, mosque/construction images, and a downloadable Monday-night MP3; sitemap assets are under `/wp-content/uploads/...`. Evidence: `https://www.husaynia.org/page-sitemap.xml`, `https://www.husaynia.org/monday-rites/`.
- **FACT F-07:** The public design uses a dark-crimson primary theme (`#890000` observed in delivered CSS), Montserrat headings, Open Sans body text, an 80px logo, and a responsive breakpoint at 921px. Evidence: delivered HTML/CSS from `https://www.husaynia.org/`.
- **FACT F-08:** Public SEO includes canonical/robots metadata, Open Graph, Twitter cards, structured data, RSS, XML sitemaps, and an advertised events iCal feed. Evidence: delivered `<head>` from `https://www.husaynia.org/`, `https://www.husaynia.org/sitemap.xml`.
- **FACT F-09:** The public site integrates Facebook/Instagram links, Facebook feeds, Stripe, Google Analytics 4, Microsoft Clarity, and WebCal/The Events Calendar content. Evidence: delivered HTML and linked resources from `https://www.husaynia.org/`.
- **FACT F-10:** `robots.txt` allows WordPress AJAX and disallows `/wp-admin/` and uploaded WPForms artifacts. Evidence: `https://www.husaynia.org/robots.txt`.
- **FACT F-11:** Payment, pledge, contact, prayer, programs, and social-gallery details are substantially JavaScript/widget dependent and were not fully observable through static retrieval; no form was submitted and no real transaction was attempted. Evidence: inspected `/donate*`, `/pledge-construction/`, `/contact-us/`, `/prayer-timings/`, `/programs/`, `/photos/`, `/videos/`.
- **FACT F-12:** `/events/` redirects to `/islamic-calendar/`. Evidence: request to `https://www.husaynia.org/events/`.

### Prior generated application (`Husaynia/`)

- **FACT F-13:** The prior repository contains a Visual Studio 17 solution with Web, Core, and Infrastructure projects; the web project targets .NET 8. Evidence: `Husaynia/Husaynia.sln:2-8`, `Husaynia/Husaynia.Web/Husaynia.Web.csproj:1-3,19-23`.
- **FACT F-14:** It configures SQL Server/EF Core, ASP.NET Core Identity with roles, caching, ImageSharp, repositories, a prayer service, and a prayer cache background service. Evidence: `Husaynia/Husaynia.Web/Program.cs:11-49`.
- **FACT F-15:** It sets HTTPS/HSTS and several response headers, but it does not set a Content-Security-Policy header despite the README/deployment claim. Evidence: `Husaynia/Husaynia.Web/Program.cs:52-75`; claim: `Husaynia/README.md:151-165`, `Husaynia/DEPLOYMENT.md:172-180`.
- **FACT F-16:** The data context models events, announcements, media, prayer times, donations, campaigns, site configuration, and Identity data. Evidence: `Husaynia/Husaynia.Infrastructure/Data/ApplicationDbContext.cs:6-20`.
- **FACT F-17:** Prayer pages, upcoming programs, event details, JSON event retrieval, and iCal export exist; the Islamic Calendar action is explicitly a placeholder. Evidence: `Husaynia/Husaynia.Web/Controllers/CalendarController.cs:26-120`, especially `39-44`.
- **FACT F-18:** Stripe Checkout is one-time `payment` mode, validates amount/name/email, records pending donations, and processes success/webhook completion. Evidence: `Husaynia/Husaynia.Web/Controllers/DonationsController.cs:42-129,136-220`.
- **FACT F-19:** The donation entity has no recurring-subscription contract. Evidence: `Husaynia/Husaynia.Core/Entities/Donation.cs:5-20`.
- **FACT F-20:** Search loads announcements and up to 100 upcoming events and filters them in memory; it is not database full-text search. Evidence: `Husaynia/Husaynia.Web/Controllers/SearchController.cs:21-64`.
- **FACT F-21:** Admin dashboard and announcement-list pages require only an authenticated user; inspected pages do not enforce an Admin role. Evidence: `Husaynia/Husaynia.Web/Areas/Admin/Pages/Index.cshtml.cs:7-9`, `Husaynia/Husaynia.Web/Areas/Admin/Pages/Announcements/Index.cshtml.cs:7-10`.
- **FACT F-22:** No automated test project is included in the solution; the README supplies only a manual checklist. Evidence: `Husaynia/Husaynia.sln:5-8`, `Husaynia/README.md:167-182`.
- **FACT F-23:** Deployment documentation describes IIS, Azure App Service, and Docker, production secrets, Stripe webhooks, monitoring, and backups, but these are documentation claims rather than verified pipelines. Evidence: `Husaynia/DEPLOYMENT.md:1-18,48-147,149-221`.
- **FACT F-24:** The prior application is evidence and a possible reuse source only; it is not the implementation target and shall not be modified by this mission.

## 3. Functional and content requirements

### Visual shell, navigation, and responsive behavior

- **REQUIREMENT R-04:** Preserve the live site's logo, dark-crimson/white visual identity, typography, spacing hierarchy, header/footer composition, controls, imagery treatment, and desktop/mobile navigation.
- **REQUIREMENT R-05:** Preserve the live menu labels, ordering, dropdown relationships, internal/external destinations, active states, keyboard behavior, mobile menu behavior, and footer legal/social links as captured in the approved visual baseline.
- **REQUIREMENT R-06:** Every public template shall reflow without horizontal page scrolling at 320, 390, 768, 1024, 1440, and 1920 CSS-pixel widths, excluding intentionally scrollable tables.
- **REQUIREMENT R-07:** Dynamic regions may differ in item text/time after baseline capture, but their container dimensions, typography, controls, loading, empty, and error presentation shall match.

### Public content and journeys

- **REQUIREMENT R-08:** Reproduce all canonical URLs in the approved URL manifest, including core pages, donation categories, Duas/religious content, event detail pages, media, legal pages, RSS/iCal/download endpoints, and legacy aliases.
- **REQUIREMENT R-09:** Preserve Arabic, transliteration, English translation, punctuation, reading order, audio links, attribution, and headings for religious-content pages; Unicode content shall round-trip without corruption.
- **REQUIREMENT R-10:** Prayer Timings shall show today's Snohomish prayer times and a navigable calendar using a configurable, deterministic calculation/provider contract, the `America/Los_Angeles` timezone, explicit daylight-saving handling, manually editable overrides, and a timestamped cached fallback. Baseline values and the accepted display tolerance shall be captured and approved before production release.
- **REQUIREMENT R-11:** Programs and Islamic Calendar shall expose current/future entries, individual event details, date/time, location, description, categories where present, and a valid downloadable iCal representation.
- **REQUIREMENT R-12:** Announcements, photos, and videos shall preserve the approved live behavior: current feed/content presentation, outbound source links, media playback/lightbox behavior, pagination/load-more behavior, and explicit loading/empty/upstream-error states.
- **REQUIREMENT R-13:** Search shall return relevant published public content, tolerate empty/whitespace input, encode user input safely, show zero-results and dependency-error states, and never expose drafts, admin content, donor data, or secrets.
- **REQUIREMENT R-14:** Contact and pledge forms shall preserve the field set, labels, required/optional rules, consent text, validation, success acknowledgement, destination/workflow, anti-abuse behavior, and privacy disclosures observable in the timestamped approved public baseline; later authorized export data shall be reconciled idempotently.
- **REQUIREMENT R-15:** Donation routes shall preserve all publicly observable categories, fields, amount choices/custom amount behavior, explanatory content, anonymity option, currency, success/cancel behavior, and receipt expectations. Stripe Checkout/webhooks shall be validated in sandbox. One-time and recurring modes shall be supported only where the approved live baseline proves them, and payment-mode/category contracts shall be configurable rather than hard-coded.
- **REQUIREMENT R-16:** Duplicate donation callbacks, browser refreshes, retries, and webhook re-delivery shall not create duplicate completed donations or increment campaign totals twice.
- **REQUIREMENT R-17:** No real payment shall be executed by automated tests or requirements validation; payment verification shall use processor test/sandbox mode.
- **REQUIREMENT R-18:** Terms, privacy, contact identity, address, social links, tax/EIN statements, donation disclaimers, and third-party disclosures shall be migrated exactly from the CTO-approved content snapshot and be editor-maintainable.

### Admin/editor and operational workflows

- **REQUIREMENT R-19:** The least-privilege roles shall be `SiteAdministrator`, `ContentEditor`, `EventEditor`, `MediaEditor`, `DonationOperator`, and `ReadOnlyAuditor`. There shall be no implicit privilege merely because a user is authenticated.
- **REQUIREMENT R-19A:** `SiteAdministrator` manages roles, permissions, integration configuration, and site settings. `ContentEditor` manages pages, announcements, and religious content. `EventEditor` manages events/programs and iCal publication. `MediaEditor` manages approved assets and metadata.
- **REQUIREMENT R-20:** Authorized media editors shall upload permitted image/audio/document types, supply title/alt text/caption, replace/archive assets without breaking preserved public URLs, and receive actionable validation for unsupported, oversized, or unsafe files.
- **REQUIREMENT R-21:** `EventEditor` shall manage program/calendar entries and regenerate public calendar/iCal output.
- **REQUIREMENT R-22:** `DonationOperator` shall view transaction status and campaign attribution and reconcile webhook state without viewing full card data. `ReadOnlyAuditor` shall read audit logs and operational reports without mutation capability.
- **REQUIREMENT R-23:** Authentication and authorization shall be deny-by-default, enforce the named role boundaries, prevent ordinary authenticated users from reaching administration, and audit every privileged action with actor, role, action, target, timestamp, outcome, and correlation identifier.
- **REQUIREMENT R-24:** Editors shall have a documented preview/publish/rollback workflow and operators shall have documented procedures for failed feeds, failed webhooks, expired credentials, content restore, and emergency unpublish.

## 4. Migration, URLs, media, SEO, and redirects

- **REQUIREMENT R-25:** Before implementation freeze, produce a machine-readable inventory of every sitemap URL, HTTP status, canonical URL, title, meta description, robots directive, OG/Twitter metadata, structured-data type, content checksum, media dependency, and redirect.
- **REQUIREMENT R-26:** Initial migration shall use a timestamped comprehensive public crawl and downloaded publicly accessible Husaynia-owned media without hotlinking the retired origin. Import adapters shall also accept later authorized WordPress DB/XML/media/configuration exports idempotently, preserve filenames/paths where required, report conflicts, and avoid overwriting newer editor changes.
- **REQUIREMENT R-27:** For every legacy URL, return the equivalent content at the same path or one deterministic permanent redirect to the approved canonical path. Redirect chains and loops are prohibited.
- **REQUIREMENT R-28:** Preserve or intentionally map `/wp-content/uploads/...` asset URLs, `/events/`, event slugs, feed/sitemap URLs, donation URLs, and religious-content slugs.
- **REQUIREMENT R-29:** Generate valid XML sitemaps and robots rules; exclude admin, authentication, private/search-result pages as approved; include only canonical indexable public URLs.
- **REQUIREMENT R-30:** Preserve page-specific canonical, title, description, OG, Twitter, logo/image, Organization/LocalBusiness/WebPage/Breadcrumb/Event structured data where applicable.
- **REQUIREMENT R-31:** Produce pre-cutover and post-cutover crawl reports. There shall be no unexplained loss of approved content, metadata, media, or internal links.
- **REQUIREMENT R-32:** Migration and deployment shall be repeatable and idempotent, support dry-run validation, identify conflicts, and document rollback without overwriting newer editor changes. If an authorized export becomes available before production release, exact export-backed reconciliation is mandatory. If it remains unavailable, the Engineering Judge shall record residual fidelity risk rather than treating absence alone as an architecture blocker.

## 5. Azure environment and deployment-stage outcomes

- **REQUIREMENT R-33:** Source shall open, restore, build, test, debug, and publish from a supported Microsoft Visual Studio release and from documented command-line commands on a clean agent.
- **REQUIREMENT R-34:** CI shall run restore, locked dependency validation, build, automated tests, formatting/static analysis, secret scanning, dependency vulnerability scanning, accessibility checks, and artifact creation on pull requests.
- **REQUIREMENT R-35:** CD shall use one immutable, versioned application artifact promoted through Development, Staging, and Production stages; environment configuration shall not require rebuilding it.
- **REQUIREMENT R-36:** Development shall deploy automatically after the protected integration branch passes. Staging shall run migrations, smoke tests, crawl/redirect checks, accessibility, visual regression, and sandbox integration tests.
- **REQUIREMENT R-37:** Production shall require an explicit approval, current backup/restore evidence, migration preflight, change record, health verification, and rollback decision point.
- **REQUIREMENT R-38:** Secrets and credentials shall come from approved Azure secret/configuration facilities, never source control, build logs, client bundles, screenshots, or generated documentation.
- **REQUIREMENT R-39:** Each environment shall have isolated data and external-integration configuration. Non-production shall not send public messages, mutate production content, or use live payment credentials.
- **REQUIREMENT R-40:** Deployment shall be repeatable, observable, and safe to retry. A failed stage shall stop promotion and leave the last known-good production release recoverable.

## 6. Measurable non-functional requirements

- **REQUIREMENT R-41 (Accessibility):** Public and admin journeys shall meet WCAG 2.2 AA. Automated scans shall have zero critical/serious violations; named keyboard and screen-reader journeys shall pass manual QA.
- **REQUIREMENT R-42 (Performance):** On representative production-like mobile tests, public landing/content templates shall meet p75 LCP ≤2.5s, INP ≤200ms, CLS ≤0.10 and Lighthouse performance ≥90, excluding a documented third-party outage.
- **REQUIREMENT R-43 (Server performance):** With cached static assets and representative data at 25 requests/second for 10 minutes, p95 server response time shall be ≤500ms for public HTML/search reads and error rate <1%.
- **REQUIREMENT R-44 (Reliability):** External feed, analytics, prayer/calendar, email, or payment dependency failure shall not prevent unrelated public pages from rendering; users receive a non-sensitive actionable state and operators receive an alert.
- **REQUIREMENT R-45 (Security):** TLS shall be enforced; secure headers shall include CSP, HSTS, nosniff, frame protection, and an appropriate referrer policy. All state-changing browser requests require CSRF protection except authenticated provider webhooks that require signature verification.
- **REQUIREMENT R-46 (Security gate):** Independent security review shall report no unresolved Critical or High findings; dependency and secret scans shall be clean or have CTO-approved exceptions.
- **REQUIREMENT R-47 (Privacy):** Collect only approved data, mask sensitive values in logs, and define retention/deletion/export workflows. GA4, Clarity/session recording, marketing trackers, and non-essential social tracking shall default denied until explicit consent; essential site and payment functions shall work before consent. Tracker configuration capability shall be preserved and implemented behavior shall match the privacy notice.
- **REQUIREMENT R-48 (Observability):** Every environment shall provide health/readiness signals, structured logs with correlation IDs, request/error/latency metrics, deployment markers, dependency telemetry, payment-webhook failures, and alerts with documented owner/action.
- **REQUIREMENT R-49 (Compatibility):** Public journeys shall pass on the latest two stable desktop versions of Chrome, Edge, Firefox, and Safari, plus current iOS Safari and Android Chrome; layouts shall be verified from 320–1920px.
- **REQUIREMENT R-50 (Recovery):** Backup/restore shall cover database, Husaynia-owned media, and configuration references. **ASSUMPTION A-01:** Until CTO changes it, target RPO is 24 hours and RTO is 4 hours, proven by a staging restore exercise.
- **REQUIREMENT R-51 (Maintainability):** Repository documentation shall let a future developer or coding agent understand scope, setup, configuration, test commands, content model, integrations, deployment stages, operations, ADR locations, and safe change boundaries without access to secrets.

## 7. Numbered acceptance criteria

1. **AC-01 — Visual parity:** **GIVEN** approved 1440×900, 1024×768, 390×844, and 320×568 baselines for each template, **WHEN** the corresponding Staging page is captured with the same viewport/data, **THEN** non-dynamic regions have ≤2% differing pixels and no unresolved reviewer-classified visual defect.
2. **AC-02 — Navigation:** **GIVEN** the approved menu manifest, **WHEN** each desktop/mobile item is activated by pointer and keyboard, **THEN** label, order, hierarchy, destination, focus state, and active state match the baseline.
3. **AC-03 — Responsive layout:** **GIVEN** widths 320, 390, 768, 1024, 1440, 1920, **WHEN** every template is rendered, **THEN** no content is clipped/overlapped and no page-level horizontal scrollbar appears.
4. **AC-04 — URL parity:** **GIVEN** the approved legacy URL manifest, **WHEN** every URL is crawled after deployment, **THEN** each returns the expected 200 or a single approved permanent redirect, with zero loops/chains.
5. **AC-05 — Core content:** **GIVEN** the approved content snapshot, **WHEN** core pages are compared, **THEN** headings, body, links, imagery, legal/contact details, and ordering match their approved source.
6. **AC-06 — Religious text:** **GIVEN** the approved Dua/rites source, **WHEN** imported and exported, **THEN** Arabic, transliteration, translation, directionality, paragraph order, and audio references match checksums/fixtures.
7. **AC-07 — Prayer today:** **GIVEN** a fixed Snohomish fixture including DST boundaries, approved calculation/provider settings, overrides, baseline values, and tolerance, **WHEN** prayer times are requested, **THEN** output is deterministic and matches the approved baseline/tolerance.
8. **AC-08 — Prayer failure:** **GIVEN** the prayer dependency is unavailable, **WHEN** the page loads, **THEN** approved cached data is labeled with its timestamp or an accessible unavailable state appears, while navigation and other content remain usable.
9. **AC-09 — Events:** **GIVEN** published/draft/past/future events, **WHEN** public lists/details are opened, **THEN** only contractually public events appear with correct timezone, location, content, and canonical URL.
10. **AC-10 — iCal:** **GIVEN** a published event, **WHEN** its iCal is downloaded and parsed, **THEN** UID, title, start/end, timezone, description, location, and canonical URL match the event.
11. **AC-11 — Social/media loading:** **GIVEN** a healthy approved feed, **WHEN** announcements/photos/videos load, **THEN** configured items, media, outbound links, and controls match the approved behavior.
12. **AC-12 — Social/media failure:** **GIVEN** timeout, rate limit, blocked cookies, or empty feed, **WHEN** the region loads, **THEN** the page remains usable and shows the approved accessible loading/empty/error state without leaking diagnostics.
13. **AC-13 — Search:** **GIVEN** published and private fixtures, **WHEN** a visitor searches valid, empty, Unicode, oversized, and injection-shaped input, **THEN** results include only relevant public items, invalid input is bounded safely, and no exception/secret/private data is exposed.
14. **AC-14 — Contact/pledge validation:** **GIVEN** the timestamped public-baseline form schema and any later authorized export reconciliation, **WHEN** required, malformed, oversized, duplicate, and bot-shaped submissions are attempted in test mode, **THEN** field errors are accessible, no partial record/message is produced, and valid input creates one auditable submission.
15. **AC-15 — Donation validation:** **GIVEN** the baseline-proven configurable payment modes/categories and Stripe sandbox, **WHEN** zero, negative, over-limit, malformed, duplicate, declined, cancelled, recurring-if-proven, and 3DS scenarios run, **THEN** each receives the approved result and no invalid/declined payment is recorded completed.
16. **AC-16 — Donation idempotency:** **GIVEN** repeated success callbacks and webhook deliveries for one sandbox transaction, **WHEN** they are processed concurrently, **THEN** exactly one completed donation and one campaign increment exist.
17. **AC-17 — Payment privacy:** **GIVEN** a donation attempt, **WHEN** logs, database, telemetry, browser storage, and admin screens are inspected, **THEN** no full card data, processor secret, or prohibited sensitive value exists.
18. **AC-18 — Admin authorization:** **GIVEN** anonymous, ordinary authenticated, `SiteAdministrator`, `ContentEditor`, `EventEditor`, `MediaEditor`, `DonationOperator`, and `ReadOnlyAuditor` identities, **WHEN** each privileged function is requested directly, **THEN** only the assigned role succeeds and all permitted/denied privileged attempts are audited.
19. **AC-19 — Editorial lifecycle:** **GIVEN** an authorized editor, **WHEN** content is created, previewed, published, updated, unpublished, and restored, **THEN** public visibility and audit history follow the approved state transitions.
20. **AC-20 — Media safety:** **GIVEN** permitted, unsupported, oversized, corrupt, and executable-shaped files, **WHEN** uploaded, **THEN** only safe permitted files are stored/served and errors state the allowed remediation.
21. **AC-21 — SEO parity:** **GIVEN** the metadata manifest, **WHEN** canonical public pages are fetched, **THEN** status, title, description, canonical, robots, OG/Twitter, structured data, and sitemap inclusion match approved values.
22. **AC-22 — Broken links/assets:** **GIVEN** a post-deployment authenticated/public crawl, **WHEN** internal links and Husaynia-owned assets are checked, **THEN** there are zero unexplained 4xx/5xx results and zero mixed-content requests.
23. **AC-23 — Accessibility:** **GIVEN** all templates and named journeys, **WHEN** automated WCAG scans and keyboard/screen-reader QA run, **THEN** R-41 passes and all controls have visible focus, accessible names, status announcements, and logical order.
24. **AC-24 — Performance:** **GIVEN** the agreed production-like profile and representative data, **WHEN** three mobile runs and the load test execute, **THEN** R-42 and R-43 pass with raw reports retained.
25. **AC-25 — Dependency isolation:** **GIVEN** each external dependency is independently timed out or returns an error, **WHEN** unrelated pages/journeys run, **THEN** they remain available and the failure is observable without cascading.
26. **AC-26 — Security controls:** **GIVEN** the deployed Staging site, **WHEN** header, TLS, CSRF, authorization, upload, webhook-signature, injection, and dependency tests run, **THEN** R-45/R-46 pass.
27. **AC-27 — Privacy consent:** **GIVEN** a new visitor who has not granted optional consent, **WHEN** public pages and sandbox payment initiation load, **THEN** essential functionality works and GA4, Clarity/session recording, marketing, and non-essential social tracking do not run; **WHEN** consent is granted or withdrawn, **THEN** configured trackers start or stop accordingly.
28. **AC-28 — Browser matrix:** **GIVEN** R-49 browsers/devices, **WHEN** home, navigation, search, prayer, event, media, contact, and sandbox donation journeys run, **THEN** no blocking functional or severity-1/2 visual defect remains.
29. **AC-29 — Clean build:** **GIVEN** a clean supported workstation/agent, **WHEN** documented restore/build/test/publish commands run, **THEN** they exit zero and create the versioned deployable artifact without local-only dependencies.
30. **AC-30 — Stage promotion:** **GIVEN** one immutable build artifact, **WHEN** promoted Development → Staging → Production, **THEN** its checksum remains identical and only environment configuration changes.
31. **AC-31 — Failed deployment:** **GIVEN** an induced migration or health-check failure, **WHEN** a stage deploy runs, **THEN** promotion stops, failure is alerted, and the documented retry/rollback leaves the previous release available.
32. **AC-32 — Backup restore:** **GIVEN** a Staging backup containing representative data/media, **WHEN** restore is executed, **THEN** integrity checks pass within R-50 RPO/RTO and application smoke tests pass.
33. **AC-33 — Observability:** **GIVEN** a synthetic request, validation failure, dependency timeout, admin edit, and sandbox webhook failure, **WHEN** each occurs, **THEN** operators can correlate it across logs/metrics/traces and follow a linked runbook.
34. **AC-34 — Documentation:** **GIVEN** a developer/agent with repository access but no prior context, **WHEN** following the docs on a clean environment, **THEN** they can build, test, run, configure sandbox integrations, deploy to non-production, and locate operational/security rules without secret disclosure.
35. **AC-35 — Migration reconciliation:** **GIVEN** the timestamped public crawl/media inventory and any authorized export available before release, **WHEN** imports are dry-run, repeated, and reconciled, **THEN** every approved page, event, religious text, media asset, redirect, metadata record, and editable setting is accounted for without duplicate or destructive overwrite; unavailable export evidence is documented as residual fidelity risk.

## 8. Assumptions

- **ASSUMPTION A-01:** RPO 24 hours and RTO 4 hours apply until changed by CTO.
- **ASSUMPTION A-02:** Dynamic content parity is judged against a timestamped, approved snapshot rather than continually changing Facebook/calendar data.
- **ASSUMPTION A-03:** Exact visual parity permits browser font-rasterization differences and approved dynamic regions; objective tolerances are AC-01.
- **ASSUMPTION A-04:** Microsoft Visual Studio means a supported current Visual Studio release capable of opening/building the chosen Microsoft solution format; no particular application architecture is frozen here.
- **ASSUMPTION A-05:** Production-like performance testing uses sanitized representative data and sandbox integrations.
- **ASSUMPTION A-06:** Existing Husaynia-owned text/media may be migrated after the organization confirms rights; third-party content remains subject to its license/terms.
- **DECISION D-01 (resolves OQ-B1):** Architecture proceeds from a timestamped comprehensive public crawl and downloaded public media, with idempotent adapters for later authorized WordPress DB/XML/media/configuration exports. Export-backed reconciliation is a production-release requirement when export data becomes available; otherwise residual fidelity risk is judged explicitly.
- **DECISION D-02 (resolves OQ-B2):** Publicly observable donation contracts are preserved and validated with Stripe sandbox Checkout/webhooks; one-time/recurring support is baseline-driven and configurable. Prayer calculation/provider settings are configurable and deterministic for Snohomish, with manual overrides, cached fallback, and pre-release baseline/tolerance approval.
- **DECISION D-03 (resolves OQ-B3):** Least-privilege roles are `SiteAdministrator`, `ContentEditor`, `EventEditor`, `MediaEditor`, `DonationOperator`, and `ReadOnlyAuditor`, with the boundaries in R-19..R-23 and full privileged-action auditing.
- **DECISION D-04 (resolves OQ-B4):** Non-essential analytics, session recording, marketing, and social tracking are consent-gated and default denied; essential public/payment behavior works before consent, tracker configuration remains available, and privacy documentation must match runtime behavior.

## 9. Open questions

- **OPEN QUESTION OQ-N1 — NON-BLOCKING:** Should RPO/RTO be stricter than A-01? Recommended: retain A-01 until traffic/business impact data justifies a higher-cost target.
- **OPEN QUESTION OQ-N2 — NON-BLOCKING:** Must the raw WordPress `/wp-*` administration URLs remain? Recommended: no; preserve public/indexed URLs and assets only, with explicit redirects/410 decisions for obsolete technical endpoints.

## 10. Out of scope

- **OUT OF SCOPE OOS-01:** Architecture selection, component boundaries, final database choice, and final Azure SKU/resource selection.
- **OUT OF SCOPE OOS-02:** Implementation, modification of `Husaynia/`, and production deployment during this requirements task.
- **OUT OF SCOPE OOS-03:** Real donations, real form submissions, use or exposure of production secrets, or payment-card handling by the application.
- **OUT OF SCOPE OOS-04:** Changing the unrelated HusayniaTabruk/T6R/T8 mission artifacts.
- **OUT OF SCOPE OOS-05:** Reproducing WordPress, Elementor, Astra, or plugin internals when the same approved public/editor outcome can be provided without public-contract change.
- **OUT OF SCOPE OOS-06:** Migrating content for which Husaynia lacks ownership/license or whose third-party terms prohibit copying; such items require an approved replacement/link strategy.
- **OUT OF SCOPE OOS-07:** Native mobile applications, member portals, livestream production, accounting/tax systems, or new business capabilities not observable on the approved public baseline.

## 11. Mission-specific Definition of Done

- **REQUIREMENT R-52:** Completion is governed by the named independent gates in `definition-of-done.md`.
- **REQUIREMENT R-53:** No gate may accept README claims as evidence when executable code, tests, deployment records, crawl reports, or runtime observations are required.
- **REQUIREMENT R-54:** The Engineering Judge may approve only when every applicable gate in `definition-of-done.md` has objective evidence and any unavailable authorized-export evidence is explicitly evaluated as residual fidelity risk.
