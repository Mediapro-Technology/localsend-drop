---
id: T-003
title: Crossing follows the screen layout
spec: S-001
status: done
depends: [T-001]
files: [Kvm.cs, InputEngine.cs, KvmRemote.cs, AdminHelper.cs, CrossingRule.cs, tests/LocalSendDrop.Tests/CrossingTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Crossing uses per-display zones with spans, push resistance, corners and the notch, and wakes an offline PC when you push into its zone. Covers A3, A4, A5 (runtime), A6, A12, A13. One card despite 6 criteria: it is all the one edge-check path in InputEngine/Kvm, and splitting it would make cards that edit the same files.

## Steps
1. Test-first: move the edge decision out of `InputEngine.CheckEdge` into pure `CrossingRule.Step(state, point, delta, zones, displays, settings, now) -> (state, Zone? cross, Zone? wake)`. Tests: `Resistance_Under120_DoesNotCross`, `Resistance_Over120_Crosses`, `Corner_Off_StaysPut`, `Corner_On_Crosses`, `Notch_On_Crosses`, `ButtonHeld_Blocks`, `OfflineZone_WakesOncePer30s`.
2. Resistance: sum the raw overshoot past the edge while pinned there, instead of 150 ms resting; 0 = cross on touch.
3. `IInputSource.SetEdges(int)` → `SetZones(Zone[] live, Zone[] offline)`; the `AdminHelper` frame carries them as JSON. `Crossed` passes the Zone.
4. `Kvm.cs`: build zones from `Settings.Zones` and link state; `OnCrossed` sends `EnterAt(targetDisplay, targetSide, KvmZones.Target(...))` (plain `Enter` to an older peer); return point and `Leave` go through the zone. Wake request → `WakeOnLan.Send(pc.Mac)` + KvmLog; learn Mac on link connect.
5. `KvmRemote.cs`: `EnterAt` puts the pointer on that display's edge; `Leave` when it crosses back over a zone facing the controller; raise `KvmZones.Arrived(point)`.
6. Extend `KvmTest.Loopback` with an `EnterAt` case on display 0.

## Done when
- [ ] `lean test` passes; `--kvm-loopback` passes (A13)
- [ ] A3, A4: unit tests above, then by hand against DEV_PC
- [ ] A5: a zone on one monitor's edge, top half only, leads across only there
- [ ] A6: after updating, the existing DEV_PC pairing crosses on its old edge with no setup
- [ ] A12: pushing into an offline PC's edge logs a wake packet, at most once per 30 s

## Watch
- Crossing math only through zones (zk 20261120-122838-2)
- Inner edges between this PC's own monitors must never cross
