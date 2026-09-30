# Husaynia Food Preparation and Tabruk Signup — Product Requirements

## Approved product contract

- **REQUIREMENT R-1 (P0):** Provide an iPhone-first service through which approved or invited Husaynia members can request commitments for food preparation, serving, or cleanup on eligible service dates.
- **REQUIREMENT R-2 (P0):** An authorized Husaynia administrator assigns and revokes Food Incharge access.
- **REQUIREMENT R-3 (P0):** A Food Incharge publishes and manages dates, requested help categories, capacity or availability, signup approvals, waitlists, rosters, cancellation rules, and date-specific communications.
- **REQUIREMENT R-4 (P0):** A signup may represent an individual, household, or team and must identify one named primary contact. Other active adult members may be included by membership reference; other household/team participants are represented only by a count and optional non-identifying group label in MVP.
- **REQUIREMENT R-5 (P0):** A signup request is not a confirmed commitment until the Food Incharge approves it; the requester can see whether it is pending, approved, waitlisted, declined, withdrawn, canceled, or reassigned.
- **REQUIREMENT R-6 (P0):** The Food Incharge can approve, decline, or waitlist a request and can reassign an available place to a waitlisted signup after a withdrawal, cancellation, or override.
- **REQUIREMENT R-7 (P0):** In-app communication supports date-specific threads for the Food Incharge and eligible signup contacts; phone numbers, email addresses, and other direct contact details remain hidden from other members.
- **REQUIREMENT R-8 (P0):** Eligible users receive in-app updates and opt-in iOS push notifications for material changes to their signup or date thread; the in-app record remains authoritative if push is delayed, disabled, or unavailable.
- **REQUIREMENT R-9 (P0):** Each date has a Food Incharge-configurable self-cancellation deadline. Before the deadline, the primary contact may cancel the signup; after it, only an authorized Food Incharge override may change the commitment.
- **REQUIREMENT R-10 (P0):** Closing a date or category prevents new requests without deleting existing requests, commitments, waitlist entries, or thread history.
- **REQUIREMENT R-11 (P0):** Canceling a date clearly marks it canceled, prevents new requests, updates affected signup states, and creates an in-app notification for affected primary contacts.
- **REQUIREMENT R-12 (P0):** A primary contact can view upcoming and past signups, minimized participant composition, selected help categories, current state, cancellation deadline, and the associated in-app thread only while thread-eligible.
- **REQUIREMENT R-13 (P0):** A Food Incharge can view the names, signup composition, selected categories, state, and in-app communication needed to coordinate dates they manage, but not unrelated private profile or direct contact details.
- **REQUIREMENT R-14 (P0):** Empty states distinguish no published dates, no matching help requested, no personal signups, and no thread messages.
- **REQUIREMENT R-15 (P0):** User-visible errors must not falsely confirm a request, approval, decline, waitlist change, cancellation, reassignment, date change, or message when the operation did not complete.

## Access, privacy, and terminology

- **REQUIREMENT R-16 (P0):** Only approved or invited Husaynia member accounts can access member signup functionality.
- **REQUIREMENT R-17 (P0):** A member cannot manage dates, approve commitments, inspect unrelated rosters, assign privileged roles, or access threads for which they are not eligible.
- **REQUIREMENT R-18 (P0):** Authorization is enforced for every protected action and record, not only by hiding controls.
- **REQUIREMENT R-19 (P0):** Public-facing product copy uses the spelling **“Tabruk.”**
- **ASSUMPTION A-1:** “Tabruk” is the approved public spelling; “Tabarruk” may appear only in a brief glossary or legacy-reference note.
- **ASSUMPTION A-2:** “Food Incharge,” “caterer,” “food preparation,” “serving,” and “cleanup” remain the working public role/category labels unless Husaynia supplies replacement copy.
- **ASSUMPTION A-3:** The MVP does not collect non-member participant names, dietary restrictions, medical data, food-safety certifications, age, minor status, or safeguarding information until Husaynia defines a necessary purpose and handling policy.
- **OUT OF SCOPE OOS-1 (MVP):** Public visitor access to dates, rosters, participant composition, member display names, or threads.
- **OUT OF SCOPE OOS-2 (MVP):** SMS, WhatsApp, email broadcast, disclosure of member contact details, or unrestricted member-to-member messaging.
- **OUT OF SCOPE OOS-3:** Android delivery before the iPhone MVP passes its acceptance and privacy gates.
- **OUT OF SCOPE OOS-4:** Architecture, technology selection, implementation, deployment, and estimates.

## Later phases

- **REQUIREMENT R-20 (P1):** Planning may add editable preparation milestones based on expected headcount after Husaynia approves the template or calculation policy.
- **REQUIREMENT R-21 (P1):** Planning may record estimated and actual date costs with currency, category, author, and change history.
- **REQUIREMENT R-22 (P1):** Prior signup configurations may be reused only after Husaynia defines what operational data may be copied and whether historical participant data may be shown.
- **REQUIREMENT R-23 (P2):** The initial caterer marketplace release supports date-linked quote requests and quote responses.
- **ASSUMPTION A-4 — NON-BLOCKING:** Marketplace payments occur outside the app. The initial marketplace release performs no money movement and stores no payment credentials. This is a cheap-to-revise phase boundary if the CTO later authorizes a separate payments scope.
- **OUT OF SCOPE OOS-5 (initial marketplace release):** In-app payments, donations, deposits, reimbursements, invoices, refunds, tax receipts, provider payouts, or stored payment credentials.
- **OUT OF SCOPE OOS-6 (until separately approved):** Caterer ratings, public provider profiles, background checks, legal verification, and accounting/tax treatment.

## MVP acceptance criteria

- **REQUIREMENT AC-1:** **Given** an account that is neither approved nor invited, **when** it attempts to access member functionality, **then** access is denied and no member, roster, or thread data is disclosed.
- **REQUIREMENT AC-2:** **Given** an authorized administrator, **when** Food Incharge access is assigned or revoked, **then** the affected account gains or loses date-management privileges accordingly.
- **REQUIREMENT AC-3:** **Given** a Food Incharge creates a valid date and opens a help category, **when** an eligible member views available dates, **then** the date, instructions, category, availability, and cancellation deadline are shown.
- **REQUIREMENT AC-4:** **Given** an open category, **when** an eligible member submits an individual, household, or team signup, **then** one request is recorded with a named primary contact, optional referenced active adult members, and a bounded unnamed-participant count; no non-member/minor name is accepted, and the state is pending rather than approved.
- **REQUIREMENT AC-5:** **Given** duplicate or concurrent submissions for the same signup, date, and category, **when** they complete, **then** no duplicate active request or commitment is created and the final state is shown.
- **REQUIREMENT AC-6:** **Given** a pending request, **when** the Food Incharge approves, declines, or waitlists it, **then** the roster and primary contact show the same resulting state and an in-app notification is created.
- **REQUIREMENT AC-7:** **Given** a category closes before a request completes, **when** the member submits, **then** no request is created and the member is told it is no longer available.
- **REQUIREMENT AC-8:** **Given** an approved signup before its cancellation deadline, **when** the primary contact cancels it, **then** the signup and roster show the same canceled state and retrying does not duplicate the action.
- **REQUIREMENT AC-9:** **Given** an approved signup after its cancellation deadline, **when** the primary contact attempts self-cancellation, **then** the commitment remains unchanged and the user is directed to the Food Incharge.
- **REQUIREMENT AC-10:** **Given** an approved signup after its deadline, **when** an authorized Food Incharge applies an override, **then** the roster records the new state and the primary contact receives an in-app update.
- **REQUIREMENT AC-11:** **Given** an available place and at least one waitlisted signup, **when** the Food Incharge reassigns the place, **then** the selected signup becomes approved, no excess approved place is created, and affected primary contacts see their current states.
- **REQUIREMENT AC-12:** **Given** a managed date, **when** the active managing Food Incharge or an active primary contact with an approved signup uses its thread, **then** the message shows sender and timestamp and remains associated with that date; pending, waitlisted, declined, withdrawn, cancelled, disabled, revoked, cross-organization, and unrelated actors are denied, and access is removed immediately when eligibility changes.
- **REQUIREMENT AC-13:** **Given** members in a date thread or roster, **when** they view participant information, **then** phone number, email address, and other direct contact details are not exposed.
- **REQUIREMENT AC-14:** **Given** a material signup state change or new eligible thread update, **when** notification processing occurs, **then** an in-app notification is recorded and an opted-in device is sent a push notification without making push delivery the authoritative record.
- **REQUIREMENT AC-15:** **Given** enrolled signups, **when** the Food Incharge cancels the date, **then** the date and affected signups are visibly canceled, new signup is impossible, and each affected primary contact receives an in-app update.
- **REQUIREMENT AC-16:** **Given** connectivity loss or a failed request during a state-changing action, **when** the app reports the outcome, **then** it does not display success unless the resulting state is confirmed and retry does not duplicate the action.
- **REQUIREMENT AC-17:** **Given** an unauthenticated, expired, disabled, or insufficiently privileged account, **when** it requests protected data or actions, **then** the request is rejected and no protected data is disclosed.
- **REQUIREMENT AC-18:** **Given** a user operating with screen reader, text enlargement, or supported switch-style navigation, **when** completing publish, signup, approval, cancellation, roster, waitlist, and thread journeys, **then** controls have meaningful labels, focus order is logical, status is not conveyed by color alone, and content remains operable at 200% text size.

## Non-functional requirements

- **REQUIREMENT NFR-1 Privacy:** Collect only identity, role, signup composition, approved in-app contact route, commitment, thread, notification, and operational data necessary for approved journeys.
- **REQUIREMENT NFR-2 Reliability:** The authoritative in-app state remains consistent across the primary-contact view and Food Incharge roster after retries, concurrency, notification failure, or connectivity loss.
- **REQUIREMENT NFR-3 Accessibility:** MVP satisfies AC-18 and uses respectful Husaynia-approved terminology.
- **ASSUMPTION A-5 — NON-BLOCKING:** No quantitative scale or response-time target is set until expected dates, members, team sizes, peak signup concurrency, and supported iOS versions are supplied; correctness and duplicate prevention remain mandatory.

## Remaining non-blocking questions

- **OPEN QUESTION NQ-1 — NON-BLOCKING:** How long are rosters, adult member references/display names, threads, notifications, privileged-access audit, and later-phase quotes retained, and who may export or delete them? **Recommended default:** Approve a written retention schedule before production personal data is collected.
- **OPEN QUESTION NQ-2 — NON-BLOCKING:** May minors or other non-members ever be named in a household/team signup, and what consent or safeguarding rules apply? **Recommended default:** MVP rejects those names and does not collect age or minor status until Husaynia approves the policy.
- **OPEN QUESTION NQ-3 — NON-BLOCKING:** What operational data may be copied from prior signups? **Recommended default:** Copy templates only; do not copy named participation.
- **OPEN QUESTION NQ-4 — NON-BLOCKING:** What expected scale and supported iOS versions define launch performance and compatibility? **Recommended default:** Obtain operating estimates before architecture and test planning.
