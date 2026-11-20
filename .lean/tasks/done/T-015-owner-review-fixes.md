---
status: done
done: 2026-11-20
no_tests: behaviour on the crossing path is covered by --kvm-loopback and OwnerTests; races need two live PCs
---
﻿---
id: T-015
title: Fixes from the review of one owner at a time
spec: S-001
status: todo
depends: [T-014]
files: [Kvm.cs, KvmRemote.cs]
tdd: after
---
## Goal
/code-review of T-014 confirmed:
- Two PCs crossing in the same instant could both win. Ownership is now claimed before the Enter goes out.
- A drag driven by the other PC triggered our own drag crossing and left a stale carry. Both are now stopped while we're controlled.
- The double-tap hand-back didn't send the clipboard home. It now does.
- The Busy wording was wrong, and is fixed.
Mixed-version peers (older than 2.7.5) don't know Busy and time out after 1 s. Both PCs are on the new version.
## Done when
- [x] `lean test` passes; `--kvm-loopback` passes (twice)
