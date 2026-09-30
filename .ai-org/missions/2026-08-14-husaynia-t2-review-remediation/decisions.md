# Decisions

- Keep the mission strictly within T2 contracts, primitives, and boundary tests.
- Treat language-level default strong IDs as possible and invalid at every public conversion boundary.
- Do not implement persistence storage or any T3 API convention.
- Security gate is N/A because this is a contract-remediation mission with no new runtime security surface; code review covers adherence to the previously reviewed architecture.

# ADR-1: Keep strong-ID raw GUID access as a validated `Value` property
Date: 2026-08-14     Status: Proposed

## Context
`StronglyTypedIds.cs` already exposes a public `Value` property and the only verified in-repo callers are tests. The defect is that `default(TId).Value` currently leaks `Guid.Empty`, even though `EnsureValid()` and `ToString()` already reject that sentinel.

## Decision
Keep the public accessor name `Value`, but back it with a private field and make the getter throw `InvalidOperationException` on the language-default instance. Do not introduce a separate `ToGuid()` API in T2.

## Consequences
All public raw-GUID conversions now fail closed without forcing a broader rename across the solution. Callers that intentionally depended on `default(TId).Value == Guid.Empty` will break, but no such in-repo production caller was verified.

## Alternatives considered
Replace `Value` with `ToGuid()` - rejected because it is a wider source break and does not add safety beyond a validated getter.

# ADR-2: Make idempotency lifecycle explicit with typed compare-and-set outcomes
Date: 2026-08-14     Status: Proposed

## Context
`IIdempotencyStore` currently exposes `FindAsync` + `AddAsync`, while `IdempotencyReceipt` is an immutable snapshot. That shape cannot express an atomic Processing -> Completed/Failed compare-and-set or typed duplicate/race/mismatch outcomes.

## Decision
Keep `FindAsync` and replace the write side with `TryCreateProcessingAsync`, `TryCompleteAsync`, and `TryFailAsync`, plus typed result enums/records. Treat duplicate, mismatch, missing, and expected-status conflicts as return values, not exceptions.

## Consequences
The Application contract becomes precise enough for future adapters and API mapping, and independent tests can freeze the lifecycle semantics before Infrastructure exists. Any future adapter must now implement the compare-and-set contract exactly.

## Alternatives considered
Keep `AddAsync` and rely on `FindAsync` + caller-side branching - rejected because it still cannot express one-step atomic transitions or race outcomes.  
Use generic `Result<T>`/`DomainError` - rejected because the review finding requires explicit typed conflict results, not string-coded errors.

# ADR-3: Guard Application dependencies with evaluated MSBuild items and mutation probes
Date: 2026-08-14     Status: Proposed

## Context
`ApplicationDependencyTests.cs` currently parses project XML and filters assembly references only to `HusayniaTabruk.*`. Imported `PackageReference`, `FrameworkReference`, or `ProjectReference` items can bypass that guard.

## Decision
Evaluate Application dependencies with `dotnet msbuild -getItem:ProjectReference -getItem:PackageReference -getItem:FrameworkReference`, reject any resolved dependency outside the allowed sets, and prove each forbidden dependency category with temp-project mutation probes.

## Consequences
The architecture test protects the real build graph, not just the checked-in XML text. The tests become slightly heavier, but they close the imported-item blind spot before T3 adds runtime code.

## Alternatives considered
Keep XML parsing - rejected because imported items are invisible to it.  
Rely on assembly references only - rejected because dormant package/framework references may not surface until later code starts using them.
