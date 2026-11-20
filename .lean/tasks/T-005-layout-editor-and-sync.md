---
id: T-005
title: Arrange screens and options, same on both
spec: S-001
status: doing
depends: [T-001]
files: [LayoutEditor.cs, KvmSync.cs, SettingsForm.cs, tests/LocalSendDrop.Tests/KvmSyncTests.cs, tests/LocalSendDrop.Tests/LayoutEditorTests.cs]
tdd: strict
---
## Goal
Settings gets a Mouse and keyboard section: a layout editor showing both PCs' monitors to scale (A5), the crossing options, and changes that reach the paired PC within 2 s with the newer change winning (A7).

## Steps
1. Test-first `KvmSync`: on change, stamp and send `Sync`; on receive, `CrossingSettings.Merge` and save if anything changed; resend after reconnect. Tests: `Receive_NewerWins_Saves`, `Receive_Older_Ignored`, `Echo_DoesNotLoop`.
2. Test-first the editor's pure part: `LayoutEditor.ZonesFrom(tiles)` turns touching tiles into zones (shared length → spans on both sides). Tests: `SideBySide_FullSpan`, `Offset_PartialSpan`, `Apart_NoZone`.
3. `LayoutEditor.cs` UI (test after): draw this PC's and peers' monitors (`KvmSync.PeerDisplays`) to scale; drag peer tiles; snap to edges; save zones with a layout stamp.
4. `SettingsForm.cs`: a section with the editor, shortcut on/off, resistance px, corners, notch, glow, arrival effect, hide idle pointer, pointer speed, scroll speed, reverse scroll. Each change goes through `KvmSync.Set`.

## Done when
- [ ] `lean test` passes
- [ ] A5: both PCs' monitors shown to scale; moving DEV_PC's tile changes where crossing happens, on both PCs
- [ ] A7: changing resistance on one PC shows on the other within 2 s; when both changed, the newer wins

## Watch
- Newer set-at stamp wins; clocks may differ between PCs (zk 20261120-122839)
