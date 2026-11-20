---
id: 20261120-130831
type: gotcha
title: The panel's device menu exists only for discovered devices, so actions for an offline paired PC need an offline row first
tags: [panel, wol, kvm]
links: []
source: T-002
created: 2026-11-20
---
Why: DropPanel lists what Discovery currently sees; an offline paired PC has no row and no right-click menu.
Apply: Wake from the panel only works while the PC is still listed; the reliable path is wake-on-push at the edge (T-003), or add an offline-paired row.
