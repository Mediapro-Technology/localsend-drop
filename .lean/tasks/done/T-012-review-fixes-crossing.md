---
status: done
done: 2026-11-20
---
﻿---
id: T-012
title: Fixes from the review of the crossing fixes
spec: S-001
status: todo
depends: []
files: [CrossingRule.cs, KvmRemote.cs, KvmZones.cs, LayoutEditor.cs, tests/LocalSendDrop.Tests/ReturnPushTests.cs, tests/LocalSendDrop.Tests/LayoutEditorTests.cs]
tdd: strict
---
## Goal
/code-review of T-010/T-011 (2.7.3) confirmed:
- A slow, steady push could never go back, because of the 300 px/s leak. The leak now starts only after a 150 ms pause.
- An outward push ended the arrival grace. Distance is now measured from the clamped position.
- With a high resistance, a drag needed less push than a plain move. A drag now needs at least as much.
- Push built at one edge carried over to another. It now resets when the pointer leaves the edge.
- The editor saved widened spans, so a tile jumped when the editor was reloaded. It now saves the real spans and widens them where they're used.
- Two doc comments were in the wrong place.
- The self-test now follows the resistance setting.
Not done (minor): the editor's accent bar can draw a widened span along an edge shared with our own other monitor.
## Done when
- [x] `lean test` passes (SlowSteadyPush_StillGoesBack, Reset_DropsPush); `--kvm-loopback` passes
