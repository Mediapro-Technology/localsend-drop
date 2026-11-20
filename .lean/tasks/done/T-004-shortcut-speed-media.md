---
id: T-004
title: Switch PCs with a shortcut, set speeds
spec: S-001
status: done
depends: [T-003]
files: [InputEngine.cs, Kvm.cs, InputShaping.cs, tests/LocalSendDrop.Tests/ShortcutTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Double-tap right Ctrl goes to the last PC (A2); pointer/scroll speed and reverse scroll apply on the other PC (A11); media keys act on the PC with the pointer (A10). Waits on T-003 only because both edit InputEngine.cs and Kvm.cs; kept separate from T-003 because together they would be well over 400 changed lines.

## Steps
1. Test-first `DoubleTap` detector in `InputShaping.cs`: right Ctrl (VK 0xA3) down-up-down-up within `DoubleTapMs`, no other key between. Tests: `DoubleTap_Within300_Fires`, `SingleTap_DoesNot`, `CtrlChord_DoesNot`, `SlowTap_DoesNot`.
2. `InputEngine.OnKey`: free → detector raises `Shortcut`; captured → the tap pair is swallowed and Kvm comes back here.
3. `Kvm.cs`: on `Shortcut`, cross to the last PC used (else the first live zone) with `EnterAt` the middle of its last display; remember the last PC.
4. Test-first speed in `InputShaping.cs`: `Scale(dx, dy, speed)` carrying the remainder; wheel × ScrollSpeed, sign flipped if ReverseScroll. Tests: `Scale_Half_KeepsRemainder`, `Reverse_FlipsWheel`.
5. Media keys: confirm VK_MEDIA_*/VK_VOLUME_* pass through forwarding untouched (`MediaKeys_AreForwarded`).

## Done when
- [ ] `lean test` passes; `--kvm-loopback` passes
- [ ] A2: double-tap right Ctrl crosses to DEV_PC; again on DEV_PC comes back
- [ ] A10: play/pause and volume pressed while on DEV_PC act there (your keyboard; Fn hotkeys may stay local)
- [ ] A11: speed 0.5 halves movement on DEV_PC; reverse scroll flips it

## Watch
- Fn/vendor media hotkeys can bypass the hook; accepted (zk 20261120-130030)
