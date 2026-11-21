---
id: T-017
title: Pushing the screen edge crosses over again
spec: S-001
status: done
depends: [T-016]
files: [CrossingRule.cs, InputEngine.cs, tests/LocalSendDrop.Tests/EdgeReplayTests.cs, tests/LocalSendDrop.Tests/HelperFrameTests.cs]
tdd: strict
done: 2026-11-21
---
## Goal
2026-11-21: the double-tap crossed, pushing the left edge never did. A hook probe on HARSHID-PC showed the low-level
hook reports the point before Windows clips it (pushing left: 1,1050 then -5,1050). Off every monitor, KvmZones.Hit
found no zone, so CrossingRule.Step reset the push on every move and the resistance never filled.

## Steps
1. CrossingRule.OnScreen(raw, last, displays): off every monitor, clamp onto the one it was on.
2. InputEngine.CheckEdge: push measured from the raw point, edge check on the clipped one.
3. Tests: replay of a push reported off-screen crosses; OnScreen clamps; live settings/screens replay; helper zones frame round-trips.

## Done when
- [x] `lean test` passes
- [x] Live: edge push on HARSHID-PC crossed to DEVLAP (11:03:47, X=0)
