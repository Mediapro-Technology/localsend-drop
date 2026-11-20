---
id: 20261120-131048
type: pattern
title: Stamp synced settings with max(clock, newest seen + 1) so a PC with a slow clock still wins with its latest change
tags: [kvm, sync]
links: []
source: T-005
created: 2026-11-20
---
Why: last-writer-wins on wall clocks loses a fresh change made on a PC whose clock is behind.
Apply: KvmSync.Set stamps max(now, highest stamp seen + 1).
