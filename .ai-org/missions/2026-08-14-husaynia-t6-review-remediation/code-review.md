# T6R Code Review

Verdict: CHANGES_REQUIRED

Blocking Medium: `HelpNeedSignups.NextWaitlistOrder` derives only from currently waitlisted children.
After the maximum-ordered signup is reassigned and its order is cleared, a later waitlist command
reuses that order. Runtime reproduction: removed order `2`, new order `2`.

Required: resolve the frozen contract's missing durable high-water representation, implement the
approved remedy, add regression coverage for maximum removal and permanent exhaustion, then rerun
invalidated gates.

Architect resolution: no exact-owned-file implementation can preserve monotonic non-reuse across
rehydration because terminal children must clear their order and the frozen `Rehydrate` contract
does not accept historical allocation state. Recommended amendment:

```csharp
public long WaitlistOrderHighWater { get; private set; }

public static Result<HelpNeedSignups> Rehydrate(
    ServiceDate serviceDate,
    HelpNeed helpNeed,
    long version,
    long waitlistOrderHighWater,
    IReadOnlyCollection<Signup> signups);
```

T8 must later persist `help_needs.waitlist_order_high_water` under the existing signup-version CAS.
