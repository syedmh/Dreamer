# TCFPlay Prompt-Scene Architecture

## Current state (verified)

- **FACT:** `src/app.js:92-103` fetches `data/playground.v1.json`, validates it, and builds a key-to-scene map.
- **FACT:** `src/config.js:1-38,88-103` validates explicit `spawn`, `wait`, `turn`, and `moveTo` arrays and fixed Digit1-Digit5 scene bindings.
- **FACT:** `src/timeline.js:1-148` executes `scene.actions` deterministically and owns actor animation state.
- **FACT:** `src/renderer.js:30-75` draws the background and visible actors only; speech has no state or renderer.
- **FACT:** `src/controls.js:13-17` maps Escape to dismiss before consulting the scene key map.
- **FACT:** `src/server-utils.mjs:22-32` allowlists the v1 JSON and every browser module.
- **FACT:** the Node test baseline is 14/14 passing on September 9, 2026.
- **FACT:** no TCFPlay-specific prior ADR was found.

## Desired data flow

`playground.v2.json prompt scene` → `validateConfig` → `compilePrompt` → normalized scene with internal actions → unchanged key mapping → timeline → renderer (actor plus speech bubble).

Prompts are configuration syntax, not runtime AI requests. Compilation occurs once during initialization, uses no network or dependency, and either returns one exact action sequence or throws a configuration error.

## Contract

```json
{
  "schemaVersion": 2,
  "scenes": [
    {
      "id": "welcome-seattle",
      "title": "Welcome Seattle",
      "key": "Digit1",
      "autoPlay": true,
      "prompt": "Girl walk from the left side of the screen, turn front facing and say \"Welcome Seattle\""
    }
  ]
}
```

Public JSON v2 scenes contain `id`, `title`, unique keyboard `key`, optional `autoPlay`, and non-empty `prompt`; they do not contain `actions`. Internally, `validateConfig(config)` returns a normalized copy whose scenes retain `prompt` and gain compiled `actions`. `compilePrompt(prompt, context)` returns action objects or throws `Configuration error: scene "<id>" prompt: <specific reason>`.

The example compiles deterministically to:

```js
[
  { type: "spawn", actor: "girl", x: -0.12, y: 0.9, facing: "right" },
  { type: "moveTo", actor: "girl", x: 0.5, y: 0.9, durationMs: 3000, walkFps: 24 },
  { type: "turn", actor: "girl", facing: "front", durationMs: 1200, fps: 8 },
  { type: "say", actor: "girl", text: "Welcome Seattle", durationMs: 2400 }
]
```

Named locations are fixed compiler constants: left offscreen `-0.12`, center `0.5`, right offscreen `1.12`, ground `0.9`. Timing and FPS are fixed constants so wording cannot alter animation nondeterministically.

## Supported grammar

Case-insensitive keywords with normalized whitespace; actor names, quoted speech, and clause order remain strict.

```text
<Actor> walk from the <left|right> side of the screen
        [,] [to the <center|opposite side> of the screen]
        [,] [turn <front|left|right> facing]
        [and say "<1..160 characters>"]

<Actor> turn <left|right|front> [facing]
        [and walk off the <left|right> side of the screen]
        [and say "<1..160 characters>"]
```

For the first form, omitted destination means center. Unsupported verbs, unknown actors, unquoted speech, contradictory direction/destination, trailing text, unsupported turn transitions, or overlong/empty speech are errors. Do not use fuzzy matching, synonyms beyond the documented tokens, or partial compilation.

## Delta by file

- `data/playground.v2.json` — add v2 prompt scenes; preserve stage, character, animation, and asset configuration. Migrate all five existing behaviors plus the requested Digit1 example.
- `src/prompt-compiler.js` — new dependency-free tokenizer/parser/compiler; export `compilePrompt(prompt, { sceneId, characters })`.
- `src/config.js` — accept schema v2 prompt scenes, call the compiler, validate compiled actions including new `say`; temporarily keep schema v1 action validation for compatibility. Replace fixed scene IDs with invariant checks for unique keys and exactly one autoplay scene.
- `src/timeline.js` — add `say`; add reverse-turn state so right/left profile can correctly animate back to front using existing frames in reverse. Keep action execution and timing model unchanged.
- `src/renderer.js` — draw deterministic Canvas speech bubbles after actors so bubbles remain on top; clamp bubble rectangle to stage bounds and wrap text to a fixed maximum width.
- `src/app.js` — fetch v2 JSON. No keyboard-flow change.
- `src/server-utils.mjs` — allowlist `playground.v2.json` and `prompt-compiler.js`; retain v1 allowlist during the compatibility window.
- `tests/prompt-compiler.test.mjs` — new grammar/compiler tests.
- `tests/config.test.mjs`, `tests/timeline.test.mjs`, `tests/server-utils.test.mjs` — update/add v2, speech, reverse-turn, compatibility, and allowlist coverage.
- `README.md` — document prompt syntax, defaults, and explicit errors.

No CSS change is required because the bubble is rendered inside Canvas. No asset, background, dependency, server, or deployment-model change is required.

## Cross-cutting behavior

- **Failure:** invalid prompts fail initialization through the existing error panel; no scene runs partially.
- **Idempotency/concurrency:** compilation is pure; each keypress creates a new state; the single active scene model remains. Escape clears actor visibility and speech through dismissal.
- **Security/trust:** JSON remains same-origin and allowlisted. Speech is drawn with Canvas `fillText`, never inserted as HTML. Enforce length/control-character validation.
- **Observability:** preserve the full scene ID and prompt error offset/clause in configuration errors; existing `showError` logs it.
- **Accessibility:** status should change to `Girl says: Welcome Seattle` when `say` starts so the canvas-only bubble is represented by the existing `aria-live` output. This can be exposed as a timeline event or derived from active action transitions; prefer a small transition result from `advanceTimeline`.

## Migration, tradeoffs, and risks

Ship v2 beside v1, switch the app to v2, and retain v1 validation/serving for one compatibility window; rollback is changing the fetch back to v1. This is additive and reversible. Remove v1 only in a later explicit schema cleanup.

The design optimizes for deterministic behavior, explicit failures, and reuse of the existing timeline. It gives up open-ended natural language: authors must follow a documented controlled-English grammar. A generic NLP library, regex-only monolith, external AI service, and replacing the timeline were rejected as unnecessary, less deterministic, or larger in blast radius.

Primary risks are grammar ambiguity, reverse-turn frame mistakes, and bubble overflow. Mitigate with anchored clause parsing, golden action-array tests, frame-boundary tests, text length limits, wrapping, and stage-edge clamping.

