---
id: 20261120-131334
type: gotcha
title: The low-level mouse hook point stops at the screen edge, so push resistance is counted as one pixel per stalled move
tags: [kvm, input]
links: []
source: T-003
created: 2026-11-20
---
Why: the hook reports the clamped position at the edge, so overshoot past it can't be read directly; counting stalled moves depends on mouse polling rate.
Apply: if resistance feels too light or heavy on a high-rate mouse, switch to raw-input deltas (WM_INPUT) for the overshoot.
