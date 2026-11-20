---
id: S-001
title: One mouse and keyboard across PCs, without Beamer
status: agreed
tags: [kvm, beamer, crossing, layout]
---
## Problem
The office runs Beamer (kalkman-code/beamer, GPL-3.0) for one mouse and keyboard across PCs,
next to LocalSend Drop, which since 2.6.0 has its own mouse-and-keyboard sharing (Kvm*.cs).
Two apps hooking the same input fight over the edges, and Beamer is a second thing to install
and update. LocalSend Drop should do everything we use Beamer for, so Beamer can be uninstalled.

## Decisions
- D1: Rebuild Beamer's features in our own C# KVM; do not speak Beamer's wire v6 or copy its code — office is Windows-only, and Beamer is GPL-3.0 while LocalSend Drop has no GPL licence (zk 20261120-122838).
- D2: While a Beamer.exe process runs, our mouse-and-keyboard sharing pauses (hooks off, links idle) and the panel says why; it resumes on its own when Beamer quits.
- D3: Shortcut switch is double-tap right Ctrl: goes to the last PC used; double-tap again comes back.
- D4: Layout is per display: a zone is a span (0–1 along it) of one edge of one of this PC's monitors, leading to a span of an edge of one monitor of a paired PC. Replaces today's one-outer-edge-per-PC model; existing pairings migrate to a full-span zone on the matching outer edge (zk 20261120-122838-2).
- D5: Crossing settings (shortcut, resistance, corners/notch, glow, effects, speeds, layout) are the same on both paired PCs: each value carries a set-at stamp and the newer one wins on sync (zk 20261120-122839).
- D6: Media keys act on the PC that has the pointer, like any other key.
- D7: Wake-on-LAN: pushing into the edge of an offline paired PC sends a magic packet to its stored MAC; the panel also gets a Wake item.
- D8: Media keys are captured by the low-level keyboard hook alone (VK_MEDIA_*, VK_VOLUME_*); vendor/Fn hotkeys that bypass it are a known limit (zk 20261120-130030).

## Defaults
- Push resistance default 120 px (as in your Beamer settings); 0 turns it off.
- Corners and the notch (top-centre) are off by default; edges on.
- Double-tap window 300 ms. Edge glow on, arrival effect on, medium size.
- Hide idle pointer after 3 s still while it is on this PC with a link active.
- Pointer and scroll speed 1.0x, reverse scroll off; applied on the PC with the physical mouse.
- Block crossing while a mouse button is held unless it is a file drag (KvmDrag keeps working).
- MAC address learnt from the peer when linked (ARP of its IP), stored with the pairing.
- Layout editor lives in Settings; tiles drawn to scale from each PC's reported displays.
- Ctrl+Alt+Home emergency return and the 1 s silence rule stay as they are.

## Research
- R1: Do media keys (play/pause, next, volume) from common keyboards reach WH_KEYBOARD_LL as VK_MEDIA_*/VK_VOLUME_* so they can be suppressed and forwarded, or do some arrive only as HID consumer reports / WM_APPCOMMAND that the hook can't block?
  → Standard keyboards: yes, they arrive as VK_MEDIA_*/VK_VOLUME_* and returning non-zero swallows them; WM_APPCOMMAND only comes later from DefWindowProc. OEM/ACPI Fn hotkeys and some vendor software may bypass the hook; Raw Input can observe but never block (source: https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-appcommand; confidence: medium; zk 20261120-130030)

## Out of scope
- Mac/Linux machines and Beamer protocol compatibility.
- Profiles (named layouts), touchpad gestures.
- Sound sharing, screen sharing changes.

## Acceptance
- A1: With Beamer.exe running, LocalSend Drop takes no input and the panel shows "Paused: Beamer is running"; within 5 s of Beamer quitting, crossing works.
- A2: Double-tap right Ctrl sends input to the last PC used; double-tap on the other PC brings it back. A single tap or a Ctrl chord does nothing extra.
- A3: With resistance 120, pushing <120 px past the edge doesn't cross; pushing further does.
- A4: With corners/notch on, the pointer crosses from that corner/notch; with them off, corners stay put.
- A5: Layout editor shows both PCs' monitors to scale; dragging a tile changes which monitor edge and span leads across, and crossing follows it on both PCs.
- A6: Pre-S-001 pairings still cross on their old edge after the update, with no setup.
- A7: Changing a crossing setting on one PC shows on the other within 2 s; the newer change wins when both changed.
- A8: Edge glow shows while the pointer is near a live edge; arrival effect plays where the pointer enters.
- A9: Idle pointer hides after 3 s and returns on movement.
- A10: Play/pause and volume keys pressed while on DEV_PC act on DEV_PC, not this PC.
- A11: Pointer/scroll speed and reverse scroll change movement on the other PC as set.
- A12: Pushing into an offline PC's edge sends a wake packet (seen in KvmLog), at most once per 30 s.
- A13: --kvm-loopback still passes.

## Risks
- Per-display zones touch every crossing path (Kvm.cs, KvmWire edges, return point): regressions in plain edge crossing. Mitigate with loopback tests per zone shape.
- Hiding the system cursor globally (SetSystemCursor) must always be restored, even on crash/exit.
- Settings sync clock skew between PCs: stamps from different clocks; last-writer may surprise.
- Media keys: laptop Fn / vendor-driver hotkeys may bypass the hook and act locally (R1, accepted). Verify A10 on the real keyboard.

## Cards
- T-001 Groundwork for layout, sync and tests
- T-002 Sharing pauses while Beamer runs
- T-003 Crossing follows the screen layout
- T-004 Switch PCs with a shortcut, set speeds
- T-005 Arrange screens and options, same on both
- T-006 Edge glow, arrival effect, idle pointer hides
