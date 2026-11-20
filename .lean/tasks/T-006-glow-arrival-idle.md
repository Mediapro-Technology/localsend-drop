---
id: T-006
title: Edge glow, arrival effect, idle pointer hides
spec: S-001
status: todo
depends: [T-001]
files: [KvmLook.cs, tests/LocalSendDrop.Tests/KvmLookTests.cs]
tdd: after
---
## Goal
A glow on a live edge while the pointer is near it, a short effect where the pointer arrives from another PC (A8), and the pointer hides after 3 s still and comes back on movement (A9).

## Steps
1. `KvmLook.Start()`: 30 ms timer reads the cursor; within 40 px of a live zone → a layered click-through window glowing along that zone's span (colour from Theme), fading when away. Off when `Crossing.Glow` is false.
2. `KvmZones.Arrived` → a ring at that point for ~400 ms (medium size), if `Crossing.Arrival`.
3. Idle: no movement for 3 s while a link is live and not crossed → `HiddenCursor.Hide("idle")`; first move → `Show("idle")`. Never touches the "crossing" reason.
4. Test after: pure `IdleClock.Tick(pos, crossed, now) -> hide/show`. Tests: `Still3s_Hides`, `Move_Shows`, `Crossed_NeverShows`.

## Done when
- [ ] `lean test` passes
- [ ] A8: glow appears near the DEV_PC edge only; the arrival ring shows on the PC the pointer enters
- [ ] A9: pointer hides after 3 s still and returns on movement; after a crash it is visible again (HiddenCursor.RecoverAfterCrash)
