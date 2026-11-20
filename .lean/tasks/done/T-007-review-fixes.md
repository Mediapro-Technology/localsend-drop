---
id: T-007
title: Fixes from the review of the Beamer work
spec: S-001
status: done
depends: [T-001, T-002, T-003, T-004, T-005, T-006]
files: [Kvm.cs, KvmSync.cs, KvmSettings.cs, WakeOnLan.cs, tests/LocalSendDrop.Tests/ReviewFixTests.cs, tests/LocalSendDrop.Tests/KvmSyncTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Fix what /lean:review found on d989964..HEAD: wake work on the hook thread (major), unchecked values from the peer, a race in the first sync, stale displays after a monitor change, and wake packets that leave by one network card only.

## Steps
1. Test-first `CrossingSettings.Merge` clamps the peer's values: speeds 0.2–4 (NaN → 1), DoubleTapMs 150–800, ResistancePx 0–2000, zones with spans outside 0–1 or From ≥ To dropped. Tests `Merge_ClampsPeerValues`, `Merge_DropsBadZones`.
2. Test-first `KvmSync.Hello` registers the link before its first send (`Hello_SetDuringHello_ReachesNewLink`); add `KvmSync.ResendDisplays()`.
3. `Kvm.OnWake` hands off with `Task.Run` (no file or socket work on the hook thread).
4. `Kvm`: on `SystemEvents.DisplaySettingsChanged` → `UpdateEdges()` and `KvmSync.ResendDisplays()`; unsubscribe in Dispose.
5. Test-first `WakeOnLan.DirectedBroadcast(ip, mask)`; `Send` goes to 255.255.255.255 and each up IPv4 interface's directed broadcast. Tests `DirectedBroadcast_Slash24`, `Parse_Formats`.
6. Test: Zone[] JSON round trip through `KvmWire.JsonMsg`/`ReadJson`.

## Done when
- [ ] `lean test` passes; `--kvm-loopback` passes (A13)
- [ ] A7: a change made while a link is coming up still reaches it
- [ ] A12: wake packet logged without delaying the pointer

## Watch
- Known limit, not fixed: one shared layout for one paired PC; multi-PC layouts need per-peer layouts (spec Risks)
