# ADR-1: Compile controlled-English scene prompts into existing timeline actions
Date: 2026-09-09     Status: Proposed

## Context
TCFPlay currently stores explicit action arrays, while the requested authoring contract stores prompts bound to keys. Runtime animation must remain deterministic, dependency-free, and compatible with existing assets and timeline behavior.

## Decision
Introduce schema v2 prompt scenes and compile a small, documented controlled-English grammar once at configuration load into the existing internal action representation. Add only the `say` action and reverse-turn state needed by the requested example. Retain schema v1 support temporarily for rollback and compatibility.

## Consequences
JSON becomes prompt-oriented without making runtime behavior probabilistic. The existing timeline remains the execution boundary, tests can assert exact compiled actions, and invalid wording fails early. Prompt authors must use supported phrasing, and grammar expansion becomes an intentional code-and-test change.

## Alternatives considered
- External or local generative AI — rejected because it adds nondeterminism, dependencies, latency, cost, and a trust boundary.
- Execute prompt text directly in the timeline — rejected because parsing concerns would contaminate deterministic runtime execution.
- Replace the action model entirely — rejected because it would rewrite proven animation logic and enlarge regression risk.
- Permissive/fuzzy parsing — rejected because silent guessing conflicts with explicit validation and predictable scenes.

# ADR-2: Render speech as deterministic Canvas state
Date: 2026-09-09     Status: Proposed

## Context
The requested prompt includes spoken text, but the current actor state and renderer have no speech concept. The application already uses one Canvas for scene presentation and an `aria-live` status output.

## Decision
Compile quoted speech to a timed `say` action, store active speech on the actor, render a clamped/wrapped bubble in Canvas, and mirror the spoken text to the status output for accessibility. Treat speech as text only and enforce length/control-character validation.

## Consequences
No DOM overlay, CSS positioning system, asset, or dependency is added. Bubble timing is reproducible and Escape dismissal remains centralized. Renderer and timeline gain a small new contract, and automated rendering geometry should be tested through pure helper functions where practical.

## Alternatives considered
- DOM speech overlay — rejected because it introduces a second coordinate/resize system.
- Browser text-to-speech — rejected because audible output, voice availability, and timing are platform-dependent and were not requested.
- Status text only — rejected because it does not visually satisfy “say” in the scene.
