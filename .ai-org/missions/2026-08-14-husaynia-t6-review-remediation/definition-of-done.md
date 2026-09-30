# T6R Definition of Done

- [ ] `HelpNeedSignups` exposes `WaitlistOrderHighWater` and exactly rehydrates with `(ServiceDate serviceDate, HelpNeed helpNeed, long version, long waitlistOrderHighWater, IReadOnlyCollection<Signup> signups)` while exclusively owning children.
- [ ] `Signup` has immutable `ServiceDateId`, exact reachable versions, chronology, internal mutators, and deep copy.
- [ ] Canonical organization/date/need identity and complete-set aggregate invariants reject malformed rehydration.
- [ ] Capacity, durable waitlist high-water, Food-Incharge-selected reassignment, active-primary uniqueness, overflow, root/child versions, and atomic failure are internal and deterministic.
- [ ] Rehydration rejects negative high-water and any current order above it, but accepts retained gaps and a high-water value above the current maximum.
- [ ] Removing/reassigning the maximum order, rehydrating, and assigning another waitlisted signup never reuses the removed order.
- [ ] `long.MaxValue` high-water exhaustion returns `version_exhausted` without changing root version, child state/version, or high-water.
- [ ] Counterfeit-date, omission/partial-snapshot, overbooking, chronology, and version attacks reject.
- [ ] Existing independent T6 coverage is migrated without weakening.
- [ ] `SignupCapacity.cs` and `HelpNeedSignupExtensions.cs` are deleted and unsafe public APIs are absent.
- [ ] Rework changes only `HelpNeedSignups.cs`, `SignupTests.cs`, and `T6IndependentSignupTests.cs`; no persistence/API/application code is added.
- [ ] Focused Signups tests and full Domain tests pass.
- [ ] Solution build/test/format, mobile lint/typecheck/tests, and Compose configuration pass.
- [ ] Independent test, security, and code-review gates pass.
- [ ] Engineering Judge verdict is APPROVED.
