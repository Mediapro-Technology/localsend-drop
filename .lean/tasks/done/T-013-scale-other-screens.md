---
status: done
done: 2026-11-20
---
﻿---
id: T-013
title: Make the other PC's screens bigger or smaller in Arrange screens
spec: S-001
status: todo
depends: []
files: [LayoutEditor.cs, SettingsForm.cs, Identity.cs, tests/LocalSendDrop.Tests/PeerSizeTests.cs]
tdd: strict
---
## Goal
User asked for an option to scale the screens: DEV_PC (1920x1080) is drawn smaller than HARSHID-PC (2560x1440),
so its tile only covers part of the edge. A "Size of the other PC's screens (percent)" field (25 to 400, default 100)
draws its tiles that much bigger or smaller, and crossing follows the scaled layout. It's kept per paired PC, on this PC.
## Steps
1. Test first: LayoutEditor.Scaled(displays, percent) scales sizes and offsets from their top-left; ClampSize.
2. Settings.PeerSize (fingerprint to percent); Reload scales the peer tiles; SaveLayout() shared with dragging.
3. SettingsForm: a number field under Arrange screens. A change reloads the editor and re-saves a layout that exists.
## Done when
- [ ] `lean test` passes with PeerSizeTests
- [ ] At 133%, DEV_PC's tile is as tall as this PC's screen and crossing works along the whole edge
