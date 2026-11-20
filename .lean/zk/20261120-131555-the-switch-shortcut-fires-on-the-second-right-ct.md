---
id: 20261120-131555
type: decision
title: The switch shortcut fires on the second right-Ctrl release, not the second press
tags: [kvm, input, shortcut]
links: []
source: T-004
created: 2026-11-20
---
Why: firing on the press would misfire on Ctrl+C typed right after a tap.
Apply: keep DoubleTap release-based; a tap pair while crossed is forwarded (two harmless Ctrl taps there) before control returns.
