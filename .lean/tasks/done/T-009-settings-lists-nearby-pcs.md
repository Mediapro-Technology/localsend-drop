---
id: T-009
title: Settings lists nearby PCs to pair with
spec: S-001
status: done
depends: []
files: [PcList.cs, SettingsForm.cs, Program.cs, tests/LocalSendDrop.Tests/PcListTests.cs]
tdd: strict
done: 2026-11-20
---
## Goal
Settings > Mouse and keyboard shows every nearby PC (desktop) and every paired PC, with its state
(connected, paired but not connected, not nearby, available) and a Pair / Unpair button. Before this,
pairing was only in the drop panel's right-click menu, so an unpaired PC showed nothing in Settings.

## Steps
1. Test first: `PcList.Rows(nearby, paired, connected)` merges nearby desktops and paired PCs by fingerprint,
   paired first, phones left out, with the status text.
2. `PcList` control: one 52 px row per PC (name, status, button), "No other PCs found" when empty.
3. SettingsForm: "PCs" card above "Arrange screens"; a 2 s timer refreshes the list and the layout editor.
   Pair runs the existing pairing flow (Settings drops TopMost meanwhile); Unpair the existing unpair.
4. Program.cs passes nearby devices, pair and unpair into SettingsForm.

## Done when
- [ ] `lean test` passes, including PcListTests
- [ ] Settings shows nearby PCs and pairing from it works
