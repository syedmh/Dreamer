# Corrected Foreground E2E / Visual QA

Status: **PASS**

- Assertions: 51 (51 passed, 0 failed)
- Screenshots: 85 (48 unique SHA-256 values)
- Focus checks: 104
- Processes: 12
- Release SHA-256 after run: `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`

## Assertions

### PASS — `release.executable.sha256`

- Expected: `"6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F"`
- Actual: `"6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F"`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe` SHA-256 `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`

### PASS — `baseline.visible-character-on-black`

- Expected: `{"characterAreaGreaterThan": 40000, "heightRatioGreaterThan": 0.7, "backgroundBlackRatioGreaterThan": 0.85}`
- Actual: `{"area": 65080, "bbox": [538, 150, 202, 551], "centroid": [640.58, 416.32], "centroidRatio": [0.50045, 0.57823], "heightRatio": 0.76528, "backgroundBlackRatio": 0.92863}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-idle.png` SHA-256 `6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4`

### PASS — `baseline.front-idle-pose`

- Expected: `{"group": "LeftTurn", "frame": 0, "minimumScore": 0.94}`
- Actual: `{"group": "LeftTurn", "frame": 0, "score": 0.98335}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-idle.png` SHA-256 `6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4`

### PASS — `directional.left-traversal-and-edge`

- Expected: `"centroid moves left and reaches left 20% edge zone"`
- Actual: `{"directional-idle": 640.58, "directional-left-walk": 482.89, "directional-left-edge": 152.58}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-left-edge.png` SHA-256 `2377AE47EAA5E7258A605FB667D8838263E4F9E468765A6B92BE2DC10214FB76`

### PASS — `directional.left-release-returns-front-at-edge`

- Expected: `"front pose remains in calibrated left edge zone"`
- Actual: `{"centroidX": 152.58, "classification": {"group": "LeftTurn", "frame": 0, "score": 0.98335}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-left-return.png` SHA-256 `2377AE47EAA5E7258A605FB667D8838263E4F9E468765A6B92BE2DC10214FB76`

### PASS — `directional.reversal-and-both-held-neutral`

- Expected: `"reversal changes pose; both-held settles to front without edge jump"`
- Actual: `{"rightWalkHash": "63F88E941F69CAC610A3C50635DBB70DD66C30FF06CACEF19B298D84352184B2", "reversalHash": "CE4D2157493EB91ED33D6E074D591EDE8196369883564663C4220AC143477121", "bothHeldClassification": {"group": "LeftTurn", "frame": 0, "score": 0.984}, "reversalX": 278.07, "bothHeldX": 278.07}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-both-held-neutral.png` SHA-256 `CE4D2157493EB91ED33D6E074D591EDE8196369883564663C4220AC143477121`

### PASS — `directional.right-traversal-and-edge`

- Expected: `"centroid reaches right 80% edge zone and returns to front"`
- Actual: `{"edgeX": 1130.07, "returnX": 1130.07, "returnClassification": {"group": "LeftTurn", "frame": 0, "score": 0.984}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\directional-right-return.png` SHA-256 `8584E97659798DCDE674BB6C1C66A536C54803AE8EEDC6EA316DCCAF39D85FB6`

### PASS — `speed.runtime-log-boundaries`

- Expected: `["WALK_SPEED multiplier=0.25x movement=60px/s walk_fps=1.5", "WALK_SPEED multiplier=3.00x movement=720px/s walk_fps=18"]`
- Actual: `["WALK_SPEED multiplier=0.75x movement=180px/s walk_fps=4.5", "WALK_SPEED multiplier=0.50x movement=120px/s walk_fps=3", "WALK_SPEED multiplier=0.25x movement=60px/s walk_fps=1.5", "WALK_SPEED multiplier=0.50x movement=120px/s walk_fps=3", "WALK_SPEED multiplier=0.75x movement=180px/s walk_fps=4.5", "WALK_SPEED multiplier=1.00x movement=240px/s walk_fps=6", "WALK_SPEED multiplier=1.25x movement=300px/s walk_fps=7.5", "WALK_SPEED multiplier=1.50x movement=360px/s walk_fps=9", "WALK_SPEED multiplier=1.75x movement=420px/s walk_fps=10.5", "WALK_SPEED multiplier=2.00x movement=480px/s walk_fps=12", "WALK_SPEED multiplier=2.25x movement=540px/s walk_fps=13.5", "WALK_SPEED multiplier=2.50x movement=600px/s walk_fps=15", "WALK_SPEED multiplier=2.75x movement=660px/s walk_fps=16.5", "WALK_SPEED multiplier=3.00x movement=720px/s walk_fps=18"]`

### PASS — `speed.measurable-traversal-difference`

- Expected: `"maximum-speed displacement is greater than 4x minimum-speed displacement"`
- Actual: `{"minimumDistancePixels": 49.52, "maximumDistancePixels": 546.3, "ratio": 11.03}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\speed-max-walk.png` SHA-256 `07CD82D207E9B9EC0ED4FDC6BE496BE1E6052961753CE2DD5EE282934C0BAD98`

### PASS — `regression.w-is-visual-noop`

- Expected: `{"changedPixels": 0, "maxChannelDelta": 0}`
- Actual: `{"sameShape": true, "changedPixels": 0, "meanAbsoluteDelta": 0.0, "maxChannelDelta": 0}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\w-noop-after.png` SHA-256 `2377AE47EAA5E7258A605FB667D8838263E4F9E468765A6B92BE2DC10214FB76`

### PASS — `clap.exact-15-step-asset-order`

- Expected: `[0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0]`
- Actual: `[{"group": "Clap", "frame": 0, "score": 0.98691}, {"group": "Clap", "frame": 1, "score": 0.9841}, {"group": "Clap", "frame": 2, "score": 0.98403}, {"group": "Clap", "frame": 3, "score": 0.98461}, {"group": "Clap", "frame": 4, "score": 0.98573}, {"group": "Clap", "frame": 3, "score": 0.98461}, {"group": "Clap", "frame": 2, "score": 0.98403}, {"group": "Clap", "frame": 1, "score": 0.9841}, {"group": "Clap", "frame": 2, "score": 0.98403}, {"group": "Clap", "frame": 3, "score": 0.98461}, {"group": "Clap", "frame": 4, "score": 0.98573}, {"group": "Clap", "frame": 3, "score": 0.98461}, {"group": "Clap", "frame": 2, "score": 0.98403}, {"group": "Clap", "frame": 1, "score": 0.9841}, {"group": "Clap", "frame": 0, "score": 0.98691}]`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\clap-sample-000.png` SHA-256 `9A70B58DE086A5B39D52F3DE4BE320897464996A784E9EBBD843944A1D3BB5D5`

### PASS — `clap.return-and-directional-interruption`

- Expected: `"returns to front; right input interrupts into directional pose; C is ignored while moving"`
- Actual: `{"return": {"group": "LeftTurn", "frame": 0, "score": 0.98335}, "interrupted": {"group": "RightWalk", "frame": 1, "score": 0.9808}, "cWhileMoving": {"group": "RightWalk", "frame": 4, "score": 0.98398}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\clap-direction-interrupted.png` SHA-256 `06CDB64A337076F6994CFA75BEB74286CC5F05B9C9B10E960CFA18CE141658AA`

### PASS — `cross.entry-three-frames`

- Expected: `[0, 1, 2]`
- Actual: `[{"group": "CrossArm", "frame": 0, "score": 0.98828}, {"group": "CrossArm", "frame": 1, "score": 0.98611}, {"group": "CrossArm", "frame": 2, "score": 0.98481}]`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\cross-entry-00.png` SHA-256 `0A21C9337905F9DC00092239B6F25FDFD6C3E0983CB2B9C41D6C6B1CFE32015D`

### PASS — `cross.held-pose-stable-and-inputs-blocked`

- Expected: `{"frame": 2, "changedPixels": 0}`
- Actual: `{"classification": {"group": "CrossArm", "frame": 2, "score": 0.98481}, "deltaAfterCAndHeldLeft": {"sameShape": true, "changedPixels": 0, "meanAbsoluteDelta": 0.0, "maxChannelDelta": 0}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\cross-held-inputs-blocked.png` SHA-256 `E0E7BD8EB63D413783202680A90EEC6DF9D385EF05A441A039A56D431A4C8CBE`

### PASS — `cross.release-six-frames`

- Expected: `[0, 1, 2, 3, 4, 5]`
- Actual: `[{"group": "CrossArmRelease", "frame": 0, "score": 0.97992}, {"group": "CrossArmRelease", "frame": 1, "score": 0.98574}, {"group": "CrossArmRelease", "frame": 2, "score": 0.98278}, {"group": "CrossArmRelease", "frame": 3, "score": 0.98556}, {"group": "CrossArmRelease", "frame": 4, "score": 0.98516}, {"group": "CrossArmRelease", "frame": 5, "score": 0.98388}]`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\cross-release-00.png` SHA-256 `2BEE5277C806361770F599DE9C4D56D97B62696EFB95EE651643FF75AA328B79`

### PASS — `cross.held-arrow-not-queued-fresh-arrow-moves`

- Expected: `"release completes at front without queued motion; fresh Left moves"`
- Actual: `{"stillClassification": {"group": "LeftTurn", "frame": 0, "score": 0.98335}, "stillX": 640.58, "freshX": 574.64, "freshClassification": {"group": "LeftWalk", "frame": 2, "score": 0.98101}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\cross-arrow-repress-moves.png` SHA-256 `0EA6DC0F0750A9C3A710C0049D3D9F28F8AF13E778E85AE7521392FB52A05017`

### PASS — `dialogue.exact-unicode-input-roundtrip`

- Expected: `"ASCII w/W | العربية | 😀🧪"`
- Actual: `"ASCII w/W | العربية | 😀🧪"`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-exact-input.png` SHA-256 `079B989E2232B09CED232263B3302B934B30FF281F772192BBDCB344C7CFB6DA`

### PASS — `dialogue.p-is-typeable-while-editing`

- Expected: `"p (replaces the Ctrl+A selection left by the round-trip check)"`
- Actual: `"p"`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-exact-input.png` SHA-256 `079B989E2232B09CED232263B3302B934B30FF281F772192BBDCB344C7CFB6DA`

### PASS — `dialogue.input-panel-and-text-visibly-render`

- Expected: `"input panel is visible and exact text changes rendered pixels"`
- Actual: `{"emptyPanel": {"present": true, "area": 13011, "bbox": [831, 626, 414, 34], "centroid": [1047.62, 642.35], "inside": true}, "changedPixels": 6090, "exactClipboard": "ASCII w/W | العربية | 😀🧪"}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-exact-input.png` SHA-256 `079B989E2232B09CED232263B3302B934B30FF281F772192BBDCB344C7CFB6DA`

### PASS — `dialogue.editing-suppresses-character-controls-and-speed`

- Expected: `"same character pose/location and no WALK_SPEED log"`
- Actual: `{"before": {"bbox": [538, 150, 202, 551], "centroid": [640.58, 416.32]}, "after": {"bbox": [538, 150, 202, 551], "centroid": [640.58, 416.32]}, "walkSpeedLogPresent": false}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-controls-after.png` SHA-256 `7F76D55B923DF7D6770073C3746A4A5A298EFF61B38FA6EB1CBC60A46497FD8E`

### PASS — `dialogue.submit-whitespace-escape-and-hide-semantics`

- Expected: `"submit shows bubble; whitespace and Escape preserve it; P hides it"`
- Actual: `{"submittedBubble": {"present": true, "area": 17427, "bbox": [457, 62, 366, 55], "centroid": [639.22, 88.76], "inside": true}, "whitespaceDelta": {"sameShape": true, "changedPixels": 0, "meanAbsoluteDelta": 0.0, "maxChannelDelta": 0}, "escapeDelta": {"sameShape": true, "changedPixels": 0, "meanAbsoluteDelta": 0.0, "maxChannelDelta": 0}, "hiddenBubble": {"present": false}, "whitespaceClipboard": "   "}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-hidden-p.png` SHA-256 `6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4`

### PASS — `dialogue.500-and-501-unicode-scalar-policy`

- Expected: `{"500InputScalars": 500, "501InputBoundedScalars": 500, "501InputDropsFinalTestTube": true, "submittedVisualsEqual": true}`
- Actual: `{"500Scalars": 500, "501Scalars": 500, "500AllEmoji": true, "501BoundedValueMatches": true, "submittedDelta": {"sameShape": true, "changedPixels": 0, "meanAbsoluteDelta": 0.0, "maxChannelDelta": 0}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-501-submitted.png` SHA-256 `3AE652EF798F165925E8963AD9CFECE076124F16C8109EABB8AACD7A5B489687`

### PASS — `fullscreen.alt-enter-closed-dialogue`

- Expected: `"Alt+Enter enters fullscreen and returns windowed while dialogue is closed"`
- Actual: `{"initial": [1280, 720], "fullscreen": [1922, 1200], "backWindowed": [1280, 720]}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-alt-enter-closed-fullscreen.png` SHA-256 `02233BC58A574B082DAB0F352A5B55E6C5CA1D0330E7E653F50764EF0AD56EBB`

### PASS — `dialogue.f11-and-escape-arbitration`

- Expected: `"F11 enters fullscreen with editor open, focused, and exact text retained; first Escape cancels editor only; second Escape exits fullscreen"`
- Actual: `{"sizes": {"dialogue-f11-open-fullscreen": [1922, 1200], "dialogue-alt-enter-open-windowed": [1280, 720], "dialogue-before-first-escape-fullscreen": [1922, 1200], "dialogue-first-escape-cancel-only": [1922, 1200], "dialogue-second-escape-exits-fullscreen": [1280, 720]}, "f11Panel": true, "clipboardBeforeFullscreen": "fullscreen w/W 😀", "clipboardAfterF11": "fullscreen w/W 😀", "firstEscapePanel": false}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-first-escape-cancel-only.png` SHA-256 `02233BC58A574B082DAB0F352A5B55E6C5CA1D0330E7E653F50764EF0AD56EBB`

### PASS — `dialogue.alt-enter-while-editing`

- Expected: `{"expectedTransitions": [[1280, 720], ["fullscreen"], [1280, 720], ["fullscreen"]], "dialogueRemainsOpen": true, "exactTextRetained": true, "lineEditFocusRetained": true, "exactlyOneTogglePerChord": true}`
- Actual: `{"actualTransitions": [[1280, 720], [1922, 1200], [1280, 720], [1922, 1200]], "dialogueAltEnterEditingSucceeded": true, "clipboardAfterF11": "fullscreen w/W 😀", "clipboardAfterAltEnterToWindowed": "fullscreen w/W 😀", "clipboardAfterAltEnterToFullscreen": "fullscreen w/W 😀", "inputPanel": {"present": true, "area": 22028, "bbox": [1248, 999, 621, 51], "centroid": [1635.13, 1024.42], "inside": true}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-alt-enter-open-fullscreen.png` SHA-256 `0559F1255CEBA9D16C40BA6DD780C44063DA5C89E5875B3C1BD05912E564D2A7`

### PASS — `dialogue.left-edge-bubble-windowed`

- Expected: `"character in calibrated edge zone; bubble inside viewport and tracks character side"`
- Actual: `{"character": {"bbox": [50, 150, 202, 551], "centroidRatio": [0.1192, 0.57823]}, "bubble": {"present": true, "area": 11379, "bbox": [29, 62, 245, 55], "centroid": [149.55, 88.65], "inside": true}, "imageSize": [1280, 720]}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-left-edge-windowed.png` SHA-256 `D3FB1766430DAB12756A541DDAB4A3E08AE79CD8A0DEC1F7BD7B13D3525C571C`

### PASS — `dialogue.left-edge-bubble-fullscreen`

- Expected: `"character in calibrated edge zone; bubble inside viewport and tracks character side"`
- Actual: `{"character": {"bbox": [74, 285, 304, 826], "centroidRatio": [0.11912, 0.57048]}, "bubble": {"present": true, "area": 26596, "bbox": [44, 153, 368, 84], "centroid": [225.53, 194.07], "inside": true}, "imageSize": [1922, 1200]}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-left-edge-fullscreen.png` SHA-256 `A73E26152936962180E24DED178D606DD3AFEDBC3C4D57AA4AD3ADB420867E58`

### PASS — `dialogue.right-edge-bubble-windowed`

- Expected: `"character in calibrated edge zone; bubble inside viewport and tracks character side"`
- Actual: `{"character": {"bbox": [1027, 150, 203, 551], "centroidRatio": [0.88287, 0.57819]}, "bubble": {"present": true, "area": 11882, "bbox": [1000, 62, 259, 55], "centroid": [1128.41, 88.58], "inside": true}, "imageSize": [1280, 720]}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-right-edge-windowed.png` SHA-256 `29D33091C8318C3709D0A08E4F7C140171A8E47CF80C8E15C5F94D89D8AAB581`

### PASS — `dialogue.right-edge-bubble-fullscreen`

- Expected: `"character in calibrated edge zone; bubble inside viewport and tracks character side"`
- Actual: `{"character": {"bbox": [1540, 285, 304, 826], "centroidRatio": [0.88187, 0.57048]}, "bubble": {"present": true, "area": 27857, "bbox": [1499, 153, 389, 84], "centroid": [1692.69, 194.01], "inside": true}, "imageSize": [1922, 1200]}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\dialogue-right-edge-fullscreen.png` SHA-256 `E3E5D6DDE9F38E48620FD6A5E5C55F3A6F4C68075804CDAF50CB25A7A0913BE9`

### PASS — `viewport.1000x800.scaled-center-and-right-dialogue`

- Expected: `{"clientSize": [1000, 800], "centerCharacterHeightApprox": 430.34, "rightCharacterZoneGreaterThan": 0.8, "bubbleInside": true}`
- Actual: `{"clientSize": [1000, 800], "centerCharacter": {"bbox": [420, 236, 158, 430], "centroidRatio": [0.50032, 0.55486]}, "rightCharacter": {"bbox": [802, 236, 159, 430], "centroidRatio": [0.88283, 0.55473]}, "bubble": {"present": true, "area": 6682, "bbox": [790, 167, 185, 44], "centroid": [881.69, 188.35], "inside": true}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\viewport-1000x800-right-dialogue.png` SHA-256 `CA59811D881AB812C7F9D644D092FB056D431A0C70E6DF319EBDA651188FFAA8`

### PASS — `viewport.1280x720.scaled-center-and-right-dialogue`

- Expected: `{"clientSize": [1280, 720], "centerCharacterHeightApprox": 550.83, "rightCharacterZoneGreaterThan": 0.8, "bubbleInside": true}`
- Actual: `{"clientSize": [1280, 720], "centerCharacter": {"bbox": [538, 150, 202, 551], "centroidRatio": [0.50045, 0.57823]}, "rightCharacter": {"bbox": [1027, 150, 203, 551], "centroidRatio": [0.88287, 0.57819]}, "bubble": {"present": true, "area": 10810, "bbox": [1011, 62, 237, 55], "centroid": [1127.46, 88.77], "inside": true}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\viewport-1280x720-right-dialogue.png` SHA-256 `61B083B7C7E9EDD8B2F94910CBCA249F52F9F3F19D4D6DE4834FFE40653C744E`

### PASS — `viewport.1536x864.scaled-center-and-right-dialogue`

- Expected: `{"clientSize": [1536, 864], "centerCharacterHeightApprox": 661.0, "rightCharacterZoneGreaterThan": 0.8, "bubbleInside": true}`
- Actual: `{"clientSize": [1536, 864], "centerCharacter": {"bbox": [645, 180, 243, 661], "centroidRatio": [0.50049, 0.57836]}, "rightCharacter": {"bbox": [1232, 180, 243, 661], "centroidRatio": [0.88265, 0.57836]}, "bubble": {"present": true, "area": 16040, "bbox": [1213, 74, 284, 67], "centroid": [1352.65, 106.79], "inside": true}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\viewport-1536x864-right-dialogue.png` SHA-256 `D14C196A79748F6B744573788C15A00B6AA91E98838059DBBEAAFCB9BB97B04C`

### PASS — `viewport.1536x960.scaled-center-and-right-dialogue`

- Expected: `{"clientSize": [1536, 960], "centerCharacterHeightApprox": 661.0, "rightCharacterZoneGreaterThan": 0.8, "bubbleInside": true}`
- Actual: `{"clientSize": [1536, 960], "centerCharacter": {"bbox": [645, 228, 243, 661], "centroidRatio": [0.50049, 0.57053]}, "rightCharacter": {"bbox": [1232, 228, 243, 661], "centroidRatio": [0.88265, 0.57053]}, "bubble": {"present": true, "area": 16005, "bbox": [1213, 122, 284, 67], "centroid": [1352.6, 154.8], "inside": true}}`
- Evidence: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\viewport-1536x960-right-dialogue.png` SHA-256 `5DA357CEB1588D07D22799C4CD637D388A95EFDE6F5EFB5A42F66A20BB3F3343`

### PASS — `process.directional.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.speed-w-noop.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.clap.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.cross.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.dialogue-text-lifecycle.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.dialogue-fullscreen-arbitration.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.dialogue-scalar-limits.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.dialogue-edges.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.viewport-1000x800.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.viewport-1280x720.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.viewport-1536x864.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `process.viewport-1536x960.clean-exit`

- Expected: `{"exitCode": 0, "runtimeErrors": 0}`
- Actual: `{"exitCode": 0, "stderr": "", "stdoutErrorLines": []}`

### PASS — `stale-capture.directional.unique-hashes`

- Expected: `{"minimumUnique": 7, "captures": 10}`
- Actual: `{"unique": 7, "captures": 10, "hashCounts": [["2377AE47EAA5E7258A605FB667D8838263E4F9E468765A6B92BE2DC10214FB76", 2], ["CE4D2157493EB91ED33D6E074D591EDE8196369883564663C4220AC143477121", 2], ["8584E97659798DCDE674BB6C1C66A536C54803AE8EEDC6EA316DCCAF39D85FB6", 2], ["6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4", 1], ["A95BD24688BD5FDC3511C0BA5671675692F07334A276E38050F38D2788DCC5F1", 1], ["4E2A036669F2E2BE654B5356F66206D54DCD7F12396D4F592C5DDEF51C2CDA39", 1], ["63F88E941F69CAC610A3C50635DBB70DD66C30FF06CACEF19B298D84352184B2", 1]]}`

### PASS — `stale-capture.clap15.unique-hashes`

- Expected: `{"minimumUnique": 5, "captures": 18}`
- Actual: `{"unique": 6, "captures": 18, "hashCounts": [["D3CFBBE473A6F792140F4E9189E33C9439F361FD5EE6E03329EAAEB32001925D", 4], ["4F2280B8A8F357F1E769B9C51D62466E0C3AFD408ABD013CC69ACA44F99163ED", 4], ["5E4A03B8B4037608BF55BE3427FFA0D188FC26BC9CDCA124DB2201B7AC67FD00", 3], ["6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4", 3], ["9A70B58DE086A5B39D52F3DE4BE320897464996A784E9EBBD843944A1D3BB5D5", 2], ["89E01CAA6E2EE0AEA602CE2F95B226BC93BF11E98017896BB12B60AF85821370", 2]]}`

### PASS — `stale-capture.crossEntry.unique-hashes`

- Expected: `{"minimumUnique": 3, "captures": 3}`
- Actual: `{"unique": 3, "captures": 3, "hashCounts": [["0A21C9337905F9DC00092239B6F25FDFD6C3E0983CB2B9C41D6C6B1CFE32015D", 1], ["79E1E3C6E91CF8B4F149DB3C9E06864DA055880EA42FDF482760606D09088CC0", 1], ["E0E7BD8EB63D413783202680A90EEC6DF9D385EF05A441A039A56D431A4C8CBE", 1]]}`

### PASS — `stale-capture.crossRelease.unique-hashes`

- Expected: `{"minimumUnique": 6, "captures": 6}`
- Actual: `{"unique": 6, "captures": 6, "hashCounts": [["2BEE5277C806361770F599DE9C4D56D97B62696EFB95EE651643FF75AA328B79", 1], ["539CD43204E0FB5E39FFC5AC573C6995B6D6A8D7A2B3150C5F23B6E95435E05A", 1], ["8A8DD3B2939923F8DF0935A46D201F6D79B033F9D460441209A72E9E5ED9C89D", 1], ["7A9586F62B607C1AF20D72D07A8D3853717088F45471E5CB518C272F736236DB", 1], ["C6CDDF596FE447AE1EE071A39C0DDF63F640C0C8DB119A78304B47D2328960F9", 1], ["7B5CCC811C072F1F6D261DEEDBDAA27ACE4F8D6348F03226B2299E4A5B4F58DE", 1]]}`

### PASS — `stale-capture.dialogue.unique-hashes`

- Expected: `{"minimumUnique": 10, "captures": 26}`
- Actual: `{"unique": 15, "captures": 26, "hashCounts": [["6E10453316B414305710DE84E0423BFA4586E0267C926582FF31E3342B77CDE4", 4], ["18B035B3BC5BD8BF63B55BF25BF322DF340D3A4F05E1DE90FE6C7501AADCF899", 3], ["0559F1255CEBA9D16C40BA6DD780C44063DA5C89E5875B3C1BD05912E564D2A7", 3], ["02233BC58A574B082DAB0F352A5B55E6C5CA1D0330E7E653F50764EF0AD56EBB", 2], ["3F1D44D52B93CFF23BA6BD2831636C13FD54B6467A1566B3EC6E29524A84BB4D", 2], ["3AE652EF798F165925E8963AD9CFECE076124F16C8109EABB8AACD7A5B489687", 2], ["29D33091C8318C3709D0A08E4F7C140171A8E47CF80C8E15C5F94D89D8AAB581", 2], ["134B057EA0EEB7AD88F7B77D060F6E2A46406D27177D95623BEC7984B15A4B60", 1], ["079B989E2232B09CED232263B3302B934B30FF281F772192BBDCB344C7CFB6DA", 1], ["0919E1866E8B2894B8A02E31A182598459DF84E92849CCFE141D0E7FD95268C4", 1], ["7F76D55B923DF7D6770073C3746A4A5A298EFF61B38FA6EB1CBC60A46497FD8E", 1], ["5814060ACE77D8D1AED904CA461C8E6BF621A23158BB3B3A3B18BAD5D0FB964E", 1], ["D3FB1766430DAB12756A541DDAB4A3E08AE79CD8A0DEC1F7BD7B13D3525C571C", 1], ["A73E26152936962180E24DED178D606DD3AFEDBC3C4D57AA4AD3ADB420867E58", 1], ["E3E5D6DDE9F38E48620FD6A5E5C55F3A6F4C68075804CDAF50CB25A7A0913BE9", 1]]}`

### PASS — `foreground.focus-verification`

- Expected: `{"failedChecks": 0}`
- Actual: `{"checks": 104, "failedChecks": []}`
