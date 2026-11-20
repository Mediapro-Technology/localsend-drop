---
id: 20261120-131555-2
type: pattern
title: Scale pointer and wheel at the sending PC's input engine, carrying the remainder
tags: [kvm, input]
links: []
source: T-004
created: 2026-11-20
---
Why: speeds are synced on both PCs, and doing it in InputEngine covers the admin helper too.
Apply: InputShaping.Scale before KvmWire.Move/Mouse; never scale again on the receiver.
