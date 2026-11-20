---
id: 20261120-131334-2
type: decision
title: Pairings with no saved zones fall back to KvmZones.Migrate at runtime
tags: [kvm, zones]
links: []
source: T-003
created: 2026-11-20
---
Why: a pairing made after startup has no zone yet, and old pairings must keep crossing on their edge (A6).
Apply: Kvm builds zones from Settings.Zones, else Migrate(pc) for each paired PC.
