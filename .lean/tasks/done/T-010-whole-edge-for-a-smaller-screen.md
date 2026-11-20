---
id: T-010
title: A smaller screen beside a bigger one uses the whole edge
spec: S-001
status: done
depends: []
files: [KvmZones.cs, LayoutEditor.cs, Kvm.cs, KvmRemote.cs, tests/LocalSendDrop.Tests/WidenTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Bug found by hand: DEV_PC (1920x1080) placed beside HARSHID-PC's 2560x1440 left edge made a zone From 0 To 0.5,
so pushing the lower half of the edge never crossed. A zone that is the only one on its edge and whose other
side covers that whole screen edge now spans the full edge (both directions). An overhanging tile keeps its
partial span (A5). Applied when zones are used, so saved layouts are fixed without re-saving.

## Steps
1. Test first `KvmZones.Widen(zones)`: smaller-inside → ours 0..1; ours-inside-theirs → target 0..1;
   overhang (both partial) unchanged; two zones on one edge unchanged.
2. Use it in LayoutEditor.ZonesFrom and Kvm.ZonesFor (covers the controlled side through KvmRemote too).

## Done when
- [ ] `lean test` passes with WidenTests
- [ ] Pushing anywhere on HARSHID-PC's left edge crosses to DEV_PC
