---
id: T-016
title: Shortcut crossing no longer bounces straight back
spec: S-001
status: done
depends: [T-015]
files: [InputEngine.cs, KvmRemote.cs, CrossingRule.cs, tests/LocalSendDrop.Tests/BounceTests.cs]
tdd: strict
done: 2026-11-21
---
## Goal
2026-11-21 logs: crossings made with the Right-Ctrl double-tap (all at {1,1080}) came back 3–4 ms after arriving on DEVLAP.
Cause: InputEngine.Cross used the made-up edge point as the delta baseline (_last), while the real pointer was elsewhere,
so the next real move was forwarded as a jump of hundreds of px; on the controlled side one such move filled the return push.

## Steps
1. InputEngine: Capture takes a baseline; Cross (shortcut, not from the hook) uses the real cursor position (`CrossingRule.Baseline`).
2. Controlled side: one move counts at most `ReturnPush.MaxStepPx` (50) toward going back (`ReturnPush.Clip`), like the hook's MaxStep.
3. Tests: Baseline picks the cursor for a shortcut crossing and the event point for an edge crossing; one huge outward move doesn't go back; steady pushes still do.

## Done when
- [ ] `lean test` passes
- [ ] BounceTests cover both behaviours
