---
id: 20261120-131048-3
type: decision
title: A synced layout is stored from the setter's view and mirrored on the receiving PC
tags: [kvm, layout, sync]
links: []
source: T-005
created: 2026-11-20
---
Why: each PC needs zones in its own terms (its displays, its edges).
Apply: KvmSync.LocalZones mirrors a peer's layout into Settings.Current.Zones; crossing code reads only Settings.Current.Zones.
