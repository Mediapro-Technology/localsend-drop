---
id: T-008
title: Settings apply at once, crossing lands right
spec: S-001
status: done
depends: [T-007]
files: [Kvm.cs, KvmSync.cs, KvmSettings.cs, KvmRemote.cs, KvmWire.cs, KvmZones.cs, InputEngine.cs, KvmLook.cs, Identity.cs, Program.cs, Pairing.cs, AdminHelper.cs, tests/LocalSendDrop.Tests/T008Tests.cs, tests/LocalSendDrop.Tests/KvmSyncTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Fix the 10 /code-review findings and 3 /lean:secure minors on d989964..02cf4b1 (report: .lean/security/2026-11-20.md). One card: the fixes share Kvm.cs/KvmSync.cs and must land together.

## Steps (test-first for each logic item; name tests after the item)
1. Local changes apply now: `KvmSync.Set` raises a local-change event (or SettingsChanged) → `Kvm.UpdateEdges`; InputEngine reads `Settings.Current.Crossing` live instead of a captured object; the helper gets the new rules.
2. Edge menu / Unpair: when a pairing's edge changes or it is removed (Program.cs SetKvmEdge, Pairing.Remove), drop that peer's entries in `Settings.Zones` (ZonesFor then falls back to Migrate). Test: change edge → crossing zone follows.
3. Beamer pause on the controlled side: `KvmRemote` refuses Enter/EnterAt while `BeamerWatch.Paused`, replying so the controller releases and says "paused on <PC>".
4. Migrated zones: mark target unknown (e.g. TargetDisplay -1) → send plain `Enter`; remote uses its own outer edge. Test: migrated zone sends Enter, not EnterAt.
5. `Leave` carries display + side (like EnterAt; old form still read); controller maps through the zone whose TargetDisplay/TargetSide match. Test: leave via the second zone returns beside it.
6. InputEngine `_lastPoint` refreshed on injected moves, Free and Capture; per-event overshoot capped to real movement. Test: stale last point doesn't skip resistance.
7. `KvmSync.Hello` returns an unregister handle, called when the link drops / session ends; no duplicate registration; ResendDisplays prunes failures. Test: reconnect 50× → one entry.
8. Kvm.Dispose: `KvmLook.IsLive = _ => false`, and `HiddenCursor.ShowAll()` clearing every reason (use on all shutdown paths).
9. KvmLook uses the same zone list as Kvm (ZonesFor fallback); `IdleClock.Reset()` when HideIdle is off.
10. Security: clamp peer `SetAt` to now + 1 day in `Sanitized()`; null members → defaults, and OnMessage never throws on the read thread; `LocalZones` uses the link fingerprint, never `layout.By`, and forces `Peer` to the sender. Tests are the report's "Prove" lines.

## Done when
- [ ] `lean test` passes (all new tests red first); `--kvm-loopback` passes (A13)
- [ ] A1 holds on both sides (controlled PC with Beamer refuses control)
- [ ] A5/A7: a setting or layout change applies on this PC without reconnecting
- [ ] A6: an old edge pairing crosses onto the other PC's outer edge
