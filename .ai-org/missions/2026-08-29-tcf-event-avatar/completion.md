# Completion

**Status:** APPROVED

The dependency-free TCF event avatar is complete. It provides a black, replaceable stage background; a recognizable `Me.jpg`-derived avatar in a white shalwar qameez and green waistcoat; smooth bounded left/right movement; a three-second `Welcome to TCF` bubble; and a restartable five-second clap.

## Gates

- Tests: PASS — 42 tests, six canonical runs, 252 total passes and no failures.
- Code review: APPROVED.
- Security: PASS — zero findings after the original portrait was made private and replaced at runtime by a metadata-free face crop.
- Browser QA: PASS at 1280x720 and 1920x1080.
- Engineering judgment: FINAL APPROVED — GO.

## Evidence

- `evidence/idle-1280x720.png`
- `evidence/idle-1920x1080.png`
- Original `Me.jpg` SHA-256: `A9D37E52A7ABA927C06046A241D63EE8514C6B95B28508750885275773C98860`
- Derived face SHA-256: `B6DB9257818554CF22F9DD7D718CCB56BB6FDA8DA576F6BF67817B53D3A113AE`

## Run

From `TCFMacot`, run `npm start`, open `http://127.0.0.1:4173`, and enter browser fullscreen.

The only remaining recommendation is a rehearsal on the intended projector and venue display chain.
