# TCFPlay

A dependency-free Canvas 2D playground that compiles a key-bound JSON prompt into deterministic Boy animation.

## Run

Requires Node.js 20 or newer.

```text
run.bat
```

Open `http://127.0.0.1:8080/`.

- `1`: appear in the middle facing front, then turn right
- `Escape`: dismiss the current scene

The page starts empty and waits for a keypress. The active scene is defined in `data\playground.v2.json`:

```json
{
  "key": "Digit1",
  "prompt": "Start middle of screen front facing then turn right"
}
```

The prompt compiler accepts this exact controlled prompt and reports an explicit error for unsupported text. It does not use AI or guess at intent.

## Frames

`BoyTurning.png` supplies nine front-to-right poses:

```text
python tools\extract_boy_turn_frames.py
```

`BoyWalk1.png` and `BoyWalk2.png` supply three key walking poses each. `BoyWalk3.png` supplies six additional poses:

```text
python tools\extract_boy_walk_frames.py
```

The extraction tools remove the baked checkerboard backgrounds. The walking extractor writes 12 review frames in sheet order: `BoyWalk1.png` left-to-right, `BoyWalk2.png` left-to-right, then `BoyWalk3.png` top-row left-to-right followed by bottom-row left-to-right. Frames 7–12 are scaled to match `walk_01.png`. These frames are not yet used in an animation sequence.

## Verify

```text
node --check server.mjs
node --check src\app.js
node --check src\prompt-compiler.js
node --test
```
