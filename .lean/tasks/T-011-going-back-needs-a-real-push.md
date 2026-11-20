---
id: T-011
title: Going back needs a real push, not a wobble
spec: S-001
status: doing
depends: []
files: [CrossingRule.cs, KvmRemote.cs, Kvm.cs, tests/LocalSendDrop.Tests/ReturnPushTests.cs]
tdd: strict
---
## Goal
Bug found by hand: crossings to DEV_PC came straight back after 0.31 s (the 300 ms grace, then 40 px of
outward push with no decay). Going back now needs the crossing resistance (at least 40 px), pushed within a
short time: push leaks away at 300 px/s, moving inward takes it off, and until the pointer has moved 4 px from
where it arrived (or 0.3 s passed) outward movement only holds it. The controlling PC also logs when its hidden
pointer didn't park mid-screen, so a one-sided-delta problem shows in kvm.log.

## Steps
1. Test first `ReturnPush` (CrossingRule.cs): grace holds, decay, inward subtracts, threshold, reset after leaving.
2. KvmRemote.Move uses it with needed = max(40, ResistancePx) (DragPush for a carried drag).
3. Kvm.OnCrossed: 250 ms after parking, log if the cursor isn't near the park point.

## Done when
- [ ] `lean test` passes with ReturnPushTests; `--kvm-loopback` passes (A13)
- [ ] Crossing to DEV_PC stays there; pushing its right edge brings the pointer back
